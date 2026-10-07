namespace CfSharp.Storage.Sqlite.Tests;

public sealed partial class CloudLocalChangeFeedTests
{
    [Theory]
    [InlineData(CloudItemKind.File)]
    [InlineData(CloudItemKind.Directory)]
    public async Task UnprovenMoveKeepsUnrelatedTargetAndJournalUntilExplicitAcknowledgement(CloudItemKind kind)
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        Guid sourceId = Guid.NewGuid();
        Guid targetId = Guid.NewGuid();
        await using (ICloudStateTransaction seed = await store.BeginTransactionAsync())
        {
            await seed.Items.UpsertAsync(new(sourceId, "source", "before", kind, "source-revision", 10, false, DateTimeOffset.UtcNow));
            await seed.Items.UpsertAsync(new(targetId, "unrelated", "after", kind, "target-revision", 20, true, DateTimeOffset.UtcNow));
            await seed.CommitAsync();
        }

        FakeSource source = new();
        await using (CloudLocalChangeFeed feed = CreateFeed(store, source))
        {
            await feed.StartAsync();
            await feed.SuppressProviderEchoAsync(CloudStateOperationKind.Move, "after", DateTimeOffset.UtcNow.AddMinutes(1), sourceId, "before", 1);
            await source.EmitAsync(new(LocalChangeSourceAction.RenamedOldName, "before"));
            await source.EmitAsync(new(LocalChangeSourceAction.RenamedNewName, "after"));
        }

        Guid operationId;
        await using (ICloudStateTransaction verify = await store.BeginTransactionAsync())
        {
            Assert.Equal("before", (await verify.Items.GetByItemIdAsync(sourceId))!.RelativePath);
            CloudItemState target = (await verify.Items.GetByItemIdAsync(targetId))!;
            Assert.True(target.IsTombstone);
            Assert.Equal("target-revision", target.RemoteRevision);
            CloudOperationJournalEntry move = Assert.Single(await verify.Operations.ListAsync(10));
            Assert.Null(move.ItemId);
            Assert.Equal(kind == CloudItemKind.Directory, LocalChangePayload.Decode(move.Payload).IsDirectory);
            operationId = move.OperationId;
            Assert.Single(await verify.EchoSuppressions.ListActiveAsync(DateTimeOffset.UtcNow));
        }

        await using CloudLocalChangeFeed restarted = CreateFeed(store, new());
        await restarted.StartAsync();
        Assert.True((await restarted.BeginScanAsync()).RequiresFullRescan);
        await restarted.AcknowledgeFullRescanAsync();
        CloudLocalChangePage page = await restarted.ReadPageAsync(await restarted.BeginScanAsync(), 0, 4);
        Assert.Equal(operationId, Assert.Single(page.Changes).OperationId);
        await using ICloudStateTransaction retained = await store.BeginTransactionAsync();
        Assert.NotNull(await retained.Operations.GetAsync(operationId));
        Assert.Empty(await retained.Checkpoints.ListAsync(CloudLocalChangeFeed.NamespaceObservationsPrefix));
    }
}
