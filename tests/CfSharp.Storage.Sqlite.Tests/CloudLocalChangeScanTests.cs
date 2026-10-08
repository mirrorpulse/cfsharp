using Microsoft.Data.Sqlite;

namespace CfSharp.Storage.Sqlite.Tests;

public sealed partial class CloudLocalChangeFeedTests
{
    [Fact]
    public async Task CustomStoreWithoutPagingRetainsLegacyBatchReads()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        CloudOperationJournalEntry expected = Assert.Single(await SeedChangesAsync(store, 1));
        await using CloudLocalChangeFeed feed = CreateFeed(new LegacyStore(store), new FakeSource());
        await feed.StartAsync();
        await Assert.ThrowsAsync<NotSupportedException>(async () => await feed.BeginScanAsync());
        Assert.Equal(expected.OperationId, Assert.Single((await feed.ReadBatchAsync()).Changes).OperationId);
        await feed.AcknowledgeAsync([expected.OperationId]);
    }

    [Fact]
    public async Task ScanReachesSuccessorBehindDeferredHeadAndReplaysAfterStoreRestart()
    {
        CloudLocalChangeScan previousScan;
        CloudOperationJournalEntry[] seed;
        await using (ICloudStateStore store = await OpenStoreAsync())
        await using (CloudLocalChangeFeed feed = CreateFeed(store, new FakeSource()))
        {
            seed = await SeedChangesAsync(store, 17);
            await feed.StartAsync();
            previousScan = await feed.BeginScanAsync();
            Assert.Equal(seed[^1].Sequence, previousScan.ThroughSequence);
            CloudLocalChangePage first = await feed.ReadPageAsync(previousScan, 0, 4);
            CloudLocalChangePage repeated = await feed.ReadPageAsync(previousScan, 0, 4);
            Assert.Equal(first.Changes.Select(value => value.OperationId), repeated.Changes.Select(value => value.OperationId));
            Assert.Equal(4, first.Changes.Count);

            CloudOperationJournalEntry later = Assert.Single(await SeedChangesAsync(store, 1));
            List<CloudLocalChange> delivered = [];
            long cursor = 0;
            CloudLocalChangePage page;
            do
            {
                page = await feed.ReadPageAsync(previousScan, cursor, 4);
                Assert.False(page.RequiresFullRescan);
                delivered.AddRange(page.Changes);
                cursor = page.LastScannedSequence;
            }
            while (page.HasMore);

            Assert.Equal(seed.Select(row => row.OperationId), delivered.Select(value => value.OperationId));
            Assert.DoesNotContain(delivered, value => value.OperationId == later.OperationId);
            Assert.Equal(seed.Select(row => row.Sequence), delivered.Select(value => value.Sequence));
            Assert.Equal("moved-3.txt", delivered[3].RelativePath);
            Assert.Equal("old-3.txt", delivered[3].PreviousRelativePath);
            Assert.Equal(seed[3].CreatedAt, delivered[3].ObservedAt);
            await feed.AcknowledgeAsync([delivered[^1].OperationId]);
            CloudLocalChangePage terminal = await feed.ReadPageAsync(previousScan, cursor, 4);
            Assert.Empty(terminal.Changes);
            Assert.False(terminal.HasMore);
            Assert.Equal(previousScan.ThroughSequence, terminal.LastScannedSequence);

            await using ICloudStateTransaction verify = await store.BeginTransactionAsync();
            Assert.Equal(seed.Take(16).Select(row => row.OperationId),
                (await verify.Operations.ListAsync(16)).Select(row => row.OperationId));
            await verify.RollbackAsync();
        }

        await using ICloudStateStore reopened = await OpenStoreAsync();
        await using CloudLocalChangeFeed restarted = CreateFeed(reopened, new FakeSource());
        await restarted.StartAsync();
        await Assert.ThrowsAsync<ArgumentException>(async () => await restarted.ReadPageAsync(previousScan, 0, 4));
        CloudLocalChangeScan fresh = await restarted.BeginScanAsync();
        Assert.NotEqual(previousScan.StoreScope, fresh.StoreScope);
        Assert.Equal(seed.Take(4).Select(row => row.OperationId),
            (await restarted.ReadPageAsync(fresh, 0, 4)).Changes.Select(value => value.OperationId));
    }

    [Fact]
    public async Task ScanHandlesAcknowledgementHolesAndRejectsInvalidConsumers()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        CloudOperationJournalEntry[] seed = await SeedChangesAsync(store, 9);
        await using CloudLocalChangeFeed feed = CreateFeed(store, new FakeSource());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await feed.BeginScanAsync());
        await feed.StartAsync();
        CloudLocalChangeScan scan = await feed.BeginScanAsync();
        CloudLocalChangePage first = await feed.ReadPageAsync(scan, 0, 4);
        await feed.AcknowledgeAsync(seed.Skip(4).Take(4).Select(row => row.OperationId));
        CloudLocalChangePage second = await feed.ReadPageAsync(scan, first.LastScannedSequence, 4);
        Assert.Equal(seed[^1].OperationId, Assert.Single(second.Changes).OperationId);
        Assert.False(second.HasMore);

        await using CloudLocalChangeFeed another = CreateFeed(store, new FakeSource());
        await another.StartAsync();
        await Assert.ThrowsAsync<ArgumentException>(async () => await another.ReadPageAsync(scan, 0, 4));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await feed.ReadPageAsync(scan, -1, 4));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await feed.ReadPageAsync(scan, scan.ThroughSequence + 1, 4));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await feed.ReadPageAsync(scan, 0, 4097));
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await feed.ReadPageAsync(scan, 0, 4, canceled.Token));
        Assert.NotEmpty((await feed.ReadPageAsync(scan, 0, 4)).Changes);
        await feed.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await feed.BeginScanAsync());
    }

    [Fact]
    public async Task EmptyScanReturnsImmediatelyAndDoesNotExpandAfterEnqueue()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        await using CloudLocalChangeFeed feed = CreateFeed(store, new FakeSource());
        await feed.StartAsync();
        CloudLocalChangeScan empty = await feed.BeginScanAsync();
        Assert.Equal(0, empty.ThroughSequence);
        await SeedChangesAsync(store, 1);
        CloudLocalChangePage page = await feed.ReadPageAsync(empty, 0, 1);
        Assert.Empty(page.Changes);
        Assert.False(page.HasMore);
        Assert.Equal(0, page.LastScannedSequence);
        Assert.Single((await feed.ReadPageAsync(await feed.BeginScanAsync(), 0, 1)).Changes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedOrForeignPayloadRetainsOffendingJournalRow(bool foreignPath)
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        Guid operationId = Guid.NewGuid();
        byte[] payload = foreignPath
            ? new LocalChangePayload("..\\outside.txt", null, false, DateTimeOffset.UtcNow).Encode()
            : [0xff, 0xff, 0xff, 0xff, 0x7f];
        await using (ICloudStateTransaction write = await store.BeginTransactionAsync())
        {
            await write.Operations.EnqueueAsync(new CloudOperationJournalEntry(operationId,
                CloudStateOperationKind.Create, null, payload, DateTimeOffset.UtcNow));
            await write.CommitAsync();
        }

        await using CloudLocalChangeFeed feed = CreateFeed(store, new FakeSource());
        await feed.StartAsync();
        CloudLocalChangeScan scan = await feed.BeginScanAsync();
        CloudLocalChangeJournalException failure = await Assert.ThrowsAsync<CloudLocalChangeJournalException>(
            async () => await feed.ReadPageAsync(scan, 0, 1));
        Assert.Equal(operationId, failure.OperationId);
        Assert.Equal(1, failure.Sequence);
        Assert.NotNull(failure.InnerException);
        await using ICloudStateTransaction verify = await store.BeginTransactionAsync();
        Assert.Equal(payload, (await verify.Operations.GetAsync(operationId))!.Payload.ToArray());
        await verify.RollbackAsync();
    }

    [Fact]
    public async Task SqliteQueryFailureReleasesScanTransactionsAndPreservesOriginalJournal()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        CloudOperationJournalEntry[] expected = await SeedChangesAsync(store, 2);
        await using CloudLocalChangeFeed feed = CreateFeed(store, new FakeSource());
        await feed.StartAsync();
        CloudLocalChangeScan scan = await feed.BeginScanAsync();
        await using SqliteConnection repair = new(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Pooling = false,
        }.ToString());
        await repair.OpenAsync();
        await SqliteSchema.ConfigureConnectionAsync(repair, 5000, CancellationToken.None);
        await using SqliteCommand command = repair.CreateCommand();
        // Interrupt the real SQLite query without deleting or rewriting any journal row.
        // Restoring the table and reusing this scan must remain possible after both failures.
        command.CommandText = "ALTER TABLE operations RENAME TO interrupted_operations;";
        await command.ExecuteNonQueryAsync();
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            SqliteCloudStateStoreException beginFailure = await Assert.ThrowsAsync<SqliteCloudStateStoreException>(
                async () => await feed.BeginScanAsync(timeout.Token));
            SqliteCloudStateStoreException pageFailure = await Assert.ThrowsAsync<SqliteCloudStateStoreException>(
                async () => await feed.ReadPageAsync(scan, 0, 1, timeout.Token));
            Assert.Equal(1, beginFailure.SqliteErrorCode);
            Assert.Equal(1, pageFailure.SqliteErrorCode);
            Assert.IsType<SqliteException>(beginFailure.InnerException);
            Assert.IsType<SqliteException>(pageFailure.InnerException);
        }
        finally
        {
            command.CommandText = "ALTER TABLE interrupted_operations RENAME TO operations;";
            await command.ExecuteNonQueryAsync();
        }

        using CancellationTokenSource recoveryTimeout = new(TimeSpan.FromSeconds(30));
        CloudLocalChangePage page = await feed.ReadPageAsync(scan, 0, 2, recoveryTimeout.Token);
        Assert.False(page.RequiresFullRescan);
        Assert.False(page.HasMore);
        Assert.Equal(expected.Select(row => row.OperationId), page.Changes.Select(change => change.OperationId));
        await using ICloudStateTransaction verify = await store.BeginTransactionAsync(recoveryTimeout.Token);
        IReadOnlyList<CloudOperationJournalEntry> retained = await verify.Operations.ListAsync(2, recoveryTimeout.Token);
        Assert.Equal(expected.Select(row => row.OperationId), retained.Select(row => row.OperationId));
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.Equal(expected[index].Sequence, retained[index].Sequence);
            Assert.Equal(expected[index].Payload.ToArray(), retained[index].Payload.ToArray());
            Assert.Equal(expected[index].CreatedAt, retained[index].CreatedAt);
            Assert.Equal(expected[index].AttemptCount, retained[index].AttemptCount);
            Assert.Equal(expected[index].RetryAfter, retained[index].RetryAfter);
        }

        await verify.RollbackAsync(recoveryTimeout.Token);
    }

    private static async Task<CloudOperationJournalEntry[]> SeedChangesAsync(ICloudStateStore store, int count)
    {
        await using ICloudStateTransaction transaction = await store.BeginTransactionAsync();
        List<CloudOperationJournalEntry> rows = [];
        for (int index = 0; index < count; index++)
        {
            CloudStateOperationKind kind = (CloudStateOperationKind)(index % 5);
            DateTimeOffset observed = DateTimeOffset.UtcNow.AddTicks(index);
            string path = kind == CloudStateOperationKind.Move ? $"moved-{index}.txt" : $"item-{index}.txt";
            LocalChangePayload payload = new(path,
                kind == CloudStateOperationKind.Move ? $"old-{index}.txt" : null, false, observed);
            rows.Add(await transaction.Operations.EnqueueAsync(new CloudOperationJournalEntry(
                Guid.NewGuid(), kind, null, payload.Encode(), observed)));
        }

        await transaction.CommitAsync();
        return rows.ToArray();
    }

    private sealed class LegacyStore(ICloudStateStore inner) : ICloudStateStore
    {
        public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new LegacyTransaction(await inner.BeginTransactionAsync(cancellationToken));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LegacyTransaction(ICloudStateTransaction inner) : ICloudStateTransaction
    {
        public ICloudItemStateRepository Items => inner.Items;
        public ICloudCheckpointRepository Checkpoints => inner.Checkpoints;
        public ICloudOperationJournal Operations { get; } = new LegacyJournal(inner.Operations);
        public ICloudConflictRepository Conflicts => inner.Conflicts;
        public ICloudRemoteBatchRepository RemoteBatches => inner.RemoteBatches;
        public ICloudEchoSuppressionRepository EchoSuppressions => inner.EchoSuppressions;
        public ValueTask CommitAsync(CancellationToken cancellationToken = default) => inner.CommitAsync(cancellationToken);
        public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class LegacyJournal(ICloudOperationJournal inner) : ICloudOperationJournal
    {
        public ValueTask<CloudOperationJournalEntry> EnqueueAsync(CloudOperationJournalEntry operation,
            CancellationToken cancellationToken = default) => inner.EnqueueAsync(operation, cancellationToken);
        public ValueTask<CloudOperationJournalEntry?> GetAsync(Guid operationId,
            CancellationToken cancellationToken = default) => inner.GetAsync(operationId, cancellationToken);
        public ValueTask<IReadOnlyList<CloudOperationJournalEntry>> ListAsync(int maximumCount,
            CancellationToken cancellationToken = default) => inner.ListAsync(maximumCount, cancellationToken);
        public ValueTask<IReadOnlyList<CloudOperationJournalEntry>> ListByItemIdAsync(Guid itemId, int maximumCount,
            CancellationToken cancellationToken = default) => inner.ListByItemIdAsync(itemId, maximumCount, cancellationToken);
        public ValueTask UpdateAsync(CloudOperationJournalEntry operation,
            CancellationToken cancellationToken = default) => inner.UpdateAsync(operation, cancellationToken);
        public ValueTask RemoveAsync(Guid operationId,
            CancellationToken cancellationToken = default) => inner.RemoveAsync(operationId, cancellationToken);
    }
}
