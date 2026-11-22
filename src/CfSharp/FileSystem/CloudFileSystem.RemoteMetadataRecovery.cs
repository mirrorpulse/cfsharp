using System.ComponentModel;

using CfSharp.Native;

using Microsoft.Win32.SafeHandles;

namespace CfSharp;

public sealed partial class CloudFileSystem
{
    /// <summary>Records the next uncertain remote metadata entry as a non-mutating conflict after full reconciliation.</summary>
    /// <param name="batch">The unchanged original batch, including its cursor envelope and entry order.</param>
    /// <param name="entryIndex">The zero-based ordinal of the metadata update to close.</param>
    /// <param name="cancellationToken">Token that cancels admission or the transaction before it commits.</param>
    /// <returns>The durable batch prefix, safe cursor and conflict. An already processed entry returns
    /// <see cref="CloudRemoteApplyEntryStatus.AlreadyApplied"/> with the current durable prefix.</returns>
    /// <exception cref="ArgumentNullException">The batch is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The entry ordinal is outside the batch.</exception>
    /// <exception cref="ArgumentException">The entry is not a metadata update or the batch fingerprint differs.</exception>
    /// <exception cref="InvalidOperationException">The owner is not started, the next entry differs, or
    /// the evidence calls for ordinary replay, or full reconciliation has not been acknowledged.</exception>
    /// <exception cref="InvalidDataException">Retained progress or evidence is inconsistent.</exception>
    /// <exception cref="Win32Exception">A native sharing or locking conflict prevents the original binding check.</exception>
    /// <exception cref="IOException">A storage operation fails or a path guard reports a sharing or locking conflict.</exception>
    /// <exception cref="CloudFilesException">A Cloud Files query reports a sharing or locking conflict.</exception>
    /// <exception cref="ObjectDisposedException">The file-system owner is stopping or disposed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was observed before completion.</exception>
    /// <remarks>
    /// Complete and acknowledge the feed's full rescan before calling. The application chooses this
    /// outcome after reconciling the actual namespace and any work required for the original change.
    /// This Windows-only operation serializes with other remote calls and commits one entry only.
    /// A recorded native completion is checked first. If the original outcome can still be
    /// verified, replay the batch normally. Closing an unverifiable entry leaves the native
    /// object, official revision and local journal untouched. It does not invoke the provider.
    /// A transient sharing or locking failure preserves the original evidence and is propagated.
    /// The conflict cannot execute a later KeepRemote decision. Repeating the request after an
    /// ambiguous commit returns the current prefix without creating another conflict.
    /// </remarks>
    public async ValueTask<CloudRemoteApplyResult> ReconcileRemoteMetadataAsync(CloudRemoteChangeBatch batch,
        int entryIndex, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentOutOfRangeException.ThrowIfNegative(entryIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(entryIndex, batch.Changes.Count);
        CloudRemoteChange change = batch.Changes[entryIndex];
        if (change.Kind != CloudRemoteChangeKind.MetadataUpdate)
        { throw new ArgumentException("Only an original remote metadata update can be reconciled.", nameof(entryIndex)); }
        EnsureStarted();
        await _remoteApplyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using CloudFileSystemOperationLease operation = await AcquireStateOperationAsync(cancellationToken).ConfigureAwait(false);
            using IDisposable publication = await _metadataEchoAuthority.AcquirePublicationAsync(cancellationToken).ConfigureAwait(false);
            await using ICloudStateTransaction transaction = await operation.StateStore.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            CloudRemoteBatchState progress = await transaction.RemoteBatches.GetAsync(batch.BatchId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The original remote metadata batch has no durable progress.");
            if (!MatchesReconciliationBatch(batch, progress))
            { throw new ArgumentException("The original batch fingerprint differs from the retained progress.", nameof(batch)); }
            ValidateReconciliationProgress(batch, progress);
            await ValidateAcceptedRemoteIdentitiesAsync(transaction, batch, progress.AppliedEntryCount, cancellationToken).ConfigureAwait(false);
            if (progress.AppliedEntryCount > entryIndex)
            { return MetadataReconciliationResult(batch, progress, entryIndex, conflict: null, conflictId: null); }
            if (progress.AppliedEntryCount != entryIndex)
            { throw new InvalidOperationException("The metadata entry is not the next entry in the original batch."); }

            string name = RemoteMetadataProjection.Name(batch, entryIndex);
            CloudStateCheckpoint checkpoint = await transaction.Checkpoints.GetAsync(name, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The original metadata entry has no pending recovery evidence.");
            RemoteMetadataProjection projection = RemoteMetadataProjection.Decode(checkpoint);
            if (projection.ProjectedAtTicks == RemoteMetadataProjection.NativeWriteNotAttempted)
            { throw new InvalidOperationException("Replay this metadata entry through ordinary remote application."); }
            CloudStateCheckpoint prepared = await transaction.Checkpoints.GetAsync(projection.ReceiptName, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original metadata attribution preparation is missing.");
            RemoteMetadataIntent intent = RemoteMetadataIntent.Decode(prepared);
            if (!intent.Covers(change.RelativePath) || (change.ItemId is Guid id && id != intent.ItemId) || intent.Completed)
            { throw new InvalidDataException("The metadata attribution does not match the uncertain batch entry."); }
            if (!intent.Reconciled || intent.RequiresReconciliation)
            { throw new InvalidOperationException("Complete and acknowledge full reconciliation before closing the uncertain metadata entry."); }
            if (projection.ProjectedAtTicks != 0 &&
                await FindRemoteMetadataProjectionMismatchAsync(transaction, change, projection, intent, target: null,
                    checkReplayAvailability: true, cancellationToken).ConfigureAwait(false) is null)
            { throw new InvalidOperationException("The original metadata outcome can still be verified. Replay the original batch."); }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            CloudItemState? current = await transaction.Items.GetByItemIdAsync(intent.ItemId, cancellationToken).ConfigureAwait(false);
            CloudRemoteConflict conflict = new(change, current, CloudRemoteConflictReason.RemoteMetadataReconciliationRequired, now);
            Guid conflictId = Guid.NewGuid();
            await transaction.Conflicts.UpsertAsync(new(conflictId, current?.ItemId, CloudStateConflictKind.Metadata,
                EncodeConflict(conflict), now), cancellationToken).ConfigureAwait(false);
            int prefix = entryIndex + 1;
            ReadOnlyMemory<byte> cursor = ReconciliationCursor(batch, prefix);
            CloudRemoteBatchState next = new(batch.BatchId, cursor.Span, prefix, batch.Changes.Count,
                prefix == batch.Changes.Count ? CloudRemoteBatchStatus.Applied : CloudRemoteBatchStatus.Applying,
                progress.Payload.Span, now, batch.Fingerprint, change.ChangeId);
            await transaction.RemoteBatches.UpsertAsync(next, cancellationToken).ConfigureAwait(false);
            await (intent with { Completed = true }).PersistAsync(transaction.Checkpoints, now, cancellationToken).ConfigureAwait(false);
            await transaction.Checkpoints.RemoveAsync(name, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return MetadataReconciliationResult(batch, next, entryIndex, conflict, conflictId);
        }
        finally { _remoteApplyGate.Release(); }
    }

    private static CloudRemoteApplyResult MetadataReconciliationResult(CloudRemoteChangeBatch batch, CloudRemoteBatchState progress,
        int entryIndex, CloudRemoteConflict? conflict, Guid? conflictId)
    {
        CloudRemoteApplyEntryResult[] entries = new CloudRemoteApplyEntryResult[batch.Changes.Count];
        for (int index = 0; index < entries.Length; index++)
        {
            CloudRemoteChange change = batch.Changes[index];
            entries[index] = index == entryIndex && conflict is not null
                ? new(change.ChangeId, CloudRemoteApplyEntryStatus.Conflict, conflict)
                : new(change.ChangeId, index < progress.AppliedEntryCount ? CloudRemoteApplyEntryStatus.AlreadyApplied : CloudRemoteApplyEntryStatus.NotProcessed);
        }
        return CreateRemoteApplyResult(progress, entries, progress.Status != CloudRemoteBatchStatus.Applied,
            conflictId is Guid id ? [id] : []);
    }

    private async ValueTask<RemoteEntryOutcome?> RecoverRemoteMetadataAsync(CloudRemoteChange change,
        string name, bool resolvingConflict, CancellationToken cancellationToken)
    {
        await using ICloudStateTransaction transaction = await _stateStore!.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        CloudStateCheckpoint? checkpoint = await transaction.Checkpoints.GetAsync(name, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null) { return null; }

        RemoteMetadataProjection projection = RemoteMetadataProjection.Decode(checkpoint);
        if (projection.ProjectedAtTicks == 0)
        {
            string reconciliation = resolvingConflict ? nameof(DismissRemoteConflictAsync) : nameof(ReconcileRemoteMetadataAsync);
            throw MetadataProjectionUnresolved(change, $"native completion is unproven; after acknowledging full reconciliation, call {reconciliation}");
        }
        CloudStateCheckpoint receipt = await transaction.Checkpoints.GetAsync(projection.ReceiptName, cancellationToken)
            .ConfigureAwait(false) ?? throw MetadataProjectionUnresolved(change, "the attribution preparation is missing");
        RemoteMetadataIntent intent = RemoteMetadataIntent.Decode(receipt);
        if (projection.ProjectedAtTicks == RemoteMetadataProjection.NativeWriteNotAttempted)
        {
            if (intent.Reconciled && intent.Completed && !intent.NativeWriteApplied && !intent.OutcomeConfirmed)
            { return null; }
            throw MetadataProjectionUnresolved(change, "the unattempted write still needs reconciliation");
        }
        await ValidateRemoteMetadataProjectionAsync(transaction, change, projection, intent, target: null, cancellationToken).ConfigureAwait(false);
        return RemoteEntryOutcome.AppliedResult with
        {
            MetadataIntent = intent with { RequiresReconciliation = !intent.Reconciled },
            RecoveredMetadataProjection = projection,
        };
    }

    private async ValueTask RecordRemoteMetadataNativeWriteAsync(string name, RemoteMetadataIntent intent)
    {
        await using ICloudStateTransaction transaction = await _stateStore!.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
        CloudStateCheckpoint checkpoint = await transaction.Checkpoints.GetAsync(name).ConfigureAwait(false)
            ?? throw new InvalidDataException("The remote metadata projection preparation is missing.");
        RemoteMetadataProjection projection = RemoteMetadataProjection.Decode(checkpoint);
        if (projection.ReceiptName != intent.Name || projection.ProjectedAtTicks != 0 || projection.OriginalRowFingerprint is null)
        { throw new InvalidDataException("The remote metadata projection preparation changed before native completion."); }

        await transaction.Checkpoints.UpsertAsync((projection with { ProjectedAtTicks = RemoteMetadataProjection.NativeWriteCompleted })
            .ToCheckpoint(name, DateTimeOffset.UtcNow)).ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private async ValueTask<bool> RecordUnattemptedRemoteMetadataAsync(string name)
    {
        using IDisposable publication = await _metadataEchoAuthority.AcquirePublicationAsync(CancellationToken.None).ConfigureAwait(false);
        await using ICloudStateTransaction transaction = await _stateStore!.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
        CloudStateCheckpoint? checkpoint = await transaction.Checkpoints.GetAsync(name).ConfigureAwait(false);
        if (checkpoint is null) { return false; }
        RemoteMetadataProjection projection = RemoteMetadataProjection.Decode(checkpoint);
        if (projection.ProjectedAtTicks != 0) { return false; }
        CloudStateCheckpoint receipt = await transaction.Checkpoints.GetAsync(projection.ReceiptName).ConfigureAwait(false)
            ?? throw new InvalidDataException("The remote metadata attribution preparation is missing.");
        RemoteMetadataIntent intent = RemoteMetadataIntent.Decode(receipt);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        RemoteMetadataIntent unattempted = intent with { Completed = true, RequiresReconciliation = true };
        await unattempted.PersistAsync(transaction.Checkpoints, now, CancellationToken.None).ConfigureAwait(false);
        if (intent.Passthrough)
        {
            await transaction.Checkpoints.RemoveAsync(intent.ProvisionalFileTimeObservationName).ConfigureAwait(false);
        }
        await transaction.Checkpoints.UpsertAsync((projection with { ProjectedAtTicks = RemoteMetadataProjection.NativeWriteNotAttempted })
            .ToCheckpoint(name, now)).ConfigureAwait(false);
        await transaction.CommitAsync().ConfigureAwait(false);
        return true;
    }

    private async ValueTask RequireCorrelatedRemoteMetadataProjectionAsync(CloudRemoteChange change, Guid itemId, CancellationToken cancellationToken)
    {
        await using ICloudStateTransaction transaction = await _stateStore!.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CloudStateCheckpoint> checkpoints = await transaction.Checkpoints.ListAsync(
            RemoteMetadataIntent.ItemPrefix(itemId), cancellationToken).ConfigureAwait(false);
        if (checkpoints.Select(RemoteMetadataIntent.Decode).Any(intent => !intent.Completed && intent.Covers(change.RelativePath)))
        {
            // Older preparations lack the immutable batch and entry link. The current
            // revision could be the result of that write, so it cannot prove a conflict.
            throw MetadataProjectionUnresolved(change, "an uncorrelated preparation already targets the current revision");
        }
    }

    private static async ValueTask StageRemoteMetadataProjectionAsync(ICloudStateTransaction transaction,
        string name, RemoteMetadataIntent intent, DateTimeOffset projectedAt, CancellationToken cancellationToken)
    {
        CloudStateCheckpoint checkpoint = await transaction.Checkpoints.GetAsync(name, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The remote metadata projection preparation is missing.");
        RemoteMetadataProjection projection = RemoteMetadataProjection.Decode(checkpoint);
        if (projection.ReceiptName != intent.Name || projection.ProjectedAtTicks != RemoteMetadataProjection.NativeWriteCompleted)
        { throw new InvalidDataException("The remote metadata native completion receipt changed."); }
        await transaction.Checkpoints.UpsertAsync((projection with { ProjectedAtTicks = projectedAt.UtcTicks })
            .ToCheckpoint(name, projectedAt), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IDisposable> HoldRemoteMetadataOutcomeAsync(ICloudStateTransaction transaction,
        CloudRemoteChange change, string name, RemoteEntryOutcome outcome, CancellationToken cancellationToken)
    {
        CloudStateCheckpoint checkpoint = await transaction.Checkpoints.GetAsync(name, cancellationToken).ConfigureAwait(false)
            ?? throw MetadataProjectionUnresolved(change, "the original projection evidence is missing");
        RemoteMetadataProjection projection = RemoteMetadataProjection.Decode(checkpoint);
        RemoteMetadataIntent intent = await ReadPreparedRemoteMetadataIntentAsync(transaction, outcome.MetadataIntent!, cancellationToken)
            .ConfigureAwait(false);
        if (projection.ReceiptName != intent.Name ||
            (outcome.RecoveredMetadataProjection is { } recovered ? recovered != projection : projection.ProjectedAtTicks <= 0))
        { throw MetadataProjectionUnresolved(change, "the original projection evidence changed"); }
        string path = ToFullPath(change.RelativePath);
        CloudPathHandleLease parents = CloudPathHandleLease.OpenParentChains(SyncRootPath, [path]);
        SafeFileHandle? target = null;
        try
        {
            target = WindowsFileMetadata.OpenNamespaceGuard(path);
            await ValidateRemoteMetadataProjectionAsync(transaction, change, projection, intent, target, cancellationToken).ConfigureAwait(false);
            if (projection.ProjectedAtTicks == RemoteMetadataProjection.NativeWriteCompleted)
            {
                // Repair the official row in the outcome transaction using the recorded
                // native success, with no second write to the placeholder.
                CloudItem item = CreateItemReference(change.RelativePath, change.ItemKind);
                CloudPlaceholderIdentity identity = new(projection.ItemId, change.RemoteId, change.RemoteRevision);
                await WriteIdentityProjectionAsync(transaction, item, identity, change.RemoteRevision,
                    localFileId: null, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            }
            return new RemoteMetadataOutcomeLease(parents, target);
        }
        catch
        {
            target?.Dispose();
            parents.Dispose();
            throw;
        }
    }

    private static async ValueTask DismissRemoteMetadataEvidenceAsync(ICloudCheckpointRepository checkpoints,
        Guid conflictId, CancellationToken cancellationToken)
    {
        string name = RemoteMetadataProjection.ConflictName(conflictId);
        CloudStateCheckpoint? checkpoint = await checkpoints.GetAsync(name, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null) { return; }
        RemoteMetadataProjection projection = RemoteMetadataProjection.Decode(checkpoint);
        CloudStateCheckpoint receipt = await checkpoints.GetAsync(projection.ReceiptName, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The remote metadata attribution preparation is missing.");
        RemoteMetadataIntent intent = RemoteMetadataIntent.Decode(receipt);
        // Dismissal ends the original authorization. Keep attribution and any
        // outstanding rescan so delayed observations still receive safe handling.
        await (intent with { Completed = true, RequiresReconciliation = !intent.Reconciled })
            .PersistAsync(checkpoints, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        await checkpoints.RemoveAsync(name, cancellationToken).ConfigureAwait(false);
    }

    private sealed class RemoteMetadataOutcomeLease(CloudPathHandleLease parents, SafeFileHandle target) : IDisposable
    {
        public void Dispose()
        {
            target.Dispose();
            parents.Dispose();
        }
    }

    private async ValueTask ValidateRemoteMetadataProjectionAsync(ICloudStateTransaction transaction, CloudRemoteChange change,
        RemoteMetadataProjection projection, RemoteMetadataIntent intent, SafeFileHandle? target, CancellationToken cancellationToken)
    {
        string? mismatch = await FindRemoteMetadataProjectionMismatchAsync(transaction, change, projection, intent, target,
            checkReplayAvailability: false, cancellationToken).ConfigureAwait(false);
        if (mismatch is not null) { throw MetadataProjectionUnresolved(change, mismatch); }
    }

    private async ValueTask<string?> FindRemoteMetadataProjectionMismatchAsync(ICloudStateTransaction transaction, CloudRemoteChange change,
        RemoteMetadataProjection projection, RemoteMetadataIntent intent, SafeFileHandle? target,
        bool checkReplayAvailability, CancellationToken cancellationToken)
    {
        if (intent.Completed || intent.Superseded || intent.Binding is null || !intent.Covers(change.RelativePath) ||
            (change.ItemId is Guid explicitId && explicitId != projection.ItemId))
        { return "the original attribution changed"; }

        bool needsProjection = projection.ProjectedAtTicks == RemoteMetadataProjection.NativeWriteCompleted;
        CloudItemState? owner = await transaction.Items.GetByRelativePathAsync(change.RelativePath, cancellationToken).ConfigureAwait(false);
        if (owner is null || owner.IsTombstone || owner.ItemId != projection.ItemId || owner.Kind != change.ItemKind ||
            (needsProjection
                ? RemoteMetadataProjection.Fingerprint(owner) != projection.OriginalRowFingerprint
                : owner.RemoteId != change.RemoteId || owner.RemoteRevision != change.RemoteRevision ||
                  owner.UpdatedAt.UtcTicks != projection.ProjectedAtTicks))
        { return "the official projection changed"; }

        if (!checkReplayAvailability)
        { return FindRemoteMetadataNativeMismatch(change, intent, ToFullPath(change.RelativePath), target); }

        try
        {
            string path = ToFullPath(change.RelativePath);
            using CloudPathHandleLease parents = CloudPathHandleLease.OpenParentChains(SyncRootPath, [path]);
            using SafeFileHandle candidate = WindowsFileMetadata.OpenNamespaceGuard(path);
            return FindRemoteMetadataNativeMismatch(change, intent, path, candidate);
        }
        catch (Exception exception) when ((exception is ArgumentException or IOException or UnauthorizedAccessException or CloudFilesException or Win32Exception) &&
            !IsMetadataRecoverySharingFailure(exception))
        {
            // Native namespace failures mean successful replay cannot be authenticated.
            // Storage failures above still propagate without closing an entry.
            return "the original native binding cannot be verified";
        }
    }

    private string? FindRemoteMetadataNativeMismatch(CloudRemoteChange change, RemoteMetadataIntent intent, string fullPath, SafeFileHandle? target)
    {
        if (target is not null &&
            (((FileAttributes)WindowsFileMetadata.ReadBasic(target.DangerousGetHandle()).Attributes).HasFlag(FileAttributes.Directory) !=
                (change.ItemKind == CloudItemKind.Directory)))
        { return "the original native item kind changed"; }
        LocalCloudItemInspection native = target is null ? CloudItemInspector.Inspect(fullPath, change.ItemKind, SyncRootPath) :
            CloudItemInspector.Inspect(target, fullPath, change.ItemKind, SyncRootPath);
        if (!native.Exists || native.Kind != change.ItemKind || native.LocalBinding != intent.Binding ||
            !CloudPlaceholderIdentity.TryDecode(native.PlaceholderIdentity, out CloudPlaceholderIdentity? identity) ||
            identity.ItemId != intent.ItemId || identity.RemoteId != change.RemoteId || identity.RemoteRevision != change.RemoteRevision ||
            (target is not null && !CloudDirectoryStateProjection.SamePath(WindowsFileMetadata.ReadFinalPath(target.DangerousGetHandle()), fullPath)))
        { return "the original native binding or identity changed"; }
        return null;
    }

    private static bool IsMetadataRecoverySharingFailure(Exception exception) => exception is
        Win32Exception { NativeErrorCode: 32 or 33 } or CloudFilesException { Win32ErrorCode: 32 or 33 } or
        IOException { InnerException: Win32Exception { NativeErrorCode: 32 or 33 } } or
        IOException { HResult: unchecked((int)0x80070020) or unchecked((int)0x80070021) };

    private static IOException MetadataProjectionUnresolved(CloudRemoteChange change, string reason) =>
        new($"Remote metadata change '{change.ChangeId}' requires reconciliation because {reason}. Retain the original request for retry.");
}
