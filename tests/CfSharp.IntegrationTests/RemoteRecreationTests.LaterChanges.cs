namespace CfSharp.IntegrationTests;

public sealed partial class RemoteRecreationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectedCreationStillFencesObservationsUntilOriginalOutcomeCommits(bool directory)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        root.Factory.Boundary = "outcome-before";
        await Assert.ThrowsAsync<IOException>(() => root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch).AsTask());
        RecreationSource source = new();
        await using CloudLocalChangeFeed feed = CloudLocalChangeFeed.CreateForTesting(root.FileSystem.SyncRootPath,
            root.Factory.Store!, new(), source);
        await feed.StartAsync();
        await source.EmitAsync(new(LocalChangeSourceAction.Modified, root.Target));
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        while (!(await feed.ReadBatchAsync(deadline.Token)).RequiresFullRescan) { await Task.Delay(10, deadline.Token); }
        CloudLocalChangeScan scan = await feed.BeginScanAsync();
        Assert.True(scan.RequiresFullRescan);
        Assert.True((await feed.ReadPageAsync(scan, 0, 4)).RequiresFullRescan);
        await Assert.ThrowsAsync<InvalidOperationException>(() => feed.AcknowledgeFullRescanAsync().AsTask());
        await using (ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync())
        {
            Assert.Empty(await verify.Operations.ListByItemIdAsync(root.SuccessorId, 10));
            Assert.True(RemoteCreationIntent.Decode(Assert.Single(await verify.Checkpoints.ListAsync(RemoteCreationIntent.Prefix)).Value).Committed);
        }
        AssertApplied(await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch), root.SuccessorBatch);
        await root.AssertGenerationsAsync();
        await feed.AcknowledgeFullRescanAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorizedResurrectionRetainsItsOriginalJournalWithoutAcknowledgingIt(bool directory)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        Guid operationId = Guid.NewGuid();
        byte[] payload = new LocalChangePayload(root.Target, null, directory, DateTimeOffset.UtcNow).Encode();
        await using (ICloudStateTransaction seed = await root.Factory.Store!.BeginTransactionAsync())
        {
            await seed.Operations.EnqueueAsync(new(operationId, CloudStateOperationKind.Delete, root.OriginalId, payload, DateTimeOffset.UtcNow));
            await seed.CommitAsync();
        }
        CloudRemoteChangeBatch resurrection = root.Birth("authorized-resurrection", root.OriginalId, "original-remote", "v3", 7, 2, 3);
        AssertApplied(await root.FileSystem.ApplyRemoteChangesAsync(resurrection, AuthorizedDelete), resurrection);
        await root.RestartAsync();
        Assert.False((await root.FileSystem.ApplyRemoteChangesAsync(resurrection, AuthorizedDelete)).RequiresRetry);
        Assert.Equal(root.OriginalId, (await root.InspectNativeIdentityAsync()).ItemId);
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        CloudOperationJournalEntry operation = (await verify.Operations.GetAsync(operationId))!;
        Assert.Equal(root.OriginalId, operation.ItemId);
        Assert.Equal(payload, operation.Payload.ToArray());
        Assert.False((await verify.Items.GetByItemIdAsync(root.OriginalId))!.IsTombstone);
        Assert.Equal(0, root.Reads);
    }

    [Theory]
    [InlineData(false, "projection-before")]
    [InlineData(true, "projection-before")]
    [InlineData(false, "outcome-before")]
    [InlineData(true, "outcome-before")]
    public async Task CreationRecoveryPreservesLaterAttributesEvenWhenTheObjectIsInSync(bool directory, string boundary)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        root.Factory.Boundary = boundary;
        if (boundary == "outcome-before")
        {
            Assert.Same(root.Factory.Error, await Assert.ThrowsAsync<IOException>(() =>
                root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch).AsTask()));
        }
        else { Assert.True((await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch)).RequiresRetry); }
        string path = Path.Combine(root.FileSystem.SyncRootPath, root.Target);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden | FileAttributes.System);
        await root.Item.SetInSyncAsync(true);
        Assert.Equal(CloudSynchronizationState.InSync, (await root.Item.InspectAsync()).SynchronizationState);
        byte[] prepared;
        await using (ICloudStateTransaction capture = await root.Factory.Store!.BeginTransactionAsync())
        {
            prepared = Assert.Single(await capture.Checkpoints.ListAsync(RemoteCreationIntent.Prefix)).Value.ToArray();
        }
        await root.RestartAsync();
        CloudRemoteApplyResult replay = await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch);
        output.WriteLine($"Later attribute replay: directory={directory}; boundary={boundary}; retry={replay.RequiresRetry}; prefix={replay.AppliedEntryCount}; attributes={File.GetAttributes(path)}.");
        Assert.True(replay.RequiresRetry);
        Assert.Equal(0, replay.AppliedEntryCount);
        Assert.IsType<IOException>(Assert.Single(replay.Entries).Error);
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden | FileAttributes.System));
        Assert.Equal(root.SuccessorId, (await root.InspectNativeIdentityAsync()).ItemId);
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        Assert.Equal(prepared, Assert.Single(await verify.Checkpoints.ListAsync(RemoteCreationIntent.Prefix)).Value.ToArray());
        Assert.True((await verify.Items.GetByItemIdAsync(root.OriginalId))!.IsTombstone);
        Assert.Equal(0, root.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorizedCreationRecoveryDoesNotIgnoreLaterLocalJournalOperations(bool directory)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        root.Factory.Boundary = "outcome-before";
        CloudRemoteApplyOptions authorized = new() { PreserveUnsynchronizedLocalContent = false };
        Assert.Same(root.Factory.Error, await Assert.ThrowsAsync<IOException>(() =>
            root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch, authorized).AsTask()));
        Guid operationId = Guid.NewGuid();
        byte[] prepared;
        byte[] payload = new LocalChangePayload(root.Target, null, directory, DateTimeOffset.UtcNow).Encode();
        await using (ICloudStateTransaction write = await root.Factory.Store!.BeginTransactionAsync())
        {
            prepared = Assert.Single(await write.Checkpoints.ListAsync(RemoteCreationIntent.Prefix)).Value.ToArray();
            await write.Operations.EnqueueAsync(new(operationId, CloudStateOperationKind.MetadataUpdate,
                root.SuccessorId, payload, DateTimeOffset.UtcNow));
            await write.CommitAsync();
        }
        await root.RestartAsync();
        CloudRemoteApplyResult replay = await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch, authorized);
        output.WriteLine($"Later journal replay: directory={directory}; retry={replay.RequiresRetry}; prefix={replay.AppliedEntryCount}.");
        Assert.True(replay.RequiresRetry);
        Assert.Equal(0, replay.AppliedEntryCount);
        Assert.IsType<IOException>(Assert.Single(replay.Entries).Error);
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        Assert.Equal(prepared, Assert.Single(await verify.Checkpoints.ListAsync(RemoteCreationIntent.Prefix)).Value.ToArray());
        CloudOperationJournalEntry retained = (await verify.Operations.GetAsync(operationId))!;
        Assert.Equal(root.SuccessorId, retained.ItemId);
        Assert.Equal(payload, retained.Payload.ToArray());
        Assert.True((await verify.Items.GetByItemIdAsync(root.OriginalId))!.IsTombstone);
        Assert.Equal(0, root.Reads);
    }
}
