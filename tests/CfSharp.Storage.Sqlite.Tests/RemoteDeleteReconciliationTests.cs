using System.Runtime.Versioning;

namespace CfSharp.Storage.Sqlite.Tests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed class RemoteDeleteReconciliationTests
{
    [Theory]
    [InlineData("retired", "retired", true)]
    [InlineData("retired", "RETIRED", false)]
    [InlineData("docs/retired", "DOCS\\RETIRED", false)]
    public async Task EquivalentWindowsPathsRetainOriginalRequestAndDurableSpellings(string durablePath, string requestedPath, bool uppercaseRoot)
    {
        await using Fixture fixture = await Fixture.OpenAsync(false, durablePath);
        CloudRemoteChange change = new("delete", CloudRemoteChangeKind.Delete, "remote", "v2", CloudItemKind.File,
            requestedPath, fixture.Item.ItemId, previousRemoteRevision: "v1");
        CloudRemoteChangeBatch batch = new("original", new byte[] { 1 }, [change], new byte[] { 9 });
        CloudRemoteDeleteReconciliationResult result = await fixture.System.ReconcileRemoteDeleteAsync(batch, 0);
        AssertRecorded(result, 1, new byte[] { 9 });
        Assert.Equal(change.RelativePath, result.Entry!.Conflict!.Change.RelativePath);
        Assert.Equal(durablePath, result.Entry.Conflict.LocalState!.RelativePath);
        await fixture.RestartAsync(uppercaseRoot ? fixture.System.SyncRootPath.ToUpperInvariant() : null);
        CloudRemoteDeleteReconciliationResult lookup = await fixture.System.GetRemoteDeleteReconciliationAsync(batch, 0);
        Assert.Equal(CloudRemoteDeleteReconciliationStatus.AlreadyRecorded, lookup.Status);
        Assert.Equal(result.ConflictId, lookup.ConflictId);
        Assert.Equal(durablePath, lookup.Entry!.Conflict!.LocalState!.RelativePath);
        Assert.Equal(change.RelativePath, lookup.Entry.Conflict.Change.RelativePath);
        Assert.Equal(result.ConflictId, (await fixture.System.ReconcileRemoteDeleteAsync(batch, 0)).ConflictId);
        await fixture.AssertOriginalsAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preview11PartialBatchRetainsItsOriginalConflictAcrossOwnersAndDismissal(bool directory)
    {
        await using Fixture fixture = await Fixture.OpenAsync(directory);
        CloudRemoteChangeBatch batch = fixture.Batch(mixed: true);
        CloudRemoteApplyResult baseline = await fixture.System.ApplyRemoteChangesAsync(batch);
        Assert.True(baseline.RequiresRetry);
        Assert.Equal(3, baseline.AppliedEntryCount);
        Assert.IsType<CloudRemoteDeleteRecoveryException>(baseline.Entries[3].Error);
        Assert.Equal(new byte[] { 4 }, baseline.SafeCursor.ToArray());
        CloudRemoteBatchState before = (await fixture.ReadProgressAsync(batch))!;
        await fixture.RestartAsync();

        CloudRemoteDeleteReconciliationResult result = await fixture.System.ReconcileRemoteDeleteAsync(batch, 3);
        AssertRecorded(result, 4, new byte[] { 5 });
        Assert.Equal(CloudRemoteBatchStatus.Applying, result.BatchStatus);
        CloudRemoteBatchState after = (await fixture.ReadProgressAsync(batch))!;
        Assert.Equal(before.Fingerprint.ToArray(), after.Fingerprint.ToArray());
        Assert.Equal(before.Payload.ToArray(), after.Payload.ToArray());
        Assert.Equal(batch.Changes[3].ChangeId, after.LastAppliedChangeId);
        await fixture.AssertOriginalsAsync();

        await fixture.RestartAsync();
        CloudRemoteDeleteReconciliationResult replay = await fixture.System.ReconcileRemoteDeleteAsync(batch, 3);
        Assert.Equal(CloudRemoteDeleteReconciliationStatus.AlreadyRecorded, replay.Status);
        Assert.Equal(result.ConflictId, replay.ConflictId);
        Assert.Equal(result.Entry!.Conflict!.DetectedAt, replay.Entry!.Conflict!.DetectedAt);
        CloudRemoteApplyResult suffix = await fixture.System.ApplyRemoteChangesAsync(batch);
        Assert.False(suffix.RequiresRetry);
        Assert.Equal(5, suffix.AppliedEntryCount);
        Assert.Equal(new byte[] { 9 }, suffix.SafeCursor.ToArray());
        Assert.Equal(CloudRemoteApplyEntryStatus.AlreadyApplied, suffix.Entries[3].Status);
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, suffix.Entries[4].Status);

        foreach (CloudRemoteConflictDecision decision in new[] { CloudRemoteConflictDecision.KeepLocal,
            CloudRemoteConflictDecision.KeepRemote, CloudRemoteConflictDecision.KeepBoth })
        {
            Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, (await fixture.System.ResolveRemoteConflictAsync(
                result.ConflictId!.Value, new(decision, decision == CloudRemoteConflictDecision.KeepBoth ? "copy" : null))).Status);
        }
        Assert.Equal(CloudRemoteConflictDismissalStatus.Dismissed,
            await fixture.System.DismissRemoteConflictAsync(result.ConflictId!.Value));
        await fixture.RestartAsync();
        CloudRemoteDeleteReconciliationResult history = await fixture.System.GetRemoteDeleteReconciliationAsync(batch, 3);
        Assert.Equal(result.ConflictId, history.ConflictId);
        Assert.Equal(4, history.AppliedEntryCount);
        Assert.Equal(new byte[] { 5 }, history.SafeCursor.ToArray());
        Assert.Null(await fixture.System.GetRemoteConflictAsync(result.ConflictId.Value));
        await fixture.AssertOriginalsAsync();
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    [InlineData("cancel-before")]
    [InlineData("cancel-after")]
    public async Task OutcomeAndExactlyOneOrdinalAreAtomicAcrossCommitAndCancellation(string boundary)
    {
        await using Fixture fixture = await Fixture.OpenAsync(false);
        CloudRemoteChangeBatch batch = fixture.Batch();
        using CancellationTokenSource cancellation = new();
        fixture.Factory.Boundary = boundary;
        fixture.Factory.Cancellation = cancellation;
        if (boundary == "cancel-after")
        {
            AssertRecorded(await fixture.System.ReconcileRemoteDeleteAsync(batch, 0, cancellationToken: cancellation.Token),
                1, new byte[] { 9 });
        }
        else if (boundary == "cancel-before")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.System.ReconcileRemoteDeleteAsync(
                batch, 0, cancellationToken: cancellation.Token).AsTask());
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => fixture.System.ReconcileRemoteDeleteAsync(batch, 0).AsTask());
        }
        bool committed = boundary.EndsWith("after", StringComparison.Ordinal);
        Assert.Equal(committed ? 1 : null, (await fixture.ReadProgressAsync(batch))?.AppliedEntryCount);
        await using (ICloudStateTransaction capture = await fixture.Factory.Store!.BeginTransactionAsync())
        {
            Assert.Equal(committed ? 1 : 0, (await capture.Conflicts.ListAsync()).Count);
            Assert.Equal(committed ? 1 : 0, (await capture.Checkpoints.ListAsync(RemoteDeleteReconciliationReceipt.Prefix)).Count);
        }
        await fixture.RestartAsync();
        CloudRemoteDeleteReconciliationResult lookup = await fixture.System.GetRemoteDeleteReconciliationAsync(batch, 0);
        Assert.Equal(committed ? CloudRemoteDeleteReconciliationStatus.AlreadyRecorded : CloudRemoteDeleteReconciliationStatus.NotFound,
            lookup.Status);
        CloudRemoteDeleteReconciliationResult retry = await fixture.System.ReconcileRemoteDeleteAsync(batch, 0);
        Assert.Equal(committed ? CloudRemoteDeleteReconciliationStatus.AlreadyRecorded : CloudRemoteDeleteReconciliationStatus.RecordedConflict,
            retry.Status);
        if (committed) { Assert.Equal(lookup.ConflictId, retry.ConflictId); }
        Assert.Equal(CloudRemoteApplyEntryStatus.AlreadyApplied,
            Assert.Single((await fixture.System.ApplyRemoteChangesAsync(batch)).Entries).Status);
        await fixture.AssertOriginalsAsync();
    }

    [Theory]
    [InlineData("item")]
    [InlineData("remote")]
    [InlineData("kind")]
    [InlineData("path")]
    [InlineData("revision")]
    [InlineData("live")]
    [InlineData("missing")]
    public async Task IncompatibleOfficialStateCannotBeAdopted(string mismatch)
    {
        await using Fixture fixture = await Fixture.OpenAsync(false);
        CloudRemoteChange change = new("delete", CloudRemoteChangeKind.Delete,
            mismatch == "remote" ? "foreign" : "remote", "v2",
            mismatch == "kind" ? CloudItemKind.Directory : CloudItemKind.File,
            mismatch == "path" ? "other" : "retired", mismatch == "item" ? Guid.NewGuid() : fixture.Item.ItemId,
            previousRemoteRevision: mismatch == "revision" ? "foreign" : "v1");
        if (mismatch is "live" or "missing")
        {
            await using ICloudStateTransaction transaction = await fixture.Factory.Store!.BeginTransactionAsync();
            if (mismatch == "missing") { await transaction.Items.RemoveAsync(fixture.Item.ItemId); }
            else
            {
                await transaction.Items.UpsertAsync(new(fixture.Item.ItemId, "remote", "retired", CloudItemKind.File,
                    "v1", 123, false, fixture.Item.UpdatedAt));
            }
            await transaction.CommitAsync();
        }
        CloudRemoteChangeBatch batch = new("original", new byte[] { 1 }, [change], new byte[] { 9 });
        Assert.Equal(CloudRemoteDeleteReconciliationStatus.NotApplicable,
            (await fixture.System.ReconcileRemoteDeleteAsync(batch, 0)).Status);
        Assert.Null(await fixture.ReadProgressAsync(batch));
        await fixture.AssertNoReceiptsAsync();
    }

    [Fact]
    public async Task OnlyTheOriginalCurrentEntryAndPreservingPolicyCanAdvance()
    {
        await using Fixture fixture = await Fixture.OpenAsync(false);
        CloudRemoteChangeBatch batch = fixture.Batch(mixed: true);
        Assert.Equal(CloudRemoteDeleteReconciliationStatus.EntryNotCurrent,
            (await fixture.System.ReconcileRemoteDeleteAsync(batch, 3)).Status);
        await fixture.System.ApplyRemoteChangesAsync(batch);
        CloudRemoteChangeBatch changed = new(batch.BatchId, new byte[] { 99 }, batch.Changes, batch.FinalCursor);
        Assert.Equal(CloudRemoteDeleteReconciliationStatus.BatchMismatch,
            (await fixture.System.ReconcileRemoteDeleteAsync(changed, 3)).Status);
        Assert.Equal(CloudRemoteDeleteReconciliationStatus.EntryNotCurrent,
            (await fixture.System.ReconcileRemoteDeleteAsync(batch, 2)).Status);
        Assert.Equal(CloudRemoteDeleteReconciliationStatus.PolicyMismatch, (await fixture.System.ReconcileRemoteDeleteAsync(
            batch, 3, new() { PreserveUnsynchronizedLocalContent = false })).Status);
        Assert.Equal(CloudRemoteDeleteReconciliationStatus.PolicyMismatch, (await fixture.System.ReconcileRemoteDeleteAsync(
            batch, 3, new() { ConflictResolver = new RefusingResolver() })).Status);
        await fixture.AssertNoReceiptsAsync();
        Assert.Equal(3, (await fixture.ReadProgressAsync(batch))!.AppliedEntryCount);
        CloudRemoteDeleteReconciliationResult result = await fixture.System.ReconcileRemoteDeleteAsync(batch, 3);
        Assert.Equal(CloudRemoteDeleteReconciliationStatus.PolicyMismatch, (await fixture.System.ReconcileRemoteDeleteAsync(
            batch, 3, new() { MaximumEntries = 1 })).Status);
        Assert.Equal(CloudRemoteDeleteReconciliationStatus.BatchMismatch,
            (await fixture.System.GetRemoteDeleteReconciliationAsync(changed, 3)).Status);
        Assert.Equal(result.ConflictId, (await fixture.System.GetRemoteDeleteReconciliationAsync(batch, 3)).ConflictId);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("move")]
    [InlineData("create")]
    public async Task OriginalNativeRecoveryEvidenceIsNeverConsumed(string intent)
    {
        await using Fixture fixture = await Fixture.OpenAsync(false);
        CloudRemoteChangeBatch batch = fixture.Batch();
        string name = intent == "delete" ? RemoteDeleteIntent.Name(batch.BatchId, 0) :
            intent == "move" ? RemoteMoveIntent.Name(batch.BatchId, 0) : RemoteCreationIntent.Name(batch.Changes[0]);
        await using (ICloudStateTransaction transaction = await fixture.Factory.Store!.BeginTransactionAsync())
        {
            await transaction.Checkpoints.UpsertAsync(new(name, new byte[] { 0xff }, DateTimeOffset.UtcNow));
            await transaction.CommitAsync();
        }
        Assert.Equal(CloudRemoteDeleteReconciliationStatus.PendingNativeRecovery,
            (await fixture.System.ReconcileRemoteDeleteAsync(batch, 0)).Status);
        Assert.Null(await fixture.ReadProgressAsync(batch));
        await fixture.AssertNoReceiptsAsync();
        await using ICloudStateTransaction capture = await fixture.Factory.Store!.BeginTransactionAsync();
        Assert.Equal(new byte[] { 0xff }, (await capture.Checkpoints.GetAsync(name))!.Value.ToArray());
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("version")]
    [InlineData("conflict")]
    [InlineData("progress")]
    [InlineData("cursor")]
    [InlineData("scope")]
    [InlineData("root")]
    public async Task CorruptOrForeignDurableOutcomesAreNotRepairedByGuessing(string corruption)
    {
        await using Fixture fixture = await Fixture.OpenAsync(false);
        CloudRemoteChangeBatch batch = fixture.Batch();
        CloudRemoteDeleteReconciliationResult original = await fixture.System.ReconcileRemoteDeleteAsync(batch, 0);
        await using (ICloudStateTransaction transaction = await fixture.Factory.Store!.BeginTransactionAsync())
        {
            string name = RemoteDeleteReconciliationReceipt.Name(batch.BatchId, 0);
            CloudStateCheckpoint checkpoint = (await transaction.Checkpoints.GetAsync(name))!;
            RemoteDeleteReconciliationReceipt receipt = RemoteDeleteReconciliationReceipt.Decode(checkpoint.Value);
            if (corruption is "receipt" or "version" or "root")
            {
                byte[] data = corruption == "root" ? (receipt with { RootPath = fixture.Area }).Encode() :
                    corruption == "version" ? new byte[] { 99, 0, 0, 0 } : new byte[] { 1 };
                await transaction.Checkpoints.UpsertAsync(new(name, data, checkpoint.UpdatedAt));
            }
            else if (corruption == "scope")
            {
                await transaction.Checkpoints.UpsertAsync(new(name, (receipt with { StoreScope = Guid.NewGuid() }).Encode(), checkpoint.UpdatedAt));
            }
            else if (corruption == "conflict") { await transaction.Conflicts.RemoveAsync(original.ConflictId!.Value); }
            else
            {
                CloudRemoteBatchState progress = (await transaction.RemoteBatches.GetAsync(batch.BatchId))!;
                await transaction.RemoteBatches.UpsertAsync(new(batch.BatchId,
                    corruption == "cursor" ? new byte[] { 7 } : progress.Cursor.Span,
                    corruption == "progress" ? 0 : 1, 1, progress.Status, progress.Payload.Span,
                    progress.UpdatedAt, progress.Fingerprint, progress.LastAppliedChangeId));
            }
            await transaction.CommitAsync();
        }
        await fixture.RestartAsync();
        if (corruption is "scope" or "root")
        {
            Assert.Equal(CloudRemoteDeleteReconciliationStatus.ScopeMismatch,
                (await fixture.System.GetRemoteDeleteReconciliationAsync(batch, 0)).Status);
            Assert.Equal(CloudRemoteDeleteReconciliationStatus.ScopeMismatch,
                (await fixture.System.ReconcileRemoteDeleteAsync(batch, 0)).Status);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.System.GetRemoteDeleteReconciliationAsync(batch, 0).AsTask());
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.System.ReconcileRemoteDeleteAsync(batch, 0).AsTask());
        }
        await fixture.AssertOriginalsAsync();
    }

    [Fact]
    public async Task UnsupportedMetadataBudgetCannotLeaveAConflictOrAdvanceTheBatch()
    {
        await using Fixture fixture = await Fixture.OpenAsync(false);
        CloudRemoteChangeBatch original = fixture.Batch();
        CloudRemoteChangeBatch oversized = new(original.BatchId, original.InitialCursor, original.Changes,
            new byte[RemoteDeleteReconciliationReceipt.MaximumBytes]);
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.System.ReconcileRemoteDeleteAsync(oversized, 0).AsTask());
        Assert.Null(await fixture.ReadProgressAsync(oversized));
        await fixture.AssertNoReceiptsAsync();
        await using (ICloudStateTransaction capture = await fixture.Factory.Store!.BeginTransactionAsync())
        {
            Assert.Empty(await capture.Conflicts.ListAsync());
        }
        await fixture.AssertOriginalsAsync();
    }

    [Fact]
    public async Task BusyCancellationAndDisposalRespectStateOnlyAdmission()
    {
        await using Fixture fixture = await Fixture.OpenAsync(false);
        CloudRemoteChangeBatch batch = fixture.Batch();
        TaskCompletionSource admitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Factory.BeforeBegin = async token => { admitted.SetResult(); await release.Task.WaitAsync(token); };
        Task<CloudRemoteDeleteReconciliationResult> first = fixture.System.ReconcileRemoteDeleteAsync(batch, 0).AsTask();
        await admitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CloudRemoteDeleteReconciliationStatus.Busy, (await fixture.System.ReconcileRemoteDeleteAsync(batch, 0)).Status);
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.System.GetRemoteDeleteReconciliationAsync(batch, 0, canceled.Token).AsTask());
        Task stopping = fixture.System.DisposeAsync().AsTask();
        Assert.False(stopping.IsCompleted);
        release.SetResult();
        AssertRecorded(await first, 1, new byte[] { 9 });
        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.RestartAsync();
        Assert.Equal(CloudRemoteDeleteReconciliationStatus.AlreadyRecorded,
            (await fixture.System.GetRemoteDeleteReconciliationAsync(batch, 0)).Status);
    }

    private static void AssertRecorded(CloudRemoteDeleteReconciliationResult result, int prefix, byte[] cursor)
    {
        Assert.Equal(CloudRemoteDeleteReconciliationStatus.RecordedConflict, result.Status);
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, result.Entry!.Status);
        Assert.Equal(CloudRemoteConflictReason.LocalDeleteReconciliationRequired, result.Entry.Conflict!.Reason);
        Assert.True(result.Entry.Conflict.LocalState!.IsTombstone);
        Assert.NotNull(result.ConflictId);
        Assert.False(result.NativeMutationAttempted);
        Assert.Equal(prefix, result.AppliedEntryCount);
        Assert.Equal(cursor, result.SafeCursor.ToArray());
    }

    private sealed class RefusingResolver : ICloudRemoteConflictResolver
    {
        public ValueTask<CloudRemoteConflictResolution> ResolveAsync(CloudRemoteConflict conflict, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Metadata recovery must not invoke the application.");
    }

    private sealed class Fixture(string area, CloudFileSystem system, Factory factory, CloudItemState item,
        IReadOnlyList<CloudOperationJournalEntry> operations) : IAsyncDisposable
    {
        internal string Area => area;
        internal CloudFileSystem System { get; private set; } = system;
        internal Factory Factory => factory;
        internal CloudItemState Item => item;
        internal CloudRemoteChangeBatch Batch(bool mixed = false)
        {
            CloudRemoteChange target = new("delete", CloudRemoteChangeKind.Delete, "remote", "v2", item.Kind,
                item.RelativePath, item.ItemId, previousRemoteRevision: "v1", cursorAfter: mixed ? new byte[] { 5 } : default);
            CloudRemoteChange Missing(int index) => new("missing-" + index, CloudRemoteChangeKind.Delete,
                "missing-" + index, "v1", CloudItemKind.File, "missing-" + index, cursorAfter: new byte[] { (byte)(index + 2) });
            return new("original", new byte[] { 1 }, mixed ? [Missing(0), Missing(1), Missing(2), target,
                new("last", CloudRemoteChangeKind.Delete, "last", "v1", CloudItemKind.File, "last")] : [target], new byte[] { 9 });
        }
        internal static async Task<Fixture> OpenAsync(bool directory, string relativePath = "retired")
        {
            string area = Path.Combine(Path.GetTempPath(), "CfSharp-delete-reconciliation", Guid.NewGuid().ToString("N"));
            string root = Path.Combine(area, "root");
            Directory.CreateDirectory(root);
            Factory factory = new(new SqliteCloudStateStoreFactory(Path.Combine(area, "state.db")));
            CloudFileSystem system = Create(root, factory);
            await system.StartAsync();
            CloudItemState item = new(Guid.NewGuid(), "remote", relativePath, directory ? CloudItemKind.Directory : CloudItemKind.File,
                "v1", 123, true, DateTimeOffset.UtcNow);
            await using ICloudStateTransaction transaction = await factory.Store!.BeginTransactionAsync();
            await transaction.Items.UpsertAsync(item);
            await transaction.Operations.EnqueueAsync(new(Guid.NewGuid(), CloudStateOperationKind.Delete, item.ItemId,
                new byte[] { 1, 2, 3 }, DateTimeOffset.UtcNow, attemptCount: 2, retryAfter: DateTimeOffset.UtcNow.AddHours(1)));
            await transaction.Operations.EnqueueAsync(new(Guid.NewGuid(), CloudStateOperationKind.ContentUpdate, null,
                new byte[] { 4, 5, 6 }, DateTimeOffset.UtcNow));
            IReadOnlyList<CloudOperationJournalEntry> originals = await transaction.Operations.ListAsync(100);
            await transaction.CommitAsync();
            return new(area, system, factory, item, originals);
        }
        internal async Task RestartAsync(string? rootPath = null)
        {
            string root = rootPath ?? System.SyncRootPath;
            await System.DisposeAsync();
            System = Create(root, factory);
            await System.StartAsync();
        }
        internal async Task<CloudRemoteBatchState?> ReadProgressAsync(CloudRemoteChangeBatch batch)
        {
            await using ICloudStateTransaction transaction = await factory.Store!.BeginTransactionAsync();
            return await transaction.RemoteBatches.GetAsync(batch.BatchId);
        }
        internal async Task AssertNoReceiptsAsync()
        {
            await using ICloudStateTransaction transaction = await factory.Store!.BeginTransactionAsync();
            Assert.Empty(await transaction.Checkpoints.ListAsync(RemoteDeleteReconciliationReceipt.Prefix));
        }
        internal async Task AssertOriginalsAsync()
        {
            await using ICloudStateTransaction transaction = await factory.Store!.BeginTransactionAsync();
            CloudItemState actual = (await transaction.Items.GetByItemIdAsync(item.ItemId))!;
            Assert.Equal(item.RelativePath, actual.RelativePath);
            Assert.Equal(item.RemoteRevision, actual.RemoteRevision);
            Assert.Equal(item.UpdatedAt, actual.UpdatedAt);
            Assert.Equal(item.LocalFileId, actual.LocalFileId);
            Assert.True(actual.IsTombstone);
            IReadOnlyList<CloudOperationJournalEntry> retained = await transaction.Operations.ListAsync(100);
            Assert.Equal(operations.Count, retained.Count);
            for (int index = 0; index < operations.Count; index++)
            {
                CloudOperationJournalEntry expected = operations[index];
                CloudOperationJournalEntry row = retained[index];
                Assert.Equal(expected.OperationId, row.OperationId);
                Assert.Equal(expected.Sequence, row.Sequence);
                Assert.Equal(expected.Kind, row.Kind);
                Assert.Equal(expected.ItemId, row.ItemId);
                Assert.Equal(expected.Payload.ToArray(), row.Payload.ToArray());
                Assert.Equal(expected.AttemptCount, row.AttemptCount);
                Assert.Equal(expected.RetryAfter, row.RetryAfter);
                Assert.Equal(expected.CreatedAt, row.CreatedAt);
            }
        }
        public async ValueTask DisposeAsync()
        {
            await System.DisposeAsync();
            Directory.Delete(area, recursive: true);
        }
        private static CloudFileSystem Create(string root, Factory factory) =>
            CloudFileSystem.CreateBuilder(root, new TestRuntime()).WithStateStore(factory).Build();
    }

    private sealed class Factory(ICloudStateStoreFactory inner) : ICloudStateStoreFactory
    {
        internal ICloudStateStore? Store { get; private set; }
        internal string? Boundary { get; set; }
        internal CancellationTokenSource? Cancellation { get; set; }
        internal Func<CancellationToken, Task>? BeforeBegin { get; set; }
        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context, CancellationToken cancellationToken = default) =>
            Store = new WrappedStore(await inner.OpenAsync(context, cancellationToken), this);
        private sealed class WrappedStore(ICloudStateStore inner, Factory owner) : ICloudStateStore
        {
            public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
            {
                Func<CancellationToken, Task>? before = owner.BeforeBegin;
                owner.BeforeBegin = null;
                if (before is not null) { await before(cancellationToken); }
                return new Transaction(await inner.BeginTransactionAsync(cancellationToken), owner);
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
        private sealed class Transaction(ICloudStateTransaction inner, Factory owner) : ICloudStateTransaction
        {
            public ICloudItemStateRepository Items => inner.Items;
            public ICloudCheckpointRepository Checkpoints => inner.Checkpoints;
            public ICloudOperationJournal Operations => inner.Operations;
            public ICloudConflictRepository Conflicts => inner.Conflicts;
            public ICloudRemoteBatchRepository RemoteBatches => inner.RemoteBatches;
            public ICloudEchoSuppressionRepository EchoSuppressions => inner.EchoSuppressions;
            public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
            {
                string? boundary = owner.Boundary;
                owner.Boundary = null;
                if (boundary == "before") { throw new IOException("Injected before reconciliation commit."); }
                if (boundary == "cancel-before") { owner.Cancellation!.Cancel(); }
                await inner.CommitAsync(cancellationToken);
                if (boundary == "cancel-after") { owner.Cancellation!.Cancel(); }
                if (boundary == "after") { throw new IOException("Injected lost commit return."); }
            }
            public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private sealed class TestRuntime : ICloudFileSystemRuntime
    {
        public ICloudFileSystemRuntimeSession Start(string syncRootPath, SyncRootRegistrationOptions? registration,
            ICloudFileContentProvider? contentProvider, ICloudStateStore stateStore) => new Session();
        private sealed class Session : ICloudFileSystemRuntimeSession
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
