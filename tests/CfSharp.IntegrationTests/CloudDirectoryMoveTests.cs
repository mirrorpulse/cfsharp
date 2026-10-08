using System.Runtime.Versioning;

using CfSharp.Storage.Sqlite;
using CfSharp.Tests.Persistence;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class CloudDirectoryMoveTests
{
    [Fact]
    public async Task PrepareCapturesLiveObjectAndIndependentDurableProofsWithoutMoving()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return;
        }

        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudDirectory source = root.FileSystem.GetDirectory("Docs");
        CloudItemSnapshot before = await source.InspectAsync();
        Assert.True(before.PlaceholderState.HasFlag(CloudPlaceholderState.Placeholder), $"Source state: {before.PlaceholderState}");
        CloudDirectoryMoveProof first = await source.PrepareMoveAsync(root.FileSystem.Root, "Moved");
        CloudDirectoryMoveProof second = await source.PrepareMoveAsync(root.FileSystem.Root, "Other");
        Assert.Equal(before.LocalBinding, first.ExpectedBinding);
        Assert.Equal(before.ItemId, first.RootItemId);
        Assert.Equal(before.PlaceholderIdentity.ToArray(), first.ExpectedPlaceholderIdentity.ToArray());
        Assert.NotEqual(first.ProofId, second.ProofId);
        Assert.Equal(first.StoreScope, second.StoreScope);
        Assert.True(Directory.Exists(source.FullPath));
        Assert.False(Directory.Exists(Path.Combine(root.RootPath, "Moved")));
        await using ICloudStateTransaction transaction = await root.Store.BeginTransactionAsync();
        Assert.Equal(first.Encode(), (await transaction.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ProofName(first.ProofId)))!.Value.ToArray());
        Assert.Equal(second.Encode(), (await transaction.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ProofName(second.ProofId)))!.Value.ToArray());
        Assert.Equal(2, (await transaction.Items.ListSubtreeAsync("Docs")).Count);
    }

    [Fact]
    public async Task PrepareRejectsOrdinaryRootForeignDestinationCancellationAndMismatchedState()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return;
        }

        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        await using DirectoryMoveTestRoot foreign = await DirectoryMoveTestRoot.OpenAsync();
        CloudDirectory source = root.FileSystem.GetDirectory("Docs");
        await Assert.ThrowsAsync<ArgumentException>(() => source.PrepareMoveAsync(foreign.FileSystem.Root, "Moved").AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => root.FileSystem.Root.PrepareMoveAsync(root.FileSystem.Root, "Moved").AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => root.FileSystem.GetDirectory("Ordinary").PrepareMoveAsync(root.FileSystem.Root, "Moved").AsTask());
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.PrepareMoveAsync(root.FileSystem.Root, "Moved", cancellation.Token).AsTask());
        await using (ICloudStateTransaction transaction = await root.Store.BeginTransactionAsync())
        {
            await transaction.Items.RemoveAsync(root.DirectoryIdentity.ItemId);
            await transaction.CommitAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.PrepareMoveAsync(root.FileSystem.Root, "Moved").AsTask());
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        Assert.Empty(await verify.Checkpoints.ListAsync("cfsharp/namespace/preparations"));
        Assert.True(Directory.Exists(source.FullPath));
    }

    [Fact]
    public async Task PrepareCommitFailureReturnsNoProofAndLeavesSourceAndPreparationsUnchanged()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return;
        }

        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        root.Faults.BeforeCommit = () => throw new IOException("Preparation commit fault.");
        await Assert.ThrowsAsync<IOException>(() => root.FileSystem.GetDirectory("Docs")
            .PrepareMoveAsync(root.FileSystem.Root, "Moved").AsTask());
        root.Faults.BeforeCommit = null;
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        Assert.Empty(await verify.Checkpoints.ListAsync("cfsharp/namespace/preparations"));
        Assert.True(Directory.Exists(Path.Combine(root.RootPath, "Docs")));
        Assert.False(Directory.Exists(Path.Combine(root.RootPath, "Moved")));
    }
}

