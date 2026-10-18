using Microsoft.Win32.SafeHandles;

namespace CfSharp.IntegrationTests;

public sealed partial class RemoteDeleteRecoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ProtectedDeleteRetainsCleanObjectsAndDurableConflictAcrossRestart(bool directory, bool children)
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(directory, children: children);
        string path = System.IO.Path.Combine(root.Path, root.Target);
        CloudItemState original;
        await using (ICloudStateTransaction capture = await root.Factory.Store!.BeginTransactionAsync())
        {
            original = (await capture.Items.GetByItemIdAsync(root.ItemId))!;
        }

        using (SafeFileHandle writer = CreateFile(path, 0x180, 7, 0, 3, 0x02200000, 0))
        {
            Assert.False(writer.IsInvalid);
            CloudRemoteApplyResult result = await root.ApplyProtectedAsync();
            Assert.False(result.RequiresRetry);
            Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, Assert.Single(result.Entries).Status);
            Guid conflictId = Assert.Single(result.ConflictIds);
            Assert.Equal(CloudRemoteConflictReason.Delete, result.Entries[0].Conflict!.Reason);
            // A durably retained conflict is a processed batch entry, not a deleted object.
            Assert.Equal(1, result.AppliedEntryCount);
            Assert.Equal(new byte[] { 9 }, result.SafeCursor.ToArray());
            SetNativeAttributes(writer, File.GetAttributes(path) | FileAttributes.Hidden);
            await root.RestartAsync();
            SetNativeAttributes(writer, File.GetAttributes(path) | FileAttributes.System);
            CloudRemoteApplyResult replay = await root.ApplyProtectedAsync();
            Assert.Equal(CloudRemoteApplyEntryStatus.AlreadyApplied, Assert.Single(replay.Entries).Status);
            Assert.Empty(replay.ConflictIds);
            Assert.NotNull(await root.FileSystem.GetRemoteConflictAsync(conflictId));
            await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
            Assert.Equivalent(original, await verify.Items.GetByItemIdAsync(root.ItemId), strict: true);
            Assert.Empty(await verify.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix));
            Assert.Single(await verify.Conflicts.ListAsync());
            Assert.Empty(await verify.Operations.ListByItemIdAsync(root.ItemId, int.MaxValue));
        }

        await root.RestartAsync();
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden | FileAttributes.System));
        if (children)
        {
            Assert.Equal(2, Directory.EnumerateFileSystemEntries(path).Count());
        }
        Assert.Equal(0, root.Provider.Reads);
    }

    [Theory]
    [InlineData(false, false, "disabled")]
    [InlineData(true, false, "disabled")]
    [InlineData(true, true, "disabled")]
    [InlineData(false, false, "resolver")]
    [InlineData(true, false, "resolver")]
    [InlineData(true, true, "resolver")]
    [InlineData(false, false, "standalone")]
    [InlineData(true, false, "standalone")]
    [InlineData(true, true, "standalone")]
    public async Task ExplicitAuthorizationDeletesCleanObjectsAndCompletesOriginalEvidence(bool directory, bool children, string authorization)
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(directory, children: children);
        KeepRemoteResolver resolver = new();
        if (authorization == "standalone")
        {
            Guid conflictId = Assert.Single((await root.ApplyProtectedAsync()).ConflictIds);
            await root.RestartAsync();
            CloudRemoteApplyEntryResult resolved = await root.FileSystem.ResolveRemoteConflictAsync(conflictId,
                new(CloudRemoteConflictDecision.KeepRemote));
            Assert.Equal(CloudRemoteApplyEntryStatus.Applied, resolved.Status);
            Assert.Null(await root.FileSystem.GetRemoteConflictAsync(conflictId));
        }
        else
        {
            CloudRemoteApplyResult result = await root.FileSystem.ApplyRemoteChangesAsync(root.Batch,
                authorization == "disabled" ? DestructiveOptions : new() { ConflictResolver = resolver });
            Assert.False(result.RequiresRetry, result.Entries[0].Error?.ToString());
            Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single(result.Entries).Status);
            Assert.Empty(result.ConflictIds);
            Assert.Equal(authorization == "resolver" ? 1 : 0, resolver.Calls);
        }

        Assert.False(File.Exists(System.IO.Path.Combine(root.Path, root.Target)) || Directory.Exists(System.IO.Path.Combine(root.Path, root.Target)));
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        Assert.True((await verify.Items.GetByItemIdAsync(root.ItemId))!.IsTombstone);
        Assert.Empty(await verify.Conflicts.ListAsync());
        Assert.Empty(await verify.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix));
        Assert.Equal(0, root.Provider.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InlineKeepRemoteAuthorizationSurvivesPreparationAndRestart(bool directory)
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(directory);
        KeepRemoteResolver resolver = new();
        root.Factory.Boundary = "prepare-after";
        root.Factory.FailuresRemaining = 1;
        CloudRemoteApplyResult interrupted = await root.FileSystem.ApplyRemoteChangesAsync(root.Batch,
            new() { ConflictResolver = resolver });
        Assert.True(interrupted.RequiresRetry);
        Assert.Equal(1, resolver.Calls);
        await using (ICloudStateTransaction capture = await root.Factory.Store!.BeginTransactionAsync())
        {
            RemoteDeleteIntent intent = RemoteDeleteIntent.Decode(Assert.Single(await capture.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix)).Value);
            Assert.True(intent.PreserveContent);
            Assert.False(intent.RequirePreservation);
            Assert.All(intent.Members, member => Assert.Null(member.DeletedAtTicks));
        }
        await root.RestartAsync();
        // The explicit decision is already sealed. Recovery does not need another resolver
        // or mistake the original requested policy for effective destructive authorization.
        CloudRemoteApplyResult replay = await root.ApplyProtectedAsync();
        Assert.False(replay.RequiresRetry, replay.Entries[0].Error?.ToString());
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single(replay.Entries).Status);
        Assert.Empty(replay.ConflictIds);
        Assert.Equal(1, resolver.Calls);
        Assert.False(File.Exists(System.IO.Path.Combine(root.Path, root.Target)) || Directory.Exists(System.IO.Path.Combine(root.Path, root.Target)));
        await using ICloudStateTransaction completed = await root.Factory.Store!.BeginTransactionAsync();
        Assert.True((await completed.Items.GetByItemIdAsync(root.ItemId))!.IsTombstone);
        Assert.Empty(await completed.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix));
        Assert.Equal(0, root.Provider.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlierProtectedPreparationCannotAuthorizeDeletionEvenWithAnUnchangedSnapshot(bool directory)
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(directory);
        root.Factory.Boundary = "prepare-after";
        root.Factory.FailuresRemaining = 1;
        Assert.True((await root.ApplyAuthorizedAsync()).RequiresRetry);
        byte[] original = await RetainAsEarlierProtectedPreparationAsync(root);
        await root.RestartAsync();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            CloudRemoteApplyResult replay = await root.ApplyProtectedAsync();
            Assert.True(replay.RequiresRetry);
            Assert.Equal(0, replay.AppliedEntryCount);
            Assert.Equal(new byte[] { 1 }, replay.SafeCursor.ToArray());
            Assert.IsType<CloudRemoteDeleteRecoveryException>(Assert.Single(replay.Entries).Error);
            Assert.Empty(replay.ConflictIds);
            await using (ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync())
            {
                Assert.Equal(original, Assert.Single(await verify.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix)).Value.ToArray());
                Assert.False((await verify.Items.GetByItemIdAsync(root.ItemId))!.IsTombstone);
            }
            await root.RestartAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => root.ApplyAuthorizedAsync());
        Assert.True(File.Exists(System.IO.Path.Combine(root.Path, root.Target)) || Directory.Exists(System.IO.Path.Combine(root.Path, root.Target)));
        Assert.Equal(0, root.Provider.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlierProtectedAcceptanceMayFinishAfterTheQueryHandleCloses(bool directory)
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(directory);
        using (SafeFileHandle query = OpenAttributeQuery(System.IO.Path.Combine(root.Path, root.Target)))
        {
            Assert.False(query.IsInvalid);
            Assert.True((await root.ApplyAuthorizedAsync()).RequiresRetry);
            await RetainAsEarlierProtectedPreparationAsync(root);
            await root.RestartAsync();
            Assert.True((await root.ApplyProtectedAsync()).RequiresRetry);
            await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
            RemoteDeleteMember member = Assert.Single(RemoteDeleteIntent.Decode(Assert.Single(
                await verify.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix)).Value).Members);
            Assert.NotNull(member.DeletedAtTicks);
            Assert.Null(member.ProjectedAtTicks);
        }
        await root.RestartAsync();
        CloudRemoteApplyResult replay = await root.ApplyProtectedAsync();
        Assert.False(replay.RequiresRetry, replay.Entries[0].Error?.ToString());
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single(replay.Entries).Status);
        Assert.Equal(new byte[] { 9 }, replay.SafeCursor.ToArray());
        await using ICloudStateTransaction completed = await root.Factory.Store!.BeginTransactionAsync();
        Assert.True((await completed.Items.GetByItemIdAsync(root.ItemId))!.IsTombstone);
        Assert.Empty(await completed.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix));
    }

    [Fact]
    public async Task EarlierProtectedPartialTreeRetainsItsCompletedChildrenAndUndeletedParent()
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(true, children: true);
        root.Factory.Boundary = "projection-after";
        root.Factory.FailuresRemaining = 1;
        Assert.True((await root.ApplyAuthorizedAsync()).RequiresRetry);
        byte[] original = await RetainAsEarlierProtectedPreparationAsync(root);
        await root.RestartAsync();
        CloudRemoteApplyResult replay = await root.ApplyProtectedAsync();
        Assert.True(replay.RequiresRetry);
        Assert.Equal(0, replay.AppliedEntryCount);
        Assert.IsType<CloudRemoteDeleteRecoveryException>(Assert.Single(replay.Entries).Error);
        Assert.Empty(replay.ConflictIds);
        Assert.Single(Directory.EnumerateFileSystemEntries(System.IO.Path.Combine(root.Path, root.Target)));
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        Assert.Equal(original, Assert.Single(await verify.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix)).Value.ToArray());
        Assert.False((await verify.Items.GetByItemIdAsync(root.ItemId))!.IsTombstone);
        Assert.Single(RemoteDeleteIntent.Decode(original).Members.Where(member => member.ProjectedAtTicks.HasValue));
    }

    private static async Task<byte[]> RetainAsEarlierProtectedPreparationAsync(DeleteRoot root)
    {
        // Use real inspected objects and real commit-boundary receipts. Only restore the
        // two policy flags written by earlier protected producers; never invent acceptance.
        await using ICloudStateTransaction seed = await root.Factory.Store!.BeginTransactionAsync();
        CloudStateCheckpoint checkpoint = Assert.Single(await seed.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix));
        RemoteDeleteIntent intent = RemoteDeleteIntent.Decode(checkpoint.Value) with { PreserveContent = true, RequirePreservation = true };
        byte[] value = intent.Encode();
        await seed.Checkpoints.UpsertAsync(new(checkpoint.Name, value, checkpoint.UpdatedAt));
        await seed.CommitAsync();
        return value;
    }

    private sealed class KeepRemoteResolver : ICloudRemoteConflictResolver
    {
        internal int Calls { get; private set; }
        public ValueTask<CloudRemoteConflictResolution> ResolveAsync(CloudRemoteConflict conflict, CancellationToken cancellationToken = default)
        {
            Assert.Equal(CloudRemoteConflictReason.Delete, conflict.Reason);
            Calls++;
            return ValueTask.FromResult(new CloudRemoteConflictResolution(CloudRemoteConflictDecision.KeepRemote));
        }
    }
}
