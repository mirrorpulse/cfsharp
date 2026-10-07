using System.Runtime.Versioning;

using CfSharp.Tests.Persistence;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class CloudDirectoryMoveTests
{
    [Fact]
    public async Task BoundedUtf8HistoryPreservesLegacyManagedDirectoryConversionAndMove()
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudDirectoryMoveProof original = await root.FileSystem.GetDirectory("Docs").PrepareMoveAsync(root.FileSystem.Root, "Other");
        CloudDirectory source = root.FileSystem.GetDirectory("Ordinary");
        string suffix = string.Join('\\', Enumerable.Repeat(new string('文', 250), 45));
        Guid deletedId = Guid.NewGuid();
        DateTimeOffset deletedAt = DateTimeOffset.UtcNow;
        await using (ICloudStateTransaction seed = await root.Store.BeginTransactionAsync())
        {
            // Official historical rows can retain paths outside the bounded recovery format,
            // independently of the current machine's native long-path support.
            await seed.Items.UpsertAsync(new(deletedId, "deleted", Path.Combine("Ordinary", suffix),
                CloudItemKind.File, "deleted-revision", 42, true, deletedAt));
            await seed.CommitAsync();
        }

        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Create("ordinary-directory");
        await source.ConvertToPlaceholderAsync(identity);
        await Assert.ThrowsAsync<CloudDirectoryEvidenceUnavailableException>(() => source.PrepareMoveAsync(root.FileSystem.Root, "Moved").AsTask());
        CloudItemMoveResult moved = await source.MoveToAsync(root.FileSystem.Root, "Moved");
        Assert.Null(moved.DirectoryReconciliation);
        Assert.Equal(identity.ItemId, moved.Snapshot.ItemId);
        Assert.False(Directory.Exists(source.FullPath));
        Assert.True(Directory.Exists(Path.Combine(root.RootPath, "Moved")));
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        Assert.Equal("Moved", (await verify.Items.GetByItemIdAsync(identity.ItemId))!.RelativePath);
        CloudItemState deleted = (await verify.Items.GetByItemIdAsync(deletedId))!;
        Assert.Equal(Path.Combine("Moved", suffix), deleted.RelativePath);
        Assert.True(deleted.IsTombstone);
        Assert.Equal("deleted-revision", deleted.RemoteRevision);
        Assert.Equal(42, deleted.LocalFileId);
        Assert.True(deleted.UpdatedAt >= deletedAt);
        Assert.Null(await verify.Checkpoints.GetAsync(CloudDirectoryProvenance.BindingName(identity.ItemId)));
        Assert.Null(await verify.Checkpoints.GetAsync(CloudDirectoryProvenance.MembersName(identity.ItemId)));
        Assert.Equal(original.Encode(), (await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ProofName(original.ProofId)))!.Value.ToArray());
    }

    [Fact]
    public async Task PublishedPreviewThreeStateSurvivesDirectoryFeedProjectionProofReplayAndRuntimeRestart()
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync(legacyState: true);
        await using (ICloudStateTransaction before = await root.Store.BeginTransactionAsync())
        {
            await PreviewThreeStateFixture.AssertRetainedAsync(before);
        }

        CloudDirectory source = root.FileSystem.GetDirectory("Docs");
        CloudDirectoryMoveProof proof = await source.PrepareMoveAsync(root.FileSystem.Root, "Moved");
        Directory.Move(source.FullPath, Path.Combine(root.RootPath, "Moved"));
        await ProcessRenameAsync(root, "Docs", "Moved");
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.AlreadyProjected, (await source.ReconcileMoveAsync(proof)).Outcome);
        await root.RestartAsync();
        CloudItemMoveResult replay = await root.FileSystem.GetDirectory("Docs").MoveToAsync(root.FileSystem.Root, "Moved", new CloudMoveOptions(proof));
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.AlreadyProjected, replay.DirectoryReconciliation!.Outcome);
        Assert.False(replay.DirectoryReconciliation.RequiresFullRescan);
        await using (ICloudStateTransaction after = await root.Store.BeginTransactionAsync())
        {
            await PreviewThreeStateFixture.AssertRetainedAsync(after, "Moved");
            Assert.NotNull(await after.Items.GetByRelativePathAsync("Moved\\child.txt"));
        }

        await using CloudLocalChangeFeed feed = CloudLocalChangeFeed.CreateForTesting(root.RootPath, root.Store, new(), new FeedSource());
        await feed.StartAsync();
        CloudLocalChangeScan scan = await feed.BeginScanAsync();
        Assert.False(scan.RequiresFullRescan);
        CloudLocalChangeJournalException foreign = await Assert.ThrowsAsync<CloudLocalChangeJournalException>(() => feed.ReadPageAsync(scan, 0, 1).AsTask());
        Assert.Equal(PreviewThreeStateFixture.OperationId, foreign.OperationId);
        CloudLocalChange move = Assert.Single((await feed.ReadPageAsync(scan, 1, 1)).Changes);
        Assert.Equal(CloudLocalChangeKind.Move, move.Kind);
        Assert.Equal("Docs", move.PreviousRelativePath);
        Assert.Equal("Moved", move.RelativePath);
        await using ICloudStateTransaction retained = await root.Store.BeginTransactionAsync();
        Assert.NotNull(await retained.Operations.GetAsync(PreviewThreeStateFixture.OperationId));
        Assert.NotNull(await retained.Operations.GetAsync(move.OperationId));
    }
}
