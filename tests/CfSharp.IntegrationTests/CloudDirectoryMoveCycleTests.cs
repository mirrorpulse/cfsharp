using System.Runtime.Versioning;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class CloudDirectoryMoveTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RenameUndoAndRenameAgainUsesNewProofAndPreservesCompletedHistory(bool restart, bool caseOnly)
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudOperationJournalEntry original = await SeedPendingChildAsync(root);
        string destination = caseOnly ? "docs" : "Moved";
        Directory.Move(Path.Combine(root.RootPath, "Docs"), Path.Combine(root.RootPath, destination));
        await ProcessRenameAsync(root, "Docs", destination);
        CloudDirectoryMoveProof first;
        CloudStateCheckpoint[] history;
        await using (ICloudStateTransaction read = await root.Store.BeginTransactionAsync())
        {
            first = CloudDirectoryMoveProof.Decode((await read.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.IntentName("Docs", destination)))!.Value.Span);
            history = (await read.Checkpoints.ListAsync($"cfsharp/namespace/preparations/{first.ProofId:N}")).ToArray();
            Assert.Equal(3, history.Length);
        }

        // A duplicate completed observation must still replay its original proof, including
        // case-only paths whose source and destination compare equal on Windows.
        await ProcessRenameAsync(root, "Docs", destination);
        await using (ICloudStateTransaction read = await root.Store.BeginTransactionAsync())
        {
            Assert.Equal(first.ProofId, CloudDirectoryMoveProof.Decode((await read.Checkpoints.GetAsync(
                CloudDirectoryMoveEvidence.IntentName("Docs", destination)))!.Value.Span).ProofId);
            Assert.False(LocalChangeCheckpoint.Decode((await read.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName))!.Value).RequiresFullRescan);
        }

        Directory.Move(Path.Combine(root.RootPath, destination), Path.Combine(root.RootPath, "Docs"));
        await ProcessRenameAsync(root, destination, "Docs");
        await AssertProjectedChildAsync(root, "Docs", original);
        if (restart)
        {
            await root.RestartAsync();
        }

        Directory.Move(Path.Combine(root.RootPath, "Docs"), Path.Combine(root.RootPath, destination));
        // The immutable old receipt still cannot project a later namespace transition. Only
        // automatic intent selection from the latest retained provenance may prepare a new move.
        CloudDirectoryMoveReconciliationResult historical = await root.FileSystem.GetDirectory("Docs").ReconcileMoveAsync(first);
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.Conflict, historical.Outcome);
        Assert.True(historical.RequiresFullRescan);
        Assert.False(historical.DurableProjectionCommitted);
        await AssertProjectedChildAsync(root, "Docs", original);

        await ProcessRenameAsync(root, "Docs", destination);
        await AssertProjectedChildAsync(root, destination, original);
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        CloudDirectoryMoveProof latest = CloudDirectoryMoveProof.Decode((await verify.Checkpoints.GetAsync(
            CloudDirectoryMoveEvidence.IntentName("Docs", destination)))!.Value.Span);
        Assert.NotEqual(first.ProofId, latest.ProofId);
        Assert.Equal(first.ExpectedBinding, latest.ExpectedBinding);
        Assert.Equal(first.ExpectedPlaceholderIdentity.ToArray(), latest.ExpectedPlaceholderIdentity.ToArray());
        Assert.NotNull(await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ReceiptName(latest.ProofId)));
        foreach (CloudStateCheckpoint saved in history)
        {
            CloudStateCheckpoint retained = (await verify.Checkpoints.GetAsync(saved.Name))!;
            Assert.Equal(saved.Value.ToArray(), retained.Value.ToArray());
            Assert.Equal(saved.UpdatedAt, retained.UpdatedAt);
        }

        Assert.Empty(await verify.Checkpoints.ListAsync(CloudLocalChangeFeed.NamespaceObservationsPrefix));
        Assert.False(LocalChangeCheckpoint.Decode((await verify.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName))!.Value).RequiresFullRescan);
        CloudOperationJournalEntry[] moves = (await verify.Operations.ListAsync(100)).Where(row => row.Kind == CloudStateOperationKind.Move).ToArray();
        Assert.Equal(4, moves.Length);
        Assert.All(moves, move => Assert.Equal(root.DirectoryIdentity.ItemId, move.ItemId));
        Assert.Equal("Docs", LocalChangePayload.Decode(moves[0].Payload).PreviousRelativePath);
        Assert.Equal(destination, LocalChangePayload.Decode(moves[0].Payload).RelativePath);
    }
}
