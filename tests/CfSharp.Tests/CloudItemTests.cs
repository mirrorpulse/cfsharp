using System.Runtime.Versioning;

namespace CfSharp.Tests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed class CloudItemTests
{
    [Fact]
    public async Task InspectionReturnsFreshImmutableFileAndRootSnapshots()
    {
        using TestDirectory root = new();
        string filePath = Path.Combine(root.Path, "report.txt");
        await File.WriteAllTextAsync(filePath, "one");
        await using CloudFileSystem fileSystem = await StartAsync(root.Path, new InspectionStore());

        CloudFile file = fileSystem.GetFile(@"folder\..\report.txt");
        CloudItemSnapshot first = await file.InspectAsync();
        await File.WriteAllTextAsync(filePath, "second-value");
        CloudItemSnapshot second = await file.InspectAsync();
        CloudItemSnapshot rootSnapshot = await fileSystem.Root.InspectAsync();

        Assert.Equal("report.txt", file.Name);
        Assert.Equal("report.txt", file.RelativePath);
        Assert.Equal(Path.GetFullPath(filePath), file.FullPath);
        Assert.Same(fileSystem.Root.GetType(), file.Parent?.GetType());
        Assert.Equal(3, first.Length);
        Assert.NotNull(first.LocalBinding);
        Assert.Equal(first.LocalBinding, second.LocalBinding);
        Assert.Null(rootSnapshot.LocalBinding);
        Assert.Equal(12, second.Length);
        Assert.Equal(3, first.Length);
        Assert.True(first.Exists);
        Assert.False(first.IsPlaceholder);
        Assert.Equal(CloudContentAvailability.NotApplicable, first.ContentAvailability);
        Assert.Equal(CloudItemKind.Directory, rootSnapshot.Kind);
        Assert.True(rootSnapshot.Exists);
        Assert.Null(rootSnapshot.Length);
        Assert.Null(fileSystem.Root.Parent);
    }

    [Fact]
    public async Task MissingLocalItemStillReturnsDurableTombstoneState()
    {
        using TestDirectory root = new();
        Guid itemId = Guid.NewGuid();
        DateTimeOffset updatedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        CloudItemState state = new(
            itemId,
            "remote-gone",
            "gone.txt",
            CloudItemKind.File,
            "revision-4",
            42,
            true,
            updatedAt);
        await using CloudFileSystem fileSystem = await StartAsync(
            root.Path,
            new InspectionStore(state));

        CloudItemSnapshot snapshot = await fileSystem.GetFile("gone.txt").InspectAsync();

        Assert.False(snapshot.Exists);
        Assert.Equal(itemId, snapshot.ItemId);
        Assert.Equal("remote-gone", snapshot.RemoteId);
        Assert.Equal("revision-4", snapshot.RemoteRevision);
        Assert.Equal(42, snapshot.LocalFileId);
        Assert.True(snapshot.IsTombstone);
        Assert.Equal(updatedAt, snapshot.DurableStateUpdatedAt);
    }

    [Fact]
    public async Task EmptyDirectoryDeleteTombstonesOrphanedDurableDescendants()
    {
        using TestDirectory root = new();
        Directory.CreateDirectory(Path.Combine(root.Path, "orphan"));
        CloudItemState directory = new(
            Guid.NewGuid(),
            "remote-directory",
            "orphan",
            CloudItemKind.Directory,
            null,
            null,
            false,
            DateTimeOffset.UtcNow);
        CloudItemState child = new(
            Guid.NewGuid(),
            "remote-child",
            Path.Combine("orphan", "stale.txt"),
            CloudItemKind.File,
            null,
            null,
            false,
            DateTimeOffset.UtcNow);
        await using CloudFileSystem fileSystem = await StartAsync(
            root.Path,
            new InspectionStore(directory, child));

        CloudItemDeleteResult result = await fileSystem.GetDirectory("orphan").DeleteAsync();

        Assert.True(result.DurableStateUpdated);
        Assert.True(result.Snapshot.IsTombstone);
        CloudItemSnapshot childSnapshot = await fileSystem
            .GetFile(Path.Combine("orphan", "stale.txt"))
            .InspectAsync();
        Assert.True(childSnapshot.IsTombstone);
    }

