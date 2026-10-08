using System.Runtime.Versioning;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class CloudDirectoryMoveTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task FeedDiscoveryKeepsKnownLocalMembersRecoverableAfterParentRename(
        bool directory, bool modified, bool explicitPreparation)
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        DiscoveryProvenanceScenario scenario = await PrepareDiscoveryProvenanceAsync(root, directory);
        await ProcessDiscoveryAsync(root, scenario.Path, modified);
        CloudItemState discovered;
        CloudOperationJournalEntry observation;
        Exception? membershipFailure = null;
        await using (ICloudStateTransaction read = await root.Store.BeginTransactionAsync())
        {
            discovered = (await read.Items.GetByRelativePathAsync(scenario.Path))!;
            Assert.NotNull(discovered);
            Assert.Equal(directory ? CloudItemKind.Directory : CloudItemKind.File, discovered.Kind);
            observation = Assert.Single((await read.Operations.ListAsync(100)).Where(row => row.ItemId == discovered.ItemId));
            await AssertCheckpointImageAsync(read, [scenario.Live[0], .. scenario.History]);
            await AssertFileMoveJournalImageAsync(read, scenario.Pending);
            Assert.Null(await read.Checkpoints.GetAsync(CloudDirectoryProvenance.BindingName(discovered.ItemId)));
            if (!explicitPreparation)
            {
                membershipFailure = await Record.ExceptionAsync(() => AssertDiscoveryMembersAsync(read, scenario, discovered));
            }
        }

        if (explicitPreparation)
        {
            await root.FileSystem.GetDirectory("Docs").PrepareMoveAsync(root.FileSystem.Root, "Moved");
        }

        // Exercise the visible recovery before asserting the retained manifest, so the
        // baseline exposes the rolled-back subtree while the explicit control succeeds.
        await AssertDiscoveredParentRecoveryAsync(root, scenario, discovered, observation);
        Assert.Null(membershipFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FeedDiscoveryMembershipFaultRollsBackAndRestartRetryPreservesHistory(bool directory)
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        DiscoveryProvenanceScenario scenario = await PrepareDiscoveryProvenanceAsync(root, directory);
        bool reached = false;
        root.Faults.CheckpointWritten = name =>
        {
            if (name == scenario.Live[1].Name)
            {
                reached = true;
                root.Faults.CheckpointWritten = null;
                throw new IOException("Discovered item ancestor membership write fault.");
            }
        };
        try
        {
            await ProcessDiscoveryAsync(root, scenario.Path, modified: false);
        }
        finally
        {
            root.Faults.CheckpointWritten = null;
        }

        Assert.True(reached);
        await using (ICloudStateTransaction verify = await root.Store.BeginTransactionAsync())
        {
            Assert.Null(await verify.Items.GetByRelativePathAsync(scenario.Path));
            await AssertCheckpointImageAsync(verify, [.. scenario.Live, .. scenario.History]);
            await AssertFileMoveJournalImageAsync(verify, scenario.Pending);
            Assert.Single(await verify.Operations.ListAsync(100));
            Assert.True(LocalChangeCheckpoint.Decode((await verify.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName))!.Value).RequiresFullRescan);
        }

        await root.RestartAsync();
        await ProcessDiscoveryAsync(root, scenario.Path, modified: false);
        CloudItemState discovered;
        CloudOperationJournalEntry observation;
        await using (ICloudStateTransaction verify = await root.Store.BeginTransactionAsync())
        {
            discovered = (await verify.Items.GetByRelativePathAsync(scenario.Path))!;
            await AssertDiscoveryMembersAsync(verify, scenario, discovered);
            await AssertCheckpointImageAsync(verify, [scenario.Live[0], .. scenario.History]);
            observation = Assert.Single((await verify.Operations.ListAsync(100)).Where(row => row.ItemId == discovered.ItemId));
        }

        // Successful retry cannot clear the durable fence from the actual storage failure.
        await using (CloudLocalChangeFeed reconciled = CloudLocalChangeFeed.CreateForTesting(root.RootPath, root.Store, new(), new FeedSource()))
        {
            await reconciled.StartAsync();
            Assert.True((await reconciled.BeginScanAsync()).RequiresFullRescan);
            await reconciled.AcknowledgeFullRescanAsync();
        }

        await AssertDiscoveredParentRecoveryAsync(root, scenario, discovered, observation);
    }

    private static async Task<DiscoveryProvenanceScenario> PrepareDiscoveryProvenanceAsync(DirectoryMoveTestRoot root, bool directory)
    {
        CloudOperationJournalEntry pending = await SeedPendingChildAsync(root);
        CloudDirectory docs = root.FileSystem.GetDirectory("Docs");
        CloudDirectoryMoveProof historical = await docs.PrepareMoveAsync(root.FileSystem.Root, "Archived");
        await docs.MoveToAsync(root.FileSystem.Root, "Archived", new CloudMoveOptions(historical));
        await root.FileSystem.GetDirectory("Archived").MoveToAsync(root.FileSystem.Root, "Docs");
        string path = directory ? "Docs\\Local" : "Docs\\local.txt";
        if (directory)
        {
            Directory.CreateDirectory(Path.Combine(root.RootPath, path));
        }
        else
        {
            await File.WriteAllBytesAsync(Path.Combine(root.RootPath, path), []);
        }

        await using ICloudStateTransaction read = await root.Store.BeginTransactionAsync();
        CloudStateCheckpoint[] live = [(await read.Checkpoints.GetAsync(CloudDirectoryProvenance.BindingName(root.DirectoryIdentity.ItemId)))!,
            (await read.Checkpoints.GetAsync(CloudDirectoryProvenance.MembersName(root.DirectoryIdentity.ItemId)))!];
        CloudStateCheckpoint[] history = (await read.Checkpoints.ListAsync($"cfsharp/namespace/preparations/{historical.ProofId:N}")).ToArray();
        Assert.Equal(3, history.Length);
        return new(path, pending, live, history);
    }

    private static async Task ProcessDiscoveryAsync(DirectoryMoveTestRoot root, string path, bool modified)
    {
        FeedSource source = new();
        await using CloudLocalChangeFeed feed = CloudLocalChangeFeed.CreateForTesting(root.RootPath, root.Store, new(), source);
        await feed.StartAsync();
        await source.EmitAsync(new(modified ? LocalChangeSourceAction.Modified : LocalChangeSourceAction.Created, path));
        await feed.DisposeAsync();
    }

    private static async Task AssertDiscoveryMembersAsync(ICloudStateTransaction transaction,
        DiscoveryProvenanceScenario scenario, CloudItemState discovered)
    {
        CloudDirectoryProvenance provenance = CloudDirectoryProvenance.Decode(scenario.Live[0].Value);
        IReadOnlyList<CloudDirectoryMember> members = CloudDirectoryMoveEvidence.DecodeMembers(
            (await transaction.Checkpoints.GetAsync(scenario.Live[1].Name))!.Value, provenance.EvidenceId, provenance.RootItemId);
        Assert.Contains(members, member => member.ItemId == discovered.ItemId && member.Kind == discovered.Kind &&
            member.Suffix == Path.GetFileName(scenario.Path));
    }

    private static async Task AssertDiscoveredParentRecoveryAsync(DirectoryMoveTestRoot root, DiscoveryProvenanceScenario scenario,
        CloudItemState discovered, CloudOperationJournalEntry observation)
    {
        Directory.Move(Path.Combine(root.RootPath, "Docs"), Path.Combine(root.RootPath, "Moved"));
        await ProcessRenameAsync(root, "Docs", "Moved");
        await AssertProjectedChildAsync(root, "Moved", scenario.Pending);
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        CloudItemState current = (await verify.Items.GetByItemIdAsync(discovered.ItemId))!;
        Assert.Equal("Moved\\" + Path.GetFileName(scenario.Path), current.RelativePath);
        Assert.Equal(discovered.RemoteId, current.RemoteId);
        Assert.Equal(discovered.Kind, current.Kind);
        Assert.Null(current.LocalFileId);
        Assert.Null(current.RemoteRevision);
        Assert.False(current.IsTombstone);
        await AssertCheckpointImageAsync(verify, scenario.History);
        await AssertFileMoveJournalImageAsync(verify, observation);
        CloudOperationJournalEntry move = Assert.Single((await verify.Operations.ListAsync(100)).Where(row => row.Kind == CloudStateOperationKind.Move));
        Assert.Equal(root.DirectoryIdentity.ItemId, move.ItemId);
        // Ordinary native children still require reconciliation even after all known rows
        // project successfully. Updating membership must not weaken that separate fence.
        Assert.True(LocalChangeCheckpoint.Decode((await verify.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName))!.Value).RequiresFullRescan);
    }

    private sealed record DiscoveryProvenanceScenario(string Path, CloudOperationJournalEntry Pending,
        CloudStateCheckpoint[] Live, CloudStateCheckpoint[] History);
}
