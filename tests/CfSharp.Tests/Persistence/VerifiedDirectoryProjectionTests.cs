namespace CfSharp.Tests.Persistence;

public sealed class VerifiedDirectoryProjectionTests
{
    [Fact]
    public async Task CurrentTombstonesAndRevisionsSurvivePartialProjectionAndReceiptCannotRegressLaterPaths()
    {
        await using ICloudStateStore store = await InMemoryCloudStateStoreContractTests.CreateFactoryForTesting()
            .OpenAsync(new CloudStateStoreContext(Path.GetTempPath()));
        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Create("root");
        CloudDirectoryMoveProof proof = new(Guid.NewGuid(), Guid.NewGuid(), "Docs", "Moved", identity.ItemId,
            new CloudLocalFileBinding(1, Guid.NewGuid(), Guid.NewGuid()), identity.Encode());
        CloudItemState originalRoot = new(identity.ItemId, "root", "Docs", CloudItemKind.Directory, "old", 1, false, DateTimeOffset.UtcNow);
        CloudItemState originalChild = new(Guid.NewGuid(), "child", "Docs\\child", CloudItemKind.File, "old", 2, false, DateTimeOffset.UtcNow);
        CloudItemState sibling = new(Guid.NewGuid(), "sibling", "Docs2\\child", CloudItemKind.File, "sibling", 3, false, DateTimeOffset.UtcNow);
        IReadOnlyList<CloudDirectoryMember> members = CloudDirectoryMoveEvidence.DecodeMembers(
            CloudDirectoryMoveEvidence.EncodeMembers(proof, [originalRoot, originalChild]), proof.ProofId, proof.RootItemId);
        await using (ICloudStateTransaction seed = await store.BeginTransactionAsync())
        {
            await seed.Items.UpsertAsync(new(originalRoot.ItemId, originalRoot.RemoteId, "Moved", originalRoot.Kind,
                "root-current", 11, false, DateTimeOffset.UtcNow));
            await seed.Items.UpsertAsync(new(originalChild.ItemId, originalChild.RemoteId, originalChild.RelativePath,
                originalChild.Kind, "child-current", 22, true, DateTimeOffset.UtcNow));
            await seed.Items.UpsertAsync(sibling);
            await seed.CommitAsync();
        }

        await using (ICloudStateTransaction rollback = await store.BeginTransactionAsync())
        {
            VerifiedDirectoryProjection projection = await CloudDirectoryStateProjection.ProjectVerifiedAsync(rollback, proof,
                members, DateTimeOffset.UtcNow, default);
            Assert.Equal(1, projection.UpdatedCount);
            Assert.False(projection.AlreadyCompleted);
        }

        await using (ICloudStateTransaction project = await store.BeginTransactionAsync())
        {
            Assert.Equal("Docs\\child", (await project.Items.GetByItemIdAsync(originalChild.ItemId))!.RelativePath);
            Assert.Null(await project.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ReceiptName(proof.ProofId)));
            await CloudDirectoryStateProjection.ProjectVerifiedAsync(project, proof, members, DateTimeOffset.UtcNow, default);
            await project.CommitAsync();
        }

        await using (ICloudStateTransaction duplicate = await store.BeginTransactionAsync())
        {
            VerifiedDirectoryProjection projection = await CloudDirectoryStateProjection.ProjectVerifiedAsync(duplicate, proof,
                members, DateTimeOffset.UtcNow, default);
            Assert.True(projection.AlreadyCompleted);
            Assert.Equal(0, projection.UpdatedCount);
            CloudItemState child = (await duplicate.Items.GetByItemIdAsync(originalChild.ItemId))!;
            Assert.Equal("Moved\\child", child.RelativePath);
            Assert.Equal("child-current", child.RemoteRevision);
            Assert.Equal(22, child.LocalFileId);
            Assert.True(child.IsTombstone);
            Assert.Equal(sibling.RelativePath, (await duplicate.Items.GetByItemIdAsync(sibling.ItemId))!.RelativePath);
            await duplicate.Items.UpsertAsync(new(originalRoot.ItemId, originalRoot.RemoteId, "Later", originalRoot.Kind,
                "later", 33, false, DateTimeOffset.UtcNow));
            await duplicate.CommitAsync();
        }

        await using ICloudStateTransaction late = await store.BeginTransactionAsync();
        await Assert.ThrowsAsync<CloudDirectoryProjectionConflictException>(() => CloudDirectoryStateProjection.ProjectVerifiedAsync(
            late, proof, members, DateTimeOffset.UtcNow, default).AsTask());
        Assert.Equal("Later", (await late.Items.GetByItemIdAsync(proof.RootItemId))!.RelativePath);
        Assert.Equal(proof.Encode(), (await late.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ReceiptName(proof.ProofId)))!.Value.ToArray());
    }

    [Theory]
    [InlineData("Docs\\unproven")]
    [InlineData("Moved\\unproven")]
    public async Task UnprovenMembershipFailsBeforeAnyWriteEvenWhenCallerCommits(string unprovenPath)
    {
        await using ICloudStateStore store = await InMemoryCloudStateStoreContractTests.CreateFactoryForTesting()
            .OpenAsync(new CloudStateStoreContext(Path.GetTempPath()));
        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Create("root");
        CloudDirectoryMoveProof proof = new(Guid.NewGuid(), Guid.NewGuid(), "Docs", "Moved", identity.ItemId,
            new CloudLocalFileBinding(1, Guid.NewGuid(), Guid.NewGuid()), identity.Encode());
        CloudItemState root = new(identity.ItemId, "root", "Docs", CloudItemKind.Directory, "old", 1, false, DateTimeOffset.UtcNow);
        Guid unrelated = Guid.NewGuid();
        await using (ICloudStateTransaction seed = await store.BeginTransactionAsync())
        {
            await seed.Items.UpsertAsync(root);
            await seed.Items.UpsertAsync(new(unrelated, "unproven", unprovenPath, CloudItemKind.File, "unproven", 2, true, DateTimeOffset.UtcNow));
            await seed.CommitAsync();
        }

        await using (ICloudStateTransaction transaction = await store.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<CloudDirectoryProjectionConflictException>(() => CloudDirectoryStateProjection.ProjectVerifiedAsync(
                transaction, proof, [new(root.ItemId, root.Kind, "")], DateTimeOffset.UtcNow, default).AsTask());
            await transaction.CommitAsync();
        }

        await using ICloudStateTransaction verify = await store.BeginTransactionAsync();
        Assert.Equal("Docs", (await verify.Items.GetByItemIdAsync(root.ItemId))!.RelativePath);
        Assert.Equal(unprovenPath, (await verify.Items.GetByItemIdAsync(unrelated))!.RelativePath);
        Assert.Null(await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ReceiptName(proof.ProofId)));
    }
}
