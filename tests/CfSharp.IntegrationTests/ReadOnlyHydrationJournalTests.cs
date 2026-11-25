namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    private const string OriginalSizePath = "original.bin";

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(CloudInSyncPolicy.None)]
    [InlineData(CloudInSyncPolicy.TrackAll)]
    public async Task ReadOnlyHydrationLeavesNoContentUpdateAndPreservesAnOrdinaryWrite(CloudInSyncPolicy inSyncPolicy)
    {
        byte[] bytes = new byte[65_537];
        new Random(20261125).NextBytes(bytes);
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path), inSyncPolicy, hydrationContent: bytes);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(25));
        CancellationToken token = deadline.Token;
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "read-only-hydration", "revision-1");
        (await fixture.System.Root.CreatePlaceholdersAsync([
            CloudFilePlaceholderSpec.CreateBuilder(fixture.File.RelativePath, identity, bytes.Length)
                .WithInSyncState(true).Build(),
        ], cancellationToken: token)).ThrowIfAnyFailed();
        CloudItemSnapshot before = await fixture.File.InspectAsync(token);
        Assert.Equal(CloudContentAvailability.OnlineOnly, before.ContentAvailability);
        Assert.Equal(0, before.ModifiedDataSize);
        string fullPath = fixture.File.FullPath;

        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed();
        await feed.StartAsync(token);
        Assert.Empty(await AccessRowsAsync(factory!));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fullPath, token));
        Assert.True(fixture.Provider.Fetches > 0);
        CloudItemSnapshot hydrated = await fixture.File.InspectAsync(token);
        Assert.Equal((before.LocalBinding, identity.ItemId, identity.RemoteRevision),
            (hydrated.LocalBinding, hydrated.ItemId, hydrated.RemoteRevision));
        Assert.Equal(CloudSynchronizationState.InSync, hydrated.SynchronizationState);
        Assert.Equal(0, hydrated.ModifiedDataSize);
        Assert.Equal(before.LastWriteTime, hydrated.LastWriteTime);
        await WaitForHydrationJournalFenceAsync(fixture, factory!, "read-finished");
        IReadOnlyList<CloudOperationJournalEntry> afterRead = await AccessRowsAsync(factory!);

        bytes[0] ^= 0xff;
        await using (FileStream writer = new(fullPath, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            writer.WriteByte(bytes[0]);
            await writer.FlushAsync(token);
        }
        await WaitForAccessRowsAsync(factory!, rows => rows.Any(row => row.Kind == CloudStateOperationKind.ContentUpdate &&
            row.ItemId == identity.ItemId && afterRead.All(previous => previous.OperationId != row.OperationId)));
        Assert.DoesNotContain(afterRead, row => row.Kind == CloudStateOperationKind.ContentUpdate &&
            LocalChangePayload.Decode(row.Payload).RelativePath == fixture.File.RelativePath);
        await feed.DisposeAsync();
        IReadOnlyList<CloudOperationJournalEntry> edits = (await AccessRowsAsync(factory!)).Where(row =>
            row.Kind == CloudStateOperationKind.ContentUpdate && row.ItemId == identity.ItemId).ToArray();
        Assert.NotEmpty(edits);
        await fixture.RestartAsync();
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed();
        await reopened.StartAsync(token);
        int fetches = fixture.Provider.Fetches;
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fullPath, token));
        Assert.Equal(fetches, fixture.Provider.Fetches);
        await WaitForHydrationJournalFenceAsync(fixture, factory!, "dirty-read-finished");
        IReadOnlyList<CloudOperationJournalEntry> retained = (await AccessRowsAsync(factory!)).Where(row =>
            row.Kind == CloudStateOperationKind.ContentUpdate && row.ItemId == identity.ItemId).ToArray();
        Assert.Equal(edits.Select(row => (row.OperationId, row.Sequence)), retained.Select(row => (row.OperationId, row.Sequence)));
        CloudRemoteChange update = new("content", CloudRemoteChangeKind.FileUpsert, identity.RemoteId, "revision-2",
            CloudItemKind.File, fixture.File.RelativePath, identity.ItemId, previousRemoteRevision: identity.RemoteRevision,
            length: bytes.Length, metadata: CloudPlaceholderMetadata.CreateFileBuilder().Build());
        CloudRemoteApplyEntryResult conflict = Assert.Single((await fixture.System.ApplyRemoteChangesAsync(
            new("dirty-hydration-update", Array.Empty<byte>(), [update], new byte[] { 1 }), cancellationToken: token)).Entries);
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, conflict.Status);
        Assert.Equal(CloudRemoteConflictReason.Content, conflict.Conflict!.Reason);
        CloudLocalChangePage page = await reopened.ReadPageAsync(await reopened.BeginScanAsync(token), 0, 100, token);
        Assert.False(page.RequiresFullRescan);
        foreach (CloudOperationJournalEntry edit in edits)
        {
            Assert.Contains(page.Changes, change => change.OperationId == edit.OperationId &&
                change.Sequence == edit.Sequence && change.ItemId == identity.ItemId);
            await reopened.AcknowledgeAsync([edit.OperationId], token);
        }
        Assert.DoesNotContain(await AccessRowsAsync(factory!), row => edits.Any(edit => edit.OperationId == row.OperationId));
    }

    [Trait("Category", "AccessJournal")]
    [Fact]
    public async Task PartialReadOnlyHydrationAndReconnectPreserveTheAcceptedContent()
    {
        byte[] bytes = new byte[256 * 1024];
        new Random(20261125).NextBytes(bytes);
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path), hydrationContent: bytes,
            hydrationPolicy: CloudHydrationPolicy.Partial);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(25));
        CancellationToken token = deadline.Token;
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "partial-read-only-hydration", "revision-1");
        (await fixture.System.Root.CreatePlaceholdersAsync([
            CloudFilePlaceholderSpec.CreateBuilder(fixture.File.RelativePath, identity, bytes.Length)
                .WithInSyncState(true).Build(),
        ], cancellationToken: token)).ThrowIfAnyFailed();
        CloudLocalFileBinding? original = (await fixture.File.InspectAsync(token)).LocalBinding;
        await using (CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed())
        {
            await feed.StartAsync(token);
            await fixture.File.HydrateAsync(new(0, Environment.SystemPageSize), token);
            CloudItemSnapshot partial = await fixture.File.InspectAsync(token);
            Assert.Equal(CloudContentAvailability.PartiallyAvailable, partial.ContentAvailability);
            Assert.Equal(CloudSynchronizationState.InSync, partial.SynchronizationState);
            await WaitForHydrationJournalFenceAsync(fixture, factory!, "partial-read-finished");
            Assert.DoesNotContain(await AccessRowsAsync(factory!), IsHydrationContentRow);
        }
        int firstFetches = fixture.Provider.Fetches;
        await fixture.RestartAsync();
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed();
        await reopened.StartAsync(token);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.File.FullPath, token));
        Assert.True(fixture.Provider.Fetches > firstFetches);
        int completedFetches = fixture.Provider.Fetches;
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.File.FullPath, token));
        Assert.Equal(completedFetches, fixture.Provider.Fetches);
        await WaitForHydrationJournalFenceAsync(fixture, factory!, "complete-read-finished");
        CloudItemSnapshot complete = await fixture.File.InspectAsync(token);
        Assert.Equal((original, identity.ItemId, identity.RemoteRevision), (complete.LocalBinding, complete.ItemId, complete.RemoteRevision));
        Assert.Equal(CloudSynchronizationState.InSync, complete.SynchronizationState);
        Assert.DoesNotContain(await AccessRowsAsync(factory!), IsHydrationContentRow);
        CloudLocalChangePage page = await reopened.ReadPageAsync(await reopened.BeginScanAsync(token), 0, 100, token);
        Assert.False(page.RequiresFullRescan);
        Assert.DoesNotContain(page.Changes, row => row.Kind == CloudLocalChangeKind.ContentUpdate &&
            row.RelativePath == fixture.File.RelativePath);

        bool IsHydrationContentRow(CloudOperationJournalEntry row) => row.Kind == CloudStateOperationKind.ContentUpdate &&
            LocalChangePayload.Decode(row.Payload).RelativePath == fixture.File.RelativePath;
    }

    [Trait("Category", "AccessJournal")]
    [Fact]
    public async Task DelayedHydrationSizeNoticeAndGenuineWriteKeepSeparateJournalIds()
    {
        byte[] bytes = new byte[65_537];
        new Random(20261125).NextBytes(bytes);
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path), hydrationContent: bytes);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(25));
        CancellationToken token = deadline.Token;
        string path = fixture.File.FullPath;
        string name = fixture.File.RelativePath;
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "delayed-read-only-hydration", "revision-1");
        (await fixture.System.Root.CreatePlaceholdersAsync([
            CloudFilePlaceholderSpec.CreateBuilder(name, identity, bytes.Length).WithInSyncState(true).Build(),
        ], cancellationToken: token)).ThrowIfAnyFailed();
        ControlledMetadataSource source = new();
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(null, source);
        await feed.StartAsync(token);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, token));
        CloudItemSnapshot hydrated = await fixture.File.InspectAsync(token);
        Assert.True(hydrated.PlaceholderState.HasFlag(CloudPlaceholderState.InSync));
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>(hydrated.LocalBinding);
        long fileId = Assert.IsType<long>(hydrated.LocalFileId);
        // The live-feed tests cover the real size subscription. Freeze the native
        // snapshot here so the delayed-delivery test does not depend on whether
        // Windows generates a size notice for this particular allocation.
        LocalSizeChange size = new(new(binding.VolumeSerialNumber, binding.SyncRootFileId,
            unchecked((ulong)fileId), ParentFileId: 0, IsDirectory: false), bytes.Length, hydrated.PlaceholderState);
        LocalChangeSourceEvent hydration = new(LocalChangeSourceAction.SizeModified, name, SizeChange: size);
        LocalChangeSourceEvent write = await CaptureNativeModificationAsync(fixture.Root, name, async () =>
        {
            await using FileStream writer = new(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            writer.WriteByte((byte)(bytes[0] ^ 0xff));
            await writer.FlushAsync(token);
        }, token);
        await source.EmitAsync(hydration);
        await source.EmitAsync(write);
        await WaitForAccessRowsAsync(factory!, rows => rows.Count == 2);
        IReadOnlyList<CloudOperationJournalEntry> originals = await AccessRowsAsync(factory!);
        Assert.Null(originals[0].ItemId);
        Assert.Equal(identity.ItemId, originals[1].ItemId);
        Assert.True((await feed.BeginScanAsync(token)).RequiresFullRescan);
        await feed.DisposeAsync();
        await fixture.RestartAsync();
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed(null, new ControlledMetadataSource());
        await reopened.StartAsync(token);
        await reopened.AcknowledgeFullRescanAsync(reopened.BeginFullRescan(), token);
        CloudLocalChangePage page = await reopened.ReadPageAsync(await reopened.BeginScanAsync(token), 0, 10, token);
        Assert.Equal(originals.Select(row => (row.OperationId, row.Sequence, row.ItemId)),
            page.Changes.Select(row => (row.OperationId, row.Sequence, row.ItemId)));
        Assert.All(page.Changes, row => Assert.Equal(CloudLocalChangeKind.ContentUpdate, row.Kind));
        await reopened.AcknowledgeAsync([originals[0].OperationId], token);
        Assert.Equal(originals[1].OperationId, Assert.Single(await AccessRowsAsync(factory!)).OperationId);
        await reopened.AcknowledgeAsync([originals[1].OperationId], token);
        Assert.Empty(await AccessRowsAsync(factory!));
        bytes[0] ^= 0xff;
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, token));
    }

    private static async Task WaitForHydrationJournalFenceAsync(Fixture fixture, AccessJournalFactory factory, string name)
    {
        Directory.CreateDirectory(Path.Combine(fixture.Root, name));
        await WaitForAccessRowsAsync(factory, rows => rows.Any(row => row.Kind == CloudStateOperationKind.Create &&
            LocalChangePayload.Decode(row.Payload).RelativePath == name));
    }

    [Fact]
    public async Task DelayedOrdinarySizeNoticeDoesNotInterruptTheOrderedMove()
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        const string original = OriginalSizePath;
        const string moved = "moved.bin";
        string originalPath = Path.Combine(fixture.Root, original);
        ControlledMetadataSource source = new();
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(null, source);
        await feed.StartAsync(token);
        await File.WriteAllTextAsync(originalPath, "local content", token);
        LocalChangeSourceEvent size = await CaptureOrdinarySizeChangeAsync(fixture.Root, original, originalPath, token);
        Assert.False(size.SizeChange!.Value.PlaceholderState.HasFlag(CloudPlaceholderState.Placeholder));
        await source.EmitAsync(new(LocalChangeSourceAction.Created, original));
        await WaitForAccessRowsAsync(factory!, rows => rows.Count == 1);
        File.Move(originalPath, Path.Combine(fixture.Root, moved));
        await source.EmitAsync(new(LocalChangeSourceAction.RenamedOldName, original));
        await source.EmitAsync(new(LocalChangeSourceAction.RenamedNewName, moved));
        await source.EmitAsync(size);
        await WaitForAccessRowsAsync(factory!, rows => rows.Count == 3);

        CloudLocalChangePage page = await feed.ReadPageAsync(await feed.BeginScanAsync(token), 0, 10, token);
        Assert.False(page.RequiresFullRescan);
        Assert.Collection(page.Changes,
            change => { Assert.Equal(CloudLocalChangeKind.Create, change.Kind); Assert.NotNull(change.ItemId); },
            change => { Assert.Equal(CloudLocalChangeKind.Move, change.Kind); Assert.Equal(moved, change.RelativePath); },
            change =>
            {
                Assert.Equal(CloudLocalChangeKind.ContentUpdate, change.Kind);
                Assert.Equal(original, change.RelativePath);
                Assert.Null(change.ItemId);
            });
        Assert.Equal(page.Changes[0].ItemId, page.Changes[1].ItemId);
        await using ICloudStateTransaction state = await factory!.Store!.BeginTransactionAsync(token);
        Assert.Null(await state.Items.GetByRelativePathAsync(original, token));
    }

    private static Task<LocalChangeSourceEvent> CaptureOrdinarySizeChangeAsync(string root, string relativePath, string fullPath, CancellationToken token) =>
        CaptureNativeModificationAsync(root, relativePath, () =>
        {
            using FileStream file = new(fullPath, FileMode.Open, FileAccess.Write, FileShare.Read);
            file.SetLength(4096);
            file.Flush(flushToDisk: true);
            return Task.CompletedTask;
        }, token, LocalChangeSourceAction.SizeModified);

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData("size-before-discovery")]
    [InlineData("size-before-commit")]
    public async Task ReplacedSizeObservationDoesNotDiscoverTheNewOccupant(string boundary)
    {
        MetadataFaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path, boundary));
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        string path = Path.Combine(fixture.Root, OriginalSizePath);
        string retiredPath = Path.Combine(fixture.Root, "retired.bin");
        await File.WriteAllTextAsync(path, "old", token);
        LocalChangeSourceEvent size = await CaptureOrdinarySizeChangeAsync(fixture.Root, OriginalSizePath, path, token);
        ControlledMetadataSource source = new();
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(null, source);
        await feed.StartAsync(token);
        factory!.Armed = true;
        await source.EmitAsync(size);
        try
        {
            await factory.OutcomePaused.Task.WaitAsync(token);
            File.Move(path, retiredPath);
            await File.WriteAllBytesAsync(path, new byte[4096], token);
        }
        finally { factory.ResumeOutcome.TrySetResult(); }

        CloudOperationJournalEntry original = await WaitForSingleMetadataJournalEntryAsync(factory.Store!, token);
        Assert.Null(original.ItemId);
        Assert.Equal(CloudStateOperationKind.ContentUpdate, original.Kind);
        await using (ICloudStateTransaction transaction = await factory.Store!.BeginTransactionAsync(token))
        {
            Assert.Null(await transaction.Items.GetByRelativePathAsync(OriginalSizePath, token));
        }

        Assert.True((await feed.BeginScanAsync(token)).RequiresFullRescan);
        await feed.AcknowledgeFullRescanAsync(feed.BeginFullRescan(), token);
        CloudLocalChangePage page = await feed.ReadPageAsync(await feed.BeginScanAsync(token), 0, 10, token);
        CloudLocalChange retained = Assert.Single(page.Changes);
        Assert.Equal((original.OperationId, original.Sequence, OriginalSizePath), (retained.OperationId, retained.Sequence, retained.RelativePath));
        Assert.Null(retained.ItemId);
        Assert.Equal(new byte[4096], await File.ReadAllBytesAsync(path, token));
        Assert.Equal((byte)'o', (await File.ReadAllBytesAsync(retiredPath, token))[0]);
    }
}
