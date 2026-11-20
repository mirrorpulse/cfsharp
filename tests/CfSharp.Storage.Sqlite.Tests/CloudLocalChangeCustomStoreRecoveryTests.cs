namespace CfSharp.Storage.Sqlite.Tests;

public sealed partial class CloudLocalChangeFeedTests
{
    [Fact]
    public async Task LegacyJournalUpdateCannotDiscardTheReceiptWhileKeepingARejectedAssociation()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        Guid operationId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        byte[] payload = new LocalChangePayload("replaced.bin", null, false, now).Encode();
        CloudOperationJournalEntry original;
        string receiptName = CloudLocalChangeFeed.SecurityValidationPrefix + operationId.ToString("N");
        await using (ICloudStateTransaction seed = await store.BeginTransactionAsync())
        {
            await seed.Items.UpsertAsync(new(itemId, "original", "replaced.bin", CloudItemKind.File, "v1", null, false, now));
            original = await seed.Operations.EnqueueAsync(new(operationId, CloudStateOperationKind.MetadataUpdate, itemId, payload, now));
            await seed.Checkpoints.UpsertAsync(new(receiptName, payload, now));
            await seed.CommitAsync();
        }
        LegacyJournalStore legacy = new(store);
        await using (CloudLocalChangeFeed feed = CloudLocalChangeFeed.CreateForTesting(_syncRootPath, legacy, new(), new FakeSource()))
        {
            Exception? failure = await Record.ExceptionAsync(() => feed.StartAsync().AsTask());
            Assert.IsType<NotSupportedException>(failure);
            Assert.False(feed.IsStarted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => feed.BeginScanAsync().AsTask());
        }
        await using (ICloudStateTransaction read = await store.BeginTransactionAsync())
        {
            CloudOperationJournalEntry retained = (await read.Operations.GetAsync(operationId))!;
            Assert.Equal((itemId, original.Sequence), (retained.ItemId, retained.Sequence));
            Assert.NotNull(await read.Checkpoints.GetAsync(receiptName));
        }
        // Upgrading only the custom journal capability makes the original evidence recoverable.
        await using CloudLocalChangeFeed recovered = CreateFeed(store, new FakeSource());
        await recovered.StartAsync();
        Assert.True((await recovered.BeginScanAsync()).RequiresFullRescan);
        await recovered.AcknowledgeFullRescanAsync();
        CloudLocalChangePage page = await recovered.ReadPageAsync(await recovered.BeginScanAsync(), 0, 10);
        CloudLocalChange change = Assert.Single(page.Changes);
        Assert.Equal((operationId, original.Sequence, (Guid?)null), (change.OperationId, change.Sequence, change.ItemId));
        await recovered.AcknowledgeAsync([operationId]);
    }

    private sealed class LegacyJournalStore(ICloudStateStore inner) : ICloudStateStore
    {
        public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new LegacyJournalTransaction(await inner.BeginTransactionAsync(cancellationToken));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LegacyJournalTransaction(ICloudStateTransaction inner) : ICloudStateTransaction
    {
        public ICloudItemStateRepository Items => inner.Items;
        public ICloudCheckpointRepository Checkpoints => inner.Checkpoints;
        public ICloudOperationJournal Operations { get; } = new RetryOnlyJournal(inner.Operations);
        public ICloudConflictRepository Conflicts => inner.Conflicts;
        public ICloudRemoteBatchRepository RemoteBatches => inner.RemoteBatches;
        public ICloudEchoSuppressionRepository EchoSuppressions => inner.EchoSuppressions;
        public ValueTask CommitAsync(CancellationToken cancellationToken = default) => inner.CommitAsync(cancellationToken);
        public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class RetryOnlyJournal(ICloudOperationJournal inner) : ICloudOperationJournal
    {
        public ValueTask<CloudOperationJournalEntry> EnqueueAsync(CloudOperationJournalEntry operation, CancellationToken cancellationToken = default) => inner.EnqueueAsync(operation, cancellationToken);
        public ValueTask<CloudOperationJournalEntry?> GetAsync(Guid operationId, CancellationToken cancellationToken = default) => inner.GetAsync(operationId, cancellationToken);
        public ValueTask<IReadOnlyList<CloudOperationJournalEntry>> ListAsync(int maximumCount, CancellationToken cancellationToken = default) => inner.ListAsync(maximumCount, cancellationToken);
        public ValueTask<IReadOnlyList<CloudOperationJournalEntry>> ListByItemIdAsync(Guid itemId, int maximumCount, CancellationToken cancellationToken = default) => inner.ListByItemIdAsync(itemId, maximumCount, cancellationToken);
        public async ValueTask UpdateAsync(CloudOperationJournalEntry operation, CancellationToken cancellationToken = default)
        {
            CloudOperationJournalEntry original = (await inner.GetAsync(operation.OperationId, cancellationToken))!;
            // The former interface summary promised only retry metadata and payload updates.
            await inner.UpdateAsync(new(operation.OperationId, original.Kind, original.ItemId, operation.Payload.Span,
                original.CreatedAt, operation.AttemptCount, operation.RetryAfter, original.Sequence), cancellationToken);
        }
        public ValueTask RemoveAsync(Guid operationId, CancellationToken cancellationToken = default) => inner.RemoveAsync(operationId, cancellationToken);
    }
}
