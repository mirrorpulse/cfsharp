namespace CfSharp.Tests.Persistence;

public sealed class CloudDirectoryStateProjectionTests
{
    [Fact]
    public async Task ProjectionRetainsFieldsAndJournalFactsAndObeysCallerRollback()
    {
        await using ICloudStateStore store = await InMemoryCloudStateStoreContractTests.CreateFactoryForTesting()
            .OpenAsync(new CloudStateStoreContext(Path.GetTempPath()));
        CloudItemState directory = new(Guid.NewGuid(), "dir", "Docs", CloudItemKind.Directory,
            "directory-revision", 11, false, DateTimeOffset.UtcNow);
        CloudItemState child = new(Guid.NewGuid(), "child", "Docs\\Deep\\child.txt", CloudItemKind.File,
            "child-revision", 12, true, DateTimeOffset.UtcNow);
        CloudItemState sibling = new(Guid.NewGuid(), "sibling", "Docs2\\sibling.txt", CloudItemKind.File,
            "sibling-revision", 13, false, DateTimeOffset.UtcNow);
        CloudOperationJournalEntry operation;
        await using (ICloudStateTransaction seed = await store.BeginTransactionAsync())
        {
            foreach (CloudItemState item in new[] { directory, child, sibling })
            {
                await seed.Items.UpsertAsync(item);
            }

            operation = await seed.Operations.EnqueueAsync(new CloudOperationJournalEntry(
                Guid.NewGuid(), CloudStateOperationKind.Move, directory.ItemId, [1, 2, 3], DateTimeOffset.UtcNow));
            await seed.CommitAsync();
        }

        DateTimeOffset updatedAt = DateTimeOffset.UtcNow.AddSeconds(1);
        await using (ICloudStateTransaction rollback = await store.BeginTransactionAsync())
        {
            await CloudDirectoryStateProjection.ProjectAsync(rollback, [directory, child], "Docs", "Moved", updatedAt, default);
        }

        await using (ICloudStateTransaction project = await store.BeginTransactionAsync())
        {
            Assert.Equal("Docs", (await project.Items.GetByItemIdAsync(directory.ItemId))!.RelativePath);
            await CloudDirectoryStateProjection.ProjectAsync(project, [directory, child], "Docs", "Moved", updatedAt, default);
            await project.CommitAsync();
        }

        await using ICloudStateTransaction verify = await store.BeginTransactionAsync();
        CloudItemState movedChild = (await verify.Items.GetByItemIdAsync(child.ItemId))!;
        Assert.Equal("Moved\\Deep\\child.txt", movedChild.RelativePath);
        Assert.Equal(child.RemoteId, movedChild.RemoteId);
        Assert.Equal(child.RemoteRevision, movedChild.RemoteRevision);
        Assert.Equal(child.LocalFileId, movedChild.LocalFileId);
        Assert.True(movedChild.IsTombstone);
        Assert.Equal(updatedAt, movedChild.UpdatedAt);
        Assert.Equal(sibling.RelativePath, (await verify.Items.GetByItemIdAsync(sibling.ItemId))!.RelativePath);
        CloudOperationJournalEntry retained = (await verify.Operations.GetAsync(operation.OperationId))!;
        Assert.Equal(operation.Sequence, retained.Sequence);
        Assert.Equal(operation.Payload.ToArray(), retained.Payload.ToArray());
        Assert.Equal(operation.CreatedAt, retained.CreatedAt);
    }

    [Fact]
    public void MappingHonorsSegmentBoundariesAndCaseOnlyRenames()
    {
        Assert.Equal("docs", CloudDirectoryStateProjection.MapPath("Docs", "Docs", "docs"));
        Assert.Equal("Moved\\a\\b.txt", CloudDirectoryStateProjection.MapPath("DOCS\\a\\b.txt", "Docs", "Moved"));
        Assert.Throws<ArgumentException>(() => CloudDirectoryStateProjection.MapPath("Docs2\\a.txt", "Docs", "Moved"));
    }
}
