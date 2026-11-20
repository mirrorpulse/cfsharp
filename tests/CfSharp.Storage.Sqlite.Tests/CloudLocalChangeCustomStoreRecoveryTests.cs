namespace CfSharp.Storage.Sqlite.Tests;

public sealed partial class CloudLocalChangeFeedTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task JournalCapabilityIsCheckedBeforeStartupAndVerifiedDuringRecovery(bool pendingReceipt, bool declaresCapability)
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
            if (pendingReceipt) { await seed.Checkpoints.UpsertAsync(new(receiptName, payload, now)); }
            await seed.CommitAsync();
        }
        LegacyJournalStore legacy = new(store, declaresCapability);
        StartupSource source = new(Path.Combine(_temporaryDirectory, "startup.lock"));
        source.Ready.TrySetResult();
        await using (CloudLocalChangeFeed feed = CloudLocalChangeFeed.CreateForTesting(_syncRootPath, legacy, new(), source))
        {
            Exception? failure = await Record.ExceptionAsync(() => feed.StartAsync().AsTask());
            Assert.IsType<NotSupportedException>(failure);
            Assert.False(source.Entered.Task.IsCompleted);
            Assert.Equal(declaresCapability ? 1 : 0, legacy.UpdateCalls);
            Assert.False(feed.IsStarted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => feed.BeginScanAsync().AsTask());
        }
        await using (ICloudStateTransaction read = await store.BeginTransactionAsync())
        {
            CloudOperationJournalEntry retained = (await read.Operations.GetAsync(operationId))!;
            Assert.Equal((itemId, original.Sequence), (retained.ItemId, retained.Sequence));
            Assert.Equal(pendingReceipt, await read.Checkpoints.GetAsync(receiptName) is not null);
        }
        // Upgrading only the custom journal capability makes the original evidence recoverable.
        await using CloudLocalChangeFeed recovered = CreateFeed(store, new FakeSource());
        await recovered.StartAsync();
        Assert.Equal(pendingReceipt, (await recovered.BeginScanAsync()).RequiresFullRescan);
        if (pendingReceipt) { await recovered.AcknowledgeFullRescanAsync(); }
        CloudLocalChangePage page = await recovered.ReadPageAsync(await recovered.BeginScanAsync(), 0, 10);
        CloudLocalChange change = Assert.Single(page.Changes);
        Assert.Equal((operationId, original.Sequence, pendingReceipt ? null : (Guid?)itemId), (change.OperationId, change.Sequence, change.ItemId));
        await recovered.AcknowledgeAsync([operationId]);
    }

    private sealed class LegacyJournalStore(ICloudStateStore inner, bool declaresCapability) : ICloudStateStore
    {
        internal bool DeclaresCapability => declaresCapability;
        internal int UpdateCalls { get; set; }
        public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            new LegacyJournalTransaction(await inner.BeginTransactionAsync(cancellationToken), this);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class LegacyJournalTransaction(ICloudStateTransaction inner, LegacyJournalStore owner) : ICloudStateTransaction
    {
        public ICloudItemStateRepository Items => inner.Items;
        public ICloudCheckpointRepository Checkpoints => inner.Checkpoints;
        public ICloudOperationJournal Operations { get; } = owner.DeclaresCapability
            ? new FalseAssociationCapability(inner.Operations, owner) : new RetryOnlyJournal(inner.Operations, owner);
        public ICloudConflictRepository Conflicts => inner.Conflicts;
        public ICloudRemoteBatchRepository RemoteBatches => inner.RemoteBatches;
        public ICloudEchoSuppressionRepository EchoSuppressions => inner.EchoSuppressions;
        public ValueTask CommitAsync(CancellationToken cancellationToken = default) => inner.CommitAsync(cancellationToken);
        public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class FalseAssociationCapability(ICloudOperationJournal inner, LegacyJournalStore owner)
        : RetryOnlyJournal(inner, owner), ICloudOperationJournalItemAssociationUpdates
    {
    }

    private class RetryOnlyJournal(ICloudOperationJournal inner, LegacyJournalStore owner) : ICloudOperationJournal
    {
        public ValueTask<CloudOperationJournalEntry> EnqueueAsync(CloudOperationJournalEntry operation, CancellationToken cancellationToken = default) => inner.EnqueueAsync(operation, cancellationToken);
        public ValueTask<CloudOperationJournalEntry?> GetAsync(Guid operationId, CancellationToken cancellationToken = default) => inner.GetAsync(operationId, cancellationToken);
        public ValueTask<IReadOnlyList<CloudOperationJournalEntry>> ListAsync(int maximumCount, CancellationToken cancellationToken = default) => inner.ListAsync(maximumCount, cancellationToken);
        public ValueTask<IReadOnlyList<CloudOperationJournalEntry>> ListByItemIdAsync(Guid itemId, int maximumCount, CancellationToken cancellationToken = default) => inner.ListByItemIdAsync(itemId, maximumCount, cancellationToken);
        public async ValueTask UpdateAsync(CloudOperationJournalEntry operation, CancellationToken cancellationToken = default)
        {
            owner.UpdateCalls++;
            CloudOperationJournalEntry original = (await inner.GetAsync(operation.OperationId, cancellationToken))!;
            // The former interface summary promised only retry metadata and payload updates.
            await inner.UpdateAsync(new(operation.OperationId, original.Kind, original.ItemId, operation.Payload.Span,
                original.CreatedAt, operation.AttemptCount, operation.RetryAfter, original.Sequence), cancellationToken);
        }
        public ValueTask RemoveAsync(Guid operationId, CancellationToken cancellationToken = default) => inner.RemoveAsync(operationId, cancellationToken);
    }
}
