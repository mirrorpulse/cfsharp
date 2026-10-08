using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class CloudDirectoryMoveTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ChildMoveRefreshesAncestorsAndAllowsLaterParentFeedRecovery(bool crossParent, bool childViaFeed)
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        AncestorMoveScenario scenario = await PrepareAncestorMoveScenarioAsync(root, crossParent);
        ConcurrentDictionary<string, int> reads = new(StringComparer.OrdinalIgnoreCase);
        ConcurrentDictionary<string, int> writes = new(StringComparer.Ordinal);
        root.Faults.SubtreeRead = path => reads.AddOrUpdate(path, 1, static (_, count) => count + 1);
        root.Faults.CheckpointWritten = name => writes.AddOrUpdate(name, 1, static (_, count) => count + 1);
        try
        {
            if (childViaFeed)
            {
                Directory.Move(Path.Combine(root.RootPath, scenario.Source), Path.Combine(root.RootPath, scenario.Target));
                await ProcessRenameAsync(root, scenario.Source, scenario.Target);
            }
            else
            {
                CloudItemMoveResult moved = await root.FileSystem.GetDirectory(scenario.Source).MoveToAsync(
                    root.FileSystem.GetDirectory(Path.GetDirectoryName(scenario.Target)!), Path.GetFileName(scenario.Target));
                Assert.Equal(CloudDirectoryMoveReconciliationOutcome.Projected, moved.DirectoryReconciliation!.Outcome);
            }
        }
        finally
        {
            root.Faults.SubtreeRead = null;
            root.Faults.CheckpointWritten = null;
        }

        Dictionary<Guid, bool> membershipMatches = new();
        Dictionary<string, CloudStateCheckpoint> afterChildBindings = new();
        await using (ICloudStateTransaction afterChild = await root.Store.BeginTransactionAsync())
        {
            foreach ((Guid id, CloudStateCheckpoint[] image) in scenario.Ancestors)
            {
                CloudStateCheckpoint binding = (await afterChild.Checkpoints.GetAsync(image[0].Name))!;
                afterChildBindings.Add(binding.Name, binding);
                CloudDirectoryProvenance provenance = CloudDirectoryProvenance.Decode(binding.Value);
                IReadOnlyList<CloudDirectoryMember> members = CloudDirectoryMoveEvidence.DecodeMembers(
                    (await afterChild.Checkpoints.GetAsync(image[1].Name))!.Value, provenance.EvidenceId, id);
                IReadOnlyList<CloudItemState> rows = await afterChild.Items.ListSubtreeAsync(provenance.RelativePath);
                membershipMatches.Add(id, members.Select(member => (member.ItemId, member.Kind, member.Suffix)).OrderBy(member => member.ItemId)
                    .SequenceEqual(rows.Select(item => (item.ItemId, item.Kind,
                        CloudDirectoryStateProjection.MapPath(item.RelativePath, provenance.RelativePath, string.Empty))).OrderBy(item => item.ItemId)));
            }
        }

        // Promote the parent's latest mutable membership without supplying a new caller proof.
        Directory.Move(Path.Combine(root.RootPath, "Docs"), Path.Combine(root.RootPath, "Moved"));
        await ProcessRenameAsync(root, "Docs", "Moved");
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        Assert.Equal("Moved", (await verify.Items.GetByItemIdAsync(root.DirectoryIdentity.ItemId))!.RelativePath);
        Assert.False(LocalChangeCheckpoint.Decode((await verify.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName))!.Value).RequiresFullRescan);
        string projectedInner = CloudDirectoryStateProjection.MapPath(scenario.Target, "Docs", "Moved");
        Assert.Equal(projectedInner, (await verify.Items.GetByItemIdAsync(scenario.InnerId))!.RelativePath);
        CloudItemState leaf = (await verify.Items.GetByItemIdAsync(scenario.Pending.ItemId!.Value))!;
        Assert.Equal(Path.Combine(projectedInner, "leaf.txt"), leaf.RelativePath);
        Assert.Equal("current-leaf", leaf.RemoteRevision);
        Assert.Equal(33, leaf.LocalFileId);
        CloudItemState deleted = (await verify.Items.GetByItemIdAsync(scenario.DeletedId))!;
        Assert.Equal(Path.Combine(projectedInner, "deleted.txt"), deleted.RelativePath);
        Assert.True(deleted.IsTombstone);
        Assert.Equal("deleted-revision", deleted.RemoteRevision);
        Assert.Equal(42, deleted.LocalFileId);
        CloudOperationJournalEntry pending = (await verify.Operations.GetAsync(scenario.Pending.OperationId))!;
        Assert.Equal(scenario.Pending.Sequence, pending.Sequence);
        Assert.Equal(scenario.Pending.Payload.ToArray(), pending.Payload.ToArray());
        Assert.Equal(scenario.Pending.CreatedAt, pending.CreatedAt);
        Assert.All(membershipMatches.Values, matches => Assert.True(matches));
        foreach ((Guid id, CloudStateCheckpoint[] image) in scenario.Ancestors)
        {
            CloudDirectoryProvenance provenance = CloudDirectoryProvenance.Decode(image[0].Value);
            Assert.Equal(1, reads.GetValueOrDefault(provenance.RelativePath));
            Assert.Equal(1, writes.GetValueOrDefault(CloudDirectoryProvenance.MembersName(id)));
            CloudStateCheckpoint binding = afterChildBindings[image[0].Name];
            Assert.Equal(image[0].Value.ToArray(), binding.Value.ToArray());
            Assert.Equal(image[0].UpdatedAt, binding.UpdatedAt);
        }

        await AssertCheckpointImageAsync(verify, scenario.History);
        Assert.Equal(childViaFeed ? 2 : 1, (await verify.Operations.ListAsync(100)).Count(item => item.Kind == CloudStateOperationKind.Move));
    }

    [Fact]
    public async Task AncestorMembershipWriteFailureRollsBackProjectionAndOriginalRetryRecovers()
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        AncestorMoveScenario scenario = await PrepareAncestorMoveScenarioAsync(root, crossParent: true);
        CloudDirectory source = root.FileSystem.GetDirectory(scenario.Source);
        CloudDirectory destination = root.FileSystem.GetDirectory(Path.GetDirectoryName(scenario.Target)!);
        IOException storageError = new("Ancestor membership write fault.");
        root.Faults.CheckpointWritten = name =>
        {
            if (name == CloudDirectoryProvenance.MembersName(root.DirectoryIdentity.ItemId))
            {
                throw storageError;
            }
        };
        try
        {
            CloudItemCoordinationException failed = await Assert.ThrowsAsync<CloudItemCoordinationException>(
                () => source.MoveToAsync(destination, "Renamed").AsTask());
            Assert.Same(storageError, failed.InnerException);
            Assert.True(failed.DirectoryReconciliation!.NativeMoveObserved);
            Assert.False(failed.DirectoryReconciliation.DurableProjectionCommitted);
        }
        finally
        {
            root.Faults.CheckpointWritten = null;
        }

        Assert.False(Directory.Exists(source.FullPath));
        Assert.True(Directory.Exists(Path.Combine(root.RootPath, scenario.Target)));
        await using (ICloudStateTransaction verify = await root.Store.BeginTransactionAsync())
        {
            Assert.Equal(scenario.Source, (await verify.Items.GetByItemIdAsync(scenario.InnerId))!.RelativePath);
            Assert.Equal(Path.Combine(scenario.Source, "leaf.txt"), (await verify.Items.GetByItemIdAsync(scenario.Pending.ItemId!.Value))!.RelativePath);
            foreach (CloudStateCheckpoint[] image in scenario.Ancestors.Values)
            {
                await AssertCheckpointImageAsync(verify, image);
            }

            CloudDirectoryMoveProof prepared = CloudDirectoryMoveProof.Decode((await verify.Checkpoints.GetAsync(
                CloudDirectoryMoveEvidence.IntentName(scenario.Source, scenario.Target)))!.Value.Span);
            Assert.Null(await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ReceiptName(prepared.ProofId)));
            await AssertCheckpointImageAsync(verify, scenario.History);
        }

        CloudItemMoveResult recovered = await source.MoveToAsync(destination, "Renamed");
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.Projected, recovered.DirectoryReconciliation!.Outcome);
        Directory.Move(Path.Combine(root.RootPath, "Docs"), Path.Combine(root.RootPath, "Moved"));
        await ProcessRenameAsync(root, "Docs", "Moved");
        await using ICloudStateTransaction after = await root.Store.BeginTransactionAsync();
        Assert.Equal("Moved", (await after.Items.GetByItemIdAsync(root.DirectoryIdentity.ItemId))!.RelativePath);
        Assert.False(LocalChangeCheckpoint.Decode((await after.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName))!.Value).RequiresFullRescan);
        await AssertCheckpointImageAsync(after, scenario.History);
    }

    private static async Task<AncestorMoveScenario> PrepareAncestorMoveScenarioAsync(DirectoryMoveTestRoot root, bool crossParent)
    {
        CloudDirectory docs = root.FileSystem.GetDirectory("Docs");
        CloudDirectoryPlaceholderSpec left = CloudDirectoryPlaceholderSpec.CreateBuilder("Left", "left")
            .WithPopulationState(CloudDirectoryPopulationState.Complete).Build();
        CloudDirectoryPlaceholderSpec right = CloudDirectoryPlaceholderSpec.CreateBuilder("Right", "right")
            .WithPopulationState(CloudDirectoryPopulationState.Complete).Build();
        await docs.CreatePlaceholdersAsync(crossParent ? [left, right] : [left]);
        CloudDirectoryPlaceholderSpec inner = CloudDirectoryPlaceholderSpec.CreateBuilder("Inner", "inner")
            .WithPopulationState(CloudDirectoryPopulationState.Complete).Build();
        await root.FileSystem.GetDirectory("Docs/Left").CreatePlaceholderAsync(inner);
        CloudFilePlaceholderSpec leaf = CloudFilePlaceholderSpec.CreateBuilder("leaf.txt", "nested-leaf", 0).Build();
        string sourcePath = "Docs\\Left\\Inner";
        await root.FileSystem.GetDirectory(sourcePath).CreatePlaceholderAsync(leaf);
        Guid deletedId = Guid.NewGuid();
        CloudOperationJournalEntry pending;
        await using (ICloudStateTransaction seed = await root.Store.BeginTransactionAsync())
        {
            await seed.Items.UpsertAsync(new(deletedId, "nested-deleted", Path.Combine(sourcePath, "deleted.txt"),
                CloudItemKind.File, "deleted-revision", 42, true, DateTimeOffset.UtcNow));
            CloudItemState current = (await seed.Items.GetByItemIdAsync(leaf.Identity.ItemId))!;
            await seed.Items.UpsertAsync(new(current.ItemId, current.RemoteId, current.RelativePath,
                current.Kind, "current-leaf", 33, false, DateTimeOffset.UtcNow));
            pending = await seed.Operations.EnqueueAsync(new(Guid.NewGuid(), CloudStateOperationKind.ContentUpdate, current.ItemId,
                new LocalChangePayload(current.RelativePath, null, false, DateTimeOffset.UtcNow).Encode(), DateTimeOffset.UtcNow));
            await seed.CommitAsync();
        }

        CloudDirectoryMoveProof historical = await docs.PrepareMoveAsync(root.FileSystem.Root, "Archived");
        await docs.MoveToAsync(root.FileSystem.Root, "Archived", new CloudMoveOptions(historical));
        await root.FileSystem.GetDirectory("Archived").MoveToAsync(root.FileSystem.Root, "Docs");
        Dictionary<Guid, CloudStateCheckpoint[]> ancestors = new();
        await using ICloudStateTransaction read = await root.Store.BeginTransactionAsync();
        foreach (Guid id in new[] { root.DirectoryIdentity.ItemId, left.Identity.ItemId }.Concat(crossParent ? [right.Identity.ItemId] : Array.Empty<Guid>()))
        {
            ancestors.Add(id, [(await read.Checkpoints.GetAsync(CloudDirectoryProvenance.BindingName(id)))!,
                (await read.Checkpoints.GetAsync(CloudDirectoryProvenance.MembersName(id)))!]);
        }

        CloudStateCheckpoint[] history = (await read.Checkpoints.ListAsync($"cfsharp/namespace/preparations/{historical.ProofId:N}")).ToArray();
        Assert.Equal(3, history.Length);
        return new(sourcePath, crossParent ? "Docs\\Right\\Renamed" : "Docs\\Left\\Renamed", inner.Identity.ItemId, deletedId,
            pending, ancestors, history);
    }

    private static async Task AssertCheckpointImageAsync(ICloudStateTransaction transaction, IEnumerable<CloudStateCheckpoint> image)
    {
        foreach (CloudStateCheckpoint saved in image)
        {
            CloudStateCheckpoint current = (await transaction.Checkpoints.GetAsync(saved.Name))!;
            Assert.Equal(saved.Value.ToArray(), current.Value.ToArray());
            Assert.Equal(saved.UpdatedAt, current.UpdatedAt);
        }
    }

    private sealed record AncestorMoveScenario(string Source, string Target, Guid InnerId, Guid DeletedId,
        CloudOperationJournalEntry Pending, IReadOnlyDictionary<Guid, CloudStateCheckpoint[]> Ancestors, CloudStateCheckpoint[] History);
}