[SupportedOSPlatform("windows10.0.16299")]
internal sealed class DirectoryMoveTestRoot(CloudFileSystem fileSystem, DirectoryMoveTestRoot.CapturingFactory factory,
    string area, CloudPlaceholderIdentity directoryIdentity) : IAsyncDisposable
{
    internal CloudFileSystem FileSystem => fileSystem;
    internal string RootPath => fileSystem.SyncRootPath;
    internal string Area => area;
    internal string DatabasePath => Path.Combine(area, "state.db");
    internal ICloudStateStore Store => factory.Store!;
    internal DirectoryMoveFaultStore Faults => factory.Store!;
    internal CloudPlaceholderIdentity DirectoryIdentity => directoryIdentity;

    internal async Task RestartAsync()
    {
        string root = RootPath;
        await fileSystem.DisposeAsync();
        fileSystem = CloudFileSystem.CreateBuilder(root).WithStateStore(factory).WithContentProvider(new EmptyContentProvider()).Build();
        await fileSystem.StartAsync();
    }

    internal static async Task<DirectoryMoveTestRoot> OpenAsync(ICloudFileContentProvider? provider = null, bool legacyState = false,
        CloudPopulationPolicy populationPolicy = CloudPopulationPolicy.Partial)
    {
        string area = Path.Combine(Path.GetTempPath(), "CfSharp-directory-move-tests", Guid.NewGuid().ToString("N"));
        string rootPath = Path.Combine(area, "root");
        Directory.CreateDirectory(Path.Combine(rootPath, "Ordinary"));
        if (legacyState)
        {
            PreviewThreeStateFixture.Extract(Path.Combine(area, "state.db"), rootPath);
        }
        Guid providerId = Guid.NewGuid();
        CapturingFactory factory = new(new SqliteCloudStateStoreFactory(Path.Combine(area, "state.db")));
        CloudFileSystem fileSystem = CloudFileSystem.CreateBuilder(rootPath).WithStateStore(factory)
            .WithRegistration(SyncRootRegistrationOptions.CreateBuilder("CfSharp Directory Move", "1.0-test")
                .WithProviderId(providerId).WithSyncRootIdentity(providerId.ToByteArray())
                .WithHydrationPolicy(CloudHydrationPolicy.Progressive).WithPopulationPolicy(populationPolicy)
                .WithRootMarkedInSync().Build()).WithContentProvider(provider ?? new EmptyContentProvider()).Build();
        bool registered = false;
        try
        {
            await fileSystem.StartAsync();
            registered = true;
            CloudDirectoryPlaceholderSpec directory = CloudDirectoryPlaceholderSpec.CreateBuilder("Docs", "directory")
                .WithPopulationState(CloudDirectoryPopulationState.Complete).Build();
            await fileSystem.Root.CreatePlaceholderAsync(directory);
            await fileSystem.GetDirectory("Docs").CreatePlaceholderAsync(CloudFilePlaceholderSpec.CreateBuilder("child.txt", "child", 0).Build());
            return new DirectoryMoveTestRoot(fileSystem, factory, area, directory.Identity);
        }
        catch
        {
            await fileSystem.DisposeAsync();
            if (registered)
            {
                CloudSyncRoot.Open(rootPath).Unregister();
            }

            Directory.Delete(area, recursive: true);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await fileSystem.DisposeAsync();
        CloudSyncRoot.Open(RootPath).Unregister();
        Directory.Delete(area, recursive: true);
    }

    internal sealed class CapturingFactory(ICloudStateStoreFactory inner) : ICloudStateStoreFactory
    {
        internal DirectoryMoveFaultStore? Store { get; private set; }
        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context, CancellationToken cancellationToken = default) =>
            Store = new DirectoryMoveFaultStore(await inner.OpenAsync(context, cancellationToken));
    }

    private sealed class EmptyContentProvider : ICloudFileContentProvider
    {
        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream>(new MemoryStream());
    }
}

