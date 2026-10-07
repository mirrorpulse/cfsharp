using System.Diagnostics;
using System.Runtime.Versioning;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class CloudDirectoryMoveTests
{
    [Theory]
    [InlineData("directory-prepared", 91)]
    [InlineData("directory-native", 92)]
    [InlineData("directory-before-projection", 93)]
    [InlineData("directory-after-projection", 94)]
    public async Task AbruptDirectoryMoveProcessExitRecoversOriginalProofAndAllDurableFacts(string mode, int exitCode)
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudOperationJournalEntry pending = await SeedPendingChildAsync(root);
        Guid tombstoneId = Guid.NewGuid();
        await using (ICloudStateTransaction seed = await root.Store.BeginTransactionAsync())
        {
            await seed.Items.UpsertAsync(new(tombstoneId, "deleted", "Docs\\gone.txt", CloudItemKind.File,
                "deleted-revision", 99, true, DateTimeOffset.UtcNow));
            await seed.CommitAsync();
        }

        await root.FileSystem.DisposeAsync();
        ProcessStartInfo start = new("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { DirectoryHarnessPath(), root.DatabasePath, root.RootPath, mode })
        {
            start.ArgumentList.Add(argument);
        }

        using Process child = Process.Start(start)!;
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(exitCode, child.ExitCode);
            Assert.True(File.Exists(root.DatabasePath + "." + mode + ".ready"));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
        }

        CloudDirectoryMoveProof proof = CloudDirectoryMoveProof.Decode(await File.ReadAllBytesAsync(root.DatabasePath + ".directory-proof"));
        await root.RestartAsync();
        bool committed = mode == "directory-after-projection";
        string initialDirectory = committed ? "Moved" : "Docs";
        await AssertProjectedChildAsync(root, initialDirectory, pending);
        await using (ICloudStateTransaction verify = await root.Store.BeginTransactionAsync())
        {
            Assert.Equal(committed, await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ReceiptName(proof.ProofId)) is not null);
            Assert.Equal(proof.Encode(), (await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ProofName(proof.ProofId)))!.Value.ToArray());
            Assert.Equal(initialDirectory + "\\gone.txt", (await verify.Items.GetByItemIdAsync(tombstoneId))!.RelativePath);
        }

        CloudDirectory original = root.FileSystem.GetDirectory("Docs");
        if (mode == "directory-prepared")
        {
            CloudDirectoryMoveReconciliationResult notMoved = await original.ReconcileMoveAsync(proof);
            Assert.Equal(CloudDirectoryMoveReconciliationOutcome.NotMoved, notMoved.Outcome);
            Assert.False(notMoved.NativeMoveObserved || notMoved.DurableProjectionCommitted);
            Directory.Move(original.FullPath, Path.Combine(root.RootPath, "Moved"));
        }

        Assert.False(Directory.Exists(original.FullPath));
        Assert.Equal(proof.ExpectedBinding, (await root.FileSystem.GetDirectory("Moved").InspectAsync()).LocalBinding);
        CloudDirectoryMoveReconciliationResult recovered = await original.ReconcileMoveAsync(proof);
        Assert.Equal(committed ? CloudDirectoryMoveReconciliationOutcome.AlreadyProjected : CloudDirectoryMoveReconciliationOutcome.Projected, recovered.Outcome);
        Assert.True(recovered.NativeMoveObserved && recovered.DurableProjectionCommitted);
        Assert.False(recovered.RequiresFullRescan);
        await AssertProjectedChildAsync(root, "Moved", pending);
        await root.RestartAsync();
        CloudDirectoryMoveReconciliationResult duplicate = await root.FileSystem.GetDirectory("Docs").ReconcileMoveAsync(proof);
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.AlreadyProjected, duplicate.Outcome);
        Assert.Equal(0, duplicate.DurableStateEntriesUpdated);
        await using ICloudStateTransaction final = await root.Store.BeginTransactionAsync();
        CloudItemState deleted = (await final.Items.GetByItemIdAsync(tombstoneId))!;
        Assert.True(deleted.IsTombstone);
        Assert.Equal("Moved\\gone.txt", deleted.RelativePath);
        Assert.Equal("deleted-revision", deleted.RemoteRevision);
        Assert.Equal(99, deleted.LocalFileId);
        Assert.Single(await final.Operations.ListAsync(100));
    }
}
