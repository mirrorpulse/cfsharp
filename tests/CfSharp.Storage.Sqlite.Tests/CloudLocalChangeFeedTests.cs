namespace CfSharp.Storage.Sqlite.Tests;

public sealed partial class CloudLocalChangeFeedTests : IAsyncLifetime
{
    private string _temporaryDirectory = null!;
    private string _syncRootPath = null!;
    private string _databasePath = null!;

    public Task InitializeAsync()
    {
        _temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "CfSharp-local-feed-tests",
            Guid.NewGuid().ToString("N"));
        _syncRootPath = Path.Combine(_temporaryDirectory, "sync-root");
        _databasePath = Path.Combine(_temporaryDirectory, "state", "state.db");
        Directory.CreateDirectory(_syncRootPath);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public void CorruptJournalStringLengthIsRejectedWithoutLargeAllocation()
    {
        byte[] payload = [0xff, 0xff, 0xff, 0xff, 0x7f];

        Assert.Throws<InvalidOperationException>(() => LocalChangePayload.Decode(payload));
    }

    [Fact]
    public async Task CreateMoveAndDeleteChangesAreDurableAndAcknowledged()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        FakeSource source = new();
        await using CloudLocalChangeFeed feed = CreateFeed(store, source);
        await feed.StartAsync();

        await source.EmitAsync(new(LocalChangeSourceAction.Created, "folder\\file.txt"));
        CloudLocalChangeBatch created = await feed.ReadBatchAsync();
        CloudLocalChange createdChange = Assert.Single(created.Changes);
        Assert.Equal(CloudLocalChangeKind.Create, createdChange.Kind);
        Assert.Equal("folder\\file.txt", createdChange.RelativePath);
        await feed.AcknowledgeAsync(
            [new CloudLocalChangeAcknowledgement(createdChange.OperationId, "revision-1")]);
        await using (ICloudStateTransaction acknowledgedState = await store.BeginTransactionAsync())
        {
            CloudItemState? item = await acknowledgedState.Items
                .GetByRelativePathAsync("folder\\file.txt");
            Assert.Equal("revision-1", item?.RemoteRevision);
            await acknowledgedState.RollbackAsync();
        }

        await source.EmitAsync(new(LocalChangeSourceAction.RenamedOldName, "folder\\file.txt"));
        await source.EmitAsync(new(LocalChangeSourceAction.RenamedNewName, "folder\\renamed.txt"));
        CloudLocalChange move = Assert.Single((await feed.ReadBatchAsync()).Changes);
        Assert.Equal(CloudLocalChangeKind.Move, move.Kind);
        Assert.Equal("folder\\file.txt", move.PreviousRelativePath);
        Assert.Equal("folder\\renamed.txt", move.RelativePath);
        await feed.AcknowledgeAsync([move.OperationId]);

        await source.EmitAsync(new(LocalChangeSourceAction.Deleted, "folder\\renamed.txt"));
        CloudLocalChange deleted = Assert.Single((await feed.ReadBatchAsync()).Changes);
        Assert.Equal(CloudLocalChangeKind.Delete, deleted.Kind);
        await feed.AcknowledgeAsync([deleted.OperationId]);

        await using ICloudStateTransaction transaction = await store.BeginTransactionAsync();
        Assert.Empty(await transaction.Operations.ListAsync(10));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task OverflowRequiresFullRescanAndCanBeCleared()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        FakeSource source = new();
        await using CloudLocalChangeFeed feed = CreateFeed(store, source);
        await feed.StartAsync();

        await source.EmitAsync(new(LocalChangeSourceAction.Overflow, string.Empty));
        CloudLocalChangeBatch rescan = await feed.ReadBatchAsync();
        Assert.True(rescan.RequiresFullRescan);
        Assert.Empty(rescan.Changes);
        await AcknowledgePersistedRescanAsync(feed);

