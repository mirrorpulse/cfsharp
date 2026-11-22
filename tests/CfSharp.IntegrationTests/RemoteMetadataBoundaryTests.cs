using System.Diagnostics;
using System.Threading.Channels;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileMetadataPassthroughPreventsEarlierAutomaticTicketsFromConsumingContent(bool creationTicket)
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        string relative = fixture.File.RelativePath;
        string path = fixture.File.FullPath;
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "upsert-then-timestamp", "remote-r0");
        if (!creationTicket)
        {
            await File.WriteAllBytesAsync(path, []);
            await fixture.File.ConvertToPlaceholderAsync(identity);
            await fixture.File.SetInSyncAsync(true);
        }
        ControlledMetadataSource source = new();
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(null, source);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        await feed.StartAsync(deadline.Token);

        CloudRemoteChange upsert = new("upsert", CloudRemoteChangeKind.FileUpsert, identity.RemoteId, "remote-r1", CloudItemKind.File,
            relative, identity.ItemId, previousRemoteRevision: creationTicket ? null : "remote-r0", length: 0,
            metadata: CloudPlaceholderMetadata.CreateFileBuilder().Build());
        CloudRemoteApplyResult applied = await fixture.System.ApplyRemoteChangesAsync(
            new("timestamp-ticket-upsert", Array.Empty<byte>(), [upsert], new byte[] { 1 }),
            new() { AttributeEchoPolicy = CloudRemoteAttributeEchoPolicy.CallerCoordinated, EchoSuppressionObservationCount = 8 }, deadline.Token);
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single(applied.Entries).Status);
        Guid originalTicket;
        Guid unrelatedTicket = Guid.NewGuid();
        await using (ICloudStateTransaction transaction = await factory!.Store!.BeginTransactionAsync(deadline.Token))
        {
            CloudEchoSuppressionState ticket = Assert.Single(await transaction.EchoSuppressions.ListActiveAsync(DateTimeOffset.UtcNow, deadline.Token));
            Assert.Equal(creationTicket ? CloudStateOperationKind.Create : CloudStateOperationKind.ContentUpdate, ticket.Kind);
            Assert.Equal(8, ticket.RemainingObservations);
            originalTicket = ticket.SuppressionId;
            Guid otherItem = Guid.NewGuid();
            await transaction.Items.UpsertAsync(new(otherItem, "unrelated-ticket-owner", "other.txt", CloudItemKind.File,
                "remote-r1", null, isTombstone: false, DateTimeOffset.UtcNow), deadline.Token);
            await transaction.EchoSuppressions.UpsertAsync(new(unrelatedTicket, otherItem, CloudStateOperationKind.ContentUpdate,
                "other.txt", ticket.Payload.Span, ticket.ExpiresAt), deadline.Token);
            await transaction.CommitAsync(deadline.Token);
        }

        DateTimeOffset timestamp = new(2025, 5, 6, 7, 8, 9, TimeSpan.Zero);
        CloudRemoteChange metadata = new("timestamp", CloudRemoteChangeKind.MetadataUpdate, identity.RemoteId,
            creationTicket ? "remote-r1" : "remote-r2",
            CloudItemKind.File, relative, identity.ItemId, previousRemoteRevision: "remote-r1",
            metadata: CloudPlaceholderMetadata.CreateFileBuilder().WithLastWriteTime(timestamp).Build());
        LocalChangeSourceEvent remote = await CaptureNativeModificationAsync(fixture.Root, relative, async () =>
        {
            CloudRemoteApplyResult passthrough = await fixture.System.ApplyRemoteChangesAsync(
                new("timestamp-ticket-passthrough", Array.Empty<byte>(), [metadata], new byte[] { 2 }),
                new() { SuppressLocalEcho = false }, deadline.Token);
            Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single(passthrough.Entries).Status);
        }, deadline.Token);
        Assert.Equal(timestamp.UtcDateTime, File.GetLastWriteTimeUtc(path));
        await source.EmitAsync(remote);
        await WaitForAccessRowsAsync(factory!, rows => rows.Count == 1);
        const string genuine = "real local contents";
        LocalChangeSourceEvent local = await CaptureNativeModificationAsync(fixture.Root, relative,
            () => File.WriteAllTextAsync(path, genuine, deadline.Token), deadline.Token);
        await source.EmitAsync(local);
        await WaitForAccessRowsAsync(factory!, rows => rows.Count == 2);
        CloudLocalChangePage page = await feed.ReadPageAsync(await feed.BeginScanAsync(deadline.Token), 0, 10, deadline.Token);
        Assert.False(page.RequiresFullRescan);
        Assert.Equal(2, page.Changes.Count);
        Assert.All(page.Changes, row =>
        {
            Assert.Equal(identity.ItemId, row.ItemId);
            Assert.Equal(CloudLocalChangeKind.ContentUpdate, row.Kind);
        });
        Assert.NotEqual(page.Changes[0].OperationId, page.Changes[1].OperationId);
        Assert.Equal(genuine, await File.ReadAllTextAsync(path, deadline.Token));
        await using ICloudStateTransaction read = await factory!.Store!.BeginTransactionAsync(deadline.Token);
        CloudEchoSuppressionState? retained = await read.EchoSuppressions.GetAsync(originalTicket, deadline.Token);
        if (creationTicket) { Assert.Equal(8, retained!.RemainingObservations); }
        else { Assert.Null(retained); }
        Assert.Equal(1, (await read.EchoSuppressions.GetAsync(unrelatedTicket, deadline.Token))!.RemainingObservations);
    }

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(CloudRemoteChangeKind.MetadataUpdate)]
    [InlineData(CloudRemoteChangeKind.DirectoryUpsert)]
    public async Task DirectoryAttributeHistoryFencesALateTimestampNotificationAtAReplacementFile(CloudRemoteChangeKind operationKind)
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        const string relative = "Documents";
        string path = Path.Combine(fixture.Root, relative);
        CloudPlaceholderIdentity directory = new(Guid.NewGuid(), "retired-attribute-directory");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder(relative, directory).WithInSyncState(true).Build(),
        ]);
        ControlledMetadataSource source = new();
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(null, source);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        await feed.StartAsync(deadline.Token);
        CloudRemoteChange attrs = new("attributes", operationKind, directory.RemoteId, "remote-r1",
            CloudItemKind.Directory, relative, directory.ItemId,
            metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().WithAttributes(FileAttributes.Directory | FileAttributes.Hidden).Build());
        CloudRemoteApplyResult applied = await fixture.System.ApplyRemoteChangesAsync(
            new("retired-directory-attributes", Array.Empty<byte>(), [attrs], new byte[] { 1 }), cancellationToken: deadline.Token);
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single(applied.Entries).Status);
        await feed.AcknowledgeFullRescanAsync(feed.BeginFullRescan(), deadline.Token);

        LocalChangeSourceEvent delayed = await CaptureNativeModificationAsync(fixture.Root, relative, () =>
        {
            Directory.SetLastWriteTimeUtc(path, new DateTime(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc));
            return Task.CompletedTask;
        }, deadline.Token);
        Directory.Move(path, Path.Combine(fixture.Root, "Retired"));
        const string replacementData = "different file generation";
        await File.WriteAllTextAsync(path, replacementData, deadline.Token);
        CloudFile file = fixture.System.GetFile(relative);
        CloudPlaceholderIdentity replacement = new(Guid.NewGuid(), "after-attribute-directory");
        await file.ConvertToPlaceholderAsync(replacement, cancellationToken: deadline.Token);
        await file.SetInSyncAsync(true, cancellationToken: deadline.Token);
        Assert.Equal(replacement.ItemId, (await file.InspectAsync(deadline.Token)).ItemId);
        await source.EmitAsync(delayed);
        await WaitForAccessRowsAsync(factory!, rows => rows.Count == 1);

        CloudOperationJournalEntry original = Assert.Single(await AccessRowsAsync(factory!));
        Assert.True((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        await feed.AcknowledgeFullRescanAsync(feed.BeginFullRescan(), deadline.Token);
        CloudLocalChangePage page = await feed.ReadPageAsync(await feed.BeginScanAsync(deadline.Token), 0, 10, deadline.Token);
        CloudLocalChange retained = Assert.Single(page.Changes);
        Assert.Null(retained.ItemId);
        Assert.Equal(CloudLocalChangeKind.ContentUpdate, retained.Kind);
        Assert.Equal((original.OperationId, original.Sequence), (retained.OperationId, retained.Sequence));
        Assert.Equal(replacementData, await File.ReadAllTextAsync(path, deadline.Token));
    }

    [Trait("Category", "AccessJournal")]
    [Fact]
    public async Task NativeFileTimestampAndSameLengthDataWriteRetainTheirOriginalObservations()
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-file-timestamp", "remote-r0");
        string path = fixture.File.FullPath;
        string relative = fixture.File.RelativePath;
        byte[] original = "old contents"u8.ToArray();
        byte[] edited = "new contents"u8.ToArray();
        await File.WriteAllBytesAsync(path, original);
        await fixture.File.ConvertToPlaceholderAsync(identity);
        await fixture.File.SetInSyncAsync(true);
        ControlledMetadataSource source = new();
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(options: null, source);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        await feed.StartAsync(deadline.Token);
        DateTimeOffset remoteTime = new(2025, 2, 3, 4, 5, 6, TimeSpan.Zero);
        CloudRemoteChange remote = new("file-time", CloudRemoteChangeKind.MetadataUpdate, identity.RemoteId, "remote-r1",
            CloudItemKind.File, relative, identity.ItemId, previousRemoteRevision: "remote-r0",
            metadata: CloudPlaceholderMetadata.CreateFileBuilder().WithLastWriteTime(remoteTime).Build());
        await RecordNativeModificationAsync(async () =>
        {
            CloudRemoteApplyResult result = await fixture.System.ApplyRemoteChangesAsync(
                new("metadata-file-time", Array.Empty<byte>(), [remote], new byte[] { 1 }), cancellationToken: deadline.Token);
            Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single(result.Entries).Status);
        });
        Assert.Equal(remoteTime.UtcDateTime, File.GetLastWriteTimeUtc(path));
        Assert.Equal(original, await File.ReadAllBytesAsync(path, deadline.Token));
        Guid remoteObservation = await ReconcileObservationAsync();

        await RecordNativeModificationAsync(async () =>
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            await stream.WriteAsync(edited, deadline.Token);
        });
        Assert.Equal(edited, await File.ReadAllBytesAsync(path, deadline.Token));
        Assert.Equal(original.Length, new FileInfo(path).Length);
        Assert.NotEqual(remoteObservation, await ReconcileObservationAsync());
        Assert.Equal(identity.ItemId, (await fixture.File.InspectAsync(deadline.Token)).ItemId);

        await fixture.File.MoveToAsync(fixture.System.Root, "historical.bin", cancellationToken: deadline.Token);
        CloudPlaceholderIdentity successor = new(Guid.NewGuid(), "metadata-file-timestamp-successor", "remote-r0");
        await File.WriteAllBytesAsync(path, original, deadline.Token);
        await fixture.File.ConvertToPlaceholderAsync(successor, cancellationToken: deadline.Token);
        await fixture.File.SetInSyncAsync(true, cancellationToken: deadline.Token);
        DateTime localTime = remoteTime.UtcDateTime.AddYears(1);
        await RecordNativeModificationAsync(() =>
        {
            File.SetLastWriteTimeUtc(path, localTime);
            return Task.CompletedTask;
        });
        Assert.Equal(CloudSynchronizationState.InSync, (await fixture.File.InspectAsync(deadline.Token)).SynchronizationState);
        CloudRemoteChange overwrite = new("overwrite", CloudRemoteChangeKind.MetadataUpdate, successor.RemoteId, "remote-r1",
            CloudItemKind.File, relative, successor.ItemId, previousRemoteRevision: "remote-r0",
            metadata: CloudPlaceholderMetadata.CreateFileBuilder().WithLastWriteTime(remoteTime).Build());
        CloudRemoteApplyResult blocked = await fixture.System.ApplyRemoteChangesAsync(
            new("timestamp-successor", Array.Empty<byte>(), [overwrite], new byte[] { 2 }), cancellationToken: deadline.Token);
        Assert.Equal(CloudRemoteConflictReason.Metadata, Assert.Single(blocked.Entries).Conflict?.Reason);
        Assert.Equal(localTime, File.GetLastWriteTimeUtc(path));

        const string moved = "successor-moved.bin";
        await fixture.File.MoveToAsync(fixture.System.Root, moved, cancellationToken: deadline.Token);
        CloudFile movedFile = fixture.System.GetFile(moved);
        await movedFile.SetInSyncAsync(true, cancellationToken: deadline.Token);
        CloudRemoteChange overwriteMoved = new("overwrite-moved", CloudRemoteChangeKind.MetadataUpdate, successor.RemoteId, "remote-r1",
            CloudItemKind.File, moved, successor.ItemId, previousRemoteRevision: "remote-r0", metadata: overwrite.Metadata);
        CloudRemoteApplyResult blockedAfterMove = await fixture.System.ApplyRemoteChangesAsync(
            new("timestamp-moved-successor", Array.Empty<byte>(), [overwriteMoved], new byte[] { 3 }), cancellationToken: deadline.Token);
        Assert.Equal(CloudRemoteConflictReason.Metadata, Assert.Single(blockedAfterMove.Entries).Conflict?.Reason);
        Assert.Equal(localTime, File.GetLastWriteTimeUtc(movedFile.FullPath));
        await ReconcileObservationAsync();

        async Task RecordNativeModificationAsync(Func<Task> mutate)
        {
            // Each native subscription starts immediately before its own mutation, so the
            // second observation cannot be a delayed callback from the remote write.
            await source.EmitAsync(await CaptureNativeModificationAsync(fixture.Root, relative, mutate, deadline.Token));
            await WaitForAccessRowsAsync(factory!, rows => rows.Count == 1);
        }

        async Task<Guid> ReconcileObservationAsync()
        {
            CloudOperationJournalEntry originalObservation = Assert.Single(await AccessRowsAsync(factory!));
            Assert.True((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
            await feed.AcknowledgeFullRescanAsync(feed.BeginFullRescan(), deadline.Token);
            CloudLocalChangePage page = await feed.ReadPageAsync(await feed.BeginScanAsync(deadline.Token), 0, 10, deadline.Token);
            Assert.False(page.RequiresFullRescan);
            CloudLocalChange change = Assert.Single(page.Changes);
            Assert.Equal(CloudLocalChangeKind.ContentUpdate, change.Kind);
            Assert.Null(change.ItemId);
            Assert.Equal(relative, change.RelativePath);
            Assert.Equal((originalObservation.OperationId, originalObservation.Sequence), (change.OperationId, change.Sequence));
            await feed.AcknowledgeAsync([change.OperationId], deadline.Token);
            return change.OperationId;
        }
    }

    [Trait("Category", "AccessJournal")]
    [Fact]
    public async Task DelayedFileTimestampCannotBindToDirectoryAtTheSamePath()
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        string relative = fixture.File.RelativePath;
        CloudPlaceholderIdentity file = new(Guid.NewGuid(), "timestamp-retired-file");
        await File.WriteAllBytesAsync(fixture.File.FullPath, "resident bytes"u8.ToArray());
        await fixture.File.ConvertToPlaceholderAsync(file);
        await fixture.File.SetInSyncAsync(true);
        ControlledMetadataSource source = new();
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(options: null, source);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        await feed.StartAsync(deadline.Token);

        CloudRemoteChange timestamp = new("timestamp", CloudRemoteChangeKind.MetadataUpdate, file.RemoteId, "remote-r1",
            CloudItemKind.File, relative, file.ItemId, metadata: CloudPlaceholderMetadata.CreateFileBuilder()
                .WithLastWriteTime(new(2025, 3, 4, 5, 6, 7, TimeSpan.Zero)).Build());
        LocalChangeSourceEvent delayed = await CaptureNativeModificationAsync(fixture.Root, relative, async () =>
        {
            CloudRemoteApplyResult applied = await fixture.System.ApplyRemoteChangesAsync(
                new("retire-timestamp-file", Array.Empty<byte>(), [timestamp], new byte[] { 1 }), cancellationToken: deadline.Token);
            Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single(applied.Entries).Status);
        }, deadline.Token);
        await feed.AcknowledgeFullRescanAsync(feed.BeginFullRescan(), deadline.Token);
        await fixture.File.MoveToAsync(fixture.System.Root, "retired.bin", cancellationToken: deadline.Token);

        CloudPlaceholderIdentity directory = new(Guid.NewGuid(), "timestamp-directory-successor");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder(relative, directory).WithInSyncState(true).Build(),
        ], cancellationToken: deadline.Token);
        CloudRemoteApplyResult passthrough = await fixture.System.ApplyRemoteChangesAsync(
            MetadataBoundaryBatch(directory, relative, CloudItemKind.Directory), new() { SuppressLocalEcho = false }, deadline.Token);
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single(passthrough.Entries).Status);
        Assert.False((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);

        await source.EmitAsync(delayed);
        await WaitForAccessRowsAsync(factory!, rows => rows.Count == 1);
        CloudOperationJournalEntry original = Assert.Single(await AccessRowsAsync(factory!));
        Assert.True((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        await feed.AcknowledgeFullRescanAsync(feed.BeginFullRescan(), deadline.Token);
        CloudLocalChangePage page = await feed.ReadPageAsync(await feed.BeginScanAsync(deadline.Token), 0, 10, deadline.Token);
        CloudLocalChange record = Assert.Single(page.Changes);
        Assert.Null(record.ItemId);
        Assert.False(record.IsDirectory);
        Assert.Equal(CloudLocalChangeKind.ContentUpdate, record.Kind);
        Assert.Equal((original.OperationId, original.Sequence), (record.OperationId, record.Sequence));
        Assert.Equal(relative, record.RelativePath);
        await feed.AcknowledgeAsync([record.OperationId], deadline.Token);
    }

    private static async Task<LocalChangeSourceEvent> CaptureNativeModificationAsync(string root, string relative,
        Func<Task> mutate, CancellationToken cancellationToken)
    {
        TaskCompletionSource<LocalChangeSourceEvent> observed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using WindowsLocalChangeSource native = new(root, 4096);
        await native.StartAsync(change =>
        {
            if (change.Action == LocalChangeSourceAction.Modified && change.RelativePath == relative)
            { observed.TrySetResult(change); }
            return ValueTask.CompletedTask;
        }, cancellationToken);
        await mutate();
        return await observed.Task.WaitAsync(cancellationToken);
    }

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshFeedRetiresHistoricalFileTimestampBeforeARealContentWrite(bool restartOwner)
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        string relative = fixture.File.RelativePath;
        string path = fixture.File.FullPath;
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "timestamp-feed-restart");
        byte[] initial = "first bytes"u8.ToArray();
        byte[] edited = "other bytes"u8.ToArray();
        await File.WriteAllBytesAsync(path, initial);
        await fixture.File.ConvertToPlaceholderAsync(identity);
        await fixture.File.SetInSyncAsync(true);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        await using (CloudLocalChangeFeed original = fixture.System.CreateLocalChangeFeed(null, new ControlledMetadataSource()))
        {
            await original.StartAsync(deadline.Token);
            CloudRemoteChange timestamp = new("file-time", CloudRemoteChangeKind.MetadataUpdate, identity.RemoteId, "remote-r1",
                CloudItemKind.File, relative, identity.ItemId, metadata: CloudPlaceholderMetadata.CreateFileBuilder()
                    .WithLastWriteTime(new(2025, 4, 5, 6, 7, 8, TimeSpan.Zero)).Build());
            Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single((await fixture.System.ApplyRemoteChangesAsync(
                new("timestamp-before-restart", Array.Empty<byte>(), [timestamp], new byte[] { 1 }), cancellationToken: deadline.Token)).Entries).Status);
        }

        if (restartOwner) { await fixture.RestartAsync(); }
        ControlledMetadataSource source = new();
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(null, source);
        await feed.StartAsync(deadline.Token);
        Assert.True((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        await feed.AcknowledgeFullRescanAsync(feed.BeginFullRescan(), deadline.Token);

        LocalChangeSourceEvent observed = await CaptureNativeModificationAsync(fixture.Root, relative, async () =>
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            await stream.WriteAsync(edited, deadline.Token);
        }, deadline.Token);
        await source.EmitAsync(observed);
        await WaitForAccessRowsAsync(factory!, rows => rows.Count == 1);
        CloudOperationJournalEntry originalRow = Assert.Single(await AccessRowsAsync(factory!));
        CloudLocalChangePage page = await feed.ReadPageAsync(await feed.BeginScanAsync(deadline.Token), 0, 10, deadline.Token);
        Assert.False(page.RequiresFullRescan);
        CloudLocalChange change = Assert.Single(page.Changes);
        Assert.Equal(CloudLocalChangeKind.ContentUpdate, change.Kind);
        Assert.Equal(identity.ItemId, change.ItemId);
        Assert.Equal((originalRow.OperationId, originalRow.Sequence), (change.OperationId, change.Sequence));
        Assert.Equal(initial.Length, new FileInfo(path).Length);
        Assert.Equal(edited, await File.ReadAllBytesAsync(path, deadline.Token));
        await feed.AcknowledgeAsync([change.OperationId], deadline.Token);
        Assert.Empty(await AccessRowsAsync(factory!));
    }

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(CloudItemKind.Directory, false)]
    [InlineData(CloudItemKind.File, false)]
    [InlineData(CloudItemKind.Directory, true)]
    [InlineData(CloudItemKind.File, true)]
    public async Task ProtectedUpsertCannotOverwriteAnUnreconciledAttributeEdit(CloudItemKind kind, bool delayDelivery)
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-protected-upsert");
        bool directory = kind == CloudItemKind.Directory;
        string relative = directory ? "Documents" : fixture.File.RelativePath;
        string path = Path.Combine(fixture.Root, relative);
        if (directory)
        {
            await fixture.System.Root.CreatePlaceholdersAsync([
                CloudDirectoryPlaceholderSpec.CreateBuilder(relative, identity).WithInSyncState(true).Build(),
            ]);
        }
        else
        {
            await File.WriteAllBytesAsync(path, "original resident content"u8.ToArray());
            await fixture.File.ConvertToPlaceholderAsync(identity);
            await fixture.File.SetInSyncAsync(true);
        }
        CloudItem item = directory ? fixture.System.GetDirectory(relative) : fixture.System.GetFile(relative);
        DelayedMetadataSource? source = delayDelivery ? new(fixture.Root) : null;
        await using CloudLocalChangeFeed feed = source is null
            ? fixture.System.CreateLocalChangeFeed() : new(fixture.Root, factory!.Store!, new(), source);
        await feed.StartAsync();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        Assert.Equal(CloudRemoteBatchStatus.Applied, (await fixture.System.ApplyRemoteChangesAsync(
            MetadataBoundaryBatch(identity, relative, kind), cancellationToken: deadline.Token)).Status);
        int notificationCount = directory ? 3 : 2;
        List<LocalChangeSourceEvent> withheld = [];
        if (source is not null)
        {
            for (int i = 1; i < notificationCount; i++) { withheld.Add(await source.NextAsync(deadline.Token)); }
        }
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        if (source is not null)
        {
            withheld.Add(await source.NextAsync(deadline.Token));
            Assert.All(withheld, change => Assert.Equal(relative, change.RelativePath));
            Assert.Empty(await AccessRowsAsync(factory!));
        }
        else
        {
            await WaitForAccessRowsAsync(factory!, rows => rows.Count >= notificationCount);
        }
        IReadOnlyList<CloudOperationJournalEntry> beforeUpsert = await AccessRowsAsync(factory!);
        Assert.True((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);

        CloudRemoteChange update = new("upsert", directory ? CloudRemoteChangeKind.DirectoryUpsert : CloudRemoteChangeKind.FileUpsert,
            identity.RemoteId, "remote-r2", kind, relative, identity.ItemId, previousRemoteRevision: "remote-r1",
            length: directory ? null : 0, metadata: (directory ? CloudPlaceholderMetadata.CreateDirectoryBuilder()
                : CloudPlaceholderMetadata.CreateFileBuilder()).WithAttributes(directory ? FileAttributes.Directory : FileAttributes.Normal).Build());
        CloudRemoteApplyResult result = await fixture.System.ApplyRemoteChangesAsync(
            new("metadata-upsert", Array.Empty<byte>(), [update], new byte[] { 2 }), cancellationToken: deadline.Token);
        CloudRemoteApplyEntryResult entry = Assert.Single(result.Entries);
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, entry.Status);
        Assert.Equal(CloudRemoteConflictReason.Metadata, entry.Conflict!.Reason);
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        Assert.Equal("remote-r1", (await item.InspectAsync(deadline.Token)).RemoteRevision);
        AssertOriginalAccessRows(beforeUpsert, await AccessRowsAsync(factory!));
        if (source is not null)
        {
            foreach (LocalChangeSourceEvent change in withheld) { await source.DeliverAsync(change); }
            await WaitForAccessRowsAsync(factory!, rows => rows.Count >= notificationCount);
        }
        IReadOnlyList<CloudOperationJournalEntry> originals = await AccessRowsAsync(factory!);
        AssertOriginalAccessRows(beforeUpsert, originals);
        Assert.All(originals, row => Assert.Null(row.ItemId));

        await feed.AcknowledgeFullRescanAsync(feed.BeginFullRescan(), deadline.Token);
        await feed.AcknowledgeAsync(originals.Select(row => row.OperationId), deadline.Token);
        CloudRemoteApplyResult afterReconciliation = await fixture.System.ApplyRemoteChangesAsync(
            new("metadata-upsert-reconciled", Array.Empty<byte>(), [update], new byte[] { 3 }), cancellationToken: deadline.Token);
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single(afterReconciliation.Entries).Status);
        Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
    }

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ProtectedUpsertRequiresLiveMetadataCoordinationBeforeNotificationDelivery(bool partialBatch, bool acknowledgeWhileAuthorized)
    {
        const string relative = "Documents";
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-coordination-protected-upsert");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder(relative, identity).WithInSyncState(true).Build(),
        ]);
        DelayedMetadataSource source = new(fixture.Root);
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(options: null, source);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        await feed.StartAsync(deadline.Token);
        CloudRemoteChangeBatch batch = MetadataBoundaryBatch(identity, relative, CloudItemKind.Directory);
        CloudRemoteApplyOptions options = new()
        {
            AttributeEchoPolicy = CloudRemoteAttributeEchoPolicy.CallerCoordinated,
            EchoSuppressionLifetime = partialBatch ? TimeSpan.FromMinutes(2) :
                acknowledgeWhileAuthorized ? TimeSpan.FromSeconds(3) : TimeSpan.FromMilliseconds(250),
            MaximumEntries = 1,
        };
        if (partialBatch)
        {
            CloudRemoteChange second = new("second", CloudRemoteChangeKind.MetadataUpdate, identity.RemoteId,
                "remote-later", CloudItemKind.Directory, relative, identity.ItemId,
                metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().Build());
            batch = new("metadata-partial", Array.Empty<byte>(), [batch.Changes[0], second], new byte[] { 2 });
        }
        CloudRemoteApplyResult first = await fixture.System.ApplyRemoteChangesAsync(batch, options, deadline.Token);
        Assert.Equal(partialBatch ? CloudRemoteBatchStatus.Applying : CloudRemoteBatchStatus.Applied, first.Status);
        Assert.Equal(partialBatch, first.RequiresRetry);
        Assert.Equal(1, first.AppliedEntryCount);
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, first.Entries[0].Status);
        if (partialBatch) { Assert.Equal(CloudRemoteApplyEntryStatus.NotProcessed, first.Entries[1].Status); }
        if (!partialBatch)
        {
            // Windows timers can wake early. Verify the same monotonic time used by
            // the authority before allowing the native writes in this regression.
            long appliedAt = Stopwatch.GetTimestamp();
            if (acknowledgeWhileAuthorized)
            {
                Assert.False((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
                await source.DeliverAsync(new(LocalChangeSourceAction.Overflow, string.Empty));
                while (true)
                {
                    await using (ICloudStateTransaction state = await factory!.Store!.BeginTransactionAsync(deadline.Token))
                    {
                        CloudStateCheckpoint? checkpoint = await state.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName, deadline.Token);
                        if (checkpoint is not null && LocalChangeCheckpoint.Decode(checkpoint.Value).RequiresFullRescan) { break; }
                    }
                    await Task.Delay(20, deadline.Token);
                }
                await feed.AcknowledgeFullRescanAsync(feed.BeginFullRescan(), deadline.Token);
                Assert.False((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
            }
            while (options.EchoSuppressionLifetime - Stopwatch.GetElapsedTime(appliedAt) is { Ticks: > 0 } remaining)
            {
                await Task.Delay(remaining + TimeSpan.FromMilliseconds(1), deadline.Token);
            }
        }

        LocalChangeSourceEvent remoteFirst = await source.NextAsync(deadline.Token);
        LocalChangeSourceEvent remoteSecond = await source.NextAsync(deadline.Token);
        string path = Path.Combine(fixture.Root, relative);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        LocalChangeSourceEvent local = await source.NextAsync(deadline.Token);
        Assert.Equal(relative, local.RelativePath);
        Assert.Empty(await AccessRowsAsync(factory!));
        await using (ICloudStateTransaction state = await factory!.Store!.BeginTransactionAsync(deadline.Token))
        {
            RemoteMetadataIntent receipt = RemoteMetadataIntent.Decode(Assert.Single(
                await state.Checkpoints.ListAsync(RemoteMetadataIntent.ItemPrefix(identity.ItemId), deadline.Token)));
            Assert.True(receipt is { Coordinated: true, OutcomeConfirmed: true, RequiresReconciliation: false, Reconciled: false });
        }

        CloudRemoteChange upsert = new("upsert", CloudRemoteChangeKind.DirectoryUpsert, identity.RemoteId, "remote-r2",
            CloudItemKind.Directory, relative, identity.ItemId, previousRemoteRevision: "remote-r1",
            metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().WithAttributes(FileAttributes.Directory).Build());
        CloudRemoteApplyResult blocked = await fixture.System.ApplyRemoteChangesAsync(
            new("metadata-protected-upsert", Array.Empty<byte>(), [upsert], new byte[] { 3 }), cancellationToken: deadline.Token);
        CloudRemoteApplyEntryResult outcome = Assert.Single(blocked.Entries);
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, outcome.Status);
        Assert.Equal(CloudRemoteConflictReason.Metadata, outcome.Conflict!.Reason);
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        Assert.Equal("remote-r1", (await fixture.System.GetDirectory(relative).InspectAsync(deadline.Token)).RemoteRevision);
        Assert.Empty(await AccessRowsAsync(factory!));
        Assert.True((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);

        await source.DeliverAsync(remoteFirst);
        await source.DeliverAsync(remoteSecond);
        await source.DeliverAsync(local);
        await WaitForAccessRowsAsync(factory!, rows => rows.Count == 3);
        IReadOnlyList<CloudOperationJournalEntry> originals = await AccessRowsAsync(factory!);
        Assert.All(originals, row => Assert.Null(row.ItemId));
        await feed.AcknowledgeFullRescanAsync(feed.BeginFullRescan(), deadline.Token);
        await AssertAccessPublicRowsAsync(factory!, feed);
        await feed.AcknowledgeAsync(originals.Select(row => row.OperationId), deadline.Token);
        CloudRemoteApplyResult reconciled = await fixture.System.ApplyRemoteChangesAsync(
            new("metadata-reconciled-upsert", Array.Empty<byte>(), [upsert], new byte[] { 4 }), cancellationToken: deadline.Token);
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single(reconciled.Entries).Status);
        Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
    }

    [Trait("Category", "AccessJournal")]
    [Fact]
    public async Task ExpiredMetadataCoordinationWakesIdleReadAndRetainsNextRealAttributeWrite()
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-expiry");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder("Documents", identity).WithInSyncState(true).Build(),
        ]);
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed();
        await feed.StartAsync();
        CloudRemoteApplyOptions options = new()
        {
            AttributeEchoPolicy = CloudRemoteAttributeEchoPolicy.CallerCoordinated,
            EchoSuppressionLifetime = TimeSpan.FromSeconds(5),
        };
        Assert.Equal(CloudRemoteBatchStatus.Applied, (await fixture.System.ApplyRemoteChangesAsync(
            MetadataBoundaryBatch(identity, "Documents", CloudItemKind.Directory), options)).Status);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        while (true)
        {
            bool drained;
            await using (ICloudStateTransaction state = await factory!.Store!.BeginTransactionAsync(deadline.Token))
            {
                CloudStateCheckpoint? checkpoint = await state.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName, deadline.Token);
                drained = checkpoint is not null && LocalChangeCheckpoint.Decode(checkpoint.Value).Observation >= 2 &&
                    (await state.Checkpoints.ListAsync(CloudLocalChangeFeed.SecurityValidationPrefix, deadline.Token)).Count == 0;
            }
            if (drained) { break; }
            await Task.Delay(20, deadline.Token);
        }
        Assert.Empty(await AccessRowsAsync(factory!));
        Assert.False((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        // Once the provider echoes have drained, expiry is the only change that
        // can wake an already waiting legacy reader.
        Task<CloudLocalChangeBatch> waiting = feed.ReadBatchAsync(deadline.Token).AsTask();
        Assert.False(waiting.IsCompleted);
        CloudLocalChangeBatch expired = await waiting;
        Assert.True(expired.RequiresFullRescan);
        Assert.Empty(expired.Changes);
        string path = Path.Combine(fixture.Root, "Documents");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        await WaitForAccessRowsAsync(factory!, rows => rows.Count != 0);
        await feed.DisposeAsync();
        CloudOperationJournalEntry original = Assert.Single(await AccessRowsAsync(factory!));
        Assert.Null(original.ItemId);
        Assert.Equal(CloudStateOperationKind.MetadataUpdate, original.Kind);
        await fixture.RestartAsync();
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed();
        await reopened.StartAsync();
        Assert.True((await reopened.BeginScanAsync()).RequiresFullRescan);
        await reopened.AcknowledgeFullRescanAsync();
        AssertOriginalAccessRows([original], await AccessRowsAsync(factory!));
        await AssertAccessPublicRowsAsync(factory!, reopened);
        await reopened.AcknowledgeAsync([original.OperationId]);
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
    }

    [Trait("Category", "AccessJournal")]
    [Fact]
    public async Task MetadataReconciliationRetainsAProviderCallbackDeliveredAfterAcknowledgement()
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-late-callback");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder("Documents", identity).WithInSyncState(true).Build(),
        ]);
        DelayedMetadataSource source = new(fixture.Root);
        await using CloudLocalChangeFeed feed = new(fixture.Root, factory!.Store!, new(), source);
        await feed.StartAsync();
        Assert.Equal(CloudRemoteBatchStatus.Applied, (await fixture.System.ApplyRemoteChangesAsync(
            MetadataBoundaryBatch(identity, "Documents", CloudItemKind.Directory))).Status);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        LocalChangeSourceEvent first = await source.NextAsync(deadline.Token);
        LocalChangeSourceEvent second = await source.NextAsync(deadline.Token);
        await source.DeliverAsync(first);
        await WaitForAccessRowsAsync(factory!, rows => rows.Count == 1);
        CloudOperationJournalEntry original = Assert.Single(await AccessRowsAsync(factory!));
        await feed.AcknowledgeFullRescanAsync(feed.BeginFullRescan(), deadline.Token);
        Assert.False((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        await source.DeliverAsync(second);
        await WaitForAccessRowsAsync(factory!, rows => rows.Count == 2);
        Assert.True((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        string path = Path.Combine(fixture.Root, "Documents");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        await source.DeliverAsync(await source.NextAsync(deadline.Token));
        await WaitForAccessRowsAsync(factory!, rows => rows.Count == 3);
        IReadOnlyList<CloudOperationJournalEntry> retained = await AccessRowsAsync(factory!);
        AssertOriginalAccessRows([original], retained);
        Assert.All(retained, row => Assert.Null(row.ItemId));
        await feed.AcknowledgeFullRescanAsync(feed.BeginFullRescan(), deadline.Token);
        await AssertAccessPublicRowsAsync(factory!, feed);
        AssertOriginalAccessRows(retained, await AccessRowsAsync(factory!));
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        await feed.AcknowledgeAsync(retained.Select(row => row.OperationId), deadline.Token);
    }

    [Trait("Category", "AccessJournal")]
    [Fact]
    public async Task NativeMetadataSharingFailureCannotAuthorizeSuppression()
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        byte[] bytes = "resident metadata sharing control"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, bytes);
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-native-failure");
        await fixture.File.ConvertToPlaceholderAsync(identity);
        await fixture.File.SetInSyncAsync(true);
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed();
        await feed.StartAsync();
        await using (FileStream reader = new(fixture.File.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            CloudRemoteApplyResult failed = await fixture.System.ApplyRemoteChangesAsync(
                MetadataBoundaryBatch(identity, fixture.File.RelativePath, CloudItemKind.File),
                new() { AttributeEchoPolicy = CloudRemoteAttributeEchoPolicy.CallerCoordinated });
            Assert.True(failed.RequiresRetry);
            Assert.IsType<CloudFilesException>(Assert.Single(failed.Entries).Error);
        }
        File.SetAttributes(fixture.File.FullPath, File.GetAttributes(fixture.File.FullPath) | FileAttributes.Hidden);
        await WaitForAccessRowsAsync(factory!, rows => rows.Count != 0);
        await feed.DisposeAsync();
        IReadOnlyList<CloudOperationJournalEntry> originals = await AccessRowsAsync(factory!);
        Assert.All(originals, row => Assert.Null(row.ItemId));
        await fixture.RestartAsync();
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed();
        await reopened.StartAsync();
        Assert.True((await reopened.BeginScanAsync()).RequiresFullRescan);
        await reopened.AcknowledgeFullRescanAsync();
        AssertOriginalAccessRows(originals, await AccessRowsAsync(factory!));
        await AssertAccessPublicRowsAsync(factory!, reopened);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.File.FullPath));
        Assert.True(File.GetAttributes(fixture.File.FullPath).HasFlag(FileAttributes.Hidden));
        Assert.Equal(0, fixture.Provider.Fetches);
    }

    private static CloudRemoteChangeBatch MetadataBoundaryBatch(CloudPlaceholderIdentity identity, string path, CloudItemKind kind) =>
        new("metadata-boundary", Array.Empty<byte>(), [new("metadata", CloudRemoteChangeKind.MetadataUpdate,
            identity.RemoteId, "remote-r1", kind, path, identity.ItemId, metadata: (kind == CloudItemKind.Directory
                ? CloudPlaceholderMetadata.CreateDirectoryBuilder() : CloudPlaceholderMetadata.CreateFileBuilder()).Build())], new byte[] { 1 });

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(CloudRemoteChangeKind.MetadataUpdate, false)]
    [InlineData(CloudRemoteChangeKind.DirectoryUpsert, false)]
    [InlineData(CloudRemoteChangeKind.DirectoryUpsert, true)]
    public async Task InPlaceMetadataWriteReplacesTheEarlierCoordinatedPolicy(CloudRemoteChangeKind kind, bool suppressEcho)
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-explicit-passthrough");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder("Documents", identity).WithInSyncState(true).Build(),
        ]);
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed();
        await feed.StartAsync();
        Assert.Equal(CloudRemoteBatchStatus.Applied, (await fixture.System.ApplyRemoteChangesAsync(
            MetadataBoundaryBatch(identity, "Documents", CloudItemKind.Directory), new()
            { AttributeEchoPolicy = CloudRemoteAttributeEchoPolicy.CallerCoordinated })).Status);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        while (true)
        {
            bool drained;
            await using (ICloudStateTransaction state = await factory!.Store!.BeginTransactionAsync(deadline.Token))
            {
                CloudStateCheckpoint? checkpoint = await state.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName, deadline.Token);
                drained = checkpoint is not null && LocalChangeCheckpoint.Decode(checkpoint.Value).Observation >= 2 &&
                    (await state.Checkpoints.ListAsync(CloudLocalChangeFeed.SecurityValidationPrefix, deadline.Token)).Count == 0;
            }
            if (drained) { break; }
            await Task.Delay(20, deadline.Token);
        }
        Assert.Empty(await AccessRowsAsync(factory!));
        CloudRemoteChange next = new("metadata", kind, identity.RemoteId,
            "remote-r2", CloudItemKind.Directory, "Documents", identity.ItemId,
            metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().WithAttributes(FileAttributes.Directory | FileAttributes.Hidden).Build());
        Assert.Equal(CloudRemoteBatchStatus.Applied, (await fixture.System.ApplyRemoteChangesAsync(
            new("metadata-passthrough", Array.Empty<byte>(), [next], new byte[] { 2 }),
            new() { SuppressLocalEcho = suppressEcho }, deadline.Token)).Status);
        string path = Path.Combine(fixture.Root, "Documents");
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        await WaitForAccessRowsAsync(factory!, rows => rows.Count >= 2);
        IReadOnlyList<CloudOperationJournalEntry> originals = await AccessRowsAsync(factory!);
        Assert.Equal(2, originals.Count);
        Assert.All(originals, row =>
        {
            Assert.Equal(CloudStateOperationKind.MetadataUpdate, row.Kind);
            Assert.Equal(suppressEcho ? null : (Guid?)identity.ItemId, row.ItemId);
            Assert.Equal("Documents", LocalChangePayload.Decode(row.Payload).RelativePath);
        });
        Assert.Equal(suppressEcho, (await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        if (suppressEcho) { await feed.AcknowledgeFullRescanAsync(deadline.Token); }
        await AssertAccessPublicRowsAsync(factory!, feed);
        await feed.AcknowledgeAsync(originals.Select(row => row.OperationId), deadline.Token);
        Assert.Empty(await AccessRowsAsync(factory!));
        Assert.Equal("remote-r2", (await fixture.System.GetDirectory("Documents").InspectAsync(deadline.Token)).RemoteRevision);
    }

    [Trait("Category", "AccessJournal")]
    [Fact]
    public async Task MetadataReceiptCannotExpandTheCallerDeclaredObjectSet()
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        CloudPlaceholderIdentity owned = new(Guid.NewGuid(), "metadata-owned-object");
        CloudPlaceholderIdentity other = new(Guid.NewGuid(), "metadata-other-object");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder("Owned", owned).WithInSyncState(true).Build(),
            CloudDirectoryPlaceholderSpec.CreateBuilder("Other", other).WithInSyncState(true).Build(),
        ]);
        CloudLocalFileBinding otherBinding = Assert.IsType<CloudLocalFileBinding>((await fixture.System.GetDirectory("Other").InspectAsync()).LocalBinding);
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed();
        await feed.StartAsync();
        Assert.Equal(CloudRemoteBatchStatus.Applied, (await fixture.System.ApplyRemoteChangesAsync(
            MetadataBoundaryBatch(owned, "Owned", CloudItemKind.Directory), new()
            { AttributeEchoPolicy = CloudRemoteAttributeEchoPolicy.CallerCoordinated })).Status);
        await using (ICloudStateTransaction store = await factory!.Store!.BeginTransactionAsync())
        {
            RemoteMetadataIntent original = Assert.Single((await store.Checkpoints.ListAsync(RemoteMetadataIntent.Prefix)).Select(RemoteMetadataIntent.Decode));
            RemoteMetadataIntent expanded = new(Guid.NewGuid(), original.CallId, other.ItemId, "Other", otherBinding,
                Coordinated: true, Completed: true);
            await expanded.PersistAsync(store.Checkpoints, DateTimeOffset.UtcNow, CancellationToken.None);
            await store.CommitAsync();
        }
        // Even a structurally valid custom-store receipt cannot enlarge the in-memory
        // native object set that the completed public call actually authorized.
        string otherPath = Path.Combine(fixture.Root, "Other");
        File.SetAttributes(otherPath, File.GetAttributes(otherPath) | FileAttributes.Hidden);
        await WaitForAccessRowsAsync(factory!, rows => rows.Any(row => LocalChangePayload.Decode(row.Payload).RelativePath == "Other"));
        await feed.DisposeAsync();
        CloudOperationJournalEntry retained = Assert.Single((await AccessRowsAsync(factory!))
            .Where(row => LocalChangePayload.Decode(row.Payload).RelativePath == "Other"));
        Assert.Null(retained.ItemId);
        Assert.True(File.GetAttributes(otherPath).HasFlag(FileAttributes.Hidden));
    }

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoordinatedMetadataReplacementRetainsTheStagedOriginalWithoutBlockingRename(bool directory)
    {
        AttributeCommitFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path, "staging"));
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-replacement");
        string relative = directory ? "Documents" : "original.bin";
        string originalPath = Path.Combine(fixture.Root, relative);
        CloudItem item;
        if (directory)
        {
            await fixture.System.Root.CreatePlaceholdersAsync([
                CloudDirectoryPlaceholderSpec.CreateBuilder(relative, identity).WithInSyncState(true).Build(),
            ]);
            item = fixture.System.GetDirectory(relative);
        }
        else
        {
            await File.WriteAllTextAsync(originalPath, "original resident content");
            item = fixture.System.GetFile(relative);
            await item.ConvertToPlaceholderAsync(identity);
            await item.SetInSyncAsync(true);
        }
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await item.InspectAsync()).LocalBinding);
        factory!.RelativePath = relative;
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed();
        await feed.StartAsync();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        Assert.Equal(CloudRemoteBatchStatus.Applied, (await fixture.System.ApplyRemoteChangesAsync(
            MetadataBoundaryBatch(identity, relative, item.Kind), new()
            { AttributeEchoPolicy = CloudRemoteAttributeEchoPolicy.CallerCoordinated }, deadline.Token)).Status);
        Guid original = await factory.Paused.Task.WaitAsync(deadline.Token);
        string retired = Path.Combine(fixture.Root, "retired");
        try
        {
            // Deliberately invalidate the declared namespace guarantee to test the native
            // generation check. Holding the journal transaction must not block this rename.
            if (directory)
            {
                Directory.Move(originalPath, retired);
                Directory.CreateDirectory(originalPath);
            }
            else
            {
                File.Move(originalPath, retired);
                await File.WriteAllTextAsync(originalPath, "replacement content", deadline.Token);
            }
            File.SetAttributes(originalPath, File.GetAttributes(originalPath) | FileAttributes.Hidden);
        }
        finally { factory.Resume.TrySetResult(); }
        while (true)
        {
            bool retained;
            await using (ICloudStateTransaction state = await factory.Inner!.BeginTransactionAsync(deadline.Token))
            {
                CloudOperationJournalEntry? operation = await state.Operations.GetAsync(original, deadline.Token);
                retained = operation is not null && operation.ItemId is null;
            }
            if (retained) { break; }
            await Task.Delay(20, deadline.Token);
        }
        await feed.DisposeAsync();
        CloudItem moved = directory ? fixture.System.GetDirectory("retired") : fixture.System.GetFile("retired");
        CloudItem replacement = directory ? fixture.System.GetDirectory(relative) : fixture.System.GetFile(relative);
        Assert.Equal(binding, (await moved.InspectAsync(deadline.Token)).LocalBinding);
        Assert.NotEqual(binding, (await replacement.InspectAsync(deadline.Token)).LocalBinding);
        IReadOnlyList<CloudOperationJournalEntry> originals;
        await using (ICloudStateTransaction state = await factory.Inner!.BeginTransactionAsync(deadline.Token))
        { originals = await state.Operations.ListAsync(100, cancellationToken: deadline.Token); }
        Assert.Contains(originals, row => row.OperationId == original && row.ItemId is null);
        // The successor's real attribute edit may bind after its creation is processed.
        // Neither that edit nor the original staged observation may publish the retired item at this path.
        Assert.DoesNotContain(originals, row => row.Kind == CloudStateOperationKind.MetadataUpdate && row.ItemId == identity.ItemId &&
            LocalChangePayload.Decode(row.Payload).RelativePath == relative);
        await fixture.RestartAsync();
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed();
        await reopened.StartAsync(deadline.Token);
        Assert.True((await reopened.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        await reopened.AcknowledgeFullRescanAsync(deadline.Token);
        await using (ICloudStateTransaction state = await factory.Inner!.BeginTransactionAsync(deadline.Token))
        { AssertOriginalAccessRows(originals, await state.Operations.ListAsync(100, cancellationToken: deadline.Token)); }
        await reopened.AcknowledgeAsync(originals.Select(row => row.OperationId), deadline.Token);
        Assert.True(File.GetAttributes(originalPath).HasFlag(FileAttributes.Hidden));
        Assert.Equal(0, fixture.Provider.Fetches);
    }

    [Trait("Category", "AccessJournal")]
    [Fact]
    public async Task DefaultMetadataPolicyDoesNotInheritAnEarlierCoordinatedWindow()
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-policy-transition");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder("Documents", identity).WithInSyncState(true).Build(),
        ]);
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed();
        await feed.StartAsync();
        CloudRemoteApplyOptions coordinated = new() { AttributeEchoPolicy = CloudRemoteAttributeEchoPolicy.CallerCoordinated };
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        for (int revision = 1; revision <= 3; revision++)
        {
            CloudRemoteChange change = new("metadata", CloudRemoteChangeKind.MetadataUpdate, identity.RemoteId,
                "remote-r" + revision, CloudItemKind.Directory, "Documents", identity.ItemId,
                metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().Build());
            CloudRemoteChangeBatch batch = new("metadata-transition-" + revision, Array.Empty<byte>(), [change], new byte[] { (byte)revision });
            Assert.Equal(CloudRemoteBatchStatus.Applied, (await fixture.System.ApplyRemoteChangesAsync(batch,
                revision == 2 ? CloudRemoteApplyOptions.Default : coordinated, deadline.Token)).Status);
            while (true)
            {
                bool drained;
                await using (ICloudStateTransaction state = await factory!.Store!.BeginTransactionAsync(deadline.Token))
                {
                    CloudStateCheckpoint? checkpoint = await state.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName, deadline.Token);
                    drained = checkpoint is not null && LocalChangeCheckpoint.Decode(checkpoint.Value).Observation >= revision * 2 &&
                        (await state.Checkpoints.ListAsync(CloudLocalChangeFeed.SecurityValidationPrefix, deadline.Token)).Count == 0;
                }
                if (drained) { break; }
                await Task.Delay(20, deadline.Token);
            }
            IReadOnlyList<CloudOperationJournalEntry> rows = await AccessRowsAsync(factory!);
            Assert.Equal(revision == 2, (await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
            if (revision == 2)
            {
                Assert.Equal(2, rows.Count);
                Assert.All(rows, row => Assert.Null(row.ItemId));
                await feed.AcknowledgeFullRescanAsync(deadline.Token);
                AssertOriginalAccessRows(rows, await AccessRowsAsync(factory!));
                await feed.AcknowledgeAsync(rows.Select(row => row.OperationId), deadline.Token);
            }
            else { Assert.Empty(rows); }
            Assert.Equal(CloudRemoteApplyEntryStatus.AlreadyApplied,
                Assert.Single((await fixture.System.ApplyRemoteChangesAsync(batch, coordinated, deadline.Token)).Entries).Status);
        }
        CloudDirectory directory = fixture.System.GetDirectory("Documents");
        await WriteSecurityObservationAsync(directory, await directory.InspectAsync(deadline.Token));
        await WaitForAccessRowsAsync(factory!, rows => rows.Count != 0);
        CloudOperationJournalEntry security = Assert.Single(await AccessRowsAsync(factory!));
        Assert.Equal(CloudStateOperationKind.MetadataUpdate, security.Kind);
        Assert.Equal(identity.ItemId, security.ItemId);
        Assert.False((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
    }

    private sealed class DelayedMetadataSource(string root) : ILocalChangeSource
    {
        private readonly WindowsLocalChangeSource _native = new(root, 4096);
        private readonly Channel<LocalChangeSourceEvent> _attributes = Channel.CreateUnbounded<LocalChangeSourceEvent>();
        private Func<LocalChangeSourceEvent, ValueTask>? _handler;
        public Task StartAsync(Func<LocalChangeSourceEvent, ValueTask> eventHandler, CancellationToken cancellationToken)
        {
            _handler = eventHandler;
            return _native.StartAsync(change =>
            {
                if (change.Action != LocalChangeSourceAction.AttributesModified) { return eventHandler(change); }
                _attributes.Writer.TryWrite(change);
                return ValueTask.CompletedTask;
            }, cancellationToken);
        }
        internal ValueTask<LocalChangeSourceEvent> NextAsync(CancellationToken cancellationToken) => _attributes.Reader.ReadAsync(cancellationToken);
        internal ValueTask DeliverAsync(LocalChangeSourceEvent change) => _handler!(change);
        public async ValueTask DisposeAsync()
        {
            await _native.DisposeAsync();
            while (_attributes.Reader.TryRead(out LocalChangeSourceEvent change)) { await _handler!(change); }
            _attributes.Writer.TryComplete();
        }
    }
}
