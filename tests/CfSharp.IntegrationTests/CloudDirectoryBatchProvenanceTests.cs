using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class CloudDirectoryMoveTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LargeCreationBatchRefreshesEachCapturedAncestorOnce(bool providerPopulation)
    {
        CloudPlaceholderSpec[] children = Enumerable.Range(0, 512)
            .Select(index => (CloudPlaceholderSpec)CloudFilePlaceholderSpec.CreateBuilder($"child-{index:D4}.txt", $"batch-{index}", 0).Build())
            .Append(CloudDirectoryPlaceholderSpec.CreateBuilder("Folder", "batch-folder")
                .WithPopulationState(CloudDirectoryPopulationState.Complete).Build()).ToArray();
        BatchDemandProvider provider = new(children);
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync(provider,
            populationPolicy: CloudPopulationPolicy.Full);
        CloudDirectory docs = root.FileSystem.GetDirectory("Docs");
        CloudPlaceholderBatchEntryResult innerCreated = await docs.CreatePlaceholderAsync(
            CloudDirectoryPlaceholderSpec.CreateBuilder("Inner", "inner")
                .WithPopulationState(CloudDirectoryPopulationState.Complete).WithInSyncState(true).Build());
        CloudDirectoryMoveProof historical = await docs.PrepareMoveAsync(root.FileSystem.Root, "Archived");
        await docs.MoveToAsync(root.FileSystem.Root, "Archived", new CloudMoveOptions(historical));
        await root.FileSystem.GetDirectory("Archived").MoveToAsync(root.FileSystem.Root, "Docs");
        CloudDirectory inner = root.FileSystem.GetDirectory("Docs/Inner");
        if (providerPopulation)
        {
            await inner.SetPopulationStateAsync(CloudDirectoryPopulationState.Partial);
            Assert.True((await inner.InspectAsync()).PlaceholderState.HasFlag(CloudPlaceholderState.Partial));
        }

        Guid deletedId = Guid.NewGuid();
        Dictionary<string, byte[]> immutable = new();
        Dictionary<Guid, byte[]> ancestorBindings = new();
        await using (ICloudStateTransaction seed = await root.Store.BeginTransactionAsync())
        {
            await seed.Items.UpsertAsync(new(deletedId, "deleted-batch-child", "Docs\\Inner\\deleted.txt",
                CloudItemKind.File, "deleted-revision", 42, true, DateTimeOffset.UtcNow));
            foreach (string name in new[] { CloudDirectoryMoveEvidence.ProofName(historical.ProofId),
                CloudDirectoryMoveEvidence.MembersName(historical.ProofId), CloudDirectoryMoveEvidence.ReceiptName(historical.ProofId) })
            {
                immutable.Add(name, (await seed.Checkpoints.GetAsync(name))!.Value.ToArray());
            }

            foreach (Guid id in new[] { root.DirectoryIdentity.ItemId, innerCreated.Specification.Identity.ItemId })
            {
                ancestorBindings.Add(id, (await seed.Checkpoints.GetAsync(CloudDirectoryProvenance.BindingName(id)))!.Value.ToArray());
            }

            await seed.CommitAsync();
        }

        ConcurrentDictionary<string, int> reads = new(StringComparer.OrdinalIgnoreCase);
        ConcurrentDictionary<string, int> writes = new(StringComparer.Ordinal);
        TaskCompletionSource committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        root.Faults.SubtreeRead = path => reads.AddOrUpdate(path, 1, static (_, count) => count + 1);
        root.Faults.CheckpointWritten = name => writes.AddOrUpdate(name, 1, static (_, count) => count + 1);
        root.Faults.AfterCommit = () => committed.TrySetResult();
        try
        {
            if (providerPopulation)
            {
                await Task.Run(() => Directory.GetFileSystemEntries(inner.FullPath)).WaitAsync(TimeSpan.FromSeconds(30));
                using Process enumeration = Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c dir /b \"{inner.FullPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })!;
                try
                {
                    await enumeration.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
                }
                finally
                {
                    if (!enumeration.HasExited)
                    {
                        enumeration.Kill(entireProcessTree: true);
                        await enumeration.WaitForExitAsync();
                    }
                }

                Assert.Equal(1, provider.RequestCount);
                await committed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            else
            {
                CloudPlaceholderBatchResult created = await inner.CreatePlaceholdersAsync(children);
                Assert.True(created.IsSuccessful);
                Assert.Equal(children.Length, created.SucceededCount);
                Assert.All(created.Entries, entry => Assert.True(entry.DurableStatePersisted));
            }
        }
        finally
        {
            root.Faults.SubtreeRead = null;
            root.Faults.CheckpointWritten = null;
            root.Faults.AfterCommit = null;
        }

        Assert.Equal(3, reads.Count);
        Assert.All(reads.Values, count => Assert.Equal(1, count));
        Assert.Equal(1, reads["Docs"]);
        Assert.Equal(1, reads["Docs\\Inner"]);
        Assert.Equal(1, reads["Docs\\Inner\\Folder"]);
        CloudItemSnapshot folder = await root.FileSystem.GetDirectory("Docs/Inner/Folder").InspectAsync();
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        foreach ((Guid id, byte[] binding) in ancestorBindings)
        {
            Assert.Equal(binding, (await verify.Checkpoints.GetAsync(CloudDirectoryProvenance.BindingName(id)))!.Value.ToArray());
            Assert.False(writes.ContainsKey(CloudDirectoryProvenance.BindingName(id)));
        }

        foreach ((string name, byte[] value) in immutable)
        {
            Assert.Equal(value, (await verify.Checkpoints.GetAsync(name))!.Value.ToArray());
            Assert.False(writes.ContainsKey(name));
        }

        foreach ((Guid id, string path) in new[] { (root.DirectoryIdentity.ItemId, "Docs"),
            (innerCreated.Specification.Identity.ItemId, "Docs\\Inner"), (children[^1].Identity.ItemId, "Docs\\Inner\\Folder") })
        {
            Assert.Equal(1, writes[CloudDirectoryProvenance.MembersName(id)]);
            CloudDirectoryProvenance provenance = CloudDirectoryProvenance.Decode(
                (await verify.Checkpoints.GetAsync(CloudDirectoryProvenance.BindingName(id)))!.Value);
            IReadOnlyList<CloudDirectoryMember> manifest = CloudDirectoryMoveEvidence.DecodeMembers(
                (await verify.Checkpoints.GetAsync(CloudDirectoryProvenance.MembersName(id)))!.Value, provenance.EvidenceId, id);
            IReadOnlyList<CloudItemState> members = await verify.Items.ListSubtreeAsync(path);
            Assert.Equal(members.Select(item => item.ItemId).Order(), manifest.Select(item => item.ItemId).Order());
            Assert.All(members, item => Assert.Contains(manifest, member => member.ItemId == item.ItemId && member.Kind == item.Kind &&
                member.Suffix == CloudDirectoryStateProjection.MapPath(item.RelativePath, path, string.Empty)));
        }

        Assert.Equal(1, writes[CloudDirectoryProvenance.BindingName(children[^1].Identity.ItemId)]);
        CloudDirectoryProvenance newBinding = CloudDirectoryProvenance.Decode(
            (await verify.Checkpoints.GetAsync(CloudDirectoryProvenance.BindingName(children[^1].Identity.ItemId)))!.Value);
        Assert.Equal(folder.LocalBinding, newBinding.Binding);
        Assert.Equal(folder.PlaceholderIdentity.ToArray(), newBinding.Identity);
        Assert.Equal(512, (await verify.Items.ListSubtreeAsync("Docs\\Inner")).Count(item => item.RemoteId.StartsWith("batch-", StringComparison.Ordinal) && item.Kind == CloudItemKind.File));
        CloudItemState deleted = (await verify.Items.GetByItemIdAsync(deletedId))!;
        Assert.True(deleted.IsTombstone);
        Assert.Equal("deleted-revision", deleted.RemoteRevision);
        Assert.Equal(42, deleted.LocalFileId);
    }

    [Fact]
    public async Task CreationBatchOmitsOversizedLiveMembershipAndPreservesPreparation()
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudDirectoryMoveProof original = await root.FileSystem.GetDirectory("Docs").PrepareMoveAsync(root.FileSystem.Root, "Moved");
        byte[] originalMembers;
        string suffix = string.Join('\\', Enumerable.Repeat(new string('文', 250), 45));
        await using (ICloudStateTransaction seed = await root.Store.BeginTransactionAsync())
        {
            originalMembers = (await seed.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.MembersName(original.ProofId)))!.Value.ToArray();
            await seed.Items.UpsertAsync(new(Guid.NewGuid(), "oversized-deleted", Path.Combine("Docs", suffix),
                CloudItemKind.File, null, null, true, DateTimeOffset.UtcNow));
            await seed.CommitAsync();
        }

        CloudDirectoryPlaceholderSpec directory = CloudDirectoryPlaceholderSpec.CreateBuilder("Folder", "bounded-folder")
            .WithPopulationState(CloudDirectoryPopulationState.Complete).Build();
        CloudPlaceholderBatchResult created = await root.FileSystem.GetDirectory("Docs").CreatePlaceholdersAsync(
            [CloudFilePlaceholderSpec.CreateBuilder("new.txt", "bounded-file", 0).Build(), directory]);
        Assert.True(created.IsSuccessful);
        Assert.All(created.Entries, entry => Assert.True(entry.DurableStatePersisted));
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        Assert.Null(await verify.Checkpoints.GetAsync(CloudDirectoryProvenance.BindingName(root.DirectoryIdentity.ItemId)));
        Assert.Null(await verify.Checkpoints.GetAsync(CloudDirectoryProvenance.MembersName(root.DirectoryIdentity.ItemId)));
        Assert.NotNull(await verify.Checkpoints.GetAsync(CloudDirectoryProvenance.BindingName(directory.Identity.ItemId)));
        Assert.Equal(original.Encode(), (await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ProofName(original.ProofId)))!.Value.ToArray());
        Assert.Equal(originalMembers, (await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.MembersName(original.ProofId)))!.Value.ToArray());
    }

    private sealed class BatchDemandProvider(CloudPlaceholderSpec[] children) : ICloudDemandProvider
    {
        internal int RequestCount { get; private set; }
        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public ValueTask<CloudProviderDirectoryPage> FetchChildrenAsync(CloudProviderFetchPlaceholdersRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            return ValueTask.FromResult(new CloudProviderDirectoryPage(children, totalCount: children.Length));
        }
    }
}
