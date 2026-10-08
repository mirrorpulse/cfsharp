using System.Runtime.Versioning;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class CloudDirectoryMoveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutomaticPreparationCancellationPreventsNativeMoveAndRetainsProof(bool capabilityFallback)
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudOperationJournalEntry pending = await SeedPendingChildAsync(root);
        CloudDirectory source = root.FileSystem.GetDirectory("Docs");
        string destination = Path.Combine(root.RootPath, "Moved");
        CloudItemState[] originalRows;
        CloudStateCheckpoint[] originalLive;
        await using (ICloudStateTransaction read = await root.Store.BeginTransactionAsync())
        {
            originalRows = (await read.Items.ListSubtreeAsync("Docs")).ToArray();
            originalLive = (await read.Checkpoints.ListAsync("cfsharp/namespace/directories")).ToArray();
        }

        using CancellationTokenSource cancellation = new();
        bool reached = false;
        root.Faults.AfterCommit = () =>
        {
            root.Faults.AfterCommit = null;
            Assert.True(Directory.Exists(source.FullPath));
            Assert.False(Directory.Exists(destination));
            reached = true;
            cancellation.Cancel();
            if (capabilityFallback)
            {
                // Inject the exact capability exception at the preparation completion
                // boundary; native IDs and the committed SQLite proof remain real.
                throw new CloudDirectoryEvidenceUnavailableException("Simulated post-preparation capability loss.");
            }
        };
        try
        {
            OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                source.MoveToAsync(root.FileSystem.Root, "Moved", cancellationToken: cancellation.Token).AsTask());
            Assert.Equal(cancellation.Token, error.CancellationToken);
        }
        finally
        {
            root.Faults.AfterCommit = null;
        }

        Assert.True(reached);
        Assert.True(Directory.Exists(source.FullPath));
        Assert.False(Directory.Exists(destination));
        CloudDirectoryMoveProof proof = (await CloudDirectoryMoveEvidence.ReadIntentAsync(root.Store, "Docs", "Moved", CancellationToken.None))!;
        Assert.NotNull(proof);
        CloudStateCheckpoint[] preparation;
        await using (ICloudStateTransaction verify = await root.Store.BeginTransactionAsync())
        {
            foreach (CloudItemState row in originalRows)
            {
                CloudItemState current = (await verify.Items.GetByItemIdAsync(row.ItemId))!;
                Assert.Equal((row.RemoteId, row.RelativePath, row.Kind, row.RemoteRevision, row.LocalFileId, row.IsTombstone, row.UpdatedAt),
                    (current.RemoteId, current.RelativePath, current.Kind, current.RemoteRevision, current.LocalFileId, current.IsTombstone, current.UpdatedAt));
            }

            await AssertCheckpointImageAsync(verify, originalLive);
            preparation = (await verify.Checkpoints.ListAsync($"cfsharp/namespace/preparations/{proof.ProofId:N}")).ToArray();
            Assert.Equal(2, preparation.Length);
            Assert.Null(await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ReceiptName(proof.ProofId)));
            await AssertFileMoveJournalImageAsync(verify, pending);
            Assert.Single(await verify.Operations.ListAsync(100));
        }

        await root.RestartAsync();
        CloudItemMoveResult retry = await root.FileSystem.GetDirectory("Docs")
            .MoveToAsync(root.FileSystem.Root, "Moved", new CloudMoveOptions(proof));
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.Projected, retry.DirectoryReconciliation!.Outcome);
        await AssertProjectedChildAsync(root, "Moved", pending);
        await using ICloudStateTransaction after = await root.Store.BeginTransactionAsync();
        await AssertCheckpointImageAsync(after, preparation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterNativeMoveStillCommitsAutomaticOrFallbackProjection(bool capabilityFallback)
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudOperationJournalEntry pending = await SeedPendingChildAsync(root);
        CloudDirectory source = root.FileSystem.GetDirectory("Docs");
        string destination = Path.Combine(root.RootPath, "Moved");
        using CancellationTokenSource cancellation = new();
        int commitsStarted = 0;
        int commitsCompleted = 0;
        root.Faults.BeforeCommit = () =>
        {
            if (++commitsStarted == 2)
            {
                Assert.False(Directory.Exists(source.FullPath));
                Assert.True(Directory.Exists(destination));
                cancellation.Cancel();
            }
        };
        root.Faults.AfterCommit = () =>
        {
            if (++commitsCompleted == 1)
            {
                Assert.True(Directory.Exists(source.FullPath));
                Assert.False(Directory.Exists(destination));
                Assert.False(cancellation.IsCancellationRequested);
                if (capabilityFallback)
                {
                    throw new CloudDirectoryEvidenceUnavailableException("Simulated post-preparation capability loss.");
                }
            }
        };
        CloudItemMoveResult result;
        try
        {
            result = await source.MoveToAsync(root.FileSystem.Root, "Moved", cancellationToken: cancellation.Token);
        }
        finally
        {
            root.Faults.BeforeCommit = null;
            root.Faults.AfterCommit = null;
        }

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(2, commitsStarted);
        Assert.Equal(2, commitsCompleted);
        Assert.Equal(2, result.DurableStateEntriesUpdated);
        if (capabilityFallback)
        {
            Assert.Null(result.DirectoryReconciliation);
        }
        else
        {
            Assert.Equal(CloudDirectoryMoveReconciliationOutcome.Projected, result.DirectoryReconciliation!.Outcome);
            Assert.True(result.DirectoryReconciliation.DurableProjectionCommitted);
        }

        await root.RestartAsync();
        await AssertProjectedChildAsync(root, "Moved", pending);
    }
}
