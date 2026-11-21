namespace CfSharp.Storage.Sqlite.Tests;

public sealed partial class CloudLocalChangeFeedTests
{
    [Fact]
    public async Task LegacyMetadataTicketCannotConsumeAGenuineDirectoryObservation()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        string path = Path.Combine(_syncRootPath, "directory");
        Directory.CreateDirectory(path);
        Guid itemId = Guid.NewGuid();
        Guid ticketId = Guid.NewGuid();
        await using (ICloudStateTransaction seed = await store.BeginTransactionAsync())
        {
            await seed.Items.UpsertAsync(new(itemId, "legacy-metadata", "directory", CloudItemKind.Directory,
                "r1", null, false, DateTimeOffset.UtcNow));
            await seed.EchoSuppressions.UpsertAsync(new(ticketId, itemId, CloudStateOperationKind.MetadataUpdate,
                "directory", Array.Empty<byte>(), DateTimeOffset.UtcNow.AddMinutes(1), remainingObservations: 4));
            await seed.CommitAsync();
        }
        FakeSource source = new();
        await using CloudLocalChangeFeed feed = CreateFeed(store, source);
        await feed.StartAsync();
        await Assert.ThrowsAsync<NotSupportedException>(() => feed.SuppressProviderEchoAsync(
            CloudStateOperationKind.MetadataUpdate, "directory", DateTimeOffset.UtcNow.AddMinutes(1), itemId).AsTask());
        await source.EmitAsync(new(LocalChangeSourceAction.Modified, "directory"));
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        CloudLocalChangeBatch batch = await feed.ReadBatchAsync(deadline.Token);
        CloudLocalChange change = Assert.Single(batch.Changes);
        Assert.Equal(CloudLocalChangeKind.MetadataUpdate, change.Kind);
        Assert.Equal(itemId, change.ItemId);
        await using ICloudStateTransaction state = await store.BeginTransactionAsync();
        Assert.Equal(4, (await state.EchoSuppressions.GetAsync(ticketId))!.RemainingObservations);
        Assert.Equal(change.OperationId, Assert.Single(await state.Operations.ListAsync(10)).OperationId);
    }

    [Fact]
    public async Task CorruptMetadataEvidenceCannotStartTheFeedOrConsumeAnOriginal()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        RemoteMetadataIntent intent = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Docs\\Mixed.BIN",
            new(1, Guid.NewGuid(), Guid.NewGuid()), Coordinated: true, Completed: true);
        CloudStateCheckpoint evidence = new(intent.Name, intent.Encode(), DateTimeOffset.UtcNow);
        Assert.Equal(intent.RelativePath, RemoteMetadataIntent.Decode(evidence).RelativePath);
        Guid operationId = Guid.NewGuid();
        byte[] payload = new LocalChangePayload(intent.RelativePath, null, false, DateTimeOffset.UtcNow).Encode();
        await using (ICloudStateTransaction seed = await store.BeginTransactionAsync())
        {
            await seed.Checkpoints.UpsertAsync(new(intent.Name, evidence.Value.Span[..^1], evidence.UpdatedAt));
            await seed.Operations.EnqueueAsync(new(operationId, CloudStateOperationKind.MetadataUpdate, null, payload, DateTimeOffset.UtcNow));
            await seed.CommitAsync();
        }
        FakeSource source = new();
        await using CloudLocalChangeFeed feed = CreateFeed(store, source);
        await Assert.ThrowsAsync<InvalidDataException>(() => feed.StartAsync().AsTask());
        await using ICloudStateTransaction retained = await store.BeginTransactionAsync();
        Assert.NotNull(await retained.Checkpoints.GetAsync(intent.Name));
        Assert.NotNull(await retained.Operations.GetAsync(operationId));
    }
}