        await source.EmitAsync(new(LocalChangeSourceAction.Created, "after-rescan.txt"));
        CloudLocalChangeBatch afterRescan = await feed.ReadBatchAsync();
        Assert.False(afterRescan.RequiresFullRescan);
        Assert.Single(afterRescan.Changes);
    }

    [Fact]
    public async Task ProviderEchoIsSuppressedAndFeedCanReplayAfterRestart()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        FakeSource source = new();
        await using (CloudLocalChangeFeed feed = CreateFeed(store, source))
        {
            await feed.StartAsync();
            await feed.SuppressProviderEchoAsync(
                CloudStateOperationKind.ContentUpdate,
                "provider.txt",
                DateTimeOffset.UtcNow.AddMinutes(1));
            await source.EmitAsync(new(LocalChangeSourceAction.Modified, "provider.txt"));
            await Task.Delay(50);
            await using ICloudStateTransaction transaction = await store.BeginTransactionAsync();
            Assert.Empty(await transaction.Operations.ListAsync(10));
            await transaction.RollbackAsync();

            await source.EmitAsync(new(LocalChangeSourceAction.Created, "replay.txt"));
        }

        FakeSource restartedSource = new();
        await using CloudLocalChangeFeed restarted = CreateFeed(store, restartedSource);
        await restarted.StartAsync();
        CloudLocalChangeBatch replay = await restarted.ReadBatchAsync();
        Assert.Equal("replay.txt", Assert.Single(replay.Changes).RelativePath);
    }

    [Fact]
    public async Task ProviderEchoSuppressionConsumesExpectedObservationsAndMatchesKind()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        FakeSource source = new();
        await using CloudLocalChangeFeed feed = CreateFeed(store, source);
        await feed.StartAsync();

        await feed.SuppressProviderEchoAsync(
            CloudStateOperationKind.ContentUpdate,
            "replayed.txt",
            DateTimeOffset.UtcNow.AddMinutes(1),
            itemId: null,
            previousRelativePath: null,
            expectedObservationCount: 2);
        await source.EmitAsync(new(LocalChangeSourceAction.Modified, "replayed.txt"));
        await source.EmitAsync(new(LocalChangeSourceAction.Modified, "replayed.txt"));
        // Suppressed observations produce no journal row to await. The public drain boundary
        // establishes completion without racing a polling deadline against worker scheduling.
        await feed.DisposeAsync();

        await using (ICloudStateTransaction transaction = await store.BeginTransactionAsync())
        {
            Assert.Empty(await transaction.Operations.ListAsync(10));
            Assert.Empty(await transaction.EchoSuppressions.ListActiveAsync(DateTimeOffset.UtcNow));
            LocalChangeCheckpoint checkpoint = LocalChangeCheckpoint.Decode(
                (await transaction.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName))!.Value);
            Assert.Equal(2, checkpoint.Observation);
            Assert.False(checkpoint.RequiresFullRescan);
            await transaction.RollbackAsync();
        }

        FakeSource restartedSource = new();
        await using CloudLocalChangeFeed restarted = CreateFeed(store, restartedSource);
        await restarted.StartAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await restartedSource.EmitAsync(new(LocalChangeSourceAction.Modified, "replayed.txt"));
        CloudLocalChangeBatch afterBudget = await restarted.ReadBatchAsync(timeout.Token);
        Assert.Equal(CloudLocalChangeKind.ContentUpdate, Assert.Single(afterBudget.Changes).Kind);
        Assert.False(afterBudget.RequiresFullRescan);

        await restarted.AcknowledgeAsync(afterBudget.Changes.Select(change => change.OperationId), timeout.Token);
        await restarted.SuppressProviderEchoAsync(
            CloudStateOperationKind.ContentUpdate,
            "kind-sensitive.txt",
            DateTimeOffset.UtcNow.AddMinutes(1));
        await restartedSource.EmitAsync(new(LocalChangeSourceAction.Created, "kind-sensitive.txt"));
        Assert.Equal(CloudLocalChangeKind.Create, Assert.Single((await restarted.ReadBatchAsync(timeout.Token)).Changes).Kind);
        await using ICloudStateTransaction kindMismatch = await store.BeginTransactionAsync();
        CloudOperationJournalEntry kindSensitiveOperation = Assert.Single(
            await kindMismatch.Operations.ListAsync(10));
        Assert.Equal(CloudStateOperationKind.Create, kindSensitiveOperation.Kind);
        Assert.Equal(1, Assert.Single(await kindMismatch.EchoSuppressions.ListActiveAsync(DateTimeOffset.UtcNow)).RemainingObservations);
        await kindMismatch.RollbackAsync();
    }

    private static async Task WaitForJournalStateAsync(
        ICloudStateStore store,
        int expectedOperations,
        int expectedSuppressions)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using ICloudStateTransaction transaction = await store.BeginTransactionAsync();
            int operationCount = (await transaction.Operations.ListAsync(10)).Count;
            int suppressionCount = (await transaction.EchoSuppressions
                .ListActiveAsync(DateTimeOffset.UtcNow)).Count;
            await transaction.RollbackAsync();
            if (operationCount == expectedOperations && suppressionCount == expectedSuppressions)
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail(
            $"Timed out waiting for journal state operations={expectedOperations}, suppressions={expectedSuppressions}.");
    }

    [Fact]
    public async Task InvalidPathAndChannelOverflowRequireAFullRescan()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        FakeSource source = new();
        await using CloudLocalChangeFeed feed = CloudLocalChangeFeed.CreateForTesting(
            _syncRootPath,
            store,
            new CloudLocalChangeFeedOptions { BufferCapacity = 64, BatchSize = 16 },
            source);
        await feed.StartAsync();

        await source.EmitAsync(new(LocalChangeSourceAction.Created, "..\\outside.txt"));
        CloudLocalChangeBatch invalidPath = await feed.ReadBatchAsync();
        Assert.True(invalidPath.RequiresFullRescan);
        await AcknowledgePersistedRescanAsync(feed);

        await source.EmitAsync(new(LocalChangeSourceAction.Overflow, string.Empty));
        CloudLocalChangeBatch overflow = await feed.ReadBatchAsync();
        Assert.True(overflow.RequiresFullRescan);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AcknowledgingDeliveredSnapshotPreservesLaterObservations(
        bool initiallyCreated)
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        FakeSource source = new();
        Guid laterOperation;
        await using (CloudLocalChangeFeed feed = CreateFeed(store, source))
        {
            await feed.StartAsync();
            await source.EmitAsync(new(initiallyCreated ? LocalChangeSourceAction.Created : LocalChangeSourceAction.Modified, "same.txt"));
            CloudLocalChange delivered = Assert.Single((await feed.ReadBatchAsync()).Changes);

            await source.EmitAsync(new(LocalChangeSourceAction.Modified, "same.txt"));
            await WaitForJournalStateAsync(store, expectedOperations: 2, expectedSuppressions: 0);
            await feed.AcknowledgeAsync([delivered.OperationId]);
            CloudLocalChange later = Assert.Single((await feed.ReadBatchAsync()).Changes);
            Assert.NotEqual(delivered.OperationId, later.OperationId);
            Assert.Equal(CloudLocalChangeKind.ContentUpdate, later.Kind);
            laterOperation = later.OperationId;
            await feed.AcknowledgeAsync([delivered.OperationId]);
        }

        await using CloudLocalChangeFeed restarted = CreateFeed(store, new FakeSource());
        await restarted.StartAsync();
        Assert.Equal(laterOperation, Assert.Single((await restarted.ReadBatchAsync()).Changes).OperationId);
        await restarted.AcknowledgeAsync([laterOperation]);
        await WaitForJournalStateAsync(store, expectedOperations: 0, expectedSuppressions: 0);
    }

    [Fact]
    public async Task BoundedJournalReplaySurvivesFeedRestart()
    {
        const int seed = 20260926;
        const int eventCount = 48;
        const int eventsBeforeRestart = 24;
        string[] paths = Enumerable
            .Range(0, eventCount)
            .Select(index =>
                $"recovery-{seed}-{index:D3}-{new Random(seed + index).NextInt64():X16}.txt")
            .ToArray();
        CloudLocalChangeFeedOptions options = new()
        {
            BufferCapacity = 64,
            BatchSize = 7,
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        };

        await using ICloudStateStore store = await OpenStoreAsync();
        int acknowledged = 0;
        CloudLocalChange[] retainedBeforeRestart;
        FakeSource source = new();
        await using (CloudLocalChangeFeed feed = CloudLocalChangeFeed.CreateForTesting(
            _syncRootPath,
            store,
            options,
            source))
        {
            await feed.StartAsync();
            for (int index = 0; index < eventsBeforeRestart; index++)
            {
                await source.EmitAsync(new(LocalChangeSourceAction.Created, paths[index]));
            }

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            CloudLocalChangeBatch batch = await feed.ReadBatchAsync(timeout.Token);
            Assert.NotEmpty(batch.Changes);
            acknowledged = batch.Changes.Count;
            await feed.AcknowledgeAsync(batch.Changes.Select(change => change.OperationId), timeout.Token);

            // This test verifies bounded durable replay, not a five-second flush of a queued
            // burst. Observe the finite committed boundary before closing the first runtime.
            using CancellationTokenSource persistenceTimeout = new(TimeSpan.FromSeconds(30));
            CloudLocalChangeScan scan;
            do
            {
                scan = await feed.BeginScanAsync(persistenceTimeout.Token);
                Assert.False(scan.RequiresFullRescan);
                if (scan.ThroughSequence < eventsBeforeRestart)
                {
                    await Task.Delay(10, persistenceTimeout.Token);
                }
            }
            while (scan.ThroughSequence < eventsBeforeRestart);

            Assert.Equal(eventsBeforeRestart, scan.ThroughSequence);
            CloudLocalChangePage retained = await feed.ReadPageAsync(scan, 0, eventCount, persistenceTimeout.Token);
            Assert.False(retained.RequiresFullRescan);
            Assert.False(retained.HasMore);
            retainedBeforeRestart = retained.Changes.ToArray();
            Assert.Equal(eventsBeforeRestart - acknowledged, retainedBeforeRestart.Length);
        }

        FakeSource restartedSource = new();
        await using CloudLocalChangeFeed restarted = CloudLocalChangeFeed.CreateForTesting(
            _syncRootPath,
            store,
            options,
            restartedSource);
        await restarted.StartAsync();
        CloudLocalChangePage replayed = await restarted.ReadPageAsync(await restarted.BeginScanAsync(), 0, eventCount);
        Assert.False(replayed.RequiresFullRescan);
        Assert.Equal(retainedBeforeRestart.Select(change => (change.OperationId, change.Sequence, change.Kind,
            change.ItemId, change.RelativePath, change.PreviousRelativePath, change.IsDirectory, change.ObservedAt)),
            replayed.Changes.Select(change => (change.OperationId, change.Sequence, change.Kind,
                change.ItemId, change.RelativePath, change.PreviousRelativePath, change.IsDirectory, change.ObservedAt)));
        for (int index = eventsBeforeRestart; index < eventCount; index++)
        {
            await restartedSource.EmitAsync(new(LocalChangeSourceAction.Created, paths[index]));
        }

        using CancellationTokenSource replayTimeout = new(TimeSpan.FromSeconds(10));
        while (acknowledged < eventCount)
        {
            CloudLocalChangeBatch batch = await restarted.ReadBatchAsync(replayTimeout.Token);
            Assert.False(batch.RequiresFullRescan);
            Assert.NotEmpty(batch.Changes);
            acknowledged += batch.Changes.Count;
            await restarted.AcknowledgeAsync(
                batch.Changes.Select(change => change.OperationId),
                replayTimeout.Token);
        }

        await using ICloudStateTransaction transaction = await store.BeginTransactionAsync();
        Assert.Empty(await transaction.Operations.ListAsync(10));
        await transaction.RollbackAsync();
    }

    private CloudLocalChangeFeed CreateFeed(ICloudStateStore store, FakeSource source) =>
        CloudLocalChangeFeed.CreateForTesting(
            _syncRootPath,
            store,
            new CloudLocalChangeFeedOptions { BufferCapacity = 8, BatchSize = 16 },
            source);

    private async ValueTask<ICloudStateStore> OpenStoreAsync() =>
        await new SqliteCloudStateStoreFactory(_databasePath)
            .OpenAsync(new CloudStateStoreContext(_syncRootPath));

    private sealed class FakeSource : ILocalChangeSource
    {
        private Func<LocalChangeSourceEvent, ValueTask>? _handler;

        public Task StartAsync(
            Func<LocalChangeSourceEvent, ValueTask> eventHandler,
            CancellationToken cancellationToken)
        {
            _handler = eventHandler;
            return Task.CompletedTask;
        }

        public ValueTask EmitAsync(LocalChangeSourceEvent change) =>
            _handler is null
                ? throw new InvalidOperationException("The fake source has not started.")
                : _handler(change);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
