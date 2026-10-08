using System.Runtime.Versioning;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class CloudDirectoryMoveTests
{
    [Fact]
    public async Task NativeWatcherProjectsWholeKnownSubtreeAndRetainsPendingJournal()
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudOperationJournalEntry original = await SeedPendingChildAsync(root);
        CloudLocalChangeFeed feed = root.FileSystem.CreateLocalChangeFeed();
        await feed.StartAsync();
        Directory.Move(Path.Combine(root.RootPath, "Docs"), Path.Combine(root.RootPath, "Moved"));
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        CloudLocalChange? move = null;
        while (move is null)
        {
            CloudLocalChangeScan scan = await feed.BeginScanAsync(timeout.Token);
            Assert.False(scan.RequiresFullRescan);
            CloudLocalChangePage page = await feed.ReadPageAsync(scan, 0, 4096, timeout.Token);
            move = page.Changes.FirstOrDefault(change => change.Kind == CloudLocalChangeKind.Move && change.RelativePath == "Moved");
            if (move is null)
            {
                await Task.Delay(20, timeout.Token);
            }
        }

        Assert.True(move.IsDirectory);
        Assert.Equal(root.DirectoryIdentity.ItemId, move.ItemId);
        await AssertProjectedChildAsync(root, "Moved", original);
    }

    [Fact]
    public async Task DelayedHistoricalRenamePreservesLatestProjectionAndOriginalOperations()
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudOperationJournalEntry original = await SeedPendingChildAsync(root);
        Directory.Move(Path.Combine(root.RootPath, "Docs"), Path.Combine(root.RootPath, "Moved"));
        await ProcessRenameAsync(root, "Docs", "Moved");
        Directory.Move(Path.Combine(root.RootPath, "Moved"), Path.Combine(root.RootPath, "Latest"));
        await ProcessRenameAsync(root, "Moved", "Latest");
        await ProcessRenameAsync(root, "Docs", "Moved");
        await AssertProjectedChildAsync(root, "Latest", original);
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        CloudOperationJournalEntry[] moves = (await verify.Operations.ListAsync(100)).Where(row => row.Kind == CloudStateOperationKind.Move).ToArray();
        Assert.Equal(3, moves.Length);
        Assert.Null(moves[^1].ItemId);
        Assert.Single(await verify.Checkpoints.ListAsync(CloudLocalChangeFeed.NamespaceObservationsPrefix));
        Assert.True(LocalChangeCheckpoint.Decode((await verify.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName))!.Value).RequiresFullRescan);
    }

    [Fact]
    public async Task NativeChangeAfterCommitRetainsOneOriginalMoveAndFencesPages()
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudOperationJournalEntry original = await SeedPendingChildAsync(root);
        Directory.Move(Path.Combine(root.RootPath, "Docs"), Path.Combine(root.RootPath, "Moved"));
        CloudOperationJournalEntry? committedMove = null;
        root.Faults.AfterCommit = () =>
        {
            root.Faults.AfterCommit = null;
            File.Delete(Path.Combine(root.RootPath, "Moved", "child.txt"));
        };
        await ProcessRenameAsync(root, "Docs", "Moved");
        await using (ICloudStateTransaction verify = await root.Store.BeginTransactionAsync())
        {
            committedMove = Assert.Single((await verify.Operations.ListAsync(100)).Where(row => row.Kind == CloudStateOperationKind.Move));
            Assert.Equal(root.DirectoryIdentity.ItemId, committedMove.ItemId);
            Assert.Single(await verify.Checkpoints.ListAsync(CloudLocalChangeFeed.NamespaceObservationsPrefix));
        }

        FeedSource source = new();
        await using CloudLocalChangeFeed restarted = CloudLocalChangeFeed.CreateForTesting(root.RootPath, root.Store, new(), source);
        await restarted.StartAsync();
        CloudLocalChangeScan scan = await restarted.BeginScanAsync();
        Assert.True(scan.RequiresFullRescan);
        Assert.Empty((await restarted.ReadPageAsync(scan, 0, 4)).Changes);
        await restarted.AcknowledgeFullRescanAsync();
        Assert.True((await restarted.ReadPageAsync(scan, 0, 4)).RequiresFullRescan);
        CloudLocalChangePage page = await restarted.ReadPageAsync(await restarted.BeginScanAsync(), 0, 4);
        Assert.Contains(page.Changes, change => change.OperationId == committedMove.OperationId && change.Sequence == committedMove.Sequence);
        await AssertProjectedChildAsync(root, "Moved", original);
    }

    [Fact]
    public async Task UnknownDescendantRollsBackAllProjectionAndRetainsFormalMove()
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudOperationJournalEntry original = await SeedPendingChildAsync(root);
        CloudDirectoryMoveProof proof = await root.FileSystem.GetDirectory("Docs").PrepareMoveAsync(root.FileSystem.Root, "Moved");
        Guid unknown = Guid.NewGuid();
        await using (ICloudStateTransaction change = await root.Store.BeginTransactionAsync())
        {
            await change.Items.UpsertAsync(new(unknown, "unknown", "Docs\\unknown.txt", CloudItemKind.File, "r-new", null, false, DateTimeOffset.UtcNow));
            await change.CommitAsync();
        }

        Directory.Move(Path.Combine(root.RootPath, "Docs"), Path.Combine(root.RootPath, "Moved"));
        await ProcessRenameAsync(root, "Docs", "Moved");
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        Assert.Equal("Docs", (await verify.Items.GetByItemIdAsync(root.DirectoryIdentity.ItemId))!.RelativePath);
        Assert.Equal("Docs\\unknown.txt", (await verify.Items.GetByItemIdAsync(unknown))!.RelativePath);
        Assert.Equal("Docs\\child.txt", (await verify.Items.GetByItemIdAsync(original.ItemId!.Value))!.RelativePath);
        Assert.Null(Assert.Single((await verify.Operations.ListAsync(100)).Where(row => row.Kind == CloudStateOperationKind.Move)).ItemId);
        Assert.Null(await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ReceiptName(proof.ProofId)));
        Assert.NotNull(await verify.Operations.GetAsync(original.OperationId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FeedPreservesKnownTombstonesAndFencesPhysicallyPresentDeletedMembers(bool physicallyPresent)
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        Guid childId;
        await using (ICloudStateTransaction change = await root.Store.BeginTransactionAsync())
        {
            CloudItemState child = (await change.Items.GetByRelativePathAsync("Docs\\child.txt"))!;
            childId = child.ItemId;
            await change.Items.UpsertAsync(new(childId, child.RemoteId, child.RelativePath, child.Kind, "deleted-revision", 42, true, child.UpdatedAt));
            await change.CommitAsync();
        }

        if (!physicallyPresent)
        {
            File.Delete(Path.Combine(root.RootPath, "Docs", "child.txt"));
        }

        Directory.Move(Path.Combine(root.RootPath, "Docs"), Path.Combine(root.RootPath, "Moved"));
        await ProcessRenameAsync(root, "Docs", "Moved");
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        CloudItemState result = (await verify.Items.GetByItemIdAsync(childId))!;
        Assert.Equal("Moved\\child.txt", result.RelativePath);
        Assert.True(result.IsTombstone);
        Assert.Equal("deleted-revision", result.RemoteRevision);
        Assert.Equal(42, result.LocalFileId);
        Assert.Equal(physicallyPresent, LocalChangeCheckpoint.Decode((await verify.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName))!.Value).RequiresFullRescan);
    }

    [Fact]
    public async Task FeedProjectionCommitFailureRollsBackSubtreeAndRetainsUnresolvedMoveForRestart()
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudOperationJournalEntry original = await SeedPendingChildAsync(root);
        CloudDirectoryMoveProof proof = await root.FileSystem.GetDirectory("Docs").PrepareMoveAsync(root.FileSystem.Root, "Moved");
        Directory.Move(Path.Combine(root.RootPath, "Docs"), Path.Combine(root.RootPath, "Moved"));
        IOException failure = new("Injected directory feed commit failure.");
        root.Faults.BeforeCommit = () =>
        {
            root.Faults.BeforeCommit = null;
            throw failure;
        };
        await ProcessRenameAsync(root, "Docs", "Moved");
        await using (ICloudStateTransaction verify = await root.Store.BeginTransactionAsync())
        {
            Assert.Equal("Docs", (await verify.Items.GetByItemIdAsync(root.DirectoryIdentity.ItemId))!.RelativePath);
            Assert.Equal("Docs\\child.txt", (await verify.Items.GetByItemIdAsync(original.ItemId!.Value))!.RelativePath);
            Assert.Null(await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ReceiptName(proof.ProofId)));
            Assert.Null(Assert.Single((await verify.Operations.ListAsync(100)).Where(row => row.Kind == CloudStateOperationKind.Move)).ItemId);
        }

        CloudDirectoryMoveReconciliationResult recovered = await root.FileSystem.GetDirectory("Docs").ReconcileMoveAsync(proof);
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.Projected, recovered.Outcome);
        await AssertProjectedChildAsync(root, "Moved", original);
        await using CloudLocalChangeFeed restarted = CloudLocalChangeFeed.CreateForTesting(root.RootPath, root.Store, new(), new FeedSource());
        await restarted.StartAsync();
        Assert.True((await restarted.BeginScanAsync()).RequiresFullRescan);
    }

    private static async Task<CloudOperationJournalEntry> SeedPendingChildAsync(DirectoryMoveTestRoot root)
    {
        await using ICloudStateTransaction transaction = await root.Store.BeginTransactionAsync();
        CloudItemState child = (await transaction.Items.GetByRelativePathAsync("Docs\\child.txt"))!;
        await transaction.Items.UpsertAsync(new(child.ItemId, child.RemoteId, child.RelativePath, child.Kind,
            "revision-pending", 123, child.IsTombstone, child.UpdatedAt));
        Guid id = Guid.NewGuid();
        await transaction.Operations.EnqueueAsync(new(id, CloudStateOperationKind.ContentUpdate, child.ItemId,
            new LocalChangePayload(child.RelativePath, null, false, DateTimeOffset.UtcNow).Encode(), DateTimeOffset.UtcNow));
        await transaction.CommitAsync();
        await using ICloudStateTransaction read = await root.Store.BeginTransactionAsync();
        return (await read.Operations.GetAsync(id))!;
    }

    private static async Task AssertProjectedChildAsync(DirectoryMoveTestRoot root, string directory, CloudOperationJournalEntry original)
    {
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        Assert.Equal(directory, (await verify.Items.GetByItemIdAsync(root.DirectoryIdentity.ItemId))!.RelativePath);
        CloudItemState child = (await verify.Items.GetByItemIdAsync(original.ItemId!.Value))!;
        Assert.Equal(directory + "\\child.txt", child.RelativePath);
        Assert.Equal("revision-pending", child.RemoteRevision);
        Assert.Equal(123, child.LocalFileId);
        Assert.False(child.IsTombstone);
        CloudOperationJournalEntry pending = (await verify.Operations.GetAsync(original.OperationId))!;
        Assert.Equal(original.Sequence, pending.Sequence);
        Assert.Equal(original.CreatedAt, pending.CreatedAt);
        Assert.Equal(original.Payload.ToArray(), pending.Payload.ToArray());
    }

    private static async Task ProcessRenameAsync(DirectoryMoveTestRoot root, string sourcePath, string targetPath)
    {
        FeedSource source = new();
        await using CloudLocalChangeFeed feed = CloudLocalChangeFeed.CreateForTesting(root.RootPath, root.Store, new(), source);
        await feed.StartAsync();
        await source.EmitAsync(new(LocalChangeSourceAction.RenamedOldName, sourcePath));
        await source.EmitAsync(new(LocalChangeSourceAction.RenamedNewName, targetPath));
        await feed.DisposeAsync();
    }

    private sealed class FeedSource : ILocalChangeSource
    {
        private Func<LocalChangeSourceEvent, ValueTask> _handler = null!;
        public Task StartAsync(Func<LocalChangeSourceEvent, ValueTask> eventHandler, CancellationToken cancellationToken)
        {
            _handler = eventHandler;
            return Task.CompletedTask;
        }

        internal ValueTask EmitAsync(LocalChangeSourceEvent sourceEvent) => _handler(sourceEvent);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
