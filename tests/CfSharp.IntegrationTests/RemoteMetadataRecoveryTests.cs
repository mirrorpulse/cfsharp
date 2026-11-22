using System.Diagnostics;

using CfSharp.Storage.Sqlite;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Trait("Category", "AccessJournal")]
    [Fact]
    public async Task UnprovenMetadataCompletionCanBeClosedWithoutRewritingAndResumeTheOriginalBatch()
    {
        MetadataFaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path, "native-completion-before"));
        const string relative = "Documents";
        string path = Path.Combine(fixture.Root, relative);
        CloudPlaceholderIdentity first = new(Guid.NewGuid(), "unproven-metadata", "remote-r0");
        CloudPlaceholderIdentity second = new(Guid.NewGuid(), "following-metadata", "remote-r0");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder(relative, first).WithInSyncState(true).Build(),
            CloudDirectoryPlaceholderSpec.CreateBuilder("Next", second).WithInSyncState(true).Build(),
        ]);
        CloudPlaceholderMetadata hidden = CloudPlaceholderMetadata.CreateDirectoryBuilder()
            .WithAttributes(FileAttributes.Directory | FileAttributes.Hidden).Build();
        CloudRemoteChange uncertain = new("uncertain", CloudRemoteChangeKind.MetadataUpdate, first.RemoteId, "remote-r1",
            CloudItemKind.Directory, relative, first.ItemId, previousRemoteRevision: "remote-r0", metadata: hidden, cursorAfter: new byte[] { 8 });
        CloudRemoteChange next = new("next", CloudRemoteChangeKind.MetadataUpdate, second.RemoteId, "remote-r1",
            CloudItemKind.Directory, "Next", second.ItemId, previousRemoteRevision: "remote-r0", metadata: hidden);
        CloudRemoteChangeBatch batch = new("metadata-unproven", new byte[] { 7 }, [uncertain, next], new byte[] { 9 });
        factory!.Armed = true;
        CloudRemoteApplyResult failed = await fixture.System.ApplyRemoteChangesAsync(batch);
        Assert.True(failed.RequiresRetry);
        Assert.Equal(new byte[] { 7 }, failed.SafeCursor.ToArray());
        Assert.IsType<CloudItemCoordinationException>(failed.Entries[0].Error);
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        await fixture.RestartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.System.ReconcileRemoteMetadataAsync(batch, 0).AsTask());
        CloudRemoteChangeBatch changed = new(batch.BatchId, batch.InitialCursor, batch.Changes, new byte[] { 10 });
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.System.ReconcileRemoteMetadataAsync(changed, 0).AsTask());

        ControlledMetadataSource source = new();
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(options: null, source);
        await feed.StartAsync();
        Assert.True((await feed.BeginScanAsync()).RequiresFullRescan);
        File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.Hidden);
        await feed.AcknowledgeFullRescanAsync(feed.BeginFullRescan(), CancellationToken.None);
        CloudRemoteApplyResult stillUnproven = await fixture.System.ApplyRemoteChangesAsync(batch);
        Assert.True(stillUnproven.RequiresRetry);
        Assert.Equal(new byte[] { 7 }, stillUnproven.SafeCursor.ToArray());

        CloudRemoteApplyResult closed = await fixture.System.ReconcileRemoteMetadataAsync(batch, 0);
        Assert.True(closed.RequiresRetry);
        Assert.Equal(1, closed.AppliedEntryCount);
        Assert.Equal(new byte[] { 8 }, closed.SafeCursor.ToArray());
        Assert.Equal(CloudRemoteConflictReason.RemoteMetadataReconciliationRequired, closed.Entries[0].Conflict!.Reason);
        Assert.Equal(CloudRemoteApplyEntryStatus.NotProcessed, closed.Entries[1].Status);
        Guid conflictId = Assert.Single(closed.ConflictIds);
        CloudRemoteApplyResult repeated = await fixture.System.ReconcileRemoteMetadataAsync(batch, 0);
        Assert.Equal(CloudRemoteApplyEntryStatus.AlreadyApplied, repeated.Entries[0].Status);
        Assert.Empty(repeated.ConflictIds);
        CloudRemoteApplyEntryResult keepRemote = await fixture.System.ResolveRemoteConflictAsync(conflictId,
            new(CloudRemoteConflictDecision.KeepRemote));
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, keepRemote.Status);

        CloudRemoteApplyResult resumed = await fixture.System.ApplyRemoteChangesAsync(batch);
        Assert.False(resumed.RequiresRetry);
        Assert.Equal(new byte[] { 9 }, resumed.SafeCursor.ToArray());
        Assert.Equal(CloudRemoteApplyEntryStatus.AlreadyApplied, resumed.Entries[0].Status);
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, resumed.Entries[1].Status);
        Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        Assert.True(File.GetAttributes(Path.Combine(fixture.Root, "Next")).HasFlag(FileAttributes.Hidden));
        await using ICloudStateTransaction state = await factory.Store!.BeginTransactionAsync();
        Assert.Equal("remote-r0", (await state.Items.GetByItemIdAsync(first.ItemId))!.RemoteRevision);
        Assert.Null(await state.Checkpoints.GetAsync(RemoteMetadataProjection.Name(batch, 0)));
        Assert.Equal(conflictId, Assert.Single(await state.Conflicts.ListAsync()).ConflictId);
    }

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData("new revision")]
    [InlineData("same revision")]
    [InlineData("new remote ID")]
    [InlineData("changed original row")]
    public async Task MetadataProjectionRollbackRepairsOriginalWriteWithoutMutatingAgain(string scenario)
    {
        MetadataFaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path, "projection-before"));
        const string relative = "Documents";
        string path = Path.Combine(fixture.Root, relative);
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-projection-rollback", "remote-r0");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder(relative, identity).WithInSyncState(true).Build(),
        ]);
        string revision = scenario == "same revision" ? "remote-r0" : "remote-r1";
        string remoteId = scenario == "new remote ID" ? "metadata-rebound" : identity.RemoteId;
        CloudRemoteChange change = new("metadata", CloudRemoteChangeKind.MetadataUpdate, remoteId, revision,
            CloudItemKind.Directory, relative, identity.ItemId, previousRemoteRevision: "remote-r0",
            metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().WithAttributes(FileAttributes.Directory | FileAttributes.Hidden).Build());
        CloudRemoteChangeBatch batch = new("metadata-fault", new byte[] { 7 }, [change], new byte[] { 8 });
        string projectionName = RemoteMetadataProjection.Name(batch, 0);
        factory!.Armed = true;

        CloudRemoteApplyResult interrupted = await fixture.System.ApplyRemoteChangesAsync(batch);
        Assert.True(interrupted.RequiresRetry);
        Assert.Equal(CloudRemoteApplyEntryStatus.Failed, Assert.Single(interrupted.Entries).Status);
        Assert.IsType<CloudItemCoordinationException>(Assert.Single(interrupted.Entries).Error);
        Assert.Equal(new byte[] { 7 }, interrupted.SafeCursor.ToArray());
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        await using (ICloudStateTransaction transaction = await factory.Store!.BeginTransactionAsync())
        {
            CloudItemState original = (await transaction.Items.GetByItemIdAsync(identity.ItemId))!;
            Assert.Equal("remote-r0", original.RemoteRevision);
            Assert.Equal(identity.RemoteId, original.RemoteId);
            RemoteMetadataProjection retained = RemoteMetadataProjection.Decode((await transaction.Checkpoints.GetAsync(projectionName))!);
            Assert.Equal(RemoteMetadataProjection.NativeWriteCompleted, retained.ProjectedAtTicks);
            Assert.Equal(RemoteMetadataProjection.Fingerprint(original), retained.OriginalRowFingerprint);
            if (scenario == "changed original row")
            {
                await transaction.Items.UpsertAsync(new(original.ItemId, original.RemoteId, original.RelativePath, original.Kind,
                    "acknowledged-local-revision", original.LocalFileId, original.IsTombstone, original.UpdatedAt));
                await transaction.CommitAsync();
            }
        }

        await fixture.RestartAsync();
        ControlledMetadataSource source = new();
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(options: null, source);
        await feed.StartAsync();
        Assert.True((await feed.BeginScanAsync()).RequiresFullRescan);
        File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.Hidden);
        await feed.AcknowledgeFullRescanAsync(feed.BeginFullRescan(), CancellationToken.None);
        CloudRemoteApplyResult replay = await fixture.System.ApplyRemoteChangesAsync(batch);
        if (scenario == "changed original row")
        {
            Assert.True(replay.RequiresRetry);
            Assert.Equal(CloudRemoteApplyEntryStatus.Failed, Assert.Single(replay.Entries).Status);
            Assert.Equal(new byte[] { 7 }, replay.SafeCursor.ToArray());
            Assert.Empty(replay.ConflictIds);
            await using ICloudStateTransaction transaction = await factory.Store!.BeginTransactionAsync();
            Assert.NotNull(await transaction.Checkpoints.GetAsync(projectionName));
            Assert.Equal("acknowledged-local-revision", (await transaction.Items.GetByItemIdAsync(identity.ItemId))!.RemoteRevision);
            return;
        }
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single(replay.Entries).Status);
        Assert.False(replay.RequiresRetry);
        Assert.Equal(new byte[] { 8 }, replay.SafeCursor.ToArray());
        Assert.Empty(replay.ConflictIds);
        Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));

        await using (ICloudStateTransaction transaction = await factory.Store!.BeginTransactionAsync())
        {
            CloudItemState projected = (await transaction.Items.GetByItemIdAsync(identity.ItemId))!;
            Assert.Equal(revision, projected.RemoteRevision);
            Assert.Equal(remoteId, projected.RemoteId);
            Assert.Null(await transaction.Checkpoints.GetAsync(projectionName));
            Assert.Empty(await transaction.Conflicts.ListAsync());
        }
        Assert.Equal(CloudRemoteApplyEntryStatus.AlreadyApplied, Assert.Single((await fixture.System.ApplyRemoteChangesAsync(batch)).Entries).Status);
    }

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task RemoteMetadataWritePreservesConcurrentReconciliation(bool suppressEcho, bool beforeNativeUpdate)
    {
        MetadataFaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path,
            beforeNativeUpdate ? "preparation-paused-after" : "native-projection-paused-after"));
        const string relative = "Documents";
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-concurrent-reconciliation");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder(relative, identity).WithInSyncState(true).Build(),
        ]);
        CloudDirectory item = fixture.System.GetDirectory(relative);
        ControlledMetadataSource source = new();
        // The preparation fault pauses inside CommitAsync after durability, before the owner
        // releases its publication lease. Publish independently to model the durable input
        // that can be present when the final guard acquires the lease.
        await using CloudLocalChangeFeed feed = beforeNativeUpdate
            ? new(fixture.Root, factory!.Store!, new(), source)
            : fixture.System.CreateLocalChangeFeed(options: null, source);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        await feed.StartAsync(deadline.Token);

        DateTime localLastWrite = new(2020, 3, 4, 12, 0, 0, DateTimeKind.Utc);
        DateTime remoteLastWrite = localLastWrite.AddDays(1);
        CloudRemoteChange metadata = new("metadata", CloudRemoteChangeKind.MetadataUpdate, identity.RemoteId,
            "remote-r1", CloudItemKind.Directory, relative, identity.ItemId,
            metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().WithLastWriteTime(remoteLastWrite).Build());
        factory!.Armed = true;
        Task<CloudRemoteApplyResult> apply = fixture.System.ApplyRemoteChangesAsync(
            new("metadata-concurrent", Array.Empty<byte>(), [metadata], new byte[] { 1 }), new()
            {
                SuppressLocalEcho = suppressEcho,
                AttributeEchoPolicy = CloudRemoteAttributeEchoPolicy.CallerCoordinated,
            }, deadline.Token).AsTask();
        CloudOperationJournalEntry original;
        try
        {
            await factory.OutcomePaused.Task.WaitAsync(deadline.Token);
            if (!beforeNativeUpdate) { Assert.Equal(remoteLastWrite, Directory.GetLastWriteTimeUtc(item.FullPath)); }
            Directory.SetLastWriteTimeUtc(item.FullPath, localLastWrite);
            await source.EmitAsync(new(LocalChangeSourceAction.Modified, relative));
            while (true)
            {
                await using ICloudStateTransaction transaction = await factory.Store!.BeginTransactionAsync(deadline.Token);
                IReadOnlyList<CloudOperationJournalEntry> rows = await transaction.Operations.ListAsync(10, cancellationToken: deadline.Token);
                if (rows.Count != 0)
                {
                    original = Assert.Single(rows);
                    RemoteMetadataIntent prepared = RemoteMetadataIntent.Decode(Assert.Single(await transaction.Checkpoints
                        .ListAsync(RemoteMetadataIntent.ItemPrefix(identity.ItemId), deadline.Token)));
                    Assert.True(prepared is { RequiresReconciliation: true, Completed: false });
                    break;
                }
                await transaction.RollbackAsync(deadline.Token);
                await Task.Delay(10, deadline.Token);
            }
        }
        finally { factory.ResumeOutcome.TrySetResult(); }

        CloudRemoteApplyEntryResult entry = Assert.Single((await apply.WaitAsync(deadline.Token)).Entries);
        Assert.Equal(beforeNativeUpdate ? CloudRemoteApplyEntryStatus.Conflict : CloudRemoteApplyEntryStatus.Applied, entry.Status);
        if (beforeNativeUpdate) { Assert.Equal(CloudRemoteConflictReason.Metadata, entry.Conflict!.Reason); }
        string? expectedRevision = beforeNativeUpdate ? null : "remote-r1";
        Assert.Equal(localLastWrite, Directory.GetLastWriteTimeUtc(item.FullPath));
        Assert.Equal(expectedRevision, (await item.InspectAsync(deadline.Token)).RemoteRevision);
        Assert.Equal(CloudStateOperationKind.MetadataUpdate, original.Kind);
        Assert.Null(original.ItemId);
        Assert.True((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);

        CloudRemoteChange upsert = new("upsert", CloudRemoteChangeKind.DirectoryUpsert, identity.RemoteId, "remote-r2",
            CloudItemKind.Directory, relative, identity.ItemId, previousRemoteRevision: expectedRevision,
            metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().WithLastWriteTime(remoteLastWrite).Build());
        CloudRemoteApplyEntryResult blocked = Assert.Single((await fixture.System.ApplyRemoteChangesAsync(
            new("metadata-upsert", Array.Empty<byte>(), [upsert], new byte[] { 2 }), cancellationToken: deadline.Token)).Entries);
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, blocked.Status);
        Assert.Equal(CloudRemoteConflictReason.Metadata, blocked.Conflict!.Reason);
        Assert.Equal(localLastWrite, Directory.GetLastWriteTimeUtc(item.FullPath));
        Assert.Equal(expectedRevision, (await item.InspectAsync(deadline.Token)).RemoteRevision);
        await using (ICloudStateTransaction transaction = await factory.Store!.BeginTransactionAsync(deadline.Token))
        {
            RemoteMetadataIntent completed = RemoteMetadataIntent.Decode(Assert.Single(await transaction.Checkpoints
                .ListAsync(RemoteMetadataIntent.ItemPrefix(identity.ItemId), deadline.Token)));
            Assert.True(completed is { Completed: true, RequiresReconciliation: true });
            Assert.Equal(!beforeNativeUpdate, completed.NativeWriteApplied);
            Assert.Equal(!beforeNativeUpdate, completed.OutcomeConfirmed);
        }

        await feed.AcknowledgeFullRescanAsync(deadline.Token);
        CloudLocalChangePage page = await feed.ReadPageAsync(await feed.BeginScanAsync(deadline.Token), 0, 10, deadline.Token);
        CloudLocalChange observation = Assert.Single(page.Changes);
        Assert.Equal((original.OperationId, original.Sequence, relative),
            (observation.OperationId, observation.Sequence, observation.RelativePath));
        Assert.Null(observation.ItemId);
        await feed.AcknowledgeAsync([original.OperationId], deadline.Token);
        CloudRemoteApplyEntryResult reconciled = Assert.Single((await fixture.System.ApplyRemoteChangesAsync(
            new("metadata-upsert-reconciled", Array.Empty<byte>(), [upsert], new byte[] { 3 }), cancellationToken: deadline.Token)).Entries);
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, reconciled.Status);
        Assert.Equal(remoteLastWrite, Directory.GetLastWriteTimeUtc(item.FullPath));
    }

    [Trait("Category", "AccessJournal")]
    [Fact]
    public async Task RejectedPassthroughRetainsADelayedRemoteObservationUnboundAfterRescan()
    {
        MetadataFaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path, "preparation-paused-after"));
        const string relative = "Documents";
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-passthrough-conflict");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder(relative, identity).WithInSyncState(true).Build(),
        ]);
        DelayedMetadataSource source = new(fixture.Root);
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(options: null, source);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        await feed.StartAsync(deadline.Token);
        CloudRemoteApplyOptions coordinated = new()
        {
            AttributeEchoPolicy = CloudRemoteAttributeEchoPolicy.CallerCoordinated,
            EchoSuppressionLifetime = TimeSpan.FromSeconds(3),
        };
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single((await fixture.System.ApplyRemoteChangesAsync(
            MetadataBoundaryBatch(identity, relative, CloudItemKind.Directory), coordinated, deadline.Token)).Entries).Status);
        long appliedAt = Stopwatch.GetTimestamp();
        LocalChangeSourceEvent delayed = await source.NextAsync(deadline.Token);
        Assert.Equal(relative, delayed.RelativePath);

        CloudDirectory item = fixture.System.GetDirectory(relative);
        CloudRemoteChange write = new("metadata", CloudRemoteChangeKind.MetadataUpdate, identity.RemoteId,
            "remote-r2", CloudItemKind.Directory, relative, identity.ItemId, previousRemoteRevision: "remote-r1",
            metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().WithAttributes(FileAttributes.Directory | FileAttributes.Hidden).Build());
        factory!.Armed = true;
        Task<CloudRemoteApplyResult> apply = fixture.System.ApplyRemoteChangesAsync(
            new("metadata-rejected-passthrough", Array.Empty<byte>(), [write], new byte[] { 2 }),
            new() { SuppressLocalEcho = false }, deadline.Token).AsTask();
        try
        {
            await factory.OutcomePaused.Task.WaitAsync(deadline.Token);
            while (coordinated.EchoSuppressionLifetime - Stopwatch.GetElapsedTime(appliedAt) is { Ticks: > 0 } remaining)
            { await Task.Delay(remaining + TimeSpan.FromMilliseconds(1), deadline.Token); }
        }
        finally { factory.ResumeOutcome.TrySetResult(); }

        CloudRemoteApplyEntryResult outcome = Assert.Single((await apply.WaitAsync(deadline.Token)).Entries);
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, outcome.Status);
        Assert.Equal(CloudRemoteConflictReason.Metadata, outcome.Conflict!.Reason);
        Assert.False(File.GetAttributes(item.FullPath).HasFlag(FileAttributes.Hidden));
        Assert.True((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        await feed.AcknowledgeFullRescanAsync(deadline.Token);
        Assert.False((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);

        await source.DeliverAsync(delayed);
        CloudLocalChangeBatch fenced = await feed.ReadBatchAsync(deadline.Token);
        Assert.True(fenced.RequiresFullRescan);
        await WaitForMetadataRescanMarkerAsync(factory.Store!, deadline.Token);
        await using (ICloudStateTransaction state = await factory.Store!.BeginTransactionAsync(deadline.Token))
        {
            RemoteMetadataIntent receipt = RemoteMetadataIntent.Decode(Assert.Single(
                await state.Checkpoints.ListAsync(RemoteMetadataIntent.ItemPrefix(identity.ItemId), deadline.Token)));
            Assert.True(receipt is { Completed: true, RequiresReconciliation: true, NativeWriteApplied: false, OutcomeConfirmed: false });
        }
        await feed.AcknowledgeFullRescanAsync(deadline.Token);
        CloudLocalChange observation = Assert.Single((await feed.ReadPageAsync(await feed.BeginScanAsync(deadline.Token), 0, 10, deadline.Token)).Changes);
        Assert.Equal(CloudLocalChangeKind.MetadataUpdate, observation.Kind);
        Assert.Equal(relative, observation.RelativePath);
        Assert.Null(observation.ItemId);
    }

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteMetadataPublicationPreservesObservationsWithOverlappingTransactions(bool pauseConfirmation)
    {
        MetadataFaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path,
            pauseConfirmation ? "overlapping-confirmation" : "overlapping-outcome"));
        const string relative = "Documents";
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-overlapping-publication");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder(relative, identity).WithInSyncState(true).Build(),
        ]);
        ControlledMetadataSource source = new();
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(options: null, source);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        await feed.StartAsync(deadline.Token);
        factory!.Armed = true;
        Task<CloudRemoteApplyResult> apply = fixture.System.ApplyRemoteChangesAsync(
            MetadataBoundaryBatch(identity, relative, CloudItemKind.Directory), new() { SuppressLocalEcho = false }, deadline.Token).AsTask();
        string path = Path.Combine(fixture.Root, relative);
        try
        {
            await factory.OutcomePaused.Task.WaitAsync(deadline.Token);
            // The custom writer has an unpublished snapshot, but an independent store
            // transaction can complete and still sees the previous committed version.
            await using (ICloudStateTransaction concurrent = await factory.Store!.BeginTransactionAsync(deadline.Token))
            {
                RemoteMetadataIntent published = RemoteMetadataIntent.Decode(Assert.Single(
                    await concurrent.Checkpoints.ListAsync(RemoteMetadataIntent.ItemPrefix(identity.ItemId), deadline.Token)));
                Assert.Equal(pauseConfirmation, published.Completed);
                Assert.False(published.OutcomeConfirmed);
                await concurrent.CommitAsync(deadline.Token);
            }
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            await source.EmitAsync(new(LocalChangeSourceAction.Modified, relative));
            // Give an unguarded feed time to commit before the stale transaction resumes.
            using CancellationTokenSource observationWindow = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            observationWindow.CancelAfter(TimeSpan.FromSeconds(1));
            try { await factory.ObservationCommitted.Task.WaitAsync(observationWindow.Token); }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
        }
        finally { factory.ResumeOutcome.TrySetResult(); }

        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single((await apply.WaitAsync(deadline.Token)).Entries).Status);
        CloudOperationJournalEntry? original = null;
        RemoteMetadataIntent receipt;
        do
        {
            await using (ICloudStateTransaction state = await factory.Store!.BeginTransactionAsync(deadline.Token))
            {
                original = (await state.Operations.ListAsync(10, deadline.Token)).SingleOrDefault();
                receipt = RemoteMetadataIntent.Decode(Assert.Single(
                    await state.Checkpoints.ListAsync(RemoteMetadataIntent.ItemPrefix(identity.ItemId), deadline.Token)));
            }
            if (original is null) { await Task.Delay(20, deadline.Token); }
        }
        while (original is null);

        Assert.True(receipt.OutcomeConfirmed);
        bool requiresReconciliation = original.ItemId is null;
        Assert.Equal(requiresReconciliation, receipt.RequiresReconciliation);
        Assert.Equal(requiresReconciliation, (await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        if (!requiresReconciliation) { Assert.Equal(identity.ItemId, original.ItemId); }
        Assert.Equal(CloudStateOperationKind.MetadataUpdate, original.Kind);
        Assert.Equal(relative, LocalChangePayload.Decode(original.Payload).RelativePath);
        CloudRemoteChange upsert = new("upsert", CloudRemoteChangeKind.DirectoryUpsert, identity.RemoteId, "remote-r2",
            CloudItemKind.Directory, relative, identity.ItemId, previousRemoteRevision: "remote-r1",
            metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().WithAttributes(FileAttributes.Directory).Build());
        CloudRemoteApplyEntryResult blocked = Assert.Single((await fixture.System.ApplyRemoteChangesAsync(
            new("protected-upsert", Array.Empty<byte>(), [upsert], new byte[] { 2 }), cancellationToken: deadline.Token)).Entries);
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, blocked.Status);
        Assert.Equal(requiresReconciliation ? CloudRemoteConflictReason.Metadata : CloudRemoteConflictReason.Content, blocked.Conflict!.Reason);
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        if (requiresReconciliation) { await feed.AcknowledgeFullRescanAsync(feed.BeginFullRescan(), deadline.Token); }
        CloudLocalChangePage page = await feed.ReadPageAsync(await feed.BeginScanAsync(deadline.Token), 0, 10, deadline.Token);
        CloudLocalChange delivered = Assert.Single(page.Changes);
        Assert.Equal((original.OperationId, original.Sequence), (delivered.OperationId, delivered.Sequence));
    }

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullRescanAcknowledgementPreservesCoordinationThatExpiresWhileWaiting(bool beforeAuthorization)
    {
        MetadataFaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path, "rescan-awaiting-authorization"));
        const string relative = "Documents";
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-rescan-expiry");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder(relative, identity).WithInSyncState(true).Build(),
        ]);
        ControlledMetadataSource source = new();
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(options: null, source);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        await feed.StartAsync(deadline.Token);
        TimeSpan lifetime = TimeSpan.FromSeconds(3);
        await source.EmitAsync(new(LocalChangeSourceAction.Overflow, string.Empty));
        await WaitForMetadataRescanMarkerAsync(factory!.Store!, deadline.Token);

        factory!.Armed = beforeAuthorization;
        Task<CloudRemoteApplyResult> apply = fixture.System.ApplyRemoteChangesAsync(
            MetadataBoundaryBatch(identity, relative, CloudItemKind.Directory), new()
            { AttributeEchoPolicy = CloudRemoteAttributeEchoPolicy.CallerCoordinated, EchoSuppressionLifetime = lifetime }, deadline.Token).AsTask();
        Task acknowledge;
        try
        {
            if (beforeAuthorization) { await factory.OutcomePaused.Task.WaitAsync(deadline.Token); }
            else { await apply.WaitAsync(deadline.Token); }
            factory.PauseNextTransaction();
            acknowledge = feed.AcknowledgeFullRescanAsync(feed.BeginFullRescan(), deadline.Token).AsTask();
        }
        finally { factory.ResumeOutcome.TrySetResult(); }

        string path = Path.Combine(fixture.Root, relative);
        try
        {
            Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single((await apply.WaitAsync(deadline.Token)).Entries).Status);
            long appliedAt = Stopwatch.GetTimestamp();
            await factory.TransactionPaused.Task.WaitAsync(deadline.Token);
            Assert.True(Stopwatch.GetElapsedTime(appliedAt) < lifetime);
            while (lifetime - Stopwatch.GetElapsedTime(appliedAt) is { Ticks: > 0 } remaining)
            { await Task.Delay(remaining + TimeSpan.FromMilliseconds(1), deadline.Token); }
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        }
        finally { factory.ResumeTransaction.TrySetResult(); }
        await acknowledge.WaitAsync(deadline.Token);

        Assert.True((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        await using (ICloudStateTransaction state = await factory.Store!.BeginTransactionAsync(deadline.Token))
        { Assert.Empty(await state.Operations.ListAsync(10, deadline.Token)); }
        CloudRemoteChange upsert = new("upsert", CloudRemoteChangeKind.DirectoryUpsert, identity.RemoteId, "remote-r2",
            CloudItemKind.Directory, relative, identity.ItemId, previousRemoteRevision: "remote-r1",
            metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().WithAttributes(FileAttributes.Directory).Build());
        CloudRemoteApplyEntryResult blocked = Assert.Single((await fixture.System.ApplyRemoteChangesAsync(
            new("protected-upsert", Array.Empty<byte>(), [upsert], new byte[] { 2 }), cancellationToken: deadline.Token)).Entries);
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, blocked.Status);
        Assert.Equal(CloudRemoteConflictReason.Metadata, blocked.Conflict!.Reason);
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
    }

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleRescanAcknowledgementCannotClearANewMetadataReceipt(bool pauseBeforePreparation)
    {
        MetadataFaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path,
            pauseBeforePreparation ? "rescan-before-preparation" : "rescan-awaiting-authorization"));
        const string relative = "Documents";
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-stale-rescan");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder(relative, identity).WithInSyncState(true).Build(),
        ]);
        ControlledMetadataSource source = new();
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(options: null, source);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        await feed.StartAsync(deadline.Token);
        await source.EmitAsync(new(LocalChangeSourceAction.Overflow, string.Empty));
        await WaitForMetadataRescanMarkerAsync(factory!.Store!, deadline.Token);
        Assert.True((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);

        factory.Armed = true;
        Task<CloudRemoteApplyResult> apply = fixture.System.ApplyRemoteChangesAsync(
            MetadataBoundaryBatch(identity, relative, CloudItemKind.Directory), cancellationToken: deadline.Token).AsTask();
        string path = Path.Combine(fixture.Root, relative);
        Task acknowledge;
        try
        {
            await factory.OutcomePaused.Task.WaitAsync(deadline.Token);
            acknowledge = feed.AcknowledgeFullRescanAsync(feed.BeginFullRescan(), deadline.Token).AsTask();
            Assert.False(acknowledge.IsCompleted);
            if (!pauseBeforePreparation) { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); }
        }
        finally { factory.ResumeOutcome.TrySetResult(); }

        if (pauseBeforePreparation)
        {
            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => acknowledge.WaitAsync(deadline.Token));
            Assert.Contains("after the rescan boundary", error.Message, StringComparison.Ordinal);
        }
        else { await acknowledge.WaitAsync(deadline.Token); }
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single((await apply.WaitAsync(deadline.Token)).Entries).Status);
        if (pauseBeforePreparation) { File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden); }

        Assert.True((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        await using (ICloudStateTransaction state = await factory.Store!.BeginTransactionAsync(deadline.Token))
        {
            RemoteMetadataIntent receipt = RemoteMetadataIntent.Decode(Assert.Single(
                await state.Checkpoints.ListAsync(RemoteMetadataIntent.ItemPrefix(identity.ItemId), deadline.Token)));
            Assert.True(receipt is { Completed: true, OutcomeConfirmed: true, Reconciled: false });
            Assert.Empty(await state.Operations.ListAsync(10, deadline.Token));
        }
        CloudRemoteChange upsert = new("upsert", CloudRemoteChangeKind.DirectoryUpsert, identity.RemoteId, "remote-r2",
            CloudItemKind.Directory, relative, identity.ItemId, previousRemoteRevision: "remote-r1",
            metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().WithAttributes(FileAttributes.Directory).Build());
        CloudRemoteApplyEntryResult blocked = Assert.Single((await fixture.System.ApplyRemoteChangesAsync(
            new("protected-upsert", Array.Empty<byte>(), [upsert], new byte[] { 2 }), cancellationToken: deadline.Token)).Entries);
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, blocked.Status);
        Assert.Equal(CloudRemoteConflictReason.Metadata, blocked.Conflict!.Reason);
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
    }

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RescanAcknowledgementCannotClearNewMetadataEvidence(bool explicitBoundary, bool deliverAnObservation)
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        const string firstPath = "Documents";
        const string secondPath = "Other";
        CloudPlaceholderIdentity first = new(Guid.NewGuid(), "metadata-rescan-original");
        CloudPlaceholderIdentity second = new(Guid.NewGuid(), "metadata-rescan-later");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder(firstPath, first).WithInSyncState(true).Build(),
            CloudDirectoryPlaceholderSpec.CreateBuilder(secondPath, second).WithInSyncState(true).Build(),
        ]);
        ControlledMetadataSource source = new();
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(options: null, source);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        await feed.StartAsync(deadline.Token);
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single((await fixture.System.ApplyRemoteChangesAsync(
            MetadataBoundaryBatch(first, firstPath, CloudItemKind.Directory), cancellationToken: deadline.Token)).Entries).Status);
        CloudRemoteChange metadata = new("metadata", CloudRemoteChangeKind.MetadataUpdate, second.RemoteId,
            "remote-r1", CloudItemKind.Directory, secondPath, second.ItemId,
            metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().Build());
        CloudRemoteChangeBatch laterBatch = new("metadata-rescan-later", Array.Empty<byte>(), [metadata], new byte[] { 2 });
        if (deliverAnObservation)
        {
            Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single((await fixture.System.ApplyRemoteChangesAsync(
                laterBatch, cancellationToken: deadline.Token)).Entries).Status);
        }

        CloudLocalChangeScan originalScan = await feed.BeginScanAsync(deadline.Token);
        Assert.True(originalScan.RequiresFullRescan);
        CloudLocalChangeRescan boundary = feed.BeginFullRescan();
        string path = Path.Combine(fixture.Root, secondPath);
        Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        if (!deliverAnObservation)
        {
            // Finish scanning before the second write begins. Without a watcher event,
            // only the newly prepared receipt protects the native edit that follows it.
            Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single((await fixture.System.ApplyRemoteChangesAsync(
                laterBatch, cancellationToken: deadline.Token)).Entries).Status);
        }
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        if (deliverAnObservation)
        {
            // Reopen an existing receipt without preparing another remote update.
            await source.EmitAsync(new(LocalChangeSourceAction.Modified, secondPath));
            await WaitForAccessRowsAsync(factory!, rows => rows.Count == 1);
            Assert.Null(Assert.Single(await AccessRowsAsync(factory!)).ItemId);
        }
        else { Assert.Empty(await AccessRowsAsync(factory!)); }
        Assert.True((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        await Assert.ThrowsAsync<InvalidOperationException>(() => explicitBoundary
            ? feed.AcknowledgeFullRescanAsync(boundary, deadline.Token).AsTask()
            : feed.AcknowledgeFullRescanAsync(deadline.Token).AsTask());

        CloudRemoteChange upsert = new("upsert", CloudRemoteChangeKind.DirectoryUpsert, second.RemoteId, "remote-r2",
            CloudItemKind.Directory, secondPath, second.ItemId, previousRemoteRevision: "remote-r1",
            metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().WithAttributes(FileAttributes.Directory).Build());
        CloudRemoteApplyEntryResult blocked = Assert.Single((await fixture.System.ApplyRemoteChangesAsync(
            new("protected-upsert", Array.Empty<byte>(), [upsert], new byte[] { 3 }), cancellationToken: deadline.Token)).Entries);
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, blocked.Status);
        Assert.Equal(CloudRemoteConflictReason.Metadata, blocked.Conflict!.Reason);
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));

        if (!explicitBoundary)
        {
            // Repeating an ACK alone cannot open a new boundary for the old scan.
            await Assert.ThrowsAsync<InvalidOperationException>(() => feed.AcknowledgeFullRescanAsync(deadline.Token).AsTask());
            Assert.True((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        }
        CloudLocalChangeRescan fresh = feed.BeginFullRescan();
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        if (explicitBoundary) { await feed.AcknowledgeFullRescanAsync(fresh, deadline.Token); }
        else { await feed.AcknowledgeFullRescanAsync(deadline.Token); }
        Assert.False((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        await Assert.ThrowsAsync<InvalidOperationException>(() => feed.AcknowledgeFullRescanAsync(fresh, deadline.Token).AsTask());

        // A page from the previous epoch cannot reserve the next legacy boundary.
        Assert.True((await feed.ReadPageAsync(originalScan, 0, 10, deadline.Token)).RequiresFullRescan);
        CloudRemoteChange next = new("metadata", CloudRemoteChangeKind.MetadataUpdate, first.RemoteId,
            "remote-r2", CloudItemKind.Directory, firstPath, first.ItemId, previousRemoteRevision: "remote-r1",
            metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().Build());
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single((await fixture.System.ApplyRemoteChangesAsync(
            new("metadata-next-rescan", Array.Empty<byte>(), [next], new byte[] { 4 }), cancellationToken: deadline.Token)).Entries).Status);
        Assert.True((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        await feed.AcknowledgeFullRescanAsync(deadline.Token);
        Assert.False((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
    }

    [Trait("Category", "AccessJournal")]
    [Fact]
    public async Task RemoteMetadataWriteRejectsAReplacementAfterPreparation()
    {
        MetadataFaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path, "preparation-paused-after"));
        const string relative = "Documents";
        const string otherPath = "Other";
        CloudPlaceholderIdentity target = new(Guid.NewGuid(), "metadata-original", "r1");
        CloudPlaceholderIdentity other = new(Guid.NewGuid(), "metadata-replacement", "r1");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder(relative, target).WithInSyncState(true).Build(),
            CloudDirectoryPlaceholderSpec.CreateBuilder(otherPath, other).WithInSyncState(true).Build(),
        ]);
        CloudDirectory item = fixture.System.GetDirectory(relative);
        CloudDirectory replacement = fixture.System.GetDirectory(otherPath);
        CloudItemSnapshot original = await item.InspectAsync();
        CloudItemSnapshot successor = await replacement.InspectAsync();
        string retired = Path.Combine(fixture.Root, "Retired");
        DateTime remoteLastWrite = new(2020, 3, 4, 12, 0, 0, DateTimeKind.Utc);
        CloudRemoteChange metadata = new("metadata", CloudRemoteChangeKind.MetadataUpdate, target.RemoteId,
            "r2", CloudItemKind.Directory, relative, target.ItemId, previousRemoteRevision: "r1",
            metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().WithLastWriteTime(remoteLastWrite).Build());
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        factory!.Armed = true;
        Task<CloudRemoteApplyResult> apply = fixture.System.ApplyRemoteChangesAsync(
            new("metadata-replacement", Array.Empty<byte>(), [metadata], new byte[] { 1 }), cancellationToken: deadline.Token).AsTask();
        DateTime replacementLastWrite;
        try
        {
            await factory.OutcomePaused.Task.WaitAsync(deadline.Token);
            Directory.Move(item.FullPath, retired);
            Directory.Move(replacement.FullPath, item.FullPath);
            replacementLastWrite = Directory.GetLastWriteTimeUtc(item.FullPath);
        }
        finally { factory.ResumeOutcome.TrySetResult(); }

        CloudRemoteApplyEntryResult rejected = Assert.Single((await apply.WaitAsync(deadline.Token)).Entries);
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, rejected.Status);
        Assert.Equal(CloudRemoteConflictReason.PathCollision, rejected.Conflict!.Reason);
        CloudItemSnapshot untouched = await item.InspectAsync(deadline.Token);
        Assert.Equal(successor.LocalBinding, untouched.LocalBinding);
        Assert.Equal(successor.PlaceholderIdentity.ToArray(), untouched.PlaceholderIdentity.ToArray());
        Assert.Equal(original.PlaceholderIdentity.ToArray(), (await fixture.System.GetDirectory("Retired").InspectAsync(deadline.Token))
            .PlaceholderIdentity.ToArray());
        Assert.Equal(replacementLastWrite, Directory.GetLastWriteTimeUtc(item.FullPath));
        await using ICloudStateTransaction state = await factory.Store!.BeginTransactionAsync(deadline.Token);
        Assert.Equal("r1", (await state.Items.GetByItemIdAsync(target.ItemId, deadline.Token))!.RemoteRevision);
        Assert.Equal("r1", (await state.Items.GetByItemIdAsync(other.ItemId, deadline.Token))!.RemoteRevision);
    }

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataUpdateRejectsAnExplicitIdForADifferentItem(bool staleIdExists)
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        const string relative = "Documents";
        CloudPlaceholderIdentity target = new(Guid.NewGuid(), "metadata-target", "r1");
        CloudPlaceholderIdentity other = new(Guid.NewGuid(), "metadata-other", "r1");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder(relative, target).WithInSyncState(true).Build(),
        ]);
        if (staleIdExists)
        {
            await fixture.System.Root.CreatePlaceholdersAsync([
                CloudDirectoryPlaceholderSpec.CreateBuilder("Other", other).WithInSyncState(true).Build(),
            ]);
        }
        CloudDirectory item = fixture.System.GetDirectory(relative);
        CloudItemSnapshot original = await item.InspectAsync();
        DateTime originalLastWrite = Directory.GetLastWriteTimeUtc(item.FullPath);
        DateTime remoteLastWrite = new(2020, 3, 4, 12, 0, 0, DateTimeKind.Utc);
        CloudPlaceholderMetadata metadata = CloudPlaceholderMetadata.CreateDirectoryBuilder().WithLastWriteTime(remoteLastWrite).Build();
        CloudRemoteChange stale = new("metadata", CloudRemoteChangeKind.MetadataUpdate, target.RemoteId, "r2",
            CloudItemKind.Directory, relative, other.ItemId, previousRemoteRevision: "r1", metadata: metadata);
        CloudRemoteApplyEntryResult rejected = Assert.Single((await fixture.System.ApplyRemoteChangesAsync(
            new("metadata-stale-id", Array.Empty<byte>(), [stale], new byte[] { 1 }))).Entries);
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, rejected.Status);
        Assert.Equal(CloudRemoteConflictReason.PathCollision, rejected.Conflict!.Reason);
        Assert.Equal(target.ItemId, rejected.Conflict.LocalState!.ItemId);
        Assert.Equal(originalLastWrite, Directory.GetLastWriteTimeUtc(item.FullPath));
        CloudItemSnapshot unchanged = await item.InspectAsync();
        Assert.Equal(original.PlaceholderIdentity.ToArray(), unchanged.PlaceholderIdentity.ToArray());
        Assert.Equal("r1", unchanged.RemoteRevision);
        await using (ICloudStateTransaction transaction = await factory!.Store!.BeginTransactionAsync())
        {
            Assert.Empty(await transaction.Checkpoints.ListAsync(RemoteMetadataIntent.Prefix));
        }

        CloudRemoteChange valid = new("metadata", CloudRemoteChangeKind.MetadataUpdate, target.RemoteId, "r2",
            CloudItemKind.Directory, relative, staleIdExists ? target.ItemId : null, previousRemoteRevision: "r1", metadata: metadata);
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single((await fixture.System.ApplyRemoteChangesAsync(
            new("metadata-valid-id", Array.Empty<byte>(), [valid], new byte[] { 2 }))).Entries).Status);
        CloudItemSnapshot applied = await item.InspectAsync();
        Assert.Equal(target.ItemId, applied.ItemId);
        Assert.Equal("r2", applied.RemoteRevision);
        Assert.Equal(remoteLastWrite, Directory.GetLastWriteTimeUtc(item.FullPath));
        await using ICloudStateTransaction state = await factory!.Store!.BeginTransactionAsync();
        RemoteMetadataIntent receipt = RemoteMetadataIntent.Decode(Assert.Single(await state.Checkpoints.ListAsync(RemoteMetadataIntent.Prefix)));
        Assert.Equal(target.ItemId, receipt.ItemId);
        Assert.Equal(original.LocalBinding, receipt.Binding);
    }

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task ProtectedMetadataWriteRechecksCoordinationAfterPreparation(bool moveDirectory, bool expireAfterPreparation)
    {
        MetadataFaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path, "preparation-paused-after"));
        const string originalPath = "Documents";
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-expiry-during-upsert");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder(originalPath, identity).WithInSyncState(true).Build(),
        ]);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        CloudRemoteApplyOptions coordinated = new()
        {
            AttributeEchoPolicy = CloudRemoteAttributeEchoPolicy.CallerCoordinated,
            EchoSuppressionLifetime = expireAfterPreparation ? TimeSpan.FromSeconds(3) : TimeSpan.FromMinutes(1),
        };
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single((await fixture.System.ApplyRemoteChangesAsync(
            MetadataBoundaryBatch(identity, originalPath, CloudItemKind.Directory), coordinated, deadline.Token)).Entries).Status);
        long appliedAt = Stopwatch.GetTimestamp();

        CloudDirectory item = fixture.System.GetDirectory(originalPath);
        if (moveDirectory)
        {
            CloudLocalFileBinding? binding = (await item.InspectAsync(deadline.Token)).LocalBinding;
            await item.MoveToAsync(fixture.System.Root, "Moved", cancellationToken: deadline.Token);
            item = fixture.System.GetDirectory("Moved");
            // The provider has accepted the rename. Windows can clear InSync during
            // the move, which is a separate reason to reject a protected update.
            CloudItemSnapshot moved = (await item.SetInSyncAsync(true, cancellationToken: deadline.Token)).Snapshot;
            Assert.NotNull(binding);
            Assert.Equal(binding, moved.LocalBinding);
            Assert.Equal(identity.ItemId, moved.ItemId);
            Assert.Equal("remote-r1", moved.RemoteRevision);
            Assert.Equal(CloudSynchronizationState.InSync, moved.SynchronizationState);
        }
        CloudRemoteChange write = new("metadata", expireAfterPreparation ? CloudRemoteChangeKind.DirectoryUpsert : CloudRemoteChangeKind.MetadataUpdate,
            identity.RemoteId, "remote-r2", CloudItemKind.Directory, item.RelativePath, identity.ItemId, previousRemoteRevision: "remote-r1",
            metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().WithAttributes(expireAfterPreparation
                ? FileAttributes.Directory : FileAttributes.Directory | FileAttributes.Hidden).Build());
        factory!.Armed = true;
        Task<CloudRemoteApplyResult> apply = fixture.System.ApplyRemoteChangesAsync(
            new("metadata-coordination-recheck", Array.Empty<byte>(), [write], new byte[] { 2 }),
            expireAfterPreparation ? new() { SuppressLocalEcho = false } : coordinated, deadline.Token).AsTask();
        try
        {
            Task preparationPaused = factory.OutcomePaused.Task;
            if (await Task.WhenAny(preparationPaused, apply).WaitAsync(deadline.Token) == apply)
            {
                CloudRemoteApplyEntryResult early = Assert.Single((await apply).Entries);
                Assert.Fail($"The write finished before preparation was paused: {early.Status}, {early.Conflict?.Reason}, {early.Error}");
            }
            if (expireAfterPreparation)
            {
                while (coordinated.EchoSuppressionLifetime - Stopwatch.GetElapsedTime(appliedAt) is { Ticks: > 0 } remaining)
                { await Task.Delay(remaining + TimeSpan.FromMilliseconds(1), deadline.Token); }
                File.SetAttributes(item.FullPath, File.GetAttributes(item.FullPath) | FileAttributes.Hidden);
            }
            await using ICloudStateTransaction state = await factory.Store!.BeginTransactionAsync(deadline.Token);
            Assert.Empty(await state.Operations.ListAsync(10, cancellationToken: deadline.Token));
        }
        finally { factory.ResumeOutcome.TrySetResult(); }
        CloudRemoteApplyEntryResult outcome = Assert.Single((await apply.WaitAsync(deadline.Token)).Entries);
        if (!expireAfterPreparation)
        {
            Assert.Equal(CloudRemoteApplyEntryStatus.Applied, outcome.Status);
            Assert.True(File.GetAttributes(item.FullPath).HasFlag(FileAttributes.Hidden));
            Assert.Equal("remote-r2", (await item.InspectAsync(deadline.Token)).RemoteRevision);
            CloudRemoteChange next = new("metadata", CloudRemoteChangeKind.MetadataUpdate, identity.RemoteId,
                "remote-r3", CloudItemKind.Directory, item.RelativePath, identity.ItemId, previousRemoteRevision: "remote-r2",
                metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().WithAttributes(FileAttributes.Directory).Build());
            Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single((await fixture.System.ApplyRemoteChangesAsync(
                new("metadata-after-move", Array.Empty<byte>(), [next], new byte[] { 3 }), coordinated, deadline.Token)).Entries).Status);
            Assert.False(File.GetAttributes(item.FullPath).HasFlag(FileAttributes.Hidden));
            return;
        }
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, outcome.Status);
        Assert.Equal(CloudRemoteConflictReason.Metadata, outcome.Conflict!.Reason);
        Assert.True(File.GetAttributes(item.FullPath).HasFlag(FileAttributes.Hidden));
        Assert.Equal("remote-r1", (await item.InspectAsync(deadline.Token)).RemoteRevision);

        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed();
        await feed.StartAsync(deadline.Token);
        Assert.True((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        await feed.AcknowledgeFullRescanAsync(deadline.Token);
        CloudRemoteApplyResult reconciled = await fixture.System.ApplyRemoteChangesAsync(
            new("metadata-after-expiry-reconciliation", Array.Empty<byte>(), [write], new byte[] { 3 }),
            cancellationToken: deadline.Token);
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single(reconciled.Entries).Status);
    }

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(CloudRemoteAttributeEchoPolicy.RequireReconciliation)]
    [InlineData(CloudRemoteAttributeEchoPolicy.CallerCoordinated)]
    public async Task AmbiguousMetadataCommitProtectsAnUnobservedNativeAttributeEdit(CloudRemoteAttributeEchoPolicy policy)
    {
        MetadataFaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path, "outcome-after"));
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-ambiguous-upsert");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder("Documents", identity).WithInSyncState(true).Build(),
        ]);
        CloudDirectory item = fixture.System.GetDirectory("Documents");
        CloudRemoteChange change = new("metadata", CloudRemoteChangeKind.MetadataUpdate, identity.RemoteId,
            "remote-r1", CloudItemKind.Directory, item.RelativePath, identity.ItemId,
            metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().Build());
        CloudRemoteChangeBatch batch = new("metadata-fault", Array.Empty<byte>(), [change], new byte[] { 1 });
        CloudRemoteApplyOptions options = new() { AttributeEchoPolicy = policy };
        factory!.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.System.ApplyRemoteChangesAsync(batch, options).AsTask());
        Assert.Equal("remote-r1", (await item.InspectAsync()).RemoteRevision);

        // Leave the feed unopened, so the native Hidden write cannot have a journal row yet.
        File.SetAttributes(item.FullPath, File.GetAttributes(item.FullPath) | FileAttributes.Hidden);
        await using (ICloudStateTransaction state = await factory.Store!.BeginTransactionAsync())
        { Assert.Empty(await state.Operations.ListAsync(10)); }
        Assert.Equal(CloudRemoteApplyEntryStatus.AlreadyApplied,
            Assert.Single((await fixture.System.ApplyRemoteChangesAsync(batch, options)).Entries).Status);
        CloudRemoteChange update = new("upsert", CloudRemoteChangeKind.DirectoryUpsert,
            identity.RemoteId, "remote-r2", CloudItemKind.Directory, item.RelativePath, identity.ItemId,
            previousRemoteRevision: "remote-r1", metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder()
                .WithAttributes(FileAttributes.Directory).Build());
        CloudRemoteApplyEntryResult upsert = Assert.Single((await fixture.System.ApplyRemoteChangesAsync(
            new("metadata-upsert", Array.Empty<byte>(), [update], new byte[] { 2 }))).Entries);
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, upsert.Status);
        Assert.Equal(CloudRemoteConflictReason.Metadata, upsert.Conflict!.Reason);
        Assert.True(File.GetAttributes(item.FullPath).HasFlag(FileAttributes.Hidden));
        Assert.Equal("remote-r1", (await item.InspectAsync()).RemoteRevision);

        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed();
        await feed.StartAsync();
        Assert.True((await feed.BeginScanAsync()).RequiresFullRescan);
    }

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SegmentedPassthroughConfirmsOnlyAcknowledgedMetadataOutcomes(bool firstCommitThrows)
    {
        MetadataFaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path, "first-outcome-after"));
        CloudPlaceholderIdentity first = new(Guid.NewGuid(), "metadata-partial-first");
        CloudPlaceholderIdentity second = new(Guid.NewGuid(), "metadata-partial-second");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder("First", first).WithInSyncState(true).Build(),
            CloudDirectoryPlaceholderSpec.CreateBuilder("Second", second).WithInSyncState(true).Build(),
        ]);
        CloudPlaceholderMetadata metadata = CloudPlaceholderMetadata.CreateDirectoryBuilder().Build();
        CloudRemoteChangeBatch batch = new("metadata-fault", Array.Empty<byte>(), [
            new("first", CloudRemoteChangeKind.MetadataUpdate, first.RemoteId, "remote-r1", CloudItemKind.Directory,
                "First", first.ItemId, metadata: metadata, cursorAfter: new byte[] { 1 }),
            new("second", CloudRemoteChangeKind.MetadataUpdate, second.RemoteId, "remote-r2", CloudItemKind.Directory,
                "Second", second.ItemId, metadata: metadata, cursorAfter: new byte[] { 2 }),
        ], new byte[] { 2 });
        CloudRemoteApplyOptions options = new() { SuppressLocalEcho = false, MaximumEntries = 1 };
        factory!.Armed = firstCommitThrows;
        if (firstCommitThrows)
        { await Assert.ThrowsAsync<IOException>(() => fixture.System.ApplyRemoteChangesAsync(batch, options).AsTask()); }
        else
        {
            CloudRemoteApplyResult partial = await fixture.System.ApplyRemoteChangesAsync(batch, options);
            Assert.Equal(CloudRemoteBatchStatus.Applying, partial.Status);
            Assert.Equal(new byte[] { 1 }, partial.SafeCursor.ToArray());
            Assert.Equal(CloudRemoteApplyEntryStatus.Applied, partial.Entries[0].Status);
        }

        await fixture.RestartAsync();
        CloudRemoteApplyResult completed = await fixture.System.ApplyRemoteChangesAsync(batch, options);
        Assert.Equal(CloudRemoteBatchStatus.Applied, completed.Status);
        Assert.Equal(new byte[] { 2 }, completed.SafeCursor.ToArray());
        Assert.Equal(new[] { CloudRemoteApplyEntryStatus.AlreadyApplied, CloudRemoteApplyEntryStatus.Applied },
            completed.Entries.Select(entry => entry.Status));
        Assert.Equal("remote-r1", (await fixture.System.GetDirectory("First").InspectAsync()).RemoteRevision);
        Assert.Equal("remote-r2", (await fixture.System.GetDirectory("Second").InspectAsync()).RemoteRevision);
        await using (ICloudStateTransaction state = await factory.Store!.BeginTransactionAsync())
        {
            RemoteMetadataIntent[] intents = (await state.Checkpoints.ListAsync(RemoteMetadataIntent.Prefix))
                .Select(RemoteMetadataIntent.Decode).ToArray();
            Assert.Equal(firstCommitThrows, Assert.Single(intents, intent => intent.ItemId == first.ItemId).HasUnconfirmedOutcome);
            Assert.False(Assert.Single(intents, intent => intent.ItemId == second.ItemId).HasUnconfirmedOutcome);
        }

        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed();
        await feed.StartAsync();
        Assert.Equal(firstCommitThrows, (await feed.BeginScanAsync()).RequiresFullRescan);
    }

    [Trait("Category", "AccessJournal")]
    [Fact]
    public async Task NativeWriteDuringRemoteMetadataOutcomeRemainsAnOriginalAfterReconciliation()
    {
        MetadataFaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path, "outcome-paused"));
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-concurrent-directory");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder("Documents", identity).WithInSyncState(true).Build(),
        ]);
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed();
        await feed.StartAsync();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        factory!.Armed = true;
        CloudRemoteChange change = new("metadata", CloudRemoteChangeKind.MetadataUpdate, identity.RemoteId,
            "remote-r1", CloudItemKind.Directory, "Documents", identity.ItemId,
            metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().Build());
        Task<CloudRemoteApplyResult> apply = fixture.System.ApplyRemoteChangesAsync(
            new("metadata-fault", Array.Empty<byte>(), [change], new byte[] { 1 }), cancellationToken: deadline.Token).AsTask();
        string path = Path.Combine(fixture.Root, "Documents");
        try
        {
            await factory.OutcomePaused.Task.WaitAsync(deadline.Token);
            Assert.False(apply.IsCompleted);
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        }
        finally { factory.ResumeOutcome.TrySetResult(); }
        Assert.Equal(CloudRemoteBatchStatus.Applied, (await apply).Status);
        CloudOperationJournalEntry[] originals;
        while (true)
        {
            await using (ICloudStateTransaction state = await factory.Store!.BeginTransactionAsync(deadline.Token))
            { originals = (await state.Operations.ListAsync(100, cancellationToken: deadline.Token)).ToArray(); }
            if (originals.Length >= 3) { break; }
            await Task.Delay(20, deadline.Token);
        }
        await feed.DisposeAsync();
        await fixture.RestartAsync();
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed();
        await reopened.StartAsync(deadline.Token);
        Assert.True((await reopened.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        await reopened.AcknowledgeFullRescanAsync(deadline.Token);
        CloudLocalChangePage page = await reopened.ReadPageAsync(await reopened.BeginScanAsync(deadline.Token), 0, 100, deadline.Token);
        Assert.Equal(originals.Select(row => (row.OperationId, row.Sequence)), page.Changes.Select(row => (row.OperationId, row.Sequence)));
        Assert.All(page.Changes, observation => Assert.Null(observation.ItemId));
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        await reopened.AcknowledgeAsync(originals.Select(row => row.OperationId), deadline.Token);
    }

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public async Task MetadataOutcomeRollbackRecoversOnlyAnOriginalProjection(bool requirePreviousRevision, bool legacyPreparation, bool replaceObject)
    {
        MetadataFaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path, "outcome-before"));
        const string relative = "Documents";
        string path = Path.Combine(fixture.Root, relative);
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "original-metadata-projection", "remote-r0");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder(relative, identity).WithInSyncState(true).Build(),
        ]);
        CloudRemoteChange change = new("metadata", CloudRemoteChangeKind.MetadataUpdate, identity.RemoteId, "remote-r1",
            CloudItemKind.Directory, relative, identity.ItemId, previousRemoteRevision: requirePreviousRevision ? "remote-r0" : null,
            metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().WithAttributes(FileAttributes.Directory | FileAttributes.Hidden).Build());
        byte[] initialCursor = [7];
        byte[] finalCursor = [8];
        CloudRemoteChangeBatch batch = new("metadata-fault", initialCursor, [change], finalCursor);
        factory!.Armed = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.System.ApplyRemoteChangesAsync(batch).AsTask());
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));

        string projectionName = RemoteMetadataProjection.Name(batch, 0);
        DateTimeOffset projectedAt;
        await using (ICloudStateTransaction transaction = await factory.Store!.BeginTransactionAsync())
        {
            CloudItemState projected = (await transaction.Items.GetByItemIdAsync(identity.ItemId))!;
            Assert.Equal("remote-r1", projected.RemoteRevision);
            projectedAt = projected.UpdatedAt;
            RemoteMetadataProjection projection = RemoteMetadataProjection.Decode((await transaction.Checkpoints.GetAsync(projectionName))!);
            Assert.Equal(projectedAt.UtcTicks, projection.ProjectedAtTicks);
            CloudRemoteBatchState unfinished = (await transaction.RemoteBatches.GetAsync(batch.BatchId))!;
            Assert.Equal(0, unfinished.AppliedEntryCount);
            Assert.Equal(initialCursor, unfinished.Cursor.ToArray());
            if (legacyPreparation)
            {
                // An older store retains only the attribution receipt, without a batch link.
                await transaction.Checkpoints.RemoveAsync(projectionName);
                await transaction.CommitAsync();
            }
        }

        string originalPath = path;
        if (replaceObject)
        {
            originalPath = Path.Combine(fixture.Root, "Original");
            Directory.Move(path, originalPath);
            Directory.CreateDirectory(path);
        }
        await fixture.RestartAsync();
        CloudRemoteApplyResult replay = await fixture.System.ApplyRemoteChangesAsync(batch);
        bool unresolved = legacyPreparation || replaceObject;
        Assert.Equal(unresolved, replay.RequiresRetry);
        Assert.Equal(unresolved ? CloudRemoteApplyEntryStatus.Failed : CloudRemoteApplyEntryStatus.Applied,
            Assert.Single(replay.Entries).Status);
        Assert.Equal(unresolved ? initialCursor : finalCursor, replay.SafeCursor.ToArray());
        Assert.Empty(replay.ConflictIds);
        Assert.True(File.GetAttributes(originalPath).HasFlag(FileAttributes.Hidden));

        await using (ICloudStateTransaction transaction = await factory.Store!.BeginTransactionAsync())
        {
            Assert.Equal(projectedAt, (await transaction.Items.GetByItemIdAsync(identity.ItemId))!.UpdatedAt);
            Assert.Empty(await transaction.Conflicts.ListAsync());
            Assert.Equal(replaceObject, await transaction.Checkpoints.GetAsync(projectionName) is not null);
        }
        if (!unresolved)
        {
            Assert.Equal(CloudRemoteApplyEntryStatus.AlreadyApplied,
                Assert.Single((await fixture.System.ApplyRemoteChangesAsync(batch)).Entries).Status);
        }
    }

    [Trait("Category", "AccessJournal")]
    [Fact]
    public async Task MetadataCancellationBeforePreparationDoesNotWaitForCleanup()
    {
        MetadataFaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path, "metadata-cancel-preparation"));
        const string relative = "Documents";
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "cancel-metadata-before-preparation");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder(relative, identity).WithInSyncState(true).Build(),
        ]);
        CloudRemoteChangeBatch batch = MetadataBoundaryBatch(identity, relative, CloudItemKind.Directory);
        using CancellationTokenSource cancellation = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        factory!.Armed = true;
        Task<CloudRemoteApplyResult> apply = fixture.System.ApplyRemoteChangesAsync(batch, cancellationToken: cancellation.Token).AsTask();
        try
        {
            await factory.OutcomePaused.Task.WaitAsync(deadline.Token);
            factory.PauseNextTransaction();
            await cancellation.CancelAsync();
            Task first = await Task.WhenAny(apply, factory.TransactionPaused.Task).WaitAsync(deadline.Token);
            Assert.Same(apply, first);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
        }
        finally
        {
            factory.ResumeOutcome.TrySetResult();
            factory.ResumeTransaction.TrySetResult();
        }

        await using ICloudStateTransaction transaction = await factory.Store!.BeginTransactionAsync(deadline.Token);
        Assert.Empty(await transaction.Checkpoints.ListAsync(RemoteMetadataProjection.Prefix, deadline.Token));
        Assert.Equal(0, (await transaction.RemoteBatches.GetAsync(batch.BatchId, deadline.Token))!.AppliedEntryCount);
    }

    [Trait("Category", "AccessJournal")]
    [Fact]
    public async Task MetadataPreparationFailureCanRetryAfterReconciliation()
    {
        MetadataFaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path, "preparation-after"));
        const string relative = "Documents";
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "unattempted-metadata-projection", "remote-r0");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder(relative, identity).WithInSyncState(true).Build(),
        ]);
        CloudRemoteChange change = new("metadata", CloudRemoteChangeKind.MetadataUpdate, identity.RemoteId, "remote-r1",
            CloudItemKind.Directory, relative, identity.ItemId, previousRemoteRevision: "remote-r0",
            metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().WithAttributes(FileAttributes.Directory | FileAttributes.Hidden).Build());
        CloudRemoteChangeBatch batch = new("metadata-fault", new byte[] { 7 }, [change], new byte[] { 8 });
        factory!.Armed = true;
        CloudRemoteApplyResult failed = await fixture.System.ApplyRemoteChangesAsync(batch);
        Assert.IsType<IOException>(Assert.Single(failed.Entries).Error);
        Assert.True(failed.RequiresRetry);
        Assert.Equal(new byte[] { 7 }, failed.SafeCursor.ToArray());
        Assert.False(File.GetAttributes(Path.Combine(fixture.Root, relative)).HasFlag(FileAttributes.Hidden));

        await fixture.RestartAsync();
        CloudRemoteApplyResult fenced = await fixture.System.ApplyRemoteChangesAsync(batch);
        Assert.True(fenced.RequiresRetry);
        Assert.Empty(fenced.ConflictIds);
        Assert.Equal(new byte[] { 7 }, fenced.SafeCursor.ToArray());
        ControlledMetadataSource source = new();
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed(options: null, source);
        await feed.StartAsync();
        Assert.True((await feed.BeginScanAsync()).RequiresFullRescan);
        await feed.AcknowledgeFullRescanAsync(feed.BeginFullRescan(), CancellationToken.None);

        CloudRemoteApplyResult replay = await fixture.System.ApplyRemoteChangesAsync(batch);
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single(replay.Entries).Status);
        Assert.False(replay.RequiresRetry);
        Assert.Equal(new byte[] { 8 }, replay.SafeCursor.ToArray());
        Assert.True(File.GetAttributes(Path.Combine(fixture.Root, relative)).HasFlag(FileAttributes.Hidden));
    }

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData("preparation-after")]
    [InlineData("outcome-before")]
    [InlineData("outcome-after")]
    [InlineData("staging-after")]
    public async Task FailedMetadataAttributionRetainsRealWritesAndOriginalIds(string boundary)
    {
        MetadataFaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path, boundary));
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-fault-directory");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder("Documents", identity).WithInSyncState(true).Build(),
        ]);
        string path = Path.Combine(fixture.Root, "Documents");
        CloudRemoteChange change = new("metadata", CloudRemoteChangeKind.MetadataUpdate, identity.RemoteId,
            "remote-r1", CloudItemKind.Directory, "Documents", identity.ItemId,
            metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().Build());
        CloudRemoteChangeBatch batch = new("metadata-fault", Array.Empty<byte>(), [change], new byte[] { 1 });
        CloudRemoteApplyOptions options = new() { AttributeEchoPolicy = CloudRemoteAttributeEchoPolicy.CallerCoordinated };
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed();
        await feed.StartAsync();
        factory!.Armed = true;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        if (boundary == "preparation-after")
        {
            CloudLocalChangeScan before = await feed.BeginScanAsync(deadline.Token);
            Assert.False(before.RequiresFullRescan);
            Task<CloudLocalChangeBatch> waiting = feed.ReadBatchAsync(deadline.Token).AsTask();
            Assert.False(waiting.IsCompleted);
            CloudRemoteApplyResult failed = await fixture.System.ApplyRemoteChangesAsync(batch, options, deadline.Token);
            Assert.True(failed.RequiresRetry);
            Assert.IsType<IOException>(Assert.Single(failed.Entries).Error);
            // The native update never ran, so no watcher event can unblock the live readers.
            Assert.True((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
            Assert.True((await feed.ReadPageAsync(before, 0, 10, deadline.Token)).RequiresFullRescan);
            Assert.True((await waiting).RequiresFullRescan);
            await using ICloudStateTransaction state = await factory.Store!.BeginTransactionAsync(deadline.Token);
            Assert.Empty(await state.Operations.ListAsync(10, cancellationToken: deadline.Token));
        }
        else if (boundary == "staging-after")
        {
            Assert.Equal(CloudRemoteBatchStatus.Applied, (await fixture.System.ApplyRemoteChangesAsync(batch, options)).Status);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => fixture.System.ApplyRemoteChangesAsync(batch, options).AsTask());
        }

        if (boundary == "staging-after")
        {
            Guid interrupted = await factory.Staged.Task.WaitAsync(deadline.Token);
            await feed.DisposeAsync();
            await using ICloudStateTransaction state = await factory.Store!.BeginTransactionAsync(deadline.Token);
            Assert.NotNull(await state.Checkpoints.GetAsync(CloudLocalChangeFeed.SecurityValidationPrefix + interrupted.ToString("N"), deadline.Token));
            Assert.Null(await state.Operations.GetAsync(interrupted, deadline.Token));
        }
        else
        {
            // A failed public call has no live coordination authority. The following native
            // write is permitted and must not redeem its completed or prepared echo receipt.
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            while (true)
            {
                int count;
                await using (ICloudStateTransaction state = await factory.Store!.BeginTransactionAsync(deadline.Token))
                { count = (await state.Operations.ListAsync(100, cancellationToken: deadline.Token)).Count; }
                if (count >= (boundary == "preparation-after" ? 1 : 3)) { break; }
                await Task.Delay(20, deadline.Token);
            }
            await feed.DisposeAsync();
        }

        IReadOnlyList<CloudOperationJournalEntry> originals;
        await using (ICloudStateTransaction state = await factory.Store!.BeginTransactionAsync(deadline.Token))
        {
            originals = await state.Operations.ListAsync(100, cancellationToken: deadline.Token);
            Assert.All(originals, row => Assert.Null(row.ItemId));
        }
        await fixture.RestartAsync();
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed();
        await reopened.StartAsync(deadline.Token);
        Assert.True((await reopened.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        IReadOnlyList<CloudOperationJournalEntry> recovered;
        await using (ICloudStateTransaction state = await factory.Store!.BeginTransactionAsync(deadline.Token))
        {
            recovered = await state.Operations.ListAsync(100, cancellationToken: deadline.Token);
            AssertOriginalAccessRows(originals, recovered);
            if (boundary == "staging-after")
            {
                Guid interrupted = await factory.Staged.Task;
                Assert.Contains(recovered, row => row.OperationId == interrupted && row.ItemId is null);
            }
        }
        CloudRemoteApplyResult replay = await fixture.System.ApplyRemoteChangesAsync(batch, options, deadline.Token);
        Assert.Equal(boundary switch
        {
            "outcome-after" or "staging-after" => CloudRemoteApplyEntryStatus.AlreadyApplied,
            "outcome-before" => CloudRemoteApplyEntryStatus.Applied,
            _ => CloudRemoteApplyEntryStatus.Failed,
        }, Assert.Single(replay.Entries).Status);
        Assert.Equal(boundary == "preparation-after", replay.RequiresRetry);
        await reopened.AcknowledgeFullRescanAsync(reopened.BeginFullRescan(), deadline.Token);
        CloudLocalChangeScan scan = await reopened.BeginScanAsync(deadline.Token);
        CloudLocalChangePage page = await reopened.ReadPageAsync(scan, 0, 100, deadline.Token);
        Assert.False(page.RequiresFullRescan);
        Assert.Equal(recovered.Select(row => (row.OperationId, row.Sequence)), page.Changes.Select(row => (row.OperationId, row.Sequence)));
        Assert.All(page.Changes, row => Assert.Null(row.ItemId));
        await reopened.AcknowledgeAsync(recovered.Select(row => row.OperationId), deadline.Token);
        if (boundary != "staging-after") { Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden)); }
        Assert.Equal(0, fixture.Provider.Fetches);
    }

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(null)]
    [InlineData("outcome-after")]
    [InlineData("passthrough-confirmation-before")]
    public async Task PassthroughMetadataReopenDistinguishesConfirmedAndAmbiguousOutcomes(string? boundary)
    {
        MetadataFaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path, boundary ?? "outcome-after"));
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-passthrough-outcome");
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder("Documents", identity).WithInSyncState(true).Build(),
        ]);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        CloudRemoteChange change = new("metadata", CloudRemoteChangeKind.MetadataUpdate, identity.RemoteId,
            "remote-r1", CloudItemKind.Directory, "Documents", identity.ItemId,
            metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().Build());
        CloudRemoteChangeBatch batch = new("metadata-fault", Array.Empty<byte>(), [change], new byte[] { 1 });
        CloudRemoteApplyOptions options = new() { SuppressLocalEcho = false };
        factory!.Armed = boundary is not null;
        if (boundary == "outcome-after")
        { await Assert.ThrowsAsync<IOException>(() => fixture.System.ApplyRemoteChangesAsync(batch, options, deadline.Token).AsTask()); }
        else
        { Assert.Equal(CloudRemoteBatchStatus.Applied, (await fixture.System.ApplyRemoteChangesAsync(batch, options, deadline.Token)).Status); }

        // A fresh feed has no captured watcher events and must read the outcome evidence itself.
        await fixture.RestartAsync();
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed();
        await reopened.StartAsync(deadline.Token);
        bool requiresReconciliation = boundary is not null;
        Assert.Equal(requiresReconciliation, (await reopened.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        CloudRemoteApplyResult replay = await fixture.System.ApplyRemoteChangesAsync(batch, options, deadline.Token);
        Assert.Equal(CloudRemoteApplyEntryStatus.AlreadyApplied, Assert.Single(replay.Entries).Status);
        Assert.Equal(requiresReconciliation, (await reopened.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        Assert.Equal("remote-r1", (await fixture.System.GetDirectory("Documents").InspectAsync(deadline.Token)).RemoteRevision);
        if (requiresReconciliation)
        {
            await reopened.AcknowledgeFullRescanAsync(deadline.Token);
            Assert.False((await reopened.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        }
    }

    private static async Task WaitForMetadataRescanMarkerAsync(ICloudStateStore store, CancellationToken cancellationToken)
    {
        while (true)
        {
            await using (ICloudStateTransaction state = await store.BeginTransactionAsync(cancellationToken))
            {
                CloudStateCheckpoint? checkpoint = await state.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName, cancellationToken);
                if (checkpoint is not null && LocalChangeCheckpoint.Decode(checkpoint.Value).RequiresFullRescan) { return; }
            }
            await Task.Delay(20, cancellationToken);
        }
    }

    private sealed class ControlledMetadataSource : ILocalChangeSource
    {
        private Func<LocalChangeSourceEvent, ValueTask>? _handler;

        public Task StartAsync(Func<LocalChangeSourceEvent, ValueTask> eventHandler, CancellationToken cancellationToken)
        {
            _handler = eventHandler;
            return Task.CompletedTask;
        }

        internal ValueTask EmitAsync(LocalChangeSourceEvent change) => _handler!(change);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class MetadataFaultFactory(string path, string boundary) : ICloudStateStoreFactory
    {
        private readonly string _boundary = boundary;
        private int _failed;
        private int _itemMetadataReads;
        private int _pauseNextTransaction;
        internal bool Armed { get; set; }
        internal ICloudStateStore? Store { get; private set; }
        internal TaskCompletionSource<Guid> Staged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource OutcomePaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ResumeOutcome { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource TransactionPaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ResumeTransaction { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ObservationCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool OverlappingWrites => _boundary is "overlapping-outcome" or "overlapping-confirmation";
        internal void PauseNextTransaction() => Volatile.Write(ref _pauseNextTransaction, 1);
        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context, CancellationToken cancellationToken = default)
        {
            Store = await new SqliteCloudStateStoreFactory(path).OpenAsync(context, cancellationToken);
            return new FaultStore(Store, this);
        }
        private sealed class FaultStore(ICloudStateStore inner, MetadataFaultFactory owner) : ICloudStateStore
        {
            public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
            {
                if (Interlocked.Exchange(ref owner._pauseNextTransaction, 0) != 0)
                {
                    owner.TransactionPaused.TrySetResult();
                    await owner.ResumeTransaction.Task.WaitAsync(cancellationToken);
                }
                return new FaultTransaction(await inner.BeginTransactionAsync(cancellationToken), owner);
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
        private sealed class FaultTransaction(ICloudStateTransaction inner, MetadataFaultFactory owner) :
            ICloudStateTransaction, ICloudCheckpointRepository, ICloudRemoteBatchRepository
        {
            private readonly List<Func<ICloudStateTransaction, CancellationToken, ValueTask>> _writes = [];
            private bool _writesReconciliation;
            public ICloudItemStateRepository Items => inner.Items;
            public ICloudCheckpointRepository Checkpoints => owner.OverlappingWrites ||
                owner._boundary is "rescan-before-preparation" or "metadata-cancel-preparation" ? this : inner.Checkpoints;
            public ICloudOperationJournal Operations => inner.Operations;
            public ICloudConflictRepository Conflicts => inner.Conflicts;
            public ICloudRemoteBatchRepository RemoteBatches => owner.OverlappingWrites ? this : inner.RemoteBatches;
            public ICloudEchoSuppressionRepository EchoSuppressions => inner.EchoSuppressions;

            ValueTask<CloudStateCheckpoint?> ICloudCheckpointRepository.GetAsync(string name, CancellationToken cancellationToken) =>
                inner.Checkpoints.GetAsync(name, cancellationToken);
            async ValueTask<IReadOnlyList<CloudStateCheckpoint>> ICloudCheckpointRepository.ListAsync(string namePrefix, CancellationToken cancellationToken)
            {
                // For a protected write, the first per-item lookup is preflight. The
                // second is inside preparation after it acquired the publication lease.
                if (owner.Armed && owner._boundary == "rescan-before-preparation" && namePrefix != RemoteMetadataIntent.Prefix &&
                    namePrefix.StartsWith(RemoteMetadataIntent.Prefix, StringComparison.Ordinal) && Interlocked.Increment(ref owner._itemMetadataReads) == 2)
                {
                    owner.OutcomePaused.TrySetResult();
                    await owner.ResumeOutcome.Task.WaitAsync(cancellationToken);
                }
                return await inner.Checkpoints.ListAsync(namePrefix, cancellationToken);
            }
            async ValueTask ICloudCheckpointRepository.UpsertAsync(CloudStateCheckpoint checkpoint, CancellationToken cancellationToken)
            {
                if (owner.Armed && owner._boundary == "metadata-cancel-preparation" &&
                    checkpoint.Name.StartsWith(RemoteMetadataProjection.Prefix, StringComparison.Ordinal))
                {
                    owner.OutcomePaused.TrySetResult();
                    await owner.ResumeOutcome.Task.WaitAsync(cancellationToken);
                }
                _writes.Add((transaction, token) => transaction.Checkpoints.UpsertAsync(checkpoint, token));
                _writesReconciliation |= checkpoint.Name.StartsWith(RemoteMetadataIntent.Prefix, StringComparison.Ordinal) &&
                    RemoteMetadataIntent.Decode(checkpoint).RequiresReconciliation;
                await inner.Checkpoints.UpsertAsync(checkpoint, cancellationToken);
            }
            ValueTask ICloudCheckpointRepository.RemoveAsync(string name, CancellationToken cancellationToken)
            {
                _writes.Add((transaction, token) => transaction.Checkpoints.RemoveAsync(name, token));
                return inner.Checkpoints.RemoveAsync(name, cancellationToken);
            }
            ValueTask<CloudRemoteBatchState?> ICloudRemoteBatchRepository.GetAsync(string batchId, CancellationToken cancellationToken) =>
                inner.RemoteBatches.GetAsync(batchId, cancellationToken);
            ValueTask ICloudRemoteBatchRepository.UpsertAsync(CloudRemoteBatchState batch, CancellationToken cancellationToken)
            {
                _writes.Add((transaction, token) => transaction.RemoteBatches.UpsertAsync(batch, token));
                return inner.RemoteBatches.UpsertAsync(batch, cancellationToken);
            }
            ValueTask ICloudRemoteBatchRepository.RemoveAsync(string batchId, CancellationToken cancellationToken)
            {
                _writes.Add((transaction, token) => transaction.RemoteBatches.RemoveAsync(batchId, token));
                return inner.RemoteBatches.RemoveAsync(batchId, cancellationToken);
            }

            public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
            {
                CloudStateCheckpoint? staged = null;
                bool cut = false;
                if (owner.Armed && Volatile.Read(ref owner._failed) == 0)
                {
                    RemoteMetadataIntent[] intents = (await Checkpoints.ListAsync(RemoteMetadataIntent.Prefix, cancellationToken))
                        .Select(RemoteMetadataIntent.Decode).ToArray();
                    IReadOnlyList<CloudStateCheckpoint> receipts = await Checkpoints.ListAsync(CloudLocalChangeFeed.SecurityValidationPrefix, cancellationToken);
                    staged = receipts.Count == 0 ? null : receipts[0];
                    cut = owner._boundary switch
                    {
                        "preparation-after" or "preparation-paused-after" => intents.Any(intent => !intent.Completed),
                        "native-projection-paused-after" => intents.Any(intent => !intent.Completed) &&
                            (await Items.GetByRelativePathAsync("Documents", cancellationToken))?.RemoteRevision == "remote-r1",
                        "projection-before" => (await Checkpoints.ListAsync(RemoteMetadataProjection.Prefix, cancellationToken))
                            .Any(checkpoint => RemoteMetadataProjection.Decode(checkpoint).ProjectedAtTicks > 0),
                        "native-completion-before" => (await Checkpoints.ListAsync(RemoteMetadataProjection.Prefix, cancellationToken))
                            .Any(checkpoint => RemoteMetadataProjection.Decode(checkpoint).ProjectedAtTicks == RemoteMetadataProjection.NativeWriteCompleted),
                        "staging-after" => staged is not null,
                        "passthrough-confirmation-before" => intents.Any(intent => intent.OutcomeConfirmed),
                        "rescan-awaiting-authorization" => intents.Any(intent => intent.OutcomeConfirmed),
                        "overlapping-outcome" => intents.Any(intent => intent.Completed && !intent.OutcomeConfirmed),
                        "overlapping-confirmation" => intents.Any(intent => intent.OutcomeConfirmed),
                        "first-outcome-after" => intents.Any(intent => intent.Completed) &&
                            (await RemoteBatches.GetAsync("metadata-fault", cancellationToken))?.AppliedEntryCount == 1,
                        _ => intents.Any(intent => intent.Completed) &&
                            (await RemoteBatches.GetAsync("metadata-fault", cancellationToken))?.Status == CloudRemoteBatchStatus.Applied,
                    };
                }
                if (!cut || Interlocked.Exchange(ref owner._failed, 1) != 0)
                {
                    await inner.CommitAsync(cancellationToken);
                    if (_writesReconciliation) { owner.ObservationCommitted.TrySetResult(); }
                    return;
                }
                if (owner.OverlappingWrites)
                {
                    // Retain the values written from the old snapshot while allowing
                    // another transaction to commit. Replay only this writer's write set.
                    await inner.RollbackAsync(cancellationToken);
                    owner.OutcomePaused.TrySetResult();
                    await owner.ResumeOutcome.Task.WaitAsync(cancellationToken);
                    await using ICloudStateTransaction writer = await owner.Store!.BeginTransactionAsync(cancellationToken);
                    foreach (Func<ICloudStateTransaction, CancellationToken, ValueTask> write in _writes)
                    { await write(writer, cancellationToken); }
                    await writer.CommitAsync(cancellationToken);
                    return;
                }
                if (owner._boundary is "preparation-paused-after" or "native-projection-paused-after" or "rescan-awaiting-authorization")
                {
                    await inner.CommitAsync(cancellationToken);
                    owner.OutcomePaused.TrySetResult();
                    await owner.ResumeOutcome.Task.WaitAsync(cancellationToken);
                    return;
                }
                if (owner._boundary == "outcome-paused")
                {
                    owner.OutcomePaused.TrySetResult();
                    await owner.ResumeOutcome.Task.WaitAsync(cancellationToken);
                    await inner.CommitAsync(cancellationToken);
                    return;
                }
                if (owner._boundary is not ("outcome-before" or "passthrough-confirmation-before" or "projection-before" or "native-completion-before"))
                { await inner.CommitAsync(cancellationToken); }
                if (staged is not null)
                { owner.Staged.TrySetResult(Guid.ParseExact(staged.Name[CloudLocalChangeFeed.SecurityValidationPrefix.Length..], "N")); }
                throw new IOException("Injected metadata attribution boundary: " + owner._boundary);
            }
            public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
