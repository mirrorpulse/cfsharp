using CfSharp.Storage.Sqlite;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
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
        Assert.Equal(boundary is "outcome-after" or "staging-after" ? CloudRemoteApplyEntryStatus.AlreadyApplied :
            CloudRemoteApplyEntryStatus.Conflict, Assert.Single(replay.Entries).Status);
        Assert.False(replay.RequiresRetry);
        await reopened.AcknowledgeFullRescanAsync(deadline.Token);
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

    private sealed class MetadataFaultFactory(string path, string boundary) : ICloudStateStoreFactory
    {
        private readonly string _boundary = boundary;
        private int _failed;
        internal bool Armed { get; set; }
        internal ICloudStateStore? Store { get; private set; }
        internal TaskCompletionSource<Guid> Staged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource OutcomePaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ResumeOutcome { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context, CancellationToken cancellationToken = default)
        {
            Store = await new SqliteCloudStateStoreFactory(path).OpenAsync(context, cancellationToken);
            return new FaultStore(Store, this);
        }
        private sealed class FaultStore(ICloudStateStore inner, MetadataFaultFactory owner) : ICloudStateStore
        {
            public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
                new FaultTransaction(await inner.BeginTransactionAsync(cancellationToken), owner);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
        private sealed class FaultTransaction(ICloudStateTransaction inner, MetadataFaultFactory owner) : ICloudStateTransaction
        {
            public ICloudItemStateRepository Items => inner.Items;
            public ICloudCheckpointRepository Checkpoints => inner.Checkpoints;
            public ICloudOperationJournal Operations => inner.Operations;
            public ICloudConflictRepository Conflicts => inner.Conflicts;
            public ICloudRemoteBatchRepository RemoteBatches => inner.RemoteBatches;
            public ICloudEchoSuppressionRepository EchoSuppressions => inner.EchoSuppressions;
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
                        "preparation-after" => intents.Any(intent => !intent.Completed),
                        "staging-after" => staged is not null,
                        "passthrough-confirmation-before" => intents.Any(intent => intent.OutcomeConfirmed),
                        "first-outcome-after" => intents.Any(intent => intent.Completed) &&
                            (await RemoteBatches.GetAsync("metadata-fault", cancellationToken))?.AppliedEntryCount == 1,
                        _ => intents.Any(intent => intent.Completed) &&
                            (await RemoteBatches.GetAsync("metadata-fault", cancellationToken))?.Status == CloudRemoteBatchStatus.Applied,
                    };
                }
                if (!cut || Interlocked.Exchange(ref owner._failed, 1) != 0)
                { await inner.CommitAsync(cancellationToken); return; }
                if (owner._boundary == "outcome-paused")
                {
                    owner.OutcomePaused.TrySetResult();
                    await owner.ResumeOutcome.Task.WaitAsync(cancellationToken);
                    await inner.CommitAsync(cancellationToken);
                    return;
                }
                if (owner._boundary is not ("outcome-before" or "passthrough-confirmation-before"))
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
