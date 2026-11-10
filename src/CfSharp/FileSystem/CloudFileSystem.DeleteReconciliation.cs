namespace CfSharp;

public sealed partial class CloudFileSystem
{
    /// <summary>Rejects one exact original remote Delete as a durable, non-destructive conflict.</summary>
    /// <param name="batch">The unchanged original batch, including its identifiers, entries, and cursor envelope.</param>
    /// <param name="entryIndex">The zero-based next durable ordinal; a previous recorded reconciliation is replayable.</param>
    /// <param name="options">The original preserving policy, or conservative defaults. A resolver is not permitted.</param>
    /// <param name="cancellationToken">Cancels admission or the transaction before commit.</param>
    /// <returns>A recorded Conflict, its previous durable result, or a typed non-mutating refusal.</returns>
    /// <exception cref="ArgumentNullException">The batch is null.</exception>
    /// <exception cref="ArgumentException">The ordinal is invalid, is not a Delete, or the options are invalid.</exception>
    /// <exception cref="InvalidOperationException">The owner has not started or its retained progress is inconsistent.</exception>
    /// <exception cref="ObjectDisposedException">The owner is stopping or disposed.</exception>
    /// <exception cref="InvalidDataException">A retained receipt or accepted prefix is corrupt or incompatible.</exception>
    /// <exception cref="NotSupportedException">The transactional store lacks a required operation, or receipt metadata exceeds its budget.</exception>
    /// <exception cref="OperationCanceledException">Cancellation precedes commit; after an ambiguous commit, query the original request.</exception>
    /// <remarks>
    /// Requires a started Windows owner and its explicitly selected transactional store. Calls
    /// serialize with remote application/resolution; contention returns Busy. Disposal drains
    /// admitted work. This method acquires no native namespace handle, invokes no application
    /// resolver/provider, and performs no enumeration, hydration, native mutation, or local ACK.
    /// It requires an exact matching official tombstone and refuses original pending native
    /// recovery. A tombstone authorizes only rejection, never a successful native completion.
    /// The conflict, versioned receipt, and exactly one entry of progress commit atomically.
    /// Existing preview.11 progress, including a partially applied batch, needs no new native
    /// receipt or SQL migration. Resubmit the original preserving options; preview.11 did not
    /// retain a complete policy for unprepared entries. This method permits no destructive policy
    /// and binds subsequent reconciliation replays to its recorded options. Receipt metadata is
    /// bounded to one MiB, with individual UTF-8 strings limited to 32768 bytes.
    /// A new batch may start at ordinal zero. No following entry is attempted. The safe cursor
    /// follows ordinary processed-conflict semantics; Applied describes batch progress, not native
    /// deletion. Query after a lost return or ordinary AlreadyApplied replay. KeepRemote/KeepBoth
    /// never execute this conflict; the application can explicitly dismiss it after its own proof.
    /// </remarks>
    public async ValueTask<CloudRemoteDeleteReconciliationResult> ReconcileRemoteDeleteAsync(
        CloudRemoteChangeBatch batch, int entryIndex, CloudRemoteApplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateDeleteReconciliationRequest(batch, entryIndex);
        CloudRemoteApplyOptions selected = options ?? CloudRemoteApplyOptions.Default;
        selected.Validate();
        EnsureStarted();
        cancellationToken.ThrowIfCancellationRequested();
        if (!selected.PreserveUnsynchronizedLocalContent || selected.ConflictResolver is not null)
        {
            return new(CloudRemoteDeleteReconciliationStatus.PolicyMismatch, entryIndex);
        }
        if (!await _remoteApplyGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new(CloudRemoteDeleteReconciliationStatus.Busy, entryIndex);
        }
        try
        {
            using CloudFileSystemOperationLease operation = await AcquireStateOperationAsync(cancellationToken).ConfigureAwait(false);
            await using ICloudStateTransaction transaction = await operation.StateStore.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            CloudRemoteDeleteReconciliationResult? recorded = await ReadDeleteReconciliationAsync(
                transaction, batch, entryIndex, selected, cancellationToken).ConfigureAwait(false);
            if (recorded is not null) { return recorded; }

            CloudRemoteBatchState? progress = await transaction.RemoteBatches.GetAsync(batch.BatchId, cancellationToken).ConfigureAwait(false);
            if (progress is not null && !MatchesReconciliationBatch(batch, progress))
            {
                return new(CloudRemoteDeleteReconciliationStatus.BatchMismatch, entryIndex);
            }
            if ((progress?.AppliedEntryCount ?? 0) != entryIndex)
            {
                return new(CloudRemoteDeleteReconciliationStatus.EntryNotCurrent, entryIndex);
            }
            if (progress is not null)
            {
                ValidateReconciliationProgress(batch, progress);
                await ValidateAcceptedRemoteIdentitiesAsync(transaction, batch, progress.AppliedEntryCount, cancellationToken).ConfigureAwait(false);
            }
            CloudRemoteChange change = batch.Changes[entryIndex];
            if (await transaction.Checkpoints.GetAsync(RemoteDeleteIntent.Name(batch.BatchId, entryIndex), cancellationToken).ConfigureAwait(false) is not null ||
                await transaction.Checkpoints.GetAsync(RemoteMoveIntent.Name(batch.BatchId, entryIndex), cancellationToken).ConfigureAwait(false) is not null ||
                await transaction.Checkpoints.GetAsync(RemoteCreationIntent.Name(change), cancellationToken).ConfigureAwait(false) is not null)
            {
                return new(CloudRemoteDeleteReconciliationStatus.PendingNativeRecovery, entryIndex);
            }
            CloudItemState? tombstone = change.ItemId is Guid itemId
                ? await transaction.Items.GetByItemIdAsync(itemId, cancellationToken).ConfigureAwait(false)
                : await transaction.Items.GetByRemoteIdAsync(change.RemoteId, cancellationToken).ConfigureAwait(false);
            if (!MatchesReconciliationTombstone(change, tombstone))
            {
                return new(CloudRemoteDeleteReconciliationStatus.NotApplicable, entryIndex);
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            Guid scope = await CloudDirectoryMoveEvidence.GetScopeAsync(transaction, create: true, cancellationToken).ConfigureAwait(false);
            ReadOnlyMemory<byte> cursor = ReconciliationCursor(batch, entryIndex + 1);
            RemoteDeleteReconciliationReceipt receipt = new(scope, SyncRootPath, batch.BatchId, entryIndex,
                batch.Fingerprint.ToArray(), Guid.NewGuid(), tombstone!, now, selected.SuppressLocalEcho,
                selected.EchoSuppressionLifetime.Ticks, selected.EchoSuppressionObservationCount, selected.MaximumEntries, cursor.ToArray());
            CloudRemoteConflict conflict = new(change, tombstone, CloudRemoteConflictReason.LocalDeleteReconciliationRequired, now);
            await transaction.Conflicts.UpsertAsync(new(receipt.ConflictId, tombstone!.ItemId, CloudStateConflictKind.Delete,
                EncodeConflict(conflict), now), cancellationToken).ConfigureAwait(false);
            await transaction.Checkpoints.UpsertAsync(new(RemoteDeleteReconciliationReceipt.Name(batch.BatchId, entryIndex),
                receipt.Encode(), now), cancellationToken).ConfigureAwait(false);
            await transaction.RemoteBatches.UpsertAsync(new(batch.BatchId, cursor.Span, entryIndex + 1, batch.Changes.Count,
                entryIndex + 1 == batch.Changes.Count ? CloudRemoteBatchStatus.Applied : CloudRemoteBatchStatus.Applying,
                progress is null ? ReadOnlySpan<byte>.Empty : progress.Payload.Span, now, batch.Fingerprint, change.ChangeId), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return CreateDeleteReconciliationResult(CloudRemoteDeleteReconciliationStatus.RecordedConflict, batch, receipt);
        }
        finally { _remoteApplyGate.Release(); }
    }

    /// <summary>Recovers the detailed original non-destructive Delete result, including after conflict dismissal.</summary>
    /// <param name="batch">The unchanged original request whose complete fingerprint is verified.</param>
    /// <param name="entryIndex">The original zero-based Delete ordinal.</param>
    /// <param name="cancellationToken">Cancels lifecycle admission or the transactional read.</param>
    /// <returns>The recorded Conflict snapshot with AlreadyRecorded status, NotFound, or a batch/scope refusal.</returns>
    /// <exception cref="ArgumentNullException">The batch is null.</exception>
    /// <exception cref="ArgumentException">The ordinal is invalid or is not a Delete.</exception>
    /// <exception cref="InvalidOperationException">The owner is not started or progress is inconsistent.</exception>
    /// <exception cref="ObjectDisposedException">The owner is stopping or disposed.</exception>
    /// <exception cref="InvalidDataException">A receipt, conflict, or its atomic progress is corrupt.</exception>
    /// <exception cref="NotSupportedException">The custom store lacks a required transactional operation.</exception>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    /// <remarks>
    /// Read-only, safe for concurrent callers, and admitted into owner disposal. No native path
    /// query, application callback, or store ownership transfer occurs. It works before application
    /// namespace re-audit and on a disconnected owner, even if registration is subsequently
    /// removed. Normal startup still validates the root/registration and opens the same store.
    /// The result is historical; later occupants, revisions,
    /// local operations, batch progress and explicit dismissal are not rewritten or acknowledged.
    /// </remarks>
    public async ValueTask<CloudRemoteDeleteReconciliationResult> GetRemoteDeleteReconciliationAsync(
        CloudRemoteChangeBatch batch, int entryIndex, CancellationToken cancellationToken = default)
    {
        ValidateDeleteReconciliationRequest(batch, entryIndex);
        using CloudFileSystemOperationLease operation = await AcquireStateOperationAsync(cancellationToken).ConfigureAwait(false);
        await using ICloudStateTransaction transaction = await operation.StateStore.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadDeleteReconciliationAsync(transaction, batch, entryIndex, null, cancellationToken).ConfigureAwait(false)
            ?? new(CloudRemoteDeleteReconciliationStatus.NotFound, entryIndex);
    }

    private static void ValidateDeleteReconciliationRequest(CloudRemoteChangeBatch batch, int entryIndex)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentOutOfRangeException.ThrowIfNegative(entryIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(entryIndex, batch.Changes.Count);
        if (batch.Changes[entryIndex].Kind != CloudRemoteChangeKind.Delete)
        {
            throw new ArgumentException("Only an original remote Delete entry can be reconciled.", nameof(entryIndex));
        }
    }

    private static bool MatchesReconciliationBatch(CloudRemoteChangeBatch batch, CloudRemoteBatchState progress) =>
        progress.BatchId == batch.BatchId && progress.TotalEntryCount == batch.Changes.Count &&
        progress.Fingerprint.Span.SequenceEqual(batch.Fingerprint.Span);

    private static bool MatchesReconciliationTombstone(CloudRemoteChange change, CloudItemState? item) =>
        item is { IsTombstone: true } && item.RemoteId == change.RemoteId && item.Kind == change.ItemKind &&
        (!change.ItemId.HasValue || item.ItemId == change.ItemId) && item.RelativePath == change.RelativePath &&
        (change.PreviousRemoteRevision is null || item.RemoteRevision == change.PreviousRemoteRevision);

    private static ReadOnlyMemory<byte> ReconciliationCursor(CloudRemoteChangeBatch batch, int prefix)
    {
        ReadOnlyMemory<byte> cursor = batch.InitialCursor;
        for (int index = 0; index < prefix; index++)
        {
            if (!batch.Changes[index].CursorAfter.IsEmpty) { cursor = batch.Changes[index].CursorAfter; }
        }
        return prefix == batch.Changes.Count && batch.Changes[^1].CursorAfter.IsEmpty ? batch.FinalCursor : cursor;
    }

    private static void ValidateReconciliationProgress(CloudRemoteChangeBatch batch, CloudRemoteBatchState progress)
    {
        int count = progress.AppliedEntryCount;
        if (count > batch.Changes.Count || !progress.Cursor.Span.SequenceEqual(ReconciliationCursor(batch, count).Span) ||
            (count > 0 && progress.LastAppliedChangeId != batch.Changes[count - 1].ChangeId) ||
            (count == 0 && progress.LastAppliedChangeId is not null) ||
            (progress.Status == CloudRemoteBatchStatus.Applied) != (count == batch.Changes.Count))
        {
            throw new InvalidDataException("The original remote batch prefix or cursor is inconsistent.");
        }
    }

    private async ValueTask<CloudRemoteDeleteReconciliationResult?> ReadDeleteReconciliationAsync(
        ICloudStateTransaction transaction, CloudRemoteChangeBatch batch, int index, CloudRemoteApplyOptions? options,
        CancellationToken cancellationToken)
    {
        CloudStateCheckpoint? checkpoint = await transaction.Checkpoints.GetAsync(
            RemoteDeleteReconciliationReceipt.Name(batch.BatchId, index), cancellationToken).ConfigureAwait(false);
        if (checkpoint is null) { return null; }
        RemoteDeleteReconciliationReceipt receipt = RemoteDeleteReconciliationReceipt.Decode(checkpoint.Value);
        if (receipt.BatchId != batch.BatchId || receipt.EntryIndex != index || !receipt.Fingerprint.AsSpan().SequenceEqual(batch.Fingerprint.Span))
        {
            return new(CloudRemoteDeleteReconciliationStatus.BatchMismatch, index);
        }
        if (receipt.RootPath != SyncRootPath || receipt.StoreScope !=
            await CloudDirectoryMoveEvidence.GetScopeAsync(transaction, false, cancellationToken).ConfigureAwait(false))
        {
            return new(CloudRemoteDeleteReconciliationStatus.ScopeMismatch, index);
        }
        if (options is not null && !receipt.MatchesPolicy(options)) { return new(CloudRemoteDeleteReconciliationStatus.PolicyMismatch, index); }
        CloudRemoteBatchState? progress = await transaction.RemoteBatches.GetAsync(batch.BatchId, cancellationToken).ConfigureAwait(false);
        if (progress is null || !MatchesReconciliationBatch(batch, progress) || progress.AppliedEntryCount <= index ||
            !MatchesReconciliationTombstone(batch.Changes[index], receipt.Tombstone) ||
            !receipt.Cursor.AsSpan().SequenceEqual(ReconciliationCursor(batch, index + 1).Span))
        {
            throw new InvalidDataException("The Delete reconciliation receipt has no matching original outcome.");
        }
        ValidateReconciliationProgress(batch, progress);
        CloudConflictState? conflict = await transaction.Conflicts.GetAsync(receipt.ConflictId, cancellationToken).ConfigureAwait(false);
        CloudRemoteConflict expected = new(batch.Changes[index], receipt.Tombstone,
            CloudRemoteConflictReason.LocalDeleteReconciliationRequired, receipt.DetectedAt);
        if (conflict is not null)
        {
            if (conflict.ItemId != receipt.Tombstone.ItemId || conflict.Kind != CloudStateConflictKind.Delete ||
                conflict.CreatedAt != receipt.DetectedAt || !conflict.Payload.Span.SequenceEqual(EncodeConflict(expected)))
            {
                throw new InvalidDataException("The original reconciliation conflict was changed.");
            }
        }
        else
        {
            CloudStateCheckpoint? dismissal = await transaction.Checkpoints.GetAsync(
                ConflictDismissalPrefix + "/" + receipt.ConflictId.ToString("N"), cancellationToken).ConfigureAwait(false);
            if (dismissal is null || !dismissal.Value.Span.SequenceEqual(new byte[] { 1 }))
            {
                throw new InvalidDataException("The original reconciliation conflict is missing without a dismissal receipt.");
            }
        }
        return CreateDeleteReconciliationResult(CloudRemoteDeleteReconciliationStatus.AlreadyRecorded, batch, receipt);
    }

    private static CloudRemoteDeleteReconciliationResult CreateDeleteReconciliationResult(
        CloudRemoteDeleteReconciliationStatus status, CloudRemoteChangeBatch batch, RemoteDeleteReconciliationReceipt receipt) =>
        new(status, receipt.EntryIndex, receipt.ConflictId, new(batch.Changes[receipt.EntryIndex].ChangeId,
            CloudRemoteApplyEntryStatus.Conflict, new(batch.Changes[receipt.EntryIndex], receipt.Tombstone,
                CloudRemoteConflictReason.LocalDeleteReconciliationRequired, receipt.DetectedAt)), receipt.EntryIndex + 1,
            receipt.EntryIndex + 1 == batch.Changes.Count ? CloudRemoteBatchStatus.Applied : CloudRemoteBatchStatus.Applying, receipt.Cursor);
}
