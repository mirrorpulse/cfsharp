using System.Runtime.Versioning;

using CfSharp.Tests.Persistence;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class CloudDirectoryMoveTests
{
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