    [Fact]
    public async Task InspectionRejectsLocalAndDurableKindMismatches()
    {
        using TestDirectory root = new();
        Directory.CreateDirectory(Path.Combine(root.Path, "folder"));
        CloudItemState directoryState = new(
            Guid.NewGuid(),
            "remote-directory",
            "missing.txt",
            CloudItemKind.Directory,
            null,
            null,
            false,
            DateTimeOffset.UtcNow);
        await using CloudFileSystem fileSystem = await StartAsync(
            root.Path,
            new InspectionStore(directoryState));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fileSystem.GetFile("folder").InspectAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fileSystem.GetFile("missing.txt").InspectAsync());
    }

    [Fact]
    public async Task ItemPathsRejectRootedTraversalAndLinkedEscapes()
    {
        using TestDirectory root = new();
        using TestDirectory outside = new();
        string linkPath = Path.Combine(root.Path, "outside-link");
        Directory.CreateSymbolicLink(linkPath, outside.Path);
        await using CloudFileSystem fileSystem = await StartAsync(root.Path, new InspectionStore());

        Assert.Throws<ArgumentException>(() => fileSystem.GetFile(@"..\outside.txt"));
        Assert.Throws<ArgumentException>(() => fileSystem.GetFile(outside.Path));
        Assert.Throws<ArgumentException>(() => fileSystem.GetFile(string.Empty));
        Assert.Throws<ArgumentException>(() => fileSystem.GetDirectory("outside-link"));
    }

    [Fact]
    public async Task ItemOperationsRequireStartedOwner()
    {
        using TestDirectory root = new();
        InspectionStore store = new();
        StubRuntime runtime = new();
        CloudFileSystem fileSystem = CloudFileSystem
            .CreateBuilder(root.Path, runtime)
            .WithStateStore(new SingleStoreFactory(store))
            .Build();

        Assert.Throws<InvalidOperationException>(() => _ = fileSystem.Root);
        await fileSystem.StartAsync();
        CloudFile file = fileSystem.GetFile("later.txt");
        await fileSystem.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => fileSystem.GetFile("later.txt"));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await file.InspectAsync());
    }

    [Fact]
    public void SnapshotOwnsPlaceholderIdentityBytes()
    {
        byte[] identity = [1, 2, 3];
        CloudItemSnapshot snapshot = new(
            CloudItemKind.File,
            true,
            FileAttributes.Offline,
            10,
            null,
            null,
            null,
            CloudPlaceholderState.Placeholder | CloudPlaceholderState.Partial,
            CloudContentAvailability.OnlineOnly,
            CloudPinState.Unpinned,
            CloudSynchronizationState.InSync,
            1,
            2,
            0,
            0,
            0,
            0,
            identity,
            null,
            DateTimeOffset.UtcNow);

        identity[0] = 9;

        Assert.Equal(1, snapshot.PlaceholderIdentity.Span[0]);
    }

    [Fact]
    public async Task DirectoryCreatesReferencesAndResolvesExistingKinds()
    {
        using TestDirectory root = new();
        Directory.CreateDirectory(Path.Combine(root.Path, "docs"));
        await File.WriteAllTextAsync(Path.Combine(root.Path, "docs", "report.txt"), "report");
        await using CloudFileSystem fileSystem = await StartAsync(root.Path, new InspectionStore());

        CloudDirectory docs = fileSystem.Root.GetDirectory("docs");
        CloudFile report = docs.GetFile("report.txt");
        CloudItem resolvedFile = fileSystem.Root.Resolve(@"docs\report.txt");
        CloudItem resolvedDirectory = fileSystem.Root.Resolve("docs");

        Assert.Equal(@"docs\report.txt", report.RelativePath);
        Assert.IsType<CloudFile>(resolvedFile);
        Assert.IsType<CloudDirectory>(resolvedDirectory);
        Assert.Equal("docs", resolvedFile.Parent?.Name);
        Assert.Throws<FileNotFoundException>(() => docs.Resolve("missing.txt"));
    }

    [Fact]
    public async Task LocalEnumerationFiltersOrdersAndRecursesExplicitly()
    {
        using TestDirectory root = new();
        Directory.CreateDirectory(Path.Combine(root.Path, "nested"));
        await File.WriteAllTextAsync(Path.Combine(root.Path, "zeta.bin"), "z");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "alpha.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(root.Path, "nested", "beta.txt"), "b");
        await using CloudFileSystem fileSystem = await StartAsync(root.Path, new InspectionStore());

        IReadOnlyList<CloudItem> topLevel = await CollectAsync(
            fileSystem.Root.EnumerateLocalChildrenAsync());
        CloudDirectoryEnumerationOptions recursiveText = CloudDirectoryEnumerationOptions
            .CreateBuilder()
            .WithSearchPattern("*.txt")
            .WithEntryKinds(CloudDirectoryEntryKinds.Files)
            .WithOrder(CloudDirectoryEnumerationOrder.NameAscending)
            .WithRecursion()
            .Build();
        IReadOnlyList<CloudItem> textFiles = await CollectAsync(
            fileSystem.Root.EnumerateLocalChildrenAsync(recursiveText));

        Assert.Equal(3, topLevel.Count);
        Assert.Contains(topLevel, item => item is CloudDirectory && item.Name == "nested");
        Assert.Contains(topLevel, item => item is CloudFile && item.Name == "alpha.txt");
        Assert.Contains(topLevel, item => item is CloudFile && item.Name == "zeta.bin");
        Assert.Equal(2, textFiles.Count);
        Assert.Equal("alpha.txt", textFiles[0].Name);
        Assert.Equal("beta.txt", textFiles[1].Name);
        Assert.Equal(@"nested\beta.txt", textFiles[1].RelativePath);
    }

    [Fact]
    public async Task EnumerationIsFreshAndDoesNotFollowDirectoryLinks()
    {
        using TestDirectory root = new();
        string target = Path.Combine(root.Path, "target");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "only-once.txt"), "content");
        Directory.CreateSymbolicLink(Path.Combine(root.Path, "alias"), target);
        await using CloudFileSystem fileSystem = await StartAsync(root.Path, new InspectionStore());
        CloudDirectoryEnumerationOptions recursive = CloudDirectoryEnumerationOptions
            .CreateBuilder()
            .WithRecursion()
            .Build();

        IReadOnlyList<CloudItem> first = await CollectAsync(
            fileSystem.Root.EnumerateLocalChildrenAsync(recursive));
        await File.WriteAllTextAsync(Path.Combine(root.Path, "added.txt"), "new");
        IReadOnlyList<CloudItem> second = await CollectAsync(
            fileSystem.Root.EnumerateLocalChildrenAsync(recursive));

        Assert.Single(first.Where(item => item.Name == "only-once.txt"));
        Assert.DoesNotContain(first, item => item.Name == "added.txt");
        Assert.Contains(second, item => item.Name == "added.txt");
    }

    [Fact]
    public async Task EnumerationRejectsLinkedEscapeAndHonorsCancellation()
    {
        using TestDirectory root = new();
        using TestDirectory outside = new();
        Directory.CreateSymbolicLink(Path.Combine(root.Path, "outside"), outside.Path);
        await using CloudFileSystem fileSystem = await StartAsync(root.Path, new InspectionStore());

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(fileSystem.Root.EnumerateLocalChildrenAsync()));

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await CollectAsync(fileSystem.Root.EnumerateLocalChildrenAsync(
                cancellationToken: cancellation.Token)));
    }

    [Fact]
    public void EnumerationOptionsValidatePatternsKindsAndOrder()
    {
        Assert.Throws<ArgumentException>(() => CloudDirectoryEnumerationOptions
            .CreateBuilder()
            .WithSearchPattern(@"nested\*.txt"));
        Assert.Throws<ArgumentOutOfRangeException>(() => CloudDirectoryEnumerationOptions
            .CreateBuilder()
            .WithEntryKinds((CloudDirectoryEntryKinds)8)
            .Build());
        Assert.Throws<ArgumentOutOfRangeException>(() => CloudDirectoryEnumerationOptions
            .CreateBuilder()
            .WithOrder((CloudDirectoryEnumerationOrder)10)
            .Build());
    }

    [Fact]
    public async Task NamespaceOperationsRejectInvalidTargetsBeforeMutation()
    {
        using TestDirectory firstRoot = new();
        using TestDirectory secondRoot = new();
        await using CloudFileSystem first = await StartAsync(
            firstRoot.Path,
            new InspectionStore());
        await using CloudFileSystem second = await StartAsync(
            secondRoot.Path,
            new InspectionStore());
        CloudFile file = first.GetFile("missing.bin");
        CloudDirectory directory = first.GetDirectory("missing-directory");

        await Assert.ThrowsAsync<ArgumentException>(() => file
            .MoveToAsync(first.Root, @"nested\invalid.bin")
            .AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => file
            .MoveToAsync(second.Root, "other.bin")
            .AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => directory
            .MoveToAsync(
                first.Root,
                "renamed",
                new CloudMoveOptions(replaceExisting: true))
            .AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.Root
            .MoveToAsync(first.Root, "renamed-root")
            .AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.Root
            .DeleteAsync()
            .AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.Root
            .DeleteTreeAsync()
            .AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => first.Root
            .SetPinStateRecursivelyAsync((CloudPinTarget)int.MaxValue)
            .AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => first.Root
            .SetAvailabilityRecursivelyAsync((CloudAvailabilityTarget)int.MaxValue)
            .AsTask());
    }

    private static async Task<IReadOnlyList<CloudItem>> CollectAsync(
        IAsyncEnumerable<CloudItem> items)
    {
        List<CloudItem> result = [];
        await foreach (CloudItem item in items)
        {
            result.Add(item);
        }

        return result;
    }

    private static async Task<CloudFileSystem> StartAsync(string rootPath, InspectionStore store)
    {
        CloudFileSystem fileSystem = CloudFileSystem
            .CreateBuilder(rootPath, new StubRuntime())
            .WithStateStore(new SingleStoreFactory(store))
            .Build();
        await fileSystem.StartAsync();
        return fileSystem;
    }

    private sealed class TestDirectory : IDisposable
    {
        internal TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "CfSharp-item-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }

    private sealed class SingleStoreFactory : ICloudStateStoreFactory
    {
        private readonly InspectionStore _store;

        internal SingleStoreFactory(InspectionStore store)
        {
            _store = store;
        }

        public ValueTask<ICloudStateStore> OpenAsync(
            CloudStateStoreContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ICloudStateStore>(_store);
        }
    }

    private sealed class InspectionStore : ICloudStateStore
    {
        private readonly Dictionary<string, CloudItemState> _items;

        internal InspectionStore(params CloudItemState[] items)
        {
            _items = items.ToDictionary(item => item.RelativePath, StringComparer.OrdinalIgnoreCase);
        }

        public ValueTask<ICloudStateTransaction> BeginTransactionAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ICloudStateTransaction>(new InspectionTransaction(_items));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InspectionTransaction : ICloudStateTransaction, ICloudItemStateRepository
    {
        private readonly Dictionary<string, CloudItemState> _target;
        private readonly Dictionary<string, CloudItemState> _items;
        private bool _disposed;

        internal InspectionTransaction(Dictionary<string, CloudItemState> items)
        {
            _target = items;
            _items = new(items, StringComparer.OrdinalIgnoreCase);
        }

        public ICloudItemStateRepository Items => this;

        public ICloudCheckpointRepository Checkpoints => throw new NotSupportedException();

        public ICloudOperationJournal Operations => throw new NotSupportedException();

        public ICloudConflictRepository Conflicts => throw new NotSupportedException();

        public ICloudRemoteBatchRepository RemoteBatches => throw new NotSupportedException();

        public ICloudEchoSuppressionRepository EchoSuppressions => throw new NotSupportedException();

        public ValueTask<CloudItemState?> GetByItemIdAsync(
            Guid itemId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_items.Values.SingleOrDefault(item => item.ItemId == itemId));

        public ValueTask<CloudItemState?> GetByRemoteIdAsync(
            string remoteId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_items.Values.SingleOrDefault(
                item => string.Equals(item.RemoteId, remoteId, StringComparison.Ordinal)));

        public ValueTask<CloudItemState?> GetByRelativePathAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            _items.TryGetValue(relativePath, out CloudItemState? item);
            return ValueTask.FromResult(item);
        }

        public ValueTask<IReadOnlyList<CloudItemState>> ListSubtreeAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string prefix = relativePath.Length == 0
                ? string.Empty
                : relativePath + Path.DirectorySeparatorChar;
            IReadOnlyList<CloudItemState> result = _items.Values
                .Where(item =>
                    string.Equals(item.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase) ||
                    (prefix.Length != 0 && item.RelativePath.StartsWith(
                        prefix,
                        StringComparison.OrdinalIgnoreCase)))
                .OrderBy(item => item.RelativePath.Length)
                .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return ValueTask.FromResult(result);
        }

        public ValueTask UpsertAsync(
            CloudItemState item,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _items[item.RelativePath] = item;
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(
            Guid itemId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? path = _items.Values
                .SingleOrDefault(item => item.ItemId == itemId)
                ?.RelativePath;
            if (path is not null)
            {
                _items.Remove(path);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask CommitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _target.Clear();
            foreach ((string path, CloudItemState item) in _items)
            {
                _target[path] = item;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubRuntime : ICloudFileSystemRuntime
    {
        public ICloudFileSystemRuntimeSession Start(
            string syncRootPath,
            SyncRootRegistrationOptions? registration,
            ICloudFileContentProvider? contentProvider,
            ICloudStateStore stateStore) =>
            new StubRuntimeSession();
    }

    private sealed class StubRuntimeSession : ICloudFileSystemRuntimeSession
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
