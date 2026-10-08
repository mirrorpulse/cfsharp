using System.Runtime.Versioning;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class CloudDirectoryMoveTests
{
    [Theory]
    [InlineData("automatic")]
    [InlineData("prepared")]
    [InlineData("feed")]
    [InlineData("retry")]
    public async Task DirectoryMoveUsesNativeParentSpellingAndPreservesRequestedChildCase(string mode)
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        Directory.CreateDirectory(Path.Combine(root.RootPath, "Parent"));
        CloudDirectory source = root.FileSystem.GetDirectory("Docs");
        CloudDirectory destination = root.FileSystem.GetDirectory("parent");
        CloudDirectoryMoveProof? proof = null;
        CloudDirectoryMoveReconciliationResult recovery;
        if (mode == "feed")
        {
            proof = await source.PrepareMoveAsync(destination, "Moved");
            Directory.Move(source.FullPath, Path.Combine(root.RootPath, "Parent", "Moved"));
            await ProcessRenameAsync(root, "Docs", "Parent\\Moved");
            recovery = await source.ReconcileMoveAsync(proof);
            Assert.Equal(CloudDirectoryMoveReconciliationOutcome.AlreadyProjected, recovery.Outcome);
        }
        else
        {
            if (mode == "prepared")
            {
                proof = await source.PrepareMoveAsync(destination, "Moved");
            }

            if (mode == "retry")
            {
                IOException storageError = new("Projection commit fault.");
                int commits = 0;
                root.Faults.BeforeCommit = () =>
                {
                    if (++commits == 2)
                    {
                        throw storageError;
                    }
                };
                try
                {
                    CloudItemCoordinationException failed = await Assert.ThrowsAsync<CloudItemCoordinationException>(
                        () => source.MoveToAsync(destination, "Moved").AsTask());
                    Assert.Same(storageError, failed.InnerException);
                    Assert.True(failed.DirectoryReconciliation!.NativeMoveObserved);
                    Assert.False(failed.DirectoryReconciliation.DurableProjectionCommitted);
                }
                finally
                {
                    root.Faults.BeforeCommit = null;
                }

                destination = root.FileSystem.GetDirectory("PARENT");
            }

            CloudItemMoveResult result = await source.MoveToAsync(destination, "Moved",
                proof is null ? CloudMoveOptions.Default : new CloudMoveOptions(proof));
            Assert.Equal("Parent\\Moved", result.Item.RelativePath);
            recovery = result.DirectoryReconciliation!;
            Assert.Equal(CloudDirectoryMoveReconciliationOutcome.Projected, recovery.Outcome);
        }

        Assert.True(recovery.DurableProjectionCommitted);
        Assert.False(recovery.RequiresFullRescan);
        Assert.False(Directory.Exists(source.FullPath));
        Assert.True(Directory.Exists(Path.Combine(root.RootPath, "Parent", "Moved")));
        await using (ICloudStateTransaction verify = await root.Store.BeginTransactionAsync())
        {
            proof ??= CloudDirectoryMoveProof.Decode((await verify.Checkpoints.GetAsync(
                CloudDirectoryMoveEvidence.IntentName("Docs", "Parent\\Moved")))!.Value.Span);
            Assert.Equal("Parent\\Moved", proof.DestinationRelativePath);
            Assert.Equal(proof.Encode(), (await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ReceiptName(proof.ProofId)))!.Value.ToArray());
            Assert.Equal("Parent\\Moved", (await verify.Items.GetByItemIdAsync(root.DirectoryIdentity.ItemId))!.RelativePath);
            Assert.NotNull(await verify.Items.GetByRelativePathAsync("Parent\\Moved\\child.txt"));
        }

        // Canonicalizing the parent must not canonicalize away an intended case-only leaf rename.
        CloudItemMoveResult renamed = await root.FileSystem.GetDirectory("Parent/Moved")
            .MoveToAsync(root.FileSystem.GetDirectory("PARENT"), "moved");
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.Projected, renamed.DirectoryReconciliation!.Outcome);
        Assert.Equal("Parent\\moved", renamed.Item.RelativePath);
        await using (ICloudStateTransaction after = await root.Store.BeginTransactionAsync())
        {
            Assert.Equal("Parent\\moved", (await after.Items.GetByItemIdAsync(root.DirectoryIdentity.ItemId))!.RelativePath);
            Assert.Equal("Parent\\moved\\child.txt", (await after.Items.GetByRemoteIdAsync("child"))!.RelativePath);
        }

        CloudItemMoveResult unchanged = await root.FileSystem.GetDirectory("parent/moved")
            .MoveToAsync(root.FileSystem.GetDirectory("PARENT"), "moved");
        Assert.Equal(0, unchanged.DurableStateEntriesUpdated);
        Assert.Null(unchanged.DirectoryReconciliation);
        CloudItemMoveResult fromAlias = await root.FileSystem.GetDirectory("PARENT/MOVED")
            .MoveToAsync(destination, "MOVED");
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.Projected, fromAlias.DirectoryReconciliation!.Outcome);
        Assert.Equal("Parent\\MOVED", fromAlias.Item.RelativePath);
    }

    [Fact]
    public async Task CanonicalParentDoesNotPermitAnotherFinalNameSpelling()
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        Directory.CreateDirectory(Path.Combine(root.RootPath, "Parent"));
        CloudDirectory source = root.FileSystem.GetDirectory("Docs");
        CloudDirectory destination = root.FileSystem.GetDirectory("parent");
        CloudDirectoryMoveProof proof = await source.PrepareMoveAsync(destination, "moved");
        await Assert.ThrowsAsync<ArgumentException>(() => source.MoveToAsync(destination, "MOVED", new CloudMoveOptions(proof)).AsTask());
        Assert.True(Directory.Exists(source.FullPath));
        Directory.Move(source.FullPath, Path.Combine(root.RootPath, "Parent", "MOVED"));
        CloudDirectoryMoveReconciliationResult result = await source.ReconcileMoveAsync(proof);
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.Conflict, result.Outcome);
        Assert.True(result.RequiresFullRescan);
        Assert.False(result.DurableProjectionCommitted);
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        Assert.Equal("Docs", (await verify.Items.GetByItemIdAsync(root.DirectoryIdentity.ItemId))!.RelativePath);
        Assert.NotNull(await verify.Items.GetByRelativePathAsync("Docs\\child.txt"));
        Assert.Null(await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ReceiptName(proof.ProofId)));
    }
}
