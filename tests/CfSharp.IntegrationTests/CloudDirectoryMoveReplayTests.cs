using System.Runtime.Versioning;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class CloudDirectoryMoveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedPreRenameBindingRecoversWithoutCallerPreparation(bool rootAlreadyProjected)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return;
        }

        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudDirectory source = root.FileSystem.GetDirectory("Docs");
        Directory.Move(source.FullPath, Path.Combine(root.RootPath, "Moved"));
        if (rootAlreadyProjected)
        {
            await using ICloudStateTransaction transaction = await root.Store.BeginTransactionAsync();
            CloudItemState original = (await transaction.Items.GetByItemIdAsync(root.DirectoryIdentity.ItemId))!;
            await transaction.Items.UpsertAsync(new(original.ItemId, original.RemoteId, "Moved", original.Kind,
                "current", original.LocalFileId, false, DateTimeOffset.UtcNow));
            await transaction.CommitAsync();
        }

        CloudItemMoveResult recovered = await source.MoveToAsync(root.FileSystem.Root, "Moved");
        Assert.Equal(rootAlreadyProjected ? 1 : 2, recovered.DurableStateEntriesUpdated);
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.Projected, recovered.DirectoryReconciliation!.Outcome);
        Assert.True(recovered.DirectoryReconciliation.NativeMoveObserved);
        Assert.False(Directory.Exists(source.FullPath));
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        Assert.NotNull(await verify.Items.GetByRelativePathAsync("Moved\\child.txt"));
    }

    [Fact]
    public async Task RemoteDirectoryMoveKeepsItsExistingRevisionAndSubtreeBehavior()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return;
        }

        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        await root.FileSystem.GetDirectory("Docs").UpdatePlaceholderAsync(
            CloudPlaceholderPatch.CreateBuilder().WithInSyncState(true).Build());
        CloudRemoteChange change = new("move-directory", CloudRemoteChangeKind.Move, "directory", "r2",
            CloudItemKind.Directory, "Moved", itemId: root.DirectoryIdentity.ItemId, previousRelativePath: "Docs");
        CloudRemoteApplyResult result = await root.FileSystem.ApplyRemoteChangesAsync(
            new CloudRemoteChangeBatch("directory-move-batch", ReadOnlyMemory<byte>.Empty, [change], new byte[] { 1 }));
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single(result.Entries).Status);
        Assert.Equal(new byte[] { 1 }, result.SafeCursor.ToArray());
        Assert.False(Directory.Exists(Path.Combine(root.RootPath, "Docs")));
        Assert.True(Directory.Exists(Path.Combine(root.RootPath, "Moved")));
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        CloudItemState directory = (await verify.Items.GetByItemIdAsync(root.DirectoryIdentity.ItemId))!;
        Assert.Equal("Moved", directory.RelativePath);
        Assert.Equal("r2", directory.RemoteRevision);
        Assert.NotNull(await verify.Items.GetByRelativePathAsync("Moved\\child.txt"));
    }

    [Fact]
    public async Task ManagedMoveAutomaticallyPreparesAndRetriesWithoutAnotherNativeMove()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return;
        }

        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudDirectory source = root.FileSystem.GetDirectory("Docs");
        int commits = 0;
        IOException error = new("Projection commit fault.");
        root.Faults.BeforeCommit = () =>
        {
            if (++commits == 2)
            {
                throw error;
            }
        };
        CloudItemCoordinationException failed = await Assert.ThrowsAsync<CloudItemCoordinationException>(() =>
            source.MoveToAsync(root.FileSystem.Root, "Moved").AsTask());
        Assert.Same(error, failed.InnerException);
        Assert.True(failed.DirectoryReconciliation!.NativeMoveObserved);
        Assert.False(failed.DirectoryReconciliation.DurableProjectionCommitted);
        Assert.Equal(Path.Combine(root.RootPath, "Moved"), failed.DestinationPath);
        Assert.False(Directory.Exists(source.FullPath));
        root.Faults.BeforeCommit = null;
        CloudItemMoveResult recovered = await source.MoveToAsync(root.FileSystem.Root, "Moved");
        Assert.Equal(2, recovered.DurableStateEntriesUpdated);
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.Projected, recovered.DirectoryReconciliation!.Outcome);
        CloudItemMoveResult duplicate = await source.MoveToAsync(root.FileSystem.Root, "Moved");
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.AlreadyProjected, duplicate.DirectoryReconciliation!.Outcome);
        Assert.Equal(0, duplicate.DurableStateEntriesUpdated);
        Assert.Equal(root.DirectoryIdentity.ItemId, recovered.Snapshot.ItemId);
    }

    [Fact]
    public async Task ExplicitProofRecoversExternallyMovedPartialRootAndRejectsAnotherDestination()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return;
        }

        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudDirectory source = root.FileSystem.GetDirectory("Docs");
        CloudDirectoryMoveProof proof = await source.PrepareMoveAsync(root.FileSystem.Root, "Moved");
        await Assert.ThrowsAsync<ArgumentException>(() => source.MoveToAsync(root.FileSystem.Root, "Other", new CloudMoveOptions(proof)).AsTask());
        Directory.Move(source.FullPath, Path.Combine(root.RootPath, "Moved"));
        await using (ICloudStateTransaction transaction = await root.Store.BeginTransactionAsync())
        {
            CloudItemState original = (await transaction.Items.GetByItemIdAsync(proof.RootItemId))!;
            await transaction.Items.UpsertAsync(new(original.ItemId, original.RemoteId, "Moved", original.Kind,
                "current", original.LocalFileId, false, DateTimeOffset.UtcNow));
            await transaction.CommitAsync();
        }

        CloudItemMoveResult result = await source.MoveToAsync(root.FileSystem.Root, "Moved", new CloudMoveOptions(proof));
        Assert.Equal(1, result.DurableStateEntriesUpdated);
        Assert.Equal("current", result.Snapshot.RemoteRevision);
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.Projected, result.DirectoryReconciliation!.Outcome);
    }

    [Fact]
    public async Task MissingProofAndReplacedTargetCannotUseLegacyDirectoryGuessing()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return;
        }

        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudDirectory source = root.FileSystem.GetDirectory("Docs");
        // Simulate a legacy directory with no pre-rename native evidence. Durable paths alone
        // are insufficient, even when the destination has a valid placeholder identity.
        await using (ICloudStateTransaction legacy = await root.Store.BeginTransactionAsync())
        {
            await legacy.Checkpoints.RemoveAsync(CloudDirectoryProvenance.BindingName(root.DirectoryIdentity.ItemId));
            await legacy.Checkpoints.RemoveAsync(CloudDirectoryProvenance.MembersName(root.DirectoryIdentity.ItemId));
            await legacy.CommitAsync();
        }

        Directory.Move(source.FullPath, Path.Combine(root.RootPath, "Moved"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => source.MoveToAsync(root.FileSystem.Root, "Moved").AsTask());
        Directory.Move(Path.Combine(root.RootPath, "Moved"), source.FullPath);
        CloudDirectoryMoveProof proof = await source.PrepareMoveAsync(root.FileSystem.Root, "Moved");
        Directory.Move(source.FullPath, Path.Combine(root.RootPath, "Original"));
        await root.FileSystem.Root.CreatePlaceholderAsync(CloudDirectoryPlaceholderSpec.CreateBuilder("Moved", "unrelated")
            .WithPopulationState(CloudDirectoryPopulationState.Complete).Build());
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.MoveToAsync(root.FileSystem.Root, "Moved", new CloudMoveOptions(proof)).AsTask());
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        Assert.Equal("Docs", (await verify.Items.GetByItemIdAsync(proof.RootItemId))!.RelativePath);
        Assert.Equal("unrelated", (await verify.Items.GetByRelativePathAsync("Moved"))!.RemoteId);
        Assert.Null(await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ReceiptName(proof.ProofId)));
    }

    [Fact]
    public async Task UnsupportedDurableProtocolNeverFallsBackToUnverifiedNativeMovement()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return;
        }

        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        await using (ICloudStateTransaction transaction = await root.Store.BeginTransactionAsync())
        {
            byte[] future = new byte[20];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(future, 2);
            await transaction.Checkpoints.UpsertAsync(new(CloudDirectoryMoveEvidence.ScopeName, future, DateTimeOffset.UtcNow));
            await transaction.CommitAsync();
        }

        await Assert.ThrowsAsync<NotSupportedException>(() => root.FileSystem.GetDirectory("Docs")
            .MoveToAsync(root.FileSystem.Root, "Moved").AsTask());
        Assert.True(Directory.Exists(Path.Combine(root.RootPath, "Docs")));
        Assert.False(Directory.Exists(Path.Combine(root.RootPath, "Moved")));
    }
}
