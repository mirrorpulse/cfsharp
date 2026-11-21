using CfSharp.Storage.Sqlite;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Trait("Category", "AccessJournal")]
    [Fact]
    public async Task NativeWriteDuringRemoteMetadataOutcomeRemainsAnOriginalAfterReconciliation()
    {
        MetadataFaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path, "outcome-paused"));
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-concurrent-directory");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder("Documents", identity).WithInSyncState(true).Build(),
        ]);
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed();
        await feed.StartAsync();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        factory!.Armed = true;
        CloudRemoteChange change = new("metadata", CloudRemoteChangeKind.MetadataUpdate, identity.RemoteId,
            "remote-r1", CloudItemKind.Directory, "Documents", identity.ItemId,
            metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().Build());
        Task<CloudRemoteApplyResult> apply = fixture.System.ApplyRemoteChangesAsync(
            new("metadata-fault", Array.Empty<byte>(), [change], new byte[] { 1 }), cancellationToken: deadline.Token).AsTask();
        string path = Path.Combine(fixture.Root, "Documents");
        try
        {
            await factory.OutcomePaused.Task.WaitAsync(deadline.Token);
            Assert.False(apply.IsCompleted);
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        }
        finally { factory.ResumeOutcome.TrySetResult(); }
        Assert.Equal(CloudRemoteBatchStatus.Applied, (await apply).Status);
        CloudOperationJournalEntry[] originals;
        while (true)
        {
            await using (ICloudStateTransaction state = await factory.Store!.BeginTransactionAsync(deadline.Token))
            { originals = (await state.Operations.ListAsync(100, cancellationToken: deadline.Token)).ToArray(); }
            if (originals.Length >= 3) { break; }
            await Task.Delay(20, deadline.Token);
        }
        await feed.DisposeAsync();
        await fixture.RestartAsync();
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed();
        await reopened.StartAsync(deadline.Token);
        Assert.True((await reopened.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        await reopened.AcknowledgeFullRescanAsync(deadline.Token);
        CloudLocalChangePage page = await reopened.ReadPageAsync(await reopened.BeginScanAsync(deadline.Token), 0, 100, deadline.Token);
        Assert.Equal(originals.Select(row => (row.OperationId, row.Sequence)), page.Changes.Select(row => (row.OperationId, row.Sequence)));
        Assert.All(page.Changes, observation => Assert.Null(observation.ItemId));
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        await reopened.AcknowledgeAsync(originals.Select(row => row.OperationId), deadline.Token);
    }

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData("preparation-after")]
    [InlineData("outcome-before")]
    [InlineData("outcome-after")]
    [InlineData("staging-after")]
    public async Task FailedMetadataAttributionRetainsRealWritesAndOriginalIds(string boundary)
    {
        MetadataFaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path, boundary));
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-fault-directory");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder("Documents", identity).WithInSyncState(true).Build(),
        ]);
        string path = Path.Combine(fixture.Root, "Documents");
        CloudRemoteChange change = new("metadata", CloudRemoteChangeKind.MetadataUpdate, identity.RemoteId,
            "remote-r1", CloudItemKind.Directory, "Documents", identity.ItemId,
            metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().Build());
        CloudRemoteChangeBatch batch = new("metadata-fault", Array.Empty<byte>(), [change], new byte[] { 1 });
        CloudRemoteApplyOptions options = new() { AttributeEchoPolicy = CloudRemoteAttributeEchoPolicy.CallerCoordinated };
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed();
        await feed.StartAsync();
        factory!.Armed = true;
        if (boundary == "preparation-after")
        {
            CloudRemoteApplyResult failed = await fixture.System.ApplyRemoteChangesAsync(batch, options);
            Assert.True(failed.RequiresRetry);
            Assert.IsType<IOException>(Assert.Single(failed.Entries).Error);
        }
        else if (boundary == "staging-after")
        {
            Assert.Equal(CloudRemoteBatchStatus.Applied, (await fixture.System.ApplyRemoteChangesAsync(batch, options)).Status);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => fixture.System.ApplyRemoteChangesAsync(batch, options).AsTask());
        }

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        if (boundary == "staging-after")
        {
            Guid interrupted = await factory.Staged.Task.WaitAsync(deadline.Token);
            await feed.DisposeAsync();
            await using ICloudStateTransaction state = await factory.Store!.BeginTransactionAsync(deadline.Token);
            Assert.NotNull(await state.Checkpoints.GetAsync(CloudLocalChangeFeed.SecurityValidationPrefix + interrupted.ToString("N"), deadline.Token));
            Assert.Null(await state.Operations.GetAsync(interrupted, deadline.Token));
        }
        else
        {
            // A failed public call has no live coordination authority. The following native
            // write is permitted and must not redeem its completed or prepared echo receipt.
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            while (true)
            {
                int count;
                await using (ICloudStateTransaction state = await factory.Store!.BeginTransactionAsync(deadline.Token))
                { count = (await state.Operations.ListAsync(100, cancellationToken: deadline.Token)).Count; }
                if (count >= (boundary == "preparation-after" ? 1 : 3)) { break; }
                await Task.Delay(20, deadline.Token);
            }
            await feed.DisposeAsync();
        }

        IReadOnlyList<CloudOperationJournalEntry> originals;
        await using (ICloudStateTransaction state = await factory.Store!.BeginTransactionAsync(deadline.Token))
        {
            originals = await state.Operations.ListAsync(100, cancellationToken: deadline.Token);
            Assert.All(originals, row => Assert.Null(row.ItemId));
        }
        await fixture.RestartAsync();
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed();
        await reopened.StartAsync(deadline.Token);
        Assert.True((await reopened.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        IReadOnlyList<CloudOperationJournalEntry> recovered;
        await using (ICloudStateTransaction state = await factory.Store!.BeginTransactionAsync(deadline.Token))
        {
            recovered = await state.Operations.ListAsync(100, cancellationToken: deadline.Token);
            AssertOriginalAccessRows(originals, recovered);
            if (boundary == "staging-after")
            {
                Guid interrupted = await factory.Staged.Task;
                Assert.Contains(recovered, row => row.OperationId == interrupted && row.ItemId is null);
            }
        }
        CloudRemoteApplyResult replay = await fixture.System.ApplyRemoteChangesAsync(batch, options, deadline.Token);
        Assert.Equal(boundary is "outcome-after" or "staging-after" ? CloudRemoteApplyEntryStatus.AlreadyApplied :
            CloudRemoteApplyEntryStatus.Conflict, Assert.Single(replay.Entries).Status);
        Assert.False(replay.RequiresRetry);
        await reopened.AcknowledgeFullRescanAsync(deadline.Token);
        CloudLocalChangeScan scan = await reopened.BeginScanAsync(deadline.Token);
        CloudLocalChangePage page = await reopened.ReadPageAsync(scan, 0, 100, deadline.Token);
        Assert.False(page.RequiresFullRescan);
        Assert.Equal(recovered.Select(row => (row.OperationId, row.Sequence)), page.Changes.Select(row => (row.OperationId, row.Sequence)));
        Assert.All(page.Changes, row => Assert.Null(row.ItemId));
        await reopened.AcknowledgeAsync(recovered.Select(row => row.OperationId), deadline.Token);
        if (boundary != "staging-after") { Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden)); }
        Assert.Equal(0, fixture.Provider.Fetches);
    }

    private sealed class MetadataFaultFactory(string path, string boundary) : ICloudStateStoreFactory
    {
        private readonly string _boundary = boundary;
        private int _failed;
        internal bool Armed { get; set; }
        internal ICloudStateStore? Store { get; private set; }
        internal TaskCompletionSource<Guid> Staged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource OutcomePaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ResumeOutcome { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context, CancellationToken cancellationToken = default)
        {
            Store = await new SqliteCloudStateStoreFactory(path).OpenAsync(context, cancellationToken);
            return new FaultStore(Store, this);
        }
        private sealed class FaultStore(ICloudStateStore inner, MetadataFaultFactory owner) : ICloudStateStore
        {
            public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
                new FaultTransaction(await inner.BeginTransactionAsync(cancellationToken), owner);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
        private sealed class FaultTransaction(ICloudStateTransaction inner, MetadataFaultFactory owner) : ICloudStateTransaction
        {
            public ICloudItemStateRepository Items => inner.Items;
            public ICloudCheckpointRepository Checkpoints => inner.Checkpoints;
            public ICloudOperationJournal Operations => inner.Operations;
            public ICloudConflictRepository Conflicts => inner.Conflicts;
            public ICloudRemoteBatchRepository RemoteBatches => inner.RemoteBatches;
            public ICloudEchoSuppressionRepository EchoSuppressions => inner.EchoSuppressions;
            public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
            {
                CloudStateCheckpoint? staged = null;
                bool cut = false;
                if (owner.Armed && Volatile.Read(ref owner._failed) == 0)
                {
                    RemoteMetadataIntent[] intents = (await Checkpoints.ListAsync(RemoteMetadataIntent.Prefix, cancellationToken))
                        .Select(RemoteMetadataIntent.Decode).ToArray();
                    IReadOnlyList<CloudStateCheckpoint> receipts = await Checkpoints.ListAsync(CloudLocalChangeFeed.SecurityValidationPrefix, cancellationToken);
                    staged = receipts.Count == 0 ? null : receipts[0];
                    cut = owner._boundary switch
                    {
                        "preparation-after" => intents.Any(intent => !intent.Completed),
                        "staging-after" => staged is not null,
                        _ => intents.Any(intent => intent.Completed) &&
                            (await RemoteBatches.GetAsync("metadata-fault", cancellationToken))?.Status == CloudRemoteBatchStatus.Applied,
                    };
                }
                if (!cut || Interlocked.Exchange(ref owner._failed, 1) != 0)
                { await inner.CommitAsync(cancellationToken); return; }
                if (owner._boundary == "outcome-paused")
                {
                    owner.OutcomePaused.TrySetResult();
                    await owner.ResumeOutcome.Task.WaitAsync(cancellationToken);
                    await inner.CommitAsync(cancellationToken);
                    return;
                }
                if (owner._boundary != "outcome-before") { await inner.CommitAsync(cancellationToken); }
                if (staged is not null)
                { owner.Staged.TrySetResult(Guid.ParseExact(staged.Name[CloudLocalChangeFeed.SecurityValidationPrefix.Length..], "N")); }
                throw new IOException("Injected metadata attribution boundary: " + owner._boundary);
            }
            public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
