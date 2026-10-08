using System.ComponentModel;
using System.Runtime.Versioning;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class CloudDirectoryMoveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconciliationCancellationReportsTheActualNativeAndCommitStage(bool duringProjection)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return;
        }

        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudDirectory source = root.FileSystem.GetDirectory("Docs");
        CloudDirectoryMoveProof proof = await source.PrepareMoveAsync(root.FileSystem.Root, "Moved");
        using CancellationTokenSource cancellation = new();
        if (duringProjection)
        {
            Directory.Move(source.FullPath, Path.Combine(root.RootPath, "Moved"));
            root.Faults.BeforeCommit = cancellation.Cancel;
        }
        else
        {
            cancellation.Cancel();
        }

        CloudDirectoryMoveReconciliationResult canceled = await source.ReconcileMoveAsync(proof, cancellation.Token);
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.Canceled, canceled.Outcome);
        Assert.Equal(duringProjection ? CloudDirectoryMoveReconciliationStage.DurableProjection :
            CloudDirectoryMoveReconciliationStage.Acquisition, canceled.Stage);
        Assert.Equal(duringProjection, canceled.NativeMoveObserved);
        Assert.False(canceled.DurableProjectionCommitted);
        Assert.IsAssignableFrom<OperationCanceledException>(canceled.Error);
        Assert.Null(canceled.NativeHResult);
        root.Faults.BeforeCommit = null;
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        Assert.Equal("Docs", (await verify.Items.GetByItemIdAsync(proof.RootItemId))!.RelativePath);
        Assert.Null(await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ReceiptName(proof.ProofId)));
    }

    [Fact]
    public async Task ReconcileProjectsCurrentFieldsPartialRootAndReceiptWithoutTouchingPendingJournal()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return;
        }

        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudDirectory source = root.FileSystem.GetDirectory("Docs");
        CloudItemState child;
        CloudOperationJournalEntry pending;
        await using (ICloudStateTransaction transaction = await root.Store.BeginTransactionAsync())
        {
            child = (await transaction.Items.GetByRelativePathAsync("Docs\\child.txt"))!;
            pending = await transaction.Operations.EnqueueAsync(new(Guid.NewGuid(), CloudStateOperationKind.ContentUpdate,
                child.ItemId, new LocalChangePayload("Docs\\child.txt", null, false, DateTimeOffset.UtcNow).Encode(), DateTimeOffset.UtcNow));
            await transaction.CommitAsync();
        }

        CloudDirectoryMoveProof proof = await source.PrepareMoveAsync(root.FileSystem.Root, "Moved");
        CloudDirectoryMoveReconciliationResult notMoved = await source.ReconcileMoveAsync(proof);
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.NotMoved, notMoved.Outcome);
        Assert.False(notMoved.NativeMoveObserved);
        Assert.False(notMoved.DurableProjectionCommitted);
        Directory.Move(source.FullPath, Path.Combine(root.RootPath, "Moved"));
        await using (ICloudStateTransaction transaction = await root.Store.BeginTransactionAsync())
        {
            CloudItemState originalRoot = (await transaction.Items.GetByItemIdAsync(proof.RootItemId))!;
            await transaction.Items.UpsertAsync(new(originalRoot.ItemId, originalRoot.RemoteId, "Moved", originalRoot.Kind,
                "root-current", 55, false, DateTimeOffset.UtcNow));
            await transaction.Items.UpsertAsync(new(child.ItemId, child.RemoteId, child.RelativePath, child.Kind,
                "child-current", 66, false, DateTimeOffset.UtcNow));
            await transaction.CommitAsync();
        }

        CloudDirectoryMoveReconciliationResult projected = await source.ReconcileMoveAsync(CloudDirectoryMoveProof.Decode(proof.Encode()));
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.Projected, projected.Outcome);
        Assert.True(projected.NativeMoveObserved);
        Assert.True(projected.DurableProjectionCommitted);
        Assert.False(projected.RequiresFullRescan);
        Assert.Equal(1, projected.DurableStateEntriesUpdated);
        Assert.Null(projected.Error);
        Assert.Null(projected.NativeHResult);
        CloudDirectoryMoveReconciliationResult duplicate = await source.ReconcileMoveAsync(proof);
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.AlreadyProjected, duplicate.Outcome);
        Assert.True(duplicate.DurableProjectionCommitted);
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        CloudItemState movedChild = (await verify.Items.GetByItemIdAsync(child.ItemId))!;
        Assert.Equal("Moved\\child.txt", movedChild.RelativePath);
        Assert.Equal("child-current", movedChild.RemoteRevision);
        Assert.Equal(66, movedChild.LocalFileId);
        CloudItemState movedRoot = (await verify.Items.GetByItemIdAsync(proof.RootItemId))!;
        Assert.Equal("root-current", movedRoot.RemoteRevision);
        Assert.Equal(55, movedRoot.LocalFileId);
        CloudOperationJournalEntry retained = (await verify.Operations.GetAsync(pending.OperationId))!;
        Assert.Equal(pending.Sequence, retained.Sequence);
        Assert.Equal(pending.Payload.ToArray(), retained.Payload.ToArray());
        Assert.Equal(pending.CreatedAt, retained.CreatedAt);
        Assert.Equal(proof.Encode(), (await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ReceiptName(proof.ProofId)))!.Value.ToArray());
        Assert.Equal("Moved", CloudDirectoryProvenance.Decode((await verify.Checkpoints.GetAsync(
            CloudDirectoryProvenance.BindingName(proof.RootItemId)))!.Value).RelativePath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectionFailureRetainsNativeFactsAndOriginalErrorWithoutFakeNativeHResult(bool win32StorageError)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return;
        }

        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudDirectory source = root.FileSystem.GetDirectory("Docs");
        CloudDirectoryMoveProof proof = await source.PrepareMoveAsync(root.FileSystem.Root, "Moved");
        Directory.Move(source.FullPath, Path.Combine(root.RootPath, "Moved"));
        Exception error = win32StorageError ? new Win32Exception(5, "Storage commit fault.") : new IOException("Storage commit fault.");
        root.Faults.BeforeCommit = () => throw error;
        CloudDirectoryMoveReconciliationResult failed = await source.ReconcileMoveAsync(proof);
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.NativeObservedProjectionPending, failed.Outcome);
        Assert.Equal(CloudDirectoryMoveReconciliationStage.DurableProjection, failed.Stage);
        Assert.True(failed.NativeMoveObserved);
        Assert.False(failed.DurableProjectionCommitted);
        Assert.Same(error, failed.Error);
        Assert.Null(failed.NativeHResult);
        root.Faults.BeforeCommit = null;
        await using (ICloudStateTransaction verify = await root.Store.BeginTransactionAsync())
        {
            Assert.Equal("Docs", (await verify.Items.GetByItemIdAsync(proof.RootItemId))!.RelativePath);
            Assert.Null(await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ReceiptName(proof.ProofId)));
        }

        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.Projected, (await source.ReconcileMoveAsync(proof)).Outcome);
        Assert.False(Directory.Exists(source.FullPath));
    }

    [Fact]
    public async Task ForeignForgedAndUnrelatedTargetStateFailClosedWithoutDeletingRows()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return;
        }

        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        await using DirectoryMoveTestRoot foreign = await DirectoryMoveTestRoot.OpenAsync();
        CloudDirectory source = root.FileSystem.GetDirectory("Docs");
        CloudDirectoryMoveProof proof = await source.PrepareMoveAsync(root.FileSystem.Root, "Moved");
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.NotApplicable,
            (await foreign.FileSystem.GetDirectory("Docs").ReconcileMoveAsync(proof)).Outcome);
        CloudDirectoryMoveProof forged = new(Guid.NewGuid(), proof.StoreScope, proof.SourceRelativePath, proof.DestinationRelativePath,
            proof.RootItemId, proof.ExpectedBinding, proof.ExpectedPlaceholderIdentity.Span);
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.NotApplicable,
            (await source.ReconcileMoveAsync(CloudDirectoryMoveProof.Decode(forged.Encode()))).Outcome);
        Directory.Move(source.FullPath, Path.Combine(root.RootPath, "Moved"));
        Guid unrelated = Guid.NewGuid();
        await using (ICloudStateTransaction seed = await root.Store.BeginTransactionAsync())
        {
            await seed.Items.UpsertAsync(new(unrelated, "unrelated", "Moved\\unrelated", CloudItemKind.File,
                "untouched", 91, true, DateTimeOffset.UtcNow));
            await seed.CommitAsync();
        }

        CloudDirectoryMoveReconciliationResult conflict = await source.ReconcileMoveAsync(proof);
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.Conflict, conflict.Outcome);
        Assert.True(conflict.NativeMoveObserved);
        Assert.False(conflict.DurableProjectionCommitted);
        Assert.True(conflict.RequiresFullRescan);
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        Assert.NotNull(await verify.Items.GetByItemIdAsync(unrelated));
        Assert.Equal("Docs", (await verify.Items.GetByItemIdAsync(proof.RootItemId))!.RelativePath);
        Assert.Null(await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ReceiptName(proof.ProofId)));
    }

    [Fact]
    public async Task CaseOnlyRecoveryUsesActualNamespaceSpellingAndNoSecondNativeMove()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return;
        }

        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudDirectory source = root.FileSystem.GetDirectory("DOCS");
        CloudDirectoryMoveProof proof = await source.PrepareMoveAsync(root.FileSystem.Root, "docs");
        Assert.Equal("Docs", proof.SourceRelativePath);
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.NotMoved, (await source.ReconcileMoveAsync(proof)).Outcome);
        Directory.Move(Path.Combine(root.RootPath, "Docs"), Path.Combine(root.RootPath, "docs"));
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.Projected, (await source.ReconcileMoveAsync(proof)).Outcome);
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        Assert.Equal("docs", (await verify.Items.GetByItemIdAsync(proof.RootItemId))!.RelativePath);
        Assert.Equal("docs\\child.txt", (await verify.Items.GetByRelativePathAsync("docs\\child.txt"))!.RelativePath);
    }
}