internal sealed class DirectoryMoveFaultStore(ICloudStateStore inner) : ICloudStateStore
{
    internal Action? BeforeCommit { get; set; }
    internal Action? AfterCommit { get; set; }
    internal Action<string>? SubtreeRead { get; set; }
    internal Action<string>? CheckpointWritten { get; set; }
    public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
        new Transaction(await inner.BeginTransactionAsync(cancellationToken), this);
    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private sealed class Transaction(ICloudStateTransaction transaction, DirectoryMoveFaultStore owner) : ICloudStateTransaction
    {
        public ICloudItemStateRepository Items => owner.SubtreeRead is null ? transaction.Items : new ObservedItems(transaction.Items, owner);
        public ICloudCheckpointRepository Checkpoints => owner.CheckpointWritten is null ? transaction.Checkpoints : new ObservedCheckpoints(transaction.Checkpoints, owner);
        public ICloudOperationJournal Operations => transaction.Operations;
        public ICloudConflictRepository Conflicts => transaction.Conflicts;
        public ICloudRemoteBatchRepository RemoteBatches => transaction.RemoteBatches;
        public ICloudEchoSuppressionRepository EchoSuppressions => transaction.EchoSuppressions;
        public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
        {
            owner.BeforeCommit?.Invoke();
            await transaction.CommitAsync(cancellationToken);
            owner.AfterCommit?.Invoke();
        }

        public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }

    private sealed class ObservedItems(ICloudItemStateRepository items, DirectoryMoveFaultStore owner) : ICloudItemStateRepository
    {
        public ValueTask<CloudItemState?> GetByItemIdAsync(Guid itemId, CancellationToken cancellationToken = default) =>
            items.GetByItemIdAsync(itemId, cancellationToken);
        public ValueTask<CloudItemState?> GetByRemoteIdAsync(string remoteId, CancellationToken cancellationToken = default) =>
            items.GetByRemoteIdAsync(remoteId, cancellationToken);
        public ValueTask<CloudItemState?> GetByRelativePathAsync(string relativePath, CancellationToken cancellationToken = default) =>
            items.GetByRelativePathAsync(relativePath, cancellationToken);
        public ValueTask<IReadOnlyList<CloudItemState>> ListSubtreeAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            owner.SubtreeRead?.Invoke(relativePath);
            return items.ListSubtreeAsync(relativePath, cancellationToken);
        }

        public ValueTask UpsertAsync(CloudItemState item, CancellationToken cancellationToken = default) => items.UpsertAsync(item, cancellationToken);
        public ValueTask RemoveAsync(Guid itemId, CancellationToken cancellationToken = default) => items.RemoveAsync(itemId, cancellationToken);
    }

    private sealed class ObservedCheckpoints(ICloudCheckpointRepository checkpoints, DirectoryMoveFaultStore owner) : ICloudCheckpointRepository
    {
        public ValueTask<CloudStateCheckpoint?> GetAsync(string name, CancellationToken cancellationToken = default) =>
            checkpoints.GetAsync(name, cancellationToken);
        public ValueTask<IReadOnlyList<CloudStateCheckpoint>> ListAsync(string namePrefix, CancellationToken cancellationToken = default) =>
            checkpoints.ListAsync(namePrefix, cancellationToken);
        public ValueTask UpsertAsync(CloudStateCheckpoint checkpoint, CancellationToken cancellationToken = default)
        {
            owner.CheckpointWritten?.Invoke(checkpoint.Name);
            return checkpoints.UpsertAsync(checkpoint, cancellationToken);
        }

        public ValueTask RemoveAsync(string name, CancellationToken cancellationToken = default) => checkpoints.RemoveAsync(name, cancellationToken);
    }
}
