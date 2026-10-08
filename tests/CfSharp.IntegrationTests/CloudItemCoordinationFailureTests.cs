using System.Runtime.Versioning;

namespace CfSharp.IntegrationTests;

public sealed class CloudItemCoordinationFailureTests
{
    [Fact]
    [SupportedOSPlatform("windows10.0.16299")]
    public async Task MoveAndDeletePreserveCompletedFileSystemWorkWhenCommitFails()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return;
        }

        string testPath = Path.Combine(
            Path.GetTempPath(),
            "CfSharp-coordination-failure-tests",
            Guid.NewGuid().ToString("N"));
        string rootPath = Path.Combine(testPath, "root");
        string sourcePath = Path.Combine(rootPath, "Source");
        string sourceChildPath = Path.Combine(sourcePath, "child.bin");
        string deletePath = Path.Combine(rootPath, "delete.bin");
        string movedPath = Path.Combine(rootPath, "Moved");
        Directory.CreateDirectory(sourcePath);
        await File.WriteAllTextAsync(sourceChildPath, "child");
        await File.WriteAllTextAsync(deletePath, "delete");
        Guid sourceId = Guid.NewGuid();
        Guid childId = Guid.NewGuid();
        Guid deleteId = Guid.NewGuid();
        FailingCommitStore store = new(
        [
            CreateState(sourceId, "remote-source", "Source", CloudItemKind.Directory),
            CreateState(
                childId,
                "remote-child",
                Path.Combine("Source", "child.bin"),
                CloudItemKind.File),
            CreateState(deleteId, "remote-delete", "delete.bin", CloudItemKind.File),
        ]);
        Guid providerId = Guid.NewGuid();
        CloudFileSystem? fileSystem = null;
        CloudSyncRoot? root = null;
        bool registered = false;

        try
        {
            SyncRootRegistrationOptions registration = SyncRootRegistrationOptions
                .CreateBuilder($"CfSharp Coordination {providerId:N}", "1.0.0-test")
                .WithProviderId(providerId)
                .WithSyncRootIdentity(providerId.ToByteArray())
                .WithHydrationPolicy(CloudHydrationPolicy.Progressive)
                .WithPopulationPolicy(CloudPopulationPolicy.Partial)
                .WithRootMarkedInSync()
                .Build();
            fileSystem = CloudFileSystem.CreateBuilder(rootPath)
                .WithStateStore(new FailingCommitStoreFactory(store))
                .WithRegistration(registration)
                .Build();
            await fileSystem.StartAsync();
            root = CloudSyncRoot.Open(rootPath);
            registered = true;

            CloudItemCoordinationException moveFailure = await Assert.ThrowsAsync<
                CloudItemCoordinationException>(() => fileSystem
                    .GetDirectory("Source")
                    .MoveToAsync(fileSystem.Root, "Moved")
                    .AsTask());
            Assert.Equal("CloudItem.Move", moveFailure.Operation);
            Assert.Equal(sourcePath, moveFailure.Path);
            Assert.Equal(movedPath, moveFailure.DestinationPath);
            IOException moveCommitFailure = Assert.IsType<IOException>(moveFailure.InnerException);
            Assert.Equal("Injected commit failure.", moveCommitFailure.Message);
            Assert.False(Directory.Exists(sourcePath));
            Assert.True(
                Directory.Exists(movedPath),
                $"Root entries: {string.Join(", ", Directory.GetFileSystemEntries(rootPath))}");

            CloudItemCoordinationException deleteFailure = await Assert.ThrowsAsync<
                CloudItemCoordinationException>(() => fileSystem
                    .GetFile("delete.bin")
                    .DeleteAsync()
                    .AsTask());
            Assert.Equal("CloudItem.Delete", deleteFailure.Operation);
            Assert.Equal(deletePath, deleteFailure.Path);
            Assert.Null(deleteFailure.DestinationPath);
            IOException deleteCommitFailure = Assert.IsType<IOException>(deleteFailure.InnerException);
            Assert.Equal("Injected commit failure.", deleteCommitFailure.Message);
            Assert.False(File.Exists(deletePath));
        }
        finally
        {
            if (fileSystem is not null)
            {
                try
                {
                    await fileSystem.DisposeAsync();
                }
                catch (Exception)
                {
                    // Preserve the original test failure while still cleaning persistent state.
                }
            }

            if (!registered)
            {
                try
                {
                    root = CloudSyncRoot.Open(rootPath);
                    registered = true;
                }
                catch (CloudFilesException)
                {
                    // No registration was created, or it was already removed successfully.
                }
            }

            if (registered && root is not null)
            {
                try
                {
                    root.Unregister();
                }
                catch (CloudFilesException)
                {
                    // Preserve the original test failure while still attempting system cleanup.
                }
            }

            if (Directory.Exists(testPath))
            {
                Directory.Delete(testPath, recursive: true);
            }
        }
    }

    private static CloudItemState CreateState(
        Guid itemId,
        string remoteId,
        string relativePath,
        CloudItemKind kind) =>
        new(
            itemId,
            remoteId,
            relativePath,
            kind,
            remoteRevision: null,
            localFileId: null,
            isTombstone: false,
            DateTimeOffset.UtcNow);

    private sealed class FailingCommitStoreFactory : ICloudStateStoreFactory
    {
        private readonly FailingCommitStore _store;

        internal FailingCommitStoreFactory(FailingCommitStore store)
        {
            _store = store;
        }

        public ValueTask<ICloudStateStore> OpenAsync(
            CloudStateStoreContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ICloudStateStore>(_store);
        }
    }

    private sealed class FailingCommitStore : ICloudStateStore
    {
        private readonly IReadOnlyDictionary<Guid, CloudItemState> _items;

        internal FailingCommitStore(IEnumerable<CloudItemState> items)
        {
            _items = items.ToDictionary(static item => item.ItemId);
        }

        public ValueTask<ICloudStateTransaction> BeginTransactionAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ICloudStateTransaction>(new Transaction(_items));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Transaction : ICloudStateTransaction, ICloudItemStateRepository
        {
            private readonly Dictionary<Guid, CloudItemState> _items;

            internal Transaction(IReadOnlyDictionary<Guid, CloudItemState> items)
            {
                _items = items.ToDictionary(static pair => pair.Key, static pair => pair.Value);
            }

            public ICloudItemStateRepository Items => this;

            public ICloudCheckpointRepository Checkpoints { get; } = new EmptyCheckpoints();

            public ICloudOperationJournal Operations => throw new NotSupportedException();

            public ICloudConflictRepository Conflicts => throw new NotSupportedException();

            public ICloudRemoteBatchRepository RemoteBatches => throw new NotSupportedException();

            public ICloudEchoSuppressionRepository EchoSuppressions => throw new NotSupportedException();

            public ValueTask<CloudItemState?> GetByItemIdAsync(
                Guid itemId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _items.TryGetValue(itemId, out CloudItemState? item);
                return ValueTask.FromResult(item);
            }

            public ValueTask<CloudItemState?> GetByRemoteIdAsync(
                string remoteId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CloudItemState? item = _items.Values.SingleOrDefault(candidate =>
                    string.Equals(candidate.RemoteId, remoteId, StringComparison.Ordinal));
                return ValueTask.FromResult(item);
            }

            public ValueTask<CloudItemState?> GetByRelativePathAsync(
                string relativePath,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                CloudItemState? item = _items.Values.SingleOrDefault(candidate =>
                    string.Equals(
                        candidate.RelativePath,
                        relativePath,
                        StringComparison.OrdinalIgnoreCase));
                return ValueTask.FromResult(item);
            }

            public ValueTask<IReadOnlyList<CloudItemState>> ListSubtreeAsync(
                string relativePath,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string primaryPrefix = relativePath + Path.DirectorySeparatorChar;
                string alternatePrefix = relativePath + Path.AltDirectorySeparatorChar;
                IReadOnlyList<CloudItemState> result = _items.Values
                    .Where(item =>
                        string.Equals(
                            item.RelativePath,
                            relativePath,
                            StringComparison.OrdinalIgnoreCase) ||
                        item.RelativePath.StartsWith(
                            primaryPrefix,
                            StringComparison.OrdinalIgnoreCase) ||
                        item.RelativePath.StartsWith(
                            alternatePrefix,
                            StringComparison.OrdinalIgnoreCase))
                    .OrderBy(static item => item.RelativePath.Length)
                    .ThenBy(static item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return ValueTask.FromResult(result);
            }

            public ValueTask UpsertAsync(
                CloudItemState item,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _items[item.ItemId] = item;
                return ValueTask.CompletedTask;
            }

            public ValueTask RemoveAsync(
                Guid itemId,
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _items.Remove(itemId);
                return ValueTask.CompletedTask;
            }

            public ValueTask CommitAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromException(new IOException("Injected commit failure."));
            }

            public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;

            private sealed class EmptyCheckpoints : ICloudCheckpointRepository
            {
                public ValueTask<CloudStateCheckpoint?> GetAsync(string name, CancellationToken cancellationToken = default)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.FromResult<CloudStateCheckpoint?>(null);
                }

                // This fault fixture has no captured provenance. Reads must report absence;
                // unexpected metadata writes still fail before the deliberate commit fault.
                public ValueTask<IReadOnlyList<CloudStateCheckpoint>> ListAsync(string namePrefix, CancellationToken cancellationToken = default) =>
                    throw new NotSupportedException();
                public ValueTask UpsertAsync(CloudStateCheckpoint checkpoint, CancellationToken cancellationToken = default) =>
                    throw new NotSupportedException();
                public ValueTask RemoveAsync(string name, CancellationToken cancellationToken = default) =>
                    throw new NotSupportedException();
            }
        }
    }
}
