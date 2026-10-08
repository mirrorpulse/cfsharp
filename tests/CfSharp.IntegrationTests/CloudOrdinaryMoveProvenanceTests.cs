using System.Runtime.Versioning;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class CloudDirectoryMoveTests
{
    [Theory]
    [InlineData("rename", false)]
    [InlineData("rename", true)]
    [InlineData("cross", false)]
    [InlineData("cross", true)]
    [InlineData("case", false)]
    [InlineData("case", true)]
    public async Task OrdinaryParentMoveRetainsManagedDescendantRecovery(string shape, bool facadeRecovery)
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        OrdinaryMoveProvenanceScenario scenario = await PrepareOrdinaryMoveProvenanceAsync(root, nested: false);
        string target = shape switch { "cross" => "Container\\Relocated", "case" => "ORDINARY", _ => "RenamedOrdinary" };
        Directory.CreateDirectory(Path.Combine(root.RootPath, "Container"));
        CloudItemMoveResult result = await root.FileSystem.GetDirectory("Ordinary").MoveToAsync(
            root.FileSystem.GetDirectory(Path.GetDirectoryName(target)!), Path.GetFileName(target));
        Assert.Null(result.DirectoryReconciliation);
        Assert.Equal(3, result.DurableStateEntriesUpdated);
        Exception? provenanceFailure = null;
        if (!facadeRecovery)
        {
            provenanceFailure = await Record.ExceptionAsync(() => AssertOrdinaryMoveProvenanceAsync(root, scenario, target));
        }

        await root.RestartAsync();
        await AssertNestedManagedRecoveryAsync(root, scenario, target, facadeRecovery);
        Assert.Null(provenanceFailure);
    }

    [Fact]
    public async Task OrdinaryParentMoveRelocatesAllNestedLiveBindingsBeforeFeedRecovery()
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        OrdinaryMoveProvenanceScenario scenario = await PrepareOrdinaryMoveProvenanceAsync(root, nested: true);
        await root.FileSystem.GetDirectory("Ordinary").MoveToAsync(root.FileSystem.Root, "RenamedOrdinary");
        Exception? provenanceFailure = await Record.ExceptionAsync(() => AssertOrdinaryMoveProvenanceAsync(root, scenario, "RenamedOrdinary"));
        await root.RestartAsync();
        await AssertNestedManagedRecoveryAsync(root, scenario, "RenamedOrdinary", facadeRecovery: false);
        Assert.Null(provenanceFailure);
    }

    [Fact]
    public async Task OrdinaryParentMembershipFaultRollsBackPathsAndBindingsBeforeRestartRetry()
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        OrdinaryMoveProvenanceScenario scenario = await PrepareOrdinaryMoveProvenanceAsync(root, nested: true);
        IOException failure = new("Ordinary parent nested membership write fault.");
        string failedName = scenario.Live.Last()[1].Name;
        bool reached = false;
        root.Faults.CheckpointWritten = name =>
        {
            if (name == failedName)
            {
                reached = true;
                root.Faults.CheckpointWritten = null;
                throw failure;
            }
        };
        try
        {
            CloudItemCoordinationException error = await Assert.ThrowsAsync<CloudItemCoordinationException>(() =>
                root.FileSystem.GetDirectory("Ordinary").MoveToAsync(root.FileSystem.Root, "RenamedOrdinary").AsTask());
            Assert.Same(failure, error.InnerException);
        }
        finally
        {
            root.Faults.CheckpointWritten = null;
        }

        Assert.True(reached);
        Assert.False(Directory.Exists(Path.Combine(root.RootPath, "Ordinary")));
        Assert.True(Directory.Exists(Path.Combine(root.RootPath, "RenamedOrdinary")));
        await using (ICloudStateTransaction verify = await root.Store.BeginTransactionAsync())
        {
            foreach (CloudItemState row in scenario.Rows)
            {
                CloudItemState current = (await verify.Items.GetByItemIdAsync(row.ItemId))!;
                Assert.Equal((row.RemoteId, row.RelativePath, row.Kind, row.RemoteRevision, row.LocalFileId, row.IsTombstone, row.UpdatedAt),
                    (current.RemoteId, current.RelativePath, current.Kind, current.RemoteRevision, current.LocalFileId, current.IsTombstone, current.UpdatedAt));
            }

            await AssertCheckpointImageAsync(verify, scenario.Live.SelectMany(image => image).Concat(scenario.History));
            await AssertFileMoveJournalImageAsync(verify, scenario.Pending);
            Assert.Single(await verify.Operations.ListAsync(100));
        }

        await root.RestartAsync();
        // Ordinary directories still cannot recover an absent source without pre-move proof.
        // Restore its native source explicitly, then retry the original authorized operation.
        await Assert.ThrowsAsync<FileNotFoundException>(() => root.FileSystem.GetDirectory("Ordinary")
            .MoveToAsync(root.FileSystem.Root, "RenamedOrdinary").AsTask());
        Directory.Move(Path.Combine(root.RootPath, "RenamedOrdinary"), Path.Combine(root.RootPath, "Ordinary"));
        await root.FileSystem.GetDirectory("Ordinary").MoveToAsync(root.FileSystem.Root, "RenamedOrdinary");
        await AssertOrdinaryMoveProvenanceAsync(root, scenario, "RenamedOrdinary");
        await AssertNestedManagedRecoveryAsync(root, scenario, "RenamedOrdinary", facadeRecovery: false);
    }

    private static async Task<OrdinaryMoveProvenanceScenario> PrepareOrdinaryMoveProvenanceAsync(DirectoryMoveTestRoot root, bool nested)
    {
        CloudDirectory docs = root.FileSystem.GetDirectory("Docs");
        if (nested)
        {
            await docs.CreatePlaceholderAsync(CloudDirectoryPlaceholderSpec.CreateBuilder("Inner", "ordinary-inner")
                .WithPopulationState(CloudDirectoryPopulationState.Complete).Build());
            await root.FileSystem.GetDirectory("Docs/Inner").CreatePlaceholderAsync(
                CloudFilePlaceholderSpec.CreateBuilder("leaf.txt", "ordinary-leaf", 0).Build());
        }

        CloudOperationJournalEntry pending = await SeedPendingChildAsync(root);
        await using (ICloudStateTransaction seed = await root.Store.BeginTransactionAsync())
        {
            await seed.Items.UpsertAsync(new(Guid.NewGuid(), "ordinary-deleted", "Docs\\deleted.txt", CloudItemKind.File,
                "deleted-revision", 456, true, DateTimeOffset.UtcNow));
            await seed.CommitAsync();
        }

        await docs.MoveToAsync(root.FileSystem.Root, "Archived");
        await root.FileSystem.GetDirectory("Archived").MoveToAsync(root.FileSystem.Root, "Docs");
        await docs.MoveToAsync(root.FileSystem.GetDirectory("Ordinary"), "Docs");
        await using ICloudStateTransaction read = await root.Store.BeginTransactionAsync();
        CloudItemState[] rows = (await read.Items.ListSubtreeAsync("Ordinary")).ToArray();
        List<CloudStateCheckpoint[]> live = [];
        foreach (CloudItemState row in rows.Where(item => item.Kind == CloudItemKind.Directory && !item.IsTombstone))
        {
            live.Add([(await read.Checkpoints.GetAsync(CloudDirectoryProvenance.BindingName(row.ItemId)))!,
                (await read.Checkpoints.GetAsync(CloudDirectoryProvenance.MembersName(row.ItemId)))!]);
        }

        CloudStateCheckpoint[] history = (await read.Checkpoints.ListAsync("cfsharp/namespace/preparations/")).ToArray();
        Assert.Equal(nested ? 2 : 1, live.Count);
        Assert.Equal(9, history.Length);
        return new(rows, live.ToArray(), history, pending);
    }

    private static async Task AssertOrdinaryMoveProvenanceAsync(DirectoryMoveTestRoot root,
        OrdinaryMoveProvenanceScenario scenario, string target)
    {
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        foreach (CloudStateCheckpoint[] image in scenario.Live)
        {
            CloudDirectoryProvenance before = CloudDirectoryProvenance.Decode(image[0].Value);
            CloudDirectoryProvenance current = CloudDirectoryProvenance.Decode((await verify.Checkpoints.GetAsync(image[0].Name))!.Value);
            Assert.Equal(CloudDirectoryStateProjection.MapPath(before.RelativePath, "Ordinary", target), current.RelativePath);
            Assert.Equal(before.RootItemId, current.RootItemId);
            Assert.Equal(before.StoreScope, current.StoreScope);
            Assert.Equal(before.Binding, current.Binding);
            Assert.Equal(before.Identity, current.Identity);
            IReadOnlyList<CloudDirectoryMember> members = CloudDirectoryMoveEvidence.DecodeMembers(
                (await verify.Checkpoints.GetAsync(image[1].Name))!.Value, current.EvidenceId, current.RootItemId);
            IReadOnlyList<CloudItemState> rows = await verify.Items.ListSubtreeAsync(current.RelativePath);
            Assert.Equal(rows.Select(item => (item.ItemId, item.Kind,
                    CloudDirectoryStateProjection.MapPath(item.RelativePath, current.RelativePath, string.Empty))).OrderBy(item => item.ItemId),
                members.Select(member => (member.ItemId, member.Kind, member.Suffix)).OrderBy(member => member.ItemId));
        }

        await AssertCheckpointImageAsync(verify, scenario.History);
        await AssertFileMoveJournalImageAsync(verify, scenario.Pending);
    }

    private static async Task AssertNestedManagedRecoveryAsync(DirectoryMoveTestRoot root,
        OrdinaryMoveProvenanceScenario scenario, string parent, bool facadeRecovery)
    {
        string source = Path.Combine(parent, "Docs");
        Assert.Equal(CloudDirectoryProvenance.Decode(scenario.Live[0][0].Value).Binding,
            (await root.FileSystem.GetDirectory(source).InspectAsync()).LocalBinding);
        if (facadeRecovery)
        {
            CloudItemMoveResult result = await root.FileSystem.GetDirectory(source).MoveToAsync(root.FileSystem.Root, "Recovered");
            Assert.Equal(CloudDirectoryMoveReconciliationOutcome.Projected, result.DirectoryReconciliation!.Outcome);
        }
        else
        {
            Directory.Move(Path.Combine(root.RootPath, source), Path.Combine(root.RootPath, "Recovered"));
            await ProcessRenameAsync(root, source, "Recovered");
        }

        await AssertProjectedChildAsync(root, "Recovered", scenario.Pending);
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        foreach (CloudItemState row in scenario.Rows)
        {
            CloudItemState current = (await verify.Items.GetByItemIdAsync(row.ItemId))!;
            Assert.Equal(CloudDirectoryStateProjection.MapPath(row.RelativePath, "Ordinary\\Docs", "Recovered"), current.RelativePath);
            Assert.Equal(row.RemoteId, current.RemoteId);
            Assert.Equal(row.RemoteRevision, current.RemoteRevision);
            Assert.Equal(row.LocalFileId, current.LocalFileId);
            Assert.Equal(row.IsTombstone, current.IsTombstone);
        }

        await AssertCheckpointImageAsync(verify, scenario.History);
        CloudStateCheckpoint? checkpoint = await verify.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName);
        Assert.False(checkpoint is not null && LocalChangeCheckpoint.Decode(checkpoint.Value).RequiresFullRescan);
        if (!facadeRecovery)
        {
            Assert.Equal(root.DirectoryIdentity.ItemId,
                Assert.Single((await verify.Operations.ListAsync(100)).Where(row => row.Kind == CloudStateOperationKind.Move)).ItemId);
        }
    }

    private sealed record OrdinaryMoveProvenanceScenario(CloudItemState[] Rows, CloudStateCheckpoint[][] Live,
        CloudStateCheckpoint[] History, CloudOperationJournalEntry Pending);
}
