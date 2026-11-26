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

        HydrationJournalSource source = new(fixture.Root);
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(null, source);
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
        await source.WaitForFenceAsync(factory!, "read-finished", SizeFromPlaceholderSnapshot(fixture.File.RelativePath, hydrated));
        IReadOnlyList<CloudOperationJournalEntry> afterRead = await AccessRowsAsync(factory!);
        Assert.DoesNotContain(afterRead, row => row.Kind == CloudStateOperationKind.ContentUpdate &&
            LocalChangePayload.Decode(row.Payload).RelativePath == fixture.File.RelativePath);

        bytes[0] ^= 0xff;
        await using (FileStream writer = new(fullPath, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            writer.WriteByte(bytes[0]);
            await writer.FlushAsync(token);
        }
        await WaitForAccessRowsAsync(factory!, rows => rows.Any(row => row.Kind == CloudStateOperationKind.ContentUpdate &&
            row.ItemId == identity.ItemId && afterRead.All(previous => previous.OperationId != row.OperationId)));
        await feed.DisposeAsync();
        IReadOnlyList<CloudOperationJournalEntry> edits = (await AccessRowsAsync(factory!)).Where(row =>
            row.Kind == CloudStateOperationKind.ContentUpdate && row.ItemId == identity.ItemId).ToArray();
        Assert.NotEmpty(edits);
        await fixture.RestartAsync();
        HydrationJournalSource restartedSource = new(fixture.Root);
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed(null, restartedSource);
        await reopened.StartAsync(token);
        int fetches = fixture.Provider.Fetches;
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fullPath, token));
        Assert.Equal(fetches, fixture.Provider.Fetches);
        await restartedSource.WaitForFenceAsync(factory!, "dirty-read-finished");
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
        HydrationJournalSource source = new(fixture.Root);
        await using (CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(null, source))
        {
            await feed.StartAsync(token);
            await fixture.File.HydrateAsync(new(0, Environment.SystemPageSize), token);
            CloudItemSnapshot partial = await fixture.File.InspectAsync(token);
            Assert.Equal(CloudContentAvailability.PartiallyAvailable, partial.ContentAvailability);
            Assert.Equal(CloudSynchronizationState.InSync, partial.SynchronizationState);
            await source.WaitForFenceAsync(factory!, "partial-read-finished", SizeFromPlaceholderSnapshot(fixture.File.RelativePath, partial));
            Assert.DoesNotContain(await AccessRowsAsync(factory!), IsHydrationContentRow);
        }
        int firstFetches = fixture.Provider.Fetches;
        await fixture.RestartAsync();
        HydrationJournalSource restartedSource = new(fixture.Root);
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed(null, restartedSource);
        await reopened.StartAsync(token);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.File.FullPath, token));
        Assert.True(fixture.Provider.Fetches > firstFetches);
        int completedFetches = fixture.Provider.Fetches;
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.File.FullPath, token));
        Assert.Equal(completedFetches, fixture.Provider.Fetches);
        CloudItemSnapshot complete = await fixture.File.InspectAsync(token);
        Assert.Equal((original, identity.ItemId, identity.RemoteRevision), (complete.LocalBinding, complete.ItemId, complete.RemoteRevision));
        Assert.Equal(CloudSynchronizationState.InSync, complete.SynchronizationState);
        await restartedSource.WaitForFenceAsync(factory!, "complete-read-finished", SizeFromPlaceholderSnapshot(fixture.File.RelativePath, complete));
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
        Assert.Equal(CloudSynchronizationState.InSync, hydrated.SynchronizationState);
        LocalChangeSourceEvent hydration = SizeFromPlaceholderSnapshot(name, hydrated);
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

    [Trait("Category", "AccessJournal")]
    [Fact]
    public async Task ReadOnlyHydrationAfterAttributeEditRetainsUnboundSizeObservation()
    {
        byte[] bytes = new byte[65_537];
        new Random(20261125).NextBytes(bytes);
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path),
            CloudInSyncPolicy.TrackAll, hydrationContent: bytes);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(25));
        CancellationToken token = deadline.Token;
        string name = fixture.File.RelativePath;
        string path = fixture.File.FullPath;
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-only-hydration", "revision-1");
        (await fixture.System.Root.CreatePlaceholdersAsync([
            CloudFilePlaceholderSpec.CreateBuilder(name, identity, bytes.Length).WithInSyncState(true).Build(),
        ], cancellationToken: token)).ThrowIfAnyFailed();
        CloudItemSnapshot before = await fixture.File.InspectAsync(token);
        Assert.Equal(CloudSynchronizationState.InSync, before.SynchronizationState);
        Assert.Equal(CloudContentAvailability.OnlineOnly, before.ContentAvailability);

        ControlledMetadataSource source = new();
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(null, source);
        await feed.StartAsync(token);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        CloudItemSnapshot metadataOnly = await fixture.File.InspectAsync(token);
        Assert.Equal(CloudSynchronizationState.NotInSync, metadataOnly.SynchronizationState);
        Assert.Equal(CloudContentAvailability.OnlineOnly, metadataOnly.ContentAvailability);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, token));
        Assert.True(fixture.Provider.Fetches > 0);
        CloudItemSnapshot hydrated = await fixture.File.InspectAsync(token);
        Assert.Equal(CloudSynchronizationState.NotInSync, hydrated.SynchronizationState);
        Assert.False(hydrated.PlaceholderState.HasFlag(CloudPlaceholderState.InSync));
        Assert.Equal(0, hydrated.ModifiedDataSize);
        Assert.True(hydrated.OnDiskDataSize > metadataOnly.OnDiskDataSize);
        Assert.Equal((before.LocalBinding, before.Length, before.LastWriteTime),
            (hydrated.LocalBinding, hydrated.Length, hydrated.LastWriteTime));

        // Allocation notifications can be coalesced. Deliver the Size record from
        // the actual post-hydration snapshot through the controlled source.
        await source.EmitAsync(SizeFromPlaceholderSnapshot(name, hydrated));
        CloudOperationJournalEntry observation = await WaitForSingleMetadataJournalEntryAsync(factory!.Store!, token);
        Assert.Equal(CloudStateOperationKind.ContentUpdate, observation.Kind);
        Assert.Null(observation.ItemId);
        Assert.True((await feed.BeginScanAsync(token)).RequiresFullRescan);
        await feed.AcknowledgeFullRescanAsync(feed.BeginFullRescan(), token);
        CloudLocalChangePage page = await feed.ReadPageAsync(await feed.BeginScanAsync(token), 0, 10, token);
        CloudLocalChange change = Assert.Single(page.Changes);
        Assert.Equal((observation.OperationId, observation.Sequence, name, (Guid?)null),
            (change.OperationId, change.Sequence, change.RelativePath, change.ItemId));
        await feed.AcknowledgeAsync([change.OperationId], token);
    }

    private static LocalChangeSourceEvent SizeFromPlaceholderSnapshot(string relativePath, CloudItemSnapshot snapshot)
    {
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>(snapshot.LocalBinding);
        long fileId = Assert.IsType<long>(snapshot.LocalFileId);
        LocalSizeChange size = new(new(binding.VolumeSerialNumber, binding.SyncRootFileId, unchecked((ulong)fileId),
            ParentFileId: 0, IsDirectory: false), Assert.IsType<long>(snapshot.Length), snapshot.PlaceholderState);
        return new(LocalChangeSourceAction.SizeModified, relativePath, SizeChange: size);
    }

    private sealed class HydrationJournalSource(string root) : ILocalChangeSource
    {
        private const string FencePrefix = "journal-fence-";
        private readonly WindowsLocalChangeSource _native = new(root, 4096);
        private Func<LocalChangeSourceEvent, ValueTask>? _deliver;

        public Task StartAsync(Func<LocalChangeSourceEvent, ValueTask> eventHandler, CancellationToken cancellationToken)
        {
            _deliver = eventHandler;
            return _native.StartAsync(change => change.RelativePath.StartsWith(FencePrefix, StringComparison.Ordinal)
                ? ValueTask.CompletedTask : eventHandler(change), cancellationToken);
        }

        internal async Task WaitForFenceAsync(AccessJournalFactory factory, string fence, LocalChangeSourceEvent? size = null)
        {
            string name = FencePrefix + fence;
            Directory.CreateDirectory(Path.Combine(root, name));
            // Windows can coalesce away the actual Size notice. Supply one based on
            // the hydrated object's native snapshot, followed by a marker in the same
            // channel. A notification from another watcher cannot satisfy the fence.
            if (size is { } target) { await _deliver!(target); }
            await _deliver!(new(LocalChangeSourceAction.Created, name));
            await WaitForAccessRowsAsync(factory, rows => rows.Any(row => row.Kind == CloudStateOperationKind.Create &&
                LocalChangePayload.Decode(row.Payload).RelativePath == name));
        }

        public ValueTask DisposeAsync() => _native.DisposeAsync();
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
        LocalChangeSourceEvent size = await CaptureSizeChangeAsync(fixture.Root, original, originalPath, token);
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

    private static Task<LocalChangeSourceEvent> CaptureSizeChangeAsync(string root, string relativePath, string fullPath, CancellationToken token) =>
        CaptureNativeModificationAsync(root, relativePath, () =>
        {
            using FileStream file = new(fullPath, FileMode.Open, FileAccess.Write, FileShare.Read);
            file.SetLength(4096);
            file.Flush(flushToDisk: true);
            return Task.CompletedTask;
        }, token, LocalChangeSourceAction.SizeModified);

    private static Task<LocalChangeSourceEvent> CaptureContentGrowthAsync(string root, string relativePath, string fullPath,
        byte value, CancellationToken token) =>
        CaptureNativeModificationAsync(root, relativePath, () =>
        {
            using FileStream file = new(fullPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            file.WriteByte(value);
            file.Flush(flushToDisk: true);
            return Task.CompletedTask;
        }, token, LocalChangeSourceAction.SizeModified);

    [Trait("Category", "AccessJournal")]
    [Fact]
    public async Task SizeAfterRevisionAcknowledgementRemainsBoundToTheSamePlaceholder()
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path), hydrationContent: "seed"u8.ToArray());
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(25));
        CancellationToken token = deadline.Token;
        string name = fixture.File.RelativePath;
        string path = fixture.File.FullPath;
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "acked-size-owner", "revision-1");
        (await fixture.System.Root.CreatePlaceholdersAsync([
            CloudFilePlaceholderSpec.CreateBuilder(name, identity, 4).WithInSyncState(true).Build(),
        ], cancellationToken: token)).ThrowIfAnyFailed();
        Assert.Equal("seed"u8.ToArray(), await File.ReadAllBytesAsync(path, token));
        CloudItemSnapshot original = await fixture.File.InspectAsync(token);
        await fixture.File.UpdatePlaceholderAsync(CloudPlaceholderPatch.CreateBuilder().WithInSyncState(false).Build(), token);

        ControlledMetadataSource source = new();
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(null, source);
        await feed.StartAsync(token);
        await source.EmitAsync(await CaptureContentGrowthAsync(fixture.Root, name, path, (byte)'1', token));
        CloudLocalChangeBatch firstBatch = await feed.ReadBatchAsync(token);
        Assert.False(firstBatch.RequiresFullRescan);
        CloudLocalChange first = Assert.Single(firstBatch.Changes);
        Assert.Equal((CloudLocalChangeKind.ContentUpdate, identity.ItemId, name), (first.Kind, first.ItemId, first.RelativePath));
        await feed.AcknowledgeAsync([new CloudLocalChangeAcknowledgement(first.OperationId, "revision-2")], token);
        await using (ICloudStateTransaction transaction = await factory!.Store!.BeginTransactionAsync(token))
        {
            CloudItemState owner = Assert.IsType<CloudItemState>(await transaction.Items.GetByItemIdAsync(identity.ItemId, token));
            Assert.Equal("revision-2", owner.RemoteRevision);
            Assert.Null(await transaction.Operations.GetAsync(first.OperationId, token));
        }
        Assert.Equal(identity.Encode(), (await fixture.File.InspectAsync(token)).PlaceholderIdentity.ToArray());

        LocalChangeSourceEvent secondNotice = await CaptureContentGrowthAsync(fixture.Root, name, path, (byte)'2', token);
        CloudItemSnapshot resized = await fixture.File.InspectAsync(token);
        Assert.Equal(6L, secondNotice.SizeChange!.Value.Length);
        Assert.Equal((original.LocalBinding, 6L), (resized.LocalBinding, resized.Length));
        Assert.True(resized.ModifiedDataSize > 0);
        Assert.Equal(identity.Encode(), resized.PlaceholderIdentity.ToArray());
        await source.EmitAsync(secondNotice);
        CloudLocalChangeBatch secondBatch = await feed.ReadBatchAsync(token);
        Assert.False(secondBatch.RequiresFullRescan);
        CloudLocalChange second = Assert.Single(secondBatch.Changes);
        Assert.Equal((first.Kind, first.ItemId, first.RelativePath), (second.Kind, second.ItemId, second.RelativePath));
        Assert.NotEqual(first.OperationId, second.OperationId);
        Assert.True(second.Sequence > first.Sequence);
        await feed.AcknowledgeAsync([second.OperationId], token);
        Assert.Empty(await AccessRowsAsync(factory!));
        Assert.Equal("seed12"u8.ToArray(), await File.ReadAllBytesAsync(path, token));
    }

    [Trait("Category", "AccessJournal")]
    [Fact]
    public async Task SizeBeforeRenamePreservesTheOriginalItemId()
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        const string renamed = "renamed.bin";
        string originalPath = Path.Combine(fixture.Root, OriginalSizePath);
        string renamedPath = Path.Combine(fixture.Root, renamed);
        ControlledMetadataSource source = new();
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(null, source);
        await feed.StartAsync(token);
        await File.WriteAllTextAsync(originalPath, "original", token);
        await source.EmitAsync(new(LocalChangeSourceAction.Created, OriginalSizePath));
        CloudOperationJournalEntry created = await WaitForSingleMetadataJournalEntryAsync(factory!.Store!, token);
        Guid itemId = Assert.IsType<Guid>(created.ItemId);
        await feed.AcknowledgeAsync([created.OperationId], token);

        File.Move(originalPath, renamedPath);
        await source.EmitAsync(await CaptureSizeChangeAsync(fixture.Root, renamed, renamedPath, token));
        CloudOperationJournalEntry size = await WaitForSingleMetadataJournalEntryAsync(factory.Store!, token);
        Assert.Null(size.ItemId);
        await using (ICloudStateTransaction state = await factory.Store!.BeginTransactionAsync(token))
        {
            Assert.Null(await state.Items.GetByRelativePathAsync(renamed, token));
            Assert.Equal(itemId, (await state.Items.GetByRelativePathAsync(OriginalSizePath, token))!.ItemId);
        }

        await source.EmitAsync(new(LocalChangeSourceAction.RenamedOldName, OriginalSizePath));
        await source.EmitAsync(new(LocalChangeSourceAction.RenamedNewName, renamed));
        await WaitForAccessRowsAsync(factory, rows => rows.Count == 2);
        CloudLocalChangePage page = await feed.ReadPageAsync(await feed.BeginScanAsync(token), 0, 10, token);
        Assert.False(page.RequiresFullRescan);
        Assert.Collection(page.Changes,
            change =>
            {
                Assert.Equal((size.OperationId, size.Sequence, renamed), (change.OperationId, change.Sequence, change.RelativePath));
                Assert.Equal(CloudLocalChangeKind.ContentUpdate, change.Kind);
                Assert.Null(change.ItemId);
            },
            change =>
            {
                Assert.Equal(CloudLocalChangeKind.Move, change.Kind);
                Assert.Equal((itemId, OriginalSizePath, renamed), (change.ItemId, change.PreviousRelativePath, change.RelativePath));
            });
        await using ICloudStateTransaction confirmed = await factory.Store!.BeginTransactionAsync(token);
        CloudItemState owner = Assert.Single(await confirmed.Items.ListSubtreeAsync(string.Empty, token));
        Assert.Equal((itemId, renamed), (owner.ItemId, owner.RelativePath));
    }

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData("size-before-append", true, false)]
    [InlineData("size-before-commit", true, false)]
    [InlineData("size-publication-committed", true, false)]
    [InlineData("size-publication-committed", false, false)]
    [InlineData("size-publication-committed", true, true)]
    [InlineData("size-publication-committed", false, true)]
    public async Task TrackedSizeObservationPublishesOnlyVerifiedAssociation(string boundary, bool replace, bool placeholder)
    {
        MetadataFaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path, boundary), hydrationContent: "old"u8.ToArray());
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        string path = Path.Combine(fixture.Root, OriginalSizePath);
        string retiredPath = Path.Combine(fixture.Root, "retired.bin");
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "size-owner", "revision-1");
        if (placeholder)
        {
            (await fixture.System.Root.CreatePlaceholdersAsync([
                CloudFilePlaceholderSpec.CreateBuilder(OriginalSizePath, identity, 3).WithInSyncState(true).Build(),
            ], cancellationToken: token)).ThrowIfAnyFailed();
            Assert.Equal("old"u8.ToArray(), await File.ReadAllBytesAsync(path, token));
            // Capture the resize after the placeholder is already out of sync so the
            // test pauses publication, independent of the timing of the native flag change.
            await fixture.System.GetFile(OriginalSizePath).UpdatePlaceholderAsync(
                CloudPlaceholderPatch.CreateBuilder().WithInSyncState(false).Build(), token);
        }
        else { await File.WriteAllTextAsync(path, "old", token); }
        LocalChangeSourceEvent size = placeholder
            ? await CaptureContentGrowthAsync(fixture.Root, OriginalSizePath, path, 42, token)
            : await CaptureSizeChangeAsync(fixture.Root, OriginalSizePath, path, token);
        Assert.Equal(placeholder, size.SizeChange!.Value.PlaceholderState.HasFlag(CloudPlaceholderState.Placeholder));
        if (placeholder)
        {
            CloudItemSnapshot resized = await fixture.System.GetFile(OriginalSizePath).InspectAsync(token);
            Assert.False(size.SizeChange.Value.PlaceholderState.HasFlag(CloudPlaceholderState.InSync));
            Assert.Equal(CloudSynchronizationState.NotInSync, resized.SynchronizationState);
            Assert.Equal(resized.Length, size.SizeChange.Value.Length);
            Assert.True(resized.ModifiedDataSize > 0);
        }
        ControlledMetadataSource source = new();
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(null, source);
        await feed.StartAsync(token);
        Guid ownerId = identity.ItemId;
        if (!placeholder)
        {
            await source.EmitAsync(new(LocalChangeSourceAction.Created, OriginalSizePath));
            CloudOperationJournalEntry created = await WaitForSingleMetadataJournalEntryAsync(factory!.Store!, token);
            ownerId = Assert.IsType<Guid>(created.ItemId);
            await feed.AcknowledgeAsync([created.OperationId], token);
        }
        factory!.Armed = true;
        await source.EmitAsync(size);
        CloudOperationJournalEntry? provisional = null;
        try
        {
            await factory.OutcomePaused.Task.WaitAsync(token);
            if (replace && placeholder)
            {
                CloudFile file = fixture.System.GetFile(OriginalSizePath);
                CloudItemSnapshot before = await file.InspectAsync(token);
                Assert.Equal(identity.Encode(), before.PlaceholderIdentity.ToArray());
                CloudPlaceholderIdentity revised = new(identity.ItemId, identity.RemoteId, "revision-2");
                Assert.Equal(0, ReplaceNativeIdentity(path, revised));
                CloudItemSnapshot after = await file.InspectAsync(token);
                Assert.NotNull(before.LocalBinding);
                Assert.Equal(before.LocalBinding, after.LocalBinding);
                Assert.Equal(revised.Encode(), after.PlaceholderIdentity.ToArray());
            }
            else if (replace)
            {
                File.Move(path, retiredPath);
                await File.WriteAllBytesAsync(path, new byte[4096], token);
            }
            if (boundary == "size-publication-committed")
            {
                provisional = await WaitForSingleMetadataJournalEntryAsync(factory.Store!, token);
                Assert.Equal(ownerId, provisional.ItemId);
                CloudLocalChangePage waiting = await feed.ReadPageAsync(await feed.BeginScanAsync(token), 0, 10, token);
                Assert.False(waiting.RequiresFullRescan);
                Assert.Empty(waiting.Changes);
                Assert.True(waiting.HasMore);
                Assert.Equal(0, waiting.LastScannedSequence);
                await Assert.ThrowsAsync<InvalidOperationException>(() => feed.AcknowledgeAsync([provisional.OperationId], token).AsTask());
            }
        }
        finally { factory.ResumeOutcome.TrySetResult(); }

        CloudLocalChangeBatch published = await feed.ReadBatchAsync(token);
        Assert.Equal(replace, published.RequiresFullRescan);
        if (replace) { await WaitForMetadataRescanMarkerAsync(factory.Store!, token); }
        CloudOperationJournalEntry original = await WaitForSingleMetadataJournalEntryAsync(factory.Store!, token);
        if (provisional is not null)
        {
            Assert.Equal((provisional.OperationId, provisional.Sequence), (original.OperationId, original.Sequence));
        }
        Guid? expectedItemId = replace ? null : ownerId;
        Assert.Equal(expectedItemId, original.ItemId);
        Assert.Equal(CloudStateOperationKind.ContentUpdate, original.Kind);
        await using (ICloudStateTransaction transaction = await factory.Store!.BeginTransactionAsync(token))
        {
            CloudItemState retainedOwner = Assert.Single(await transaction.Items.ListSubtreeAsync(string.Empty, token));
            Assert.Equal((ownerId, OriginalSizePath), (retainedOwner.ItemId, retainedOwner.RelativePath));
            if (placeholder) { Assert.Equal(identity.RemoteRevision, retainedOwner.RemoteRevision); }
        }

        if (replace)
        {
            await feed.AcknowledgeFullRescanAsync(feed.BeginFullRescan(), token);
            if (!placeholder)
            {
                Assert.Equal(new byte[4096], await File.ReadAllBytesAsync(path, token));
                Assert.Equal((byte)'o', (await File.ReadAllBytesAsync(retiredPath, token))[0]);
            }
        }
        CloudLocalChangePage page = await feed.ReadPageAsync(await feed.BeginScanAsync(token), 0, 10, token);
        CloudLocalChange retained = Assert.Single(page.Changes);
        Assert.Equal((original.OperationId, original.Sequence, OriginalSizePath), (retained.OperationId, retained.Sequence, retained.RelativePath));
        Assert.Equal(expectedItemId, retained.ItemId);
        await feed.AcknowledgeAsync([retained.OperationId], token);
    }

    [Trait("Category", "AccessJournal")]
    [Fact]
    public async Task OrdinarySizeObservationDoesNotReuseAnUnboundPlaceholderItem()
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        string relativePath = fixture.File.RelativePath;
        string path = fixture.File.FullPath;
        string retiredPath = Path.Combine(fixture.Root, "retired.bin");
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "original-size-placeholder", "revision-1");
        (await fixture.System.Root.CreatePlaceholdersAsync([
            CloudFilePlaceholderSpec.CreateBuilder(relativePath, identity, 4096).WithInSyncState(true).Build(),
        ], cancellationToken: token)).ThrowIfAnyFailed();
        CloudItemState original;
        await using (ICloudStateTransaction transaction = await factory!.Store!.BeginTransactionAsync(token))
        {
            original = Assert.IsType<CloudItemState>(await transaction.Items.GetByRelativePathAsync(relativePath, token));
            Assert.Equal(identity.ItemId, original.ItemId);
            Assert.Null(original.LocalFileId);
        }

        ControlledMetadataSource source = new();
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(null, source);
        await feed.StartAsync(token);
        File.Move(path, retiredPath);
        await File.WriteAllTextAsync(path, "replacement", token);
        LocalChangeSourceEvent size = await CaptureSizeChangeAsync(fixture.Root, relativePath, path, token);
        Assert.False(size.SizeChange!.Value.PlaceholderState.HasFlag(CloudPlaceholderState.Placeholder));
        // Deliver Size before the name source has projected the replacement.
        await source.EmitAsync(size);

        CloudOperationJournalEntry observation = await WaitForSingleMetadataJournalEntryAsync(factory!.Store!, token);
        Assert.Equal(CloudStateOperationKind.ContentUpdate, observation.Kind);
        Assert.Null(observation.ItemId);
        await using (ICloudStateTransaction transaction = await factory.Store!.BeginTransactionAsync(token))
        {
            CloudItemState retained = Assert.IsType<CloudItemState>(await transaction.Items.GetByRelativePathAsync(relativePath, token));
            Assert.Equal((original.ItemId, original.RemoteId, original.RemoteRevision, original.LocalFileId, original.UpdatedAt),
                (retained.ItemId, retained.RemoteId, retained.RemoteRevision, retained.LocalFileId, retained.UpdatedAt));
        }

        Assert.True((await feed.BeginScanAsync(token)).RequiresFullRescan);
        await feed.AcknowledgeFullRescanAsync(feed.BeginFullRescan(), token);
        CloudLocalChangePage page = await feed.ReadPageAsync(await feed.BeginScanAsync(token), 0, 10, token);
        CloudLocalChange change = Assert.Single(page.Changes);
        Assert.Equal((observation.OperationId, observation.Sequence, relativePath), (change.OperationId, change.Sequence, change.RelativePath));
        Assert.Null(change.ItemId);
        await feed.AcknowledgeAsync([change.OperationId], token);
        Assert.Empty(await AccessRowsAsync(factory));
        Assert.Equal("replacement"u8.ToArray(), (await File.ReadAllBytesAsync(path, token))[..11]);
        Assert.True(File.Exists(retiredPath));
    }
}
