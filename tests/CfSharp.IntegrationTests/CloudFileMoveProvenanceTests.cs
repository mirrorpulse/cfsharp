using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class CloudDirectoryMoveTests
{
    [Theory]
    [InlineData("rename", false)]
    [InlineData("rename", true)]
    [InlineData("case", false)]
    [InlineData("case", true)]
    [InlineData("cross", false)]
    [InlineData("cross", true)]
    [InlineData("out", false)]
    [InlineData("out", true)]
    [InlineData("in", false)]
    [InlineData("in", true)]
    public async Task FileMovesRefreshLiveAncestorsAndKeepParentFeedRecovery(string shape, bool viaFeed)
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        FileMoveMembershipScenario scenario = await PrepareFileMoveMembershipAsync(root, shape);
        ConcurrentDictionary<string, int> reads = new(StringComparer.OrdinalIgnoreCase);
        ConcurrentDictionary<string, int> writes = new(StringComparer.Ordinal);
        root.Faults.SubtreeRead = path => reads.AddOrUpdate(path, 1, static (_, count) => count + 1);
        root.Faults.CheckpointWritten = name => writes.AddOrUpdate(name, 1, static (_, count) => count + 1);
        try
        {
            if (viaFeed)
            {
                File.Move(Path.Combine(root.RootPath, scenario.Source), Path.Combine(root.RootPath, scenario.Target));
                await ProcessRenameAsync(root, scenario.Source, scenario.Target);
            }
            else
            {
                CloudItemMoveResult result = await root.FileSystem.GetFile(scenario.Source).MoveToAsync(
                    root.FileSystem.GetDirectory(Path.GetDirectoryName(scenario.Target)!), Path.GetFileName(scenario.Target));
                Assert.Equal(1, result.DurableStateEntriesUpdated);
                Assert.Null(result.DirectoryReconciliation);
            }
        }
        finally
        {
            root.Faults.SubtreeRead = null;
            root.Faults.CheckpointWritten = null;
        }

        Exception? membershipFailure = await Record.ExceptionAsync(() => AssertFileMoveMembershipAsync(root, scenario));
        // Defer the membership assertion so the baseline also exercises the user-visible
        // parent rollback/rescan rather than stopping at its stale checkpoint.
        await AssertFileMoveParentRecoveryAsync(root, scenario);
        Assert.Null(membershipFailure);
        foreach ((Guid id, CloudStateCheckpoint[] image) in scenario.Ancestors)
        {
            CloudDirectoryProvenance provenance = CloudDirectoryProvenance.Decode(image[0].Value);
            Assert.Equal(1, reads.GetValueOrDefault(provenance.RelativePath));
            Assert.Equal(1, writes.GetValueOrDefault(CloudDirectoryProvenance.MembersName(id)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileMoveMembershipFaultRollsBackAndRestartRetryPreservesPendingJournal(bool viaFeed)
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        FileMoveMembershipScenario scenario = await PrepareFileMoveMembershipAsync(root, "cross");
        CloudLocalFileBinding? originalBinding = (await root.FileSystem.GetFile(scenario.Source).InspectAsync()).LocalBinding;
        Assert.NotNull(originalBinding);
        IOException failure = new("File move ancestor membership write fault.");
        string failedMemberName = scenario.Ancestors.Values.First(image =>
            CloudDirectoryProvenance.Decode(image[0].Value).RelativePath == "Docs\\Left")[1].Name;
        bool reached = false;
        root.Faults.CheckpointWritten = name =>
        {
            if (name == failedMemberName)
            {
                reached = true;
                root.Faults.CheckpointWritten = null;
                throw failure;
            }
        };
        try
        {
            if (viaFeed)
            {
                File.Move(Path.Combine(root.RootPath, scenario.Source), Path.Combine(root.RootPath, scenario.Target));
                await ProcessRenameAsync(root, scenario.Source, scenario.Target);
            }
            else
            {
                CloudItemCoordinationException error = await Assert.ThrowsAsync<CloudItemCoordinationException>(() =>
                    root.FileSystem.GetFile(scenario.Source).MoveToAsync(
                        root.FileSystem.GetDirectory("Docs/Right"), "renamed.txt").AsTask());
                Assert.Same(failure, error.InnerException);
            }
        }
        finally
        {
            root.Faults.CheckpointWritten = null;
        }

        Assert.True(reached);
        Assert.False(File.Exists(Path.Combine(root.RootPath, scenario.Source)));
        Assert.True(File.Exists(Path.Combine(root.RootPath, scenario.Target)));
        CloudOperationJournalEntry? unresolved = null;
        await using (ICloudStateTransaction verify = await root.Store.BeginTransactionAsync())
        {
            Assert.Equal(scenario.Source, (await verify.Items.GetByItemIdAsync(scenario.FileId))!.RelativePath);
            foreach (CloudStateCheckpoint[] image in scenario.Ancestors.Values)
            {
                await AssertCheckpointImageAsync(verify, image);
            }

            await AssertCheckpointImageAsync(verify, scenario.History);
            await AssertFileMoveJournalImageAsync(verify, scenario.Pending);
            if (viaFeed)
            {
                unresolved = Assert.Single((await verify.Operations.ListAsync(100)).Where(row => row.Kind == CloudStateOperationKind.Move));
                Assert.Null(unresolved.ItemId);
                Assert.True(LocalChangeCheckpoint.Decode((await verify.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName))!.Value).RequiresFullRescan);
            }
            else
            {
                Assert.Single(await verify.Operations.ListAsync(100));
            }
        }

        await root.RestartAsync();
        if (viaFeed)
        {
            await ProcessRenameAsync(root, scenario.Source, scenario.Target);
        }
        else
        {
            await root.FileSystem.GetFile(scenario.Source).MoveToAsync(root.FileSystem.GetDirectory("Docs/Right"), "renamed.txt");
        }

        Assert.Equal(originalBinding, (await root.FileSystem.GetFile(scenario.Target).InspectAsync()).LocalBinding);
        await AssertFileMoveMembershipAsync(root, scenario);
        if (viaFeed)
        {
            // A real storage failure retains its fence until application reconciliation. A
            // successful replay refreshes membership but never ACKs the original observation.
            await using CloudLocalChangeFeed reconciled = CloudLocalChangeFeed.CreateForTesting(root.RootPath, root.Store, new(), new FeedSource());
            await reconciled.StartAsync();
            Assert.True((await reconciled.BeginScanAsync()).RequiresFullRescan);
            await reconciled.AcknowledgeFullRescanAsync();
        }

        await AssertFileMoveParentRecoveryAsync(root, scenario);
        await using ICloudStateTransaction after = await root.Store.BeginTransactionAsync();
        if (unresolved is not null)
        {
            await AssertFileMoveJournalImageAsync(after, unresolved);
        }
    }

    private static async Task<FileMoveMembershipScenario> PrepareFileMoveMembershipAsync(DirectoryMoveTestRoot root, string shape)
    {
        CloudDirectoryPlaceholderSpec left = CloudDirectoryPlaceholderSpec.CreateBuilder("Left", "file-left")
            .WithPopulationState(CloudDirectoryPopulationState.Complete).Build();
        CloudDirectoryPlaceholderSpec right = CloudDirectoryPlaceholderSpec.CreateBuilder("Right", "file-right")
            .WithPopulationState(CloudDirectoryPopulationState.Complete).Build();
        CloudDirectory docs = root.FileSystem.GetDirectory("Docs");
        await docs.CreatePlaceholdersAsync(shape == "cross" ? [left, right] : [left]);
        string source = shape == "in" ? "outside.txt" : "Docs\\Left\\child.txt";
        string target = shape switch
        {
            "cross" => "Docs\\Right\\renamed.txt",
            "out" => "outside.txt",
            "case" => "Docs\\Left\\CHILD.TXT",
            _ => "Docs\\Left\\renamed.txt",
        };
        CloudFilePlaceholderSpec file = CloudFilePlaceholderSpec.CreateBuilder(Path.GetFileName(source), "file-movable", 0).Build();
        await root.FileSystem.GetDirectory(Path.GetDirectoryName(source)!).CreatePlaceholderAsync(file);
        Guid deletedId = Guid.NewGuid();
        CloudOperationJournalEntry pending;
        await using (ICloudStateTransaction seed = await root.Store.BeginTransactionAsync())
        {
            CloudItemState current = (await seed.Items.GetByItemIdAsync(file.Identity.ItemId))!;
            await seed.Items.UpsertAsync(new(current.ItemId, current.RemoteId, current.RelativePath, current.Kind,
                "file-current", 33, false, current.UpdatedAt));
            await seed.Items.UpsertAsync(new(deletedId, "file-deleted", "Docs\\Left\\deleted.txt", CloudItemKind.File,
                "file-deleted-revision", 42, true, DateTimeOffset.UtcNow));
            pending = await seed.Operations.EnqueueAsync(new(Guid.NewGuid(), CloudStateOperationKind.ContentUpdate, current.ItemId,
                new LocalChangePayload(source, null, false, DateTimeOffset.UtcNow).Encode(), DateTimeOffset.UtcNow));
            await seed.CommitAsync();
        }

        CloudDirectoryMoveProof historical = await docs.PrepareMoveAsync(root.FileSystem.Root, "Archived");
        await docs.MoveToAsync(root.FileSystem.Root, "Archived", new CloudMoveOptions(historical));
        await root.FileSystem.GetDirectory("Archived").MoveToAsync(root.FileSystem.Root, "Docs");
        Dictionary<Guid, CloudStateCheckpoint[]> ancestors = new();
        await using ICloudStateTransaction read = await root.Store.BeginTransactionAsync();
        foreach (Guid id in new[] { root.DirectoryIdentity.ItemId, left.Identity.ItemId }.Concat(shape == "cross" ? [right.Identity.ItemId] : Array.Empty<Guid>()))
        {
            ancestors.Add(id, [(await read.Checkpoints.GetAsync(CloudDirectoryProvenance.BindingName(id)))!,
                (await read.Checkpoints.GetAsync(CloudDirectoryProvenance.MembersName(id)))!]);
        }

        CloudStateCheckpoint[] history = (await read.Checkpoints.ListAsync($"cfsharp/namespace/preparations/{historical.ProofId:N}")).ToArray();
        Assert.Equal(3, history.Length);
        return new(source, target, file.Identity.ItemId, deletedId, pending, ancestors, history);
    }

    private static async Task AssertFileMoveMembershipAsync(DirectoryMoveTestRoot root, FileMoveMembershipScenario scenario)
    {
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        Assert.Equal(scenario.Target, (await verify.Items.GetByItemIdAsync(scenario.FileId))!.RelativePath);
        foreach ((Guid id, CloudStateCheckpoint[] image) in scenario.Ancestors)
        {
            await AssertCheckpointImageAsync(verify, [image[0]]);
            CloudDirectoryProvenance provenance = CloudDirectoryProvenance.Decode(image[0].Value);
            IReadOnlyList<CloudDirectoryMember> members = CloudDirectoryMoveEvidence.DecodeMembers(
                (await verify.Checkpoints.GetAsync(image[1].Name))!.Value, provenance.EvidenceId, id);
            IReadOnlyList<CloudItemState> rows = await verify.Items.ListSubtreeAsync(provenance.RelativePath);
            Assert.Equal(rows.Select(item => (item.ItemId, item.Kind,
                    CloudDirectoryStateProjection.MapPath(item.RelativePath, provenance.RelativePath, string.Empty))).OrderBy(item => item.ItemId),
                members.Select(member => (member.ItemId, member.Kind, member.Suffix)).OrderBy(member => member.ItemId));
        }

        await AssertCheckpointImageAsync(verify, scenario.History);
        await AssertFileMoveJournalImageAsync(verify, scenario.Pending);
    }

    private static async Task AssertFileMoveParentRecoveryAsync(DirectoryMoveTestRoot root, FileMoveMembershipScenario scenario)
    {
        Directory.Move(Path.Combine(root.RootPath, "Docs"), Path.Combine(root.RootPath, "Moved"));
        await ProcessRenameAsync(root, "Docs", "Moved");
        await using (ICloudStateTransaction verify = await root.Store.BeginTransactionAsync())
        {
            Assert.Equal("Moved", (await verify.Items.GetByItemIdAsync(root.DirectoryIdentity.ItemId))!.RelativePath);
            CloudItemState file = (await verify.Items.GetByItemIdAsync(scenario.FileId))!;
            Assert.Equal(scenario.Target.StartsWith("Docs\\", StringComparison.Ordinal)
                ? CloudDirectoryStateProjection.MapPath(scenario.Target, "Docs", "Moved") : scenario.Target, file.RelativePath);
            Assert.Equal("file-current", file.RemoteRevision);
            Assert.Equal(33, file.LocalFileId);
            CloudItemState deleted = (await verify.Items.GetByItemIdAsync(scenario.DeletedId))!;
            Assert.Equal("Moved\\Left\\deleted.txt", deleted.RelativePath);
            Assert.True(deleted.IsTombstone);
            Assert.Equal("file-deleted-revision", deleted.RemoteRevision);
            Assert.Equal(42, deleted.LocalFileId);
            await AssertCheckpointImageAsync(verify, scenario.History);
            await AssertFileMoveJournalImageAsync(verify, scenario.Pending);
        }

        await using CloudLocalChangeFeed delivery = CloudLocalChangeFeed.CreateForTesting(root.RootPath, root.Store, new(), new FeedSource());
        await delivery.StartAsync();
        CloudLocalChangeBatch batch = await delivery.ReadBatchAsync();
        Assert.False(batch.RequiresFullRescan);
        CloudLocalChangeScan scan = await delivery.BeginScanAsync();
        Assert.False(scan.RequiresFullRescan);
        CloudLocalChangePage page = await delivery.ReadPageAsync(scan, 0, 100);
        Assert.False(page.RequiresFullRescan);
        Assert.Equal(batch.Changes.Select(change => change.OperationId), page.Changes.Select(change => change.OperationId));
        Assert.Contains(page.Changes, change => change.ItemId == root.DirectoryIdentity.ItemId && change.Kind == CloudLocalChangeKind.Move);
    }

    private static async Task AssertFileMoveJournalImageAsync(ICloudStateTransaction transaction, CloudOperationJournalEntry image)
    {
        CloudOperationJournalEntry actual = (await transaction.Operations.GetAsync(image.OperationId))!;
        Assert.Equal(image.Sequence, actual.Sequence);
        Assert.Equal(image.Kind, actual.Kind);
        Assert.Equal(image.ItemId, actual.ItemId);
        Assert.Equal(image.CreatedAt, actual.CreatedAt);
        Assert.Equal(image.Payload.ToArray(), actual.Payload.ToArray());
    }

    private sealed record FileMoveMembershipScenario(string Source, string Target, Guid FileId, Guid DeletedId,
        CloudOperationJournalEntry Pending, IReadOnlyDictionary<Guid, CloudStateCheckpoint[]> Ancestors, CloudStateCheckpoint[] History);
}
