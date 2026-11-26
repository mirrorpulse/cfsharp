namespace CfSharp.Storage.Sqlite.Tests;

public sealed partial class CloudLocalChangeFeedTests
{
    [Fact]
    public async Task UnvalidatedMetadataWithholdsOnlyItsSuffixWithoutAdvancingTheCursor()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        await using CloudLocalChangeFeed feed = CreateFeed(store, new FakeSource());
        await feed.StartAsync();
        Guid pendingId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        byte[] payload = new LocalChangePayload("history.bin", null, false, now).Encode();
        CloudOperationJournalEntry earlier;
        await using (ICloudStateTransaction publication = await store.BeginTransactionAsync())
        {
            earlier = await publication.Operations.EnqueueAsync(new(Guid.NewGuid(), CloudStateOperationKind.MetadataUpdate, null, payload, now));
            await publication.Operations.EnqueueAsync(new(pendingId, CloudStateOperationKind.MetadataUpdate, null, payload, now));
            await publication.Checkpoints.UpsertAsync(new(CloudLocalChangeFeed.SecurityValidationPrefix + pendingId.ToString("N"), payload, now));
            await publication.CommitAsync();
        }
        CloudLocalChangeScan scan = await feed.BeginScanAsync();
        CloudLocalChangePage page = await feed.ReadPageAsync(scan, 0, 10);
        Assert.False(page.RequiresFullRescan);
        Assert.Equal(earlier.OperationId, Assert.Single(page.Changes).OperationId);
        Assert.Equal(earlier.Sequence, page.LastScannedSequence);
        Assert.True(page.HasMore);
        Assert.Equal(earlier.OperationId, Assert.Single((await feed.ReadBatchAsync()).Changes).OperationId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => feed.AcknowledgeAsync([pendingId]).AsTask());
        await feed.AcknowledgeAsync([earlier.OperationId]);
        CloudLocalChangePage waiting = await feed.ReadPageAsync(scan, earlier.Sequence, 10);
        Assert.Empty(waiting.Changes);
        Assert.Equal(earlier.Sequence, waiting.LastScannedSequence);
        Assert.True(waiting.HasMore);
        await feed.DisposeAsync();
        await using CloudLocalChangeFeed reopened = CreateFeed(store, new FakeSource());
        await reopened.StartAsync();
        Assert.True((await reopened.BeginScanAsync()).RequiresFullRescan);
    }

    [Theory]
    [InlineData(CloudStateOperationKind.MetadataUpdate)]
    [InlineData(CloudStateOperationKind.ContentUpdate)]
    public async Task CommittedUnvalidatedObservationRecoversUnboundWithItsOriginalSequence(CloudStateOperationKind kind)
    {
        Guid operationId = Guid.NewGuid();
        Guid itemId = Guid.NewGuid();
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        byte[] payload = new LocalChangePayload("history.bin", null, false, observedAt).Encode();
        CloudOperationJournalEntry original;
        await using (ICloudStateStore previous = await OpenStoreAsync())
        await using (ICloudStateTransaction interrupted = await previous.BeginTransactionAsync())
        {
            await interrupted.Items.UpsertAsync(new(itemId, "original", "history.bin", CloudItemKind.File,
                "accepted", null, false, observedAt));
            original = await interrupted.Operations.EnqueueAsync(new(operationId, kind,
                itemId, payload, observedAt, attemptCount: 2, retryAfter: observedAt.AddMinutes(1)));
            await interrupted.Checkpoints.UpsertAsync(new(CloudLocalChangeFeed.SecurityValidationPrefix + operationId.ToString("N"),
                payload, observedAt));
            await interrupted.CommitAsync();
        }
        await using ICloudStateStore store = await OpenStoreAsync();
        await using CloudLocalChangeFeed feed = CreateFeed(store, new FakeSource());
        await feed.StartAsync();
        await using (ICloudStateTransaction read = await store.BeginTransactionAsync())
        {
            CloudOperationJournalEntry recovered = (await read.Operations.GetAsync(operationId))!;
            Assert.Null(recovered.ItemId);
            Assert.Equal((original.Sequence, original.Kind, original.CreatedAt, original.AttemptCount, original.RetryAfter),
                (recovered.Sequence, recovered.Kind, recovered.CreatedAt, recovered.AttemptCount, recovered.RetryAfter));
            Assert.Equal(original.Payload.ToArray(), recovered.Payload.ToArray());
            Assert.Empty(await read.Checkpoints.ListAsync(CloudLocalChangeFeed.SecurityValidationPrefix));
        }
        Assert.True((await feed.BeginScanAsync()).RequiresFullRescan);
        await feed.AcknowledgeFullRescanAsync();
        CloudLocalChangePage page = await feed.ReadPageAsync(await feed.BeginScanAsync(), 0, 10);
        CloudLocalChange change = Assert.Single(page.Changes);
        Assert.Equal((operationId, original.Sequence, (Guid?)null, "history.bin"),
            (change.OperationId, change.Sequence, change.ItemId, change.RelativePath));
        await feed.AcknowledgeAsync([operationId]);
    }
}
