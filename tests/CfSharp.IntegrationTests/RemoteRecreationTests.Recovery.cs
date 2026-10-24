namespace CfSharp.IntegrationTests;

public sealed partial class RemoteRecreationTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task InlineResolutionReusesAuthorizedCreationAcrossRestart(bool directory, bool keepBoth, bool failProjection)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        CloudRemoteChange original = root.SuccessorBatch.Changes[0];
        CloudRemoteChange change = new(original.ChangeId, original.Kind, keepBoth ? "original-remote" : original.RemoteId,
            original.RemoteRevision, original.ItemKind, original.RelativePath, original.ItemId,
            previousRemoteRevision: keepBoth ? null : "missing-revision", length: original.Length, metadata: original.Metadata);
        CloudRemoteChangeBatch batch = new("successor", new byte[] { 2 }, [change], new byte[] { 3 });
        CopyResolver resolver = new(keepBoth);
        string actualPath = keepBoth ? "Docs\\copy" : root.Target;
        CloudItem item = directory ? root.FileSystem.GetDirectory(actualPath) : root.FileSystem.GetFile(actualPath);
        if (failProjection) { root.Factory.Boundary = "projection-before"; }
        try
        {
            CloudRemoteApplyResult initial = await root.FileSystem.ApplyRemoteChangesAsync(batch, new() { ConflictResolver = resolver });
            if (failProjection)
            {
                Assert.True(initial.RequiresRetry);
                Assert.Same(root.Factory.Error, Assert.Single(initial.Entries).Error);
            }
            else { AssertApplied(initial, batch); }
            Guid allocated = CloudPlaceholderIdentity.Decode((await item.InspectAsync()).PlaceholderIdentity.Span).ItemId;
            if (keepBoth) { Assert.NotEqual(root.SuccessorId, allocated); }
            else { Assert.Equal(root.SuccessorId, allocated); }
            Assert.Equal(1, resolver.Calls);
            await root.RestartAsync();
            item = directory ? root.FileSystem.GetDirectory(actualPath) : root.FileSystem.GetFile(actualPath);
            CloudRemoteApplyResult replay = await root.FileSystem.ApplyRemoteChangesAsync(batch);
            Assert.False(replay.RequiresRetry);
            Assert.Equal(failProjection ? CloudRemoteApplyEntryStatus.Applied : CloudRemoteApplyEntryStatus.AlreadyApplied,
                Assert.Single(replay.Entries).Status);
            Assert.Equal(allocated, CloudPlaceholderIdentity.Decode((await item.InspectAsync()).PlaceholderIdentity.Span).ItemId);
            Assert.Equal(1, resolver.Calls);
            await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
            Assert.True((await verify.Items.GetByItemIdAsync(root.OriginalId))!.IsTombstone);
            Assert.Equal(allocated, (await verify.Items.GetByRelativePathAsync(actualPath))!.ItemId);
            Assert.Empty(await verify.Checkpoints.ListAsync(RemoteCreationIntent.Prefix));
            Assert.NotNull(await verify.Checkpoints.GetAsync(RemoteCreationIntent.IdentityConflictName(batch.BatchId, 0)));
            Assert.Equal(0, root.Reads);
        }
        finally
        {
            if (keepBoth && (File.Exists(Path.Combine(root.FileSystem.SyncRootPath, actualPath)) ||
                Directory.Exists(Path.Combine(root.FileSystem.SyncRootPath, actualPath))))
            {
                await item.DeleteAsync();
            }
        }
    }

    private sealed class CopyResolver(bool keepBoth) : ICloudRemoteConflictResolver
    {
        internal int Calls { get; private set; }
        public ValueTask<CloudRemoteConflictResolution> ResolveAsync(CloudRemoteConflict conflict, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(keepBoth ? new(CloudRemoteConflictDecision.KeepBoth, "Docs\\copy") :
                new CloudRemoteConflictResolution(CloudRemoteConflictDecision.KeepRemote));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedParentAcrossReopenCannotAuthorizePreparedSuccessor(bool directory)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        root.Factory.Boundary = "prepare-after";
        Assert.True((await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch)).RequiresRetry);
        byte[] original;
        await using (ICloudStateTransaction capture = await root.Factory.Store!.BeginTransactionAsync())
        {
            original = Assert.Single(await capture.Checkpoints.ListAsync(RemoteCreationIntent.Prefix)).Value.ToArray();
        }
        string parent = Path.Combine(root.FileSystem.SyncRootPath, "Docs");
        string retained = Path.Combine(root.FileSystem.SyncRootPath, "retained-parent");
        Directory.Move(parent, retained);
        Directory.CreateDirectory(parent);
        try
        {
            await root.RestartAsync();
            CloudRemoteApplyResult replay = await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch);
            Assert.True(replay.RequiresRetry);
            Assert.Equal(0, replay.AppliedEntryCount);
            Assert.IsType<IOException>(Assert.Single(replay.Entries).Error);
            Assert.False(File.Exists(Path.Combine(parent, "recreated")) || Directory.Exists(Path.Combine(parent, "recreated")));
            await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
            Assert.Equal(original, Assert.Single(await verify.Checkpoints.ListAsync(RemoteCreationIntent.Prefix)).Value.ToArray());
            Assert.True((await verify.Items.GetByItemIdAsync(root.OriginalId))!.IsTombstone);
        }
        finally
        {
            Directory.Delete(parent);
            Directory.Move(retained, parent);
        }
    }

    [Theory]
    [InlineData(false, "prepare-before")]
    [InlineData(true, "prepare-before")]
    [InlineData(false, "prepare-after")]
    [InlineData(true, "prepare-after")]
    [InlineData(false, "native-after")]
    [InlineData(true, "native-after")]
    [InlineData(false, "projection-before")]
    [InlineData(true, "projection-before")]
    [InlineData(false, "projection-after")]
    [InlineData(true, "projection-after")]
    [InlineData(false, "outcome-before")]
    [InlineData(true, "outcome-before")]
    [InlineData(false, "outcome-after")]
    [InlineData(true, "outcome-after")]
    public async Task InterruptedSuccessorUsesOriginalIdentityAcrossOfficialStoreReopen(bool directory, string boundary)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        root.Factory.Boundary = boundary;
        if (boundary.StartsWith("outcome", StringComparison.Ordinal))
        {
            Assert.Same(root.Factory.Error, await Assert.ThrowsAsync<IOException>(() => root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch).AsTask()));
        }
        else
        {
            CloudRemoteApplyResult failed = await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch);
            Assert.True(failed.RequiresRetry);
            Assert.Equal(0, failed.AppliedEntryCount);
            Assert.Equal(new byte[] { 2 }, failed.SafeCursor.ToArray());
            Assert.Same(root.Factory.Error, Assert.Single(failed.Entries).Error);
        }
        Assert.Equal(1, root.Factory.Faults);
        await root.RestartAsync();
        CloudRemoteApplyResult replay = await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch);
        Assert.False(replay.RequiresRetry);
        Assert.Equal(1, replay.AppliedEntryCount);
        Assert.Equal(new byte[] { 3 }, replay.SafeCursor.ToArray());
        await root.AssertGenerationsAsync();
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        Assert.Empty(await verify.Checkpoints.ListAsync(RemoteCreationIntent.Prefix));
        Assert.Equal(root.SuccessorBatch.Fingerprint.ToArray(), (await verify.RemoteBatches.GetAsync("successor"))!.Fingerprint.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostNativeReceiptDoesNotAdoptAnUnprovenMatchingPlaceholder(bool directory)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        root.Factory.Boundary = "native-before";
        Assert.True((await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch)).RequiresRetry);
        Assert.Equal(root.SuccessorId, (await root.InspectNativeIdentityAsync()).ItemId);
        await root.RestartAsync();
        CloudRemoteApplyResult replay = await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch);
        Assert.True(replay.RequiresRetry);
        Assert.Equal(0, replay.AppliedEntryCount);
        Assert.IsType<IOException>(Assert.Single(replay.Entries).Error);
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        RemoteCreationIntent intent = RemoteCreationIntent.Decode(Assert.Single(await verify.Checkpoints.ListAsync(RemoteCreationIntent.Prefix)).Value);
        Assert.Equal(root.SuccessorId, intent.Identity.ItemId);
        Assert.Null(intent.CreatedObject);
        Assert.Null(await verify.Items.GetByItemIdAsync(root.SuccessorId));
        Assert.True((await verify.Items.GetByItemIdAsync(root.OriginalId))!.IsTombstone);
    }

    [Theory]
    [InlineData(false, "none")]
    [InlineData(true, "none")]
    [InlineData(false, "prepare-after")]
    [InlineData(true, "prepare-after")]
    [InlineData(false, "native-after")]
    [InlineData(true, "native-after")]
    public async Task CancellationKeepsSuccessorEvidenceWithoutPublishingAnUnsafeCursor(bool directory, string boundary)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        using CancellationTokenSource cancellation = new();
        if (boundary == "none") { cancellation.Cancel(); }
        else
        {
            root.Factory.Boundary = boundary;
            root.Factory.AtBoundary = cancellation.Cancel;
            root.Factory.ThrowAtBoundary = false;
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => root.FileSystem.ApplyRemoteChangesAsync(
            root.SuccessorBatch, cancellationToken: cancellation.Token).AsTask());
        await using (ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync())
        {
            Assert.True((await verify.Items.GetByItemIdAsync(root.OriginalId))!.IsTombstone);
            Assert.True((await verify.RemoteBatches.GetAsync("successor"))?.AppliedEntryCount is null or 0);
        }
        await root.RestartAsync();
        AssertApplied(await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch), root.SuccessorBatch);
        await root.AssertGenerationsAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementWithCopiedIdentityCannotInheritAnOriginalCreationReceipt(bool directory)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        root.Factory.Boundary = "projection-before";
        Assert.True((await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch)).RequiresRetry);
        ReplaceNative(root, new(root.SuccessorId, "successor-remote", "v3"));
        await root.RestartAsync();
        CloudRemoteApplyResult replay = await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch);
        Assert.True(replay.RequiresRetry);
        Assert.Equal(0, replay.AppliedEntryCount);
        Assert.Equal(root.SuccessorId, (await root.InspectNativeIdentityAsync()).ItemId);
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        Assert.Single(await verify.Checkpoints.ListAsync(RemoteCreationIntent.Prefix));
        Assert.Null(await verify.Items.GetByItemIdAsync(root.SuccessorId));
        Assert.True((await verify.Items.GetByItemIdAsync(root.OriginalId))!.IsTombstone);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalCreationTransactionRejectsAReplacementBeforePrefixPublication(bool directory)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        CloudPlaceholderIdentity foreign = new(Guid.NewGuid(), "foreign", "f1");
        root.Factory.AfterProjection = () => ReplaceNative(root, foreign);
        await Assert.ThrowsAsync<IOException>(() => root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch).AsTask());
        Assert.Equal(foreign.ItemId, (await root.InspectNativeIdentityAsync()).ItemId);
        await root.RestartAsync();
        Assert.True((await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch)).RequiresRetry);
        Assert.Equal(foreign.ItemId, (await root.InspectNativeIdentityAsync()).ItemId);
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        Assert.Equal(0, (await verify.RemoteBatches.GetAsync("successor"))!.AppliedEntryCount);
        Assert.True(RemoteCreationIntent.Decode(Assert.Single(await verify.Checkpoints.ListAsync(RemoteCreationIntent.Prefix)).Value).Committed);
    }

    private static void ReplaceNative(RecreationRoot root, CloudPlaceholderIdentity identity)
    {
        string path = Path.Combine(root.FileSystem.SyncRootPath, root.Target);
        if (root.Kind == CloudItemKind.Directory) { Directory.Delete(path); }
        else { File.Delete(path); }
        CloudPlaceholderSpec spec = root.Kind == CloudItemKind.Directory
            ? CloudDirectoryPlaceholderSpec.CreateBuilder("recreated", identity).WithInSyncState(true).Build()
            : CloudFilePlaceholderSpec.CreateBuilder("recreated", identity, 7).WithInSyncState(true).Build();
        CloudPlaceholderNativeBatchResult result = CloudPlaceholderPlatform.CreatePlaceholders(Path.GetDirectoryName(path)!, [spec], true);
        Assert.Equal(0, result.HResult);
        Assert.Equal(1U, result.EntriesProcessed);
        Assert.Equal(0, Assert.Single(result.Entries).HResult);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task StandaloneKeepRemoteValidatesCreationBeforeRemovingConflict(bool directory, bool replace)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        CloudRemoteChange original = root.SuccessorBatch.Changes[0];
        CloudRemoteChange stale = new(original.ChangeId, original.Kind, original.RemoteId, original.RemoteRevision,
            original.ItemKind, original.RelativePath, original.ItemId, previousRemoteRevision: "missing-revision",
            length: original.Length, metadata: original.Metadata);
        CloudRemoteChangeBatch batch = new("successor", new byte[] { 2 }, [stale], new byte[] { 3 });
        CloudRemoteApplyResult initial = await root.FileSystem.ApplyRemoteChangesAsync(batch);
        Guid conflict = Assert.Single(initial.ConflictIds);
        if (replace)
        {
            CloudPlaceholderIdentity foreign = new(Guid.NewGuid(), "foreign", "f1");
            root.Factory.AfterProjection = () => ReplaceNative(root, foreign);
            await Assert.ThrowsAsync<IOException>(() => root.FileSystem.ResolveRemoteConflictAsync(
                conflict, new(CloudRemoteConflictDecision.KeepRemote)).AsTask());
            Assert.Equal(foreign.ItemId, (await root.InspectNativeIdentityAsync()).ItemId);
        }
        else
        {
            Assert.Equal(CloudRemoteApplyEntryStatus.Applied, (await root.FileSystem.ResolveRemoteConflictAsync(
                conflict, new(CloudRemoteConflictDecision.KeepRemote))).Status);
            await root.AssertGenerationsAsync();
        }
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        if (replace)
        {
            Assert.NotNull(await verify.Conflicts.GetAsync(conflict));
            Assert.Single(await verify.Checkpoints.ListAsync(RemoteCreationIntent.Prefix));
        }
        else
        {
            Assert.Null(await verify.Conflicts.GetAsync(conflict));
            Assert.Empty(await verify.Checkpoints.ListAsync(RemoteCreationIntent.Prefix));
        }
    }
}
