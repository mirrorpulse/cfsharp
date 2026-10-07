namespace CfSharp.Storage.Sqlite.Tests;

public sealed partial class CloudLocalChangeFeedTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LossAfterCaptureBlocksBacklogAndInvalidatesScanAfterAcknowledgement(bool watcherError)
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        CloudOperationJournalEntry[] rows = await SeedChangesAsync(store, 9);
        FakeSource source = new();
        await using CloudLocalChangeFeed feed = CreateFeed(store, source);
        await feed.StartAsync();
        CloudLocalChangeScan scan = await feed.BeginScanAsync();
        CloudLocalChangePage first = await feed.ReadPageAsync(scan, 0, 4);
        await source.EmitAsync(new(watcherError ? LocalChangeSourceAction.Error : LocalChangeSourceAction.Overflow,
            string.Empty, watcherError ? 5 : 0));
        CloudLocalChangePage blocked = await feed.ReadPageAsync(scan, first.LastScannedSequence, 4);
        Assert.True(blocked.RequiresFullRescan);
        Assert.Empty(blocked.Changes);
        Assert.True(blocked.HasMore);
        Assert.Equal(first.LastScannedSequence, blocked.LastScannedSequence);
        CloudLocalChangeBatch legacy = await feed.ReadBatchAsync();
        Assert.True(legacy.RequiresFullRescan);
        Assert.Empty(legacy.Changes);

        await AcknowledgePersistedRescanAsync(feed);
        Assert.True((await feed.ReadPageAsync(scan, first.LastScannedSequence, 4)).RequiresFullRescan);
        Assert.False((await feed.BeginScanAsync()).RequiresFullRescan);
        await using ICloudStateTransaction verify = await store.BeginTransactionAsync();
        Assert.Equal(rows.Select(row => row.OperationId),
            (await verify.Operations.ListAsync(20)).Select(row => row.OperationId));
        Assert.False(LocalChangeCheckpoint.Decode((await verify.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName))!.Value).RequiresFullRescan);
    }

    [Fact]
    public async Task LossAtPageTransactionReleaseWithholdsAlreadyReadChanges()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        await SeedChangesAsync(store, 2);
        RaceStore raceStore = new(store);
        FakeSource source = new();
        await using CloudLocalChangeFeed feed = CreateFeed(raceStore, source);
        await feed.StartAsync();
        CloudLocalChangeScan scan = await feed.BeginScanAsync();
        raceStore.AfterRollback = () => source.EmitAsync(new(LocalChangeSourceAction.Overflow, string.Empty));
        CloudLocalChangePage page = await feed.ReadPageAsync(scan, 0, 1);
        Assert.True(page.RequiresFullRescan);
        Assert.Empty(page.Changes);
        Assert.Equal(0, page.LastScannedSequence);
        Assert.True(page.HasMore);
    }

    [Fact]
    public async Task LossDuringRescanCommitCannotBeAcknowledgedByThatCommit()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        await SeedChangesAsync(store, 2);
        RaceStore raceStore = new(store);
        FakeSource source = new();
        await using CloudLocalChangeFeed feed = CreateFeed(raceStore, source);
        await feed.StartAsync();
        CloudLocalChangeScan original = await feed.BeginScanAsync();
        raceStore.BeforeCommit = () => source.EmitAsync(new(LocalChangeSourceAction.Error, string.Empty, 5));
        await feed.AcknowledgeFullRescanAsync();
        Assert.True((await feed.ReadPageAsync(original, 0, 1)).RequiresFullRescan);
        Assert.True((await feed.BeginScanAsync()).RequiresFullRescan);
        Assert.True((await feed.ReadBatchAsync()).RequiresFullRescan);
        await AcknowledgePersistedRescanAsync(feed);
        Assert.False((await feed.BeginScanAsync()).RequiresFullRescan);
    }

    [Fact]
    public async Task CreationIntentAndRetainedObservationFenceScansUntilExplicitRecovery()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        await SeedChangesAsync(store, 2);
        await using CloudLocalChangeFeed feed = CreateFeed(store, new FakeSource());
        await feed.StartAsync();
        CloudLocalChangeScan original = await feed.BeginScanAsync();
        string intentName = RemoteCreationIntent.Prefix + "/test";
        string observationName = RemoteCreationIntent.ObservationsPrefix + "/test";
        await using (ICloudStateTransaction write = await store.BeginTransactionAsync())
        {
            RemoteCreationIntent intent = new("pending", CloudItemKind.Directory,
                new CloudPlaceholderIdentity(Guid.NewGuid(), "pending-remote"), new byte[32], false);
            await write.Checkpoints.UpsertAsync(new CloudStateCheckpoint(intentName, intent.Encode(), DateTimeOffset.UtcNow));
            await write.Checkpoints.UpsertAsync(new CloudStateCheckpoint(observationName, [1], DateTimeOffset.UtcNow));
            await write.CommitAsync();
        }

        Assert.True((await feed.BeginScanAsync()).RequiresFullRescan);
        Assert.True((await feed.ReadPageAsync(original, 0, 1)).RequiresFullRescan);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await feed.AcknowledgeFullRescanAsync());
        await using (ICloudStateTransaction recover = await store.BeginTransactionAsync())
        {
            await recover.Checkpoints.RemoveAsync(intentName);
            await recover.CommitAsync();
        }

        // Removing a completed creation intent does not implicitly accept its uncertain observations.
        Assert.True((await feed.ReadPageAsync(original, 0, 1)).RequiresFullRescan);
        await feed.AcknowledgeFullRescanAsync();
        Assert.True((await feed.ReadPageAsync(original, 0, 1)).RequiresFullRescan);
        Assert.False((await feed.BeginScanAsync()).RequiresFullRescan);
        await using ICloudStateTransaction verify = await store.BeginTransactionAsync();
        Assert.Null(await verify.Checkpoints.GetAsync(observationName));
        Assert.Equal(2, (await verify.Operations.ListAsync(10)).Count);
    }

    [Fact]
    public async Task DurableRescanFenceSurvivesFullStoreAndFeedRestart()
    {
        await using (ICloudStateStore store = await OpenStoreAsync())
        await using (CloudLocalChangeFeed feed = CreateFeed(store, new FakeSource()))
        {
            await SeedChangesAsync(store, 2);
            await feed.StartAsync();
            await using ICloudStateTransaction write = await store.BeginTransactionAsync();
            await write.Checkpoints.UpsertAsync(new CloudStateCheckpoint(CloudLocalChangeFeed.CheckpointName,
                new LocalChangeCheckpoint(42, true).Encode(), DateTimeOffset.UtcNow));
            await write.CommitAsync();
        }

        await using ICloudStateStore reopened = await OpenStoreAsync();
        await using CloudLocalChangeFeed restarted = CreateFeed(reopened, new FakeSource());
        await restarted.StartAsync();
        CloudLocalChangeScan scan = await restarted.BeginScanAsync();
        Assert.True(scan.RequiresFullRescan);
        CloudLocalChangePage page = await restarted.ReadPageAsync(scan, 0, 1);
        Assert.Empty(page.Changes);
        Assert.True(page.HasMore);
        Assert.True(page.RequiresFullRescan);
    }

    private static async Task AcknowledgePersistedRescanAsync(CloudLocalChangeFeed feed)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (true)
        {
            try
            {
                await feed.AcknowledgeFullRescanAsync(timeout.Token);
                return;
            }
            catch (InvalidOperationException exception) when (exception.Message.StartsWith("A new loss signal", StringComparison.Ordinal))
            {
                await Task.Delay(10, timeout.Token);
            }
        }
    }

    private sealed class RaceStore(ICloudStateStore inner) : ICloudStateStore
    {
        internal Func<ValueTask>? AfterRollback { get; set; }
        internal Func<ValueTask>? BeforeCommit { get; set; }
        public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new RaceTransaction(await inner.BeginTransactionAsync(cancellationToken), this);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RaceTransaction(ICloudStateTransaction inner, RaceStore store) : ICloudStateTransaction
    {
        public ICloudItemStateRepository Items => inner.Items;
        public ICloudCheckpointRepository Checkpoints => inner.Checkpoints;
        public ICloudOperationJournal Operations => inner.Operations;
        public ICloudConflictRepository Conflicts => inner.Conflicts;
        public ICloudRemoteBatchRepository RemoteBatches => inner.RemoteBatches;
        public ICloudEchoSuppressionRepository EchoSuppressions => inner.EchoSuppressions;
        public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
        {
            Func<ValueTask>? hook = store.BeforeCommit;
            store.BeforeCommit = null;
            if (hook is not null)
            {
                await hook();
            }

            await inner.CommitAsync(cancellationToken);
        }

        public async ValueTask RollbackAsync(CancellationToken cancellationToken = default)
        {
            await inner.RollbackAsync(cancellationToken);
            Func<ValueTask>? hook = store.AfterRollback;
            store.AfterRollback = null;
            if (hook is not null)
            {
                await hook();
            }
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
