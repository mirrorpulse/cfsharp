using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CfSharp;

public sealed partial class CloudFileSystem
{
    private const int RemoteConflictEnvelopeVersion = 1;
    private readonly SemaphoreSlim _remoteApplyGate = new(1, 1);

    /// <summary>
    /// Applies an immutable application-supplied remote change batch to this sync root.
    /// </summary>
    /// <param name="batch">Ordered remote changes and their opaque cursor envelope.</param>
    /// <param name="options">Apply policy, or the conservative defaults.</param>
    /// <param name="cancellationToken">
    /// Token observed between durable entries and before native namespace mutations. A native call
    /// that has already started cannot be interrupted.
    /// </param>
    /// <returns>
    /// Per-entry outcomes and the cursor after the highest durably completed entry. Entries after
    /// a failure or the per-call limit are reported as not processed.
    /// </returns>
    /// <exception cref="ArgumentNullException">The batch is null.</exception>
    /// <exception cref="ArgumentException">The batch conflicts with existing durable progress.</exception>
    /// <exception cref="InvalidDataException">
    /// A retained file-move preparation is corrupt or belongs to another policy, store, or root.
    /// </exception>
    /// <exception cref="InvalidOperationException">The file system has not started.</exception>
    /// <exception cref="CloudFilesException">Windows rejects a requested namespace mutation.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was observed at a safe boundary.</exception>
    /// <remarks>
    /// Remote content bytes never enter this method. File upserts update placeholder length,
    /// metadata, identity, and revision while leaving hydration to the configured provider. A
    /// durable batch record is written after each entry, so a process restart resumes from the
    /// last committed entry without publishing an unsafe cursor.
    /// File moves retain versioned original object, ancestry, batch, and apply-policy evidence
    /// before native mutation. Replay authenticates that evidence before checking previous
    /// revisions, including when both revisions are equal. Successful recovery reports
    /// <see cref="CloudRemoteApplyEntryStatus.Applied"/> after durable outcome publication;
    /// completed batch prefixes report <see cref="CloudRemoteApplyEntryStatus.AlreadyApplied"/>.
    /// Changed objects, ancestry, or local work leave the entry failed and retryable with its
    /// preparation retained. Mismatched policy or preparation is rejected before progress changes.
    /// Storage failures during preparation reads or outcome publication propagate to the
    /// caller; replay rereads durable state, including when a commit completed before throwing.
    /// Cancellation after native success finishes item projection without cancellation and may
    /// leave outcome publication for replay. This protocol covers file moves submitted through
    /// this method; it cannot authenticate historical file moves without original evidence or
    /// settle a directory move's missing remote-batch outcome. Use one active file-system owner
    /// per root/store; calls on that owner serialize and disposal drains admitted work.
    /// </remarks>
    public async ValueTask<CloudRemoteApplyResult> ApplyRemoteChangesAsync(
        CloudRemoteChangeBatch batch,
        CloudRemoteApplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        EnsureStarted();
        await _remoteApplyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Keep one admitted operation for the complete batch. This covers the head/tail
            // journal transactions as well as every native entry mutation, so DisposeAsync cannot
            // release the state store between entries. Establish the nested-operation context in
            // this caller frame after the asynchronous acquisition has completed.
            using CloudFileSystemOperationLease operation = await AcquireOperationAsync(
                    [CloudItemOperationScope.Subtree(SyncRootPath)],
                    cancellationToken)
                .ConfigureAwait(false);
            operation.EstablishContext();
            return await ApplyRemoteChangesCoreAsync(batch, options, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _remoteApplyGate.Release();
        }
    }

    private async ValueTask<CloudRemoteApplyResult> ApplyRemoteChangesCoreAsync(
        CloudRemoteChangeBatch batch,
        CloudRemoteApplyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        CloudRemoteApplyOptions selectedOptions = options ?? CloudRemoteApplyOptions.Default;
        selectedOptions.Validate();
        EnsureStarted();

        CloudRemoteBatchState progress = await ReadOrInitializeRemoteBatchAsync(
                batch,
                selectedOptions,
                cancellationToken)
            .ConfigureAwait(false);
        List<CloudRemoteApplyEntryResult> results = [];
        List<Guid> conflictIds = [];
        for (int index = 0; index < progress.AppliedEntryCount; index++)
        {
            results.Add(new CloudRemoteApplyEntryResult(
                batch.Changes[index].ChangeId,
                CloudRemoteApplyEntryStatus.AlreadyApplied));
        }

        if (progress.Status is CloudRemoteBatchStatus.Applied)
        {
            for (int index = progress.AppliedEntryCount; index < batch.Changes.Count; index++)
            {
                results.Add(new CloudRemoteApplyEntryResult(
                    batch.Changes[index].ChangeId,
                    CloudRemoteApplyEntryStatus.AlreadyApplied));
            }

            return CreateRemoteApplyResult(progress, results, requiresRetry: false, conflictIds);
        }

        int attempted = 0;
        while (progress.AppliedEntryCount < batch.Changes.Count &&
               attempted < selectedOptions.MaximumEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = progress.AppliedEntryCount;
            CloudRemoteChange change = batch.Changes[index];
            RemoteChangeRequest request = new(batch, index, selectedOptions);
            // Reject a mismatched pending preparation before changing any batch progress.
            RemoteMoveIntent? pendingMove = change.Kind == CloudRemoteChangeKind.Move && change.ItemKind == CloudItemKind.File
                ? await ReadRemoteMoveIntentAsync(request, cancellationToken).ConfigureAwait(false)
                : null;
            RemoteEntryOutcome outcome;
            try
            {
                outcome = pendingMove is not null
                    ? await CompleteRemoteFileMoveAsync(change, pendingMove, cancellationToken).ConfigureAwait(false)
                    : await ApplyRemoteEntryAsync(
                        change,
                        selectedOptions,
                        cancellationToken,
                        request)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                try
                {
                    progress = await PersistRemoteBatchFailureAsync(
                            batch,
                            progress,
                            exception,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception persistenceError)
                {
                    throw new AggregateException("Remote entry application and failure-state persistence both failed; retain the original batch.",
                        exception, persistenceError);
                }
                results.Add(new CloudRemoteApplyEntryResult(
                    change.ChangeId,
                    CloudRemoteApplyEntryStatus.Failed,
                    error: exception));
                for (int remaining = index + 1; remaining < batch.Changes.Count; remaining++)
                {
                    results.Add(new CloudRemoteApplyEntryResult(
                        batch.Changes[remaining].ChangeId,
                        CloudRemoteApplyEntryStatus.NotProcessed));
                }

                return CreateRemoteApplyResult(progress, results, requiresRetry: true, conflictIds);
            }

            Guid? conflictId = null;
            if (outcome.Conflict is not null)
            {
                conflictId = Guid.NewGuid();
                conflictIds.Add(conflictId.Value);
            }

            ReadOnlyMemory<byte> cursor = change.CursorAfter.IsEmpty
                ? progress.Cursor
                : change.CursorAfter;
            CloudRemoteBatchStatus status = index == batch.Changes.Count - 1
                ? CloudRemoteBatchStatus.Applied
                : CloudRemoteBatchStatus.Applying;
            if (status is CloudRemoteBatchStatus.Applied && change.CursorAfter.IsEmpty)
            {
                cursor = batch.FinalCursor;
            }

            progress = await PersistRemoteBatchOutcomeAsync(
                    batch,
                    progress,
                    index + 1,
                    change,
                    outcome,
                    conflictId,
                    cursor,
                    status,
                    cancellationToken)
                .ConfigureAwait(false);
            results.Add(new CloudRemoteApplyEntryResult(
                change.ChangeId,
                outcome.Status,
                outcome.Conflict));
            attempted++;
        }

        bool requiresRetry = progress.Status is not CloudRemoteBatchStatus.Applied;
        if (requiresRetry)
        {
            for (int remaining = progress.AppliedEntryCount; remaining < batch.Changes.Count; remaining++)
            {
                results.Add(new CloudRemoteApplyEntryResult(
                    batch.Changes[remaining].ChangeId,
                    CloudRemoteApplyEntryStatus.NotProcessed));
            }
        }

        return CreateRemoteApplyResult(progress, results, requiresRetry, conflictIds);
    }

    /// <summary>
    /// Resolves one durable remote conflict after an earlier batch recorded it safely.
    /// </summary>
    /// <param name="conflictId">Stable identifier returned by a remote apply result.</param>
    /// <param name="resolution">Explicit keep-local, keep-remote, keep-both, or defer decision.</param>
    /// <param name="cancellationToken">Token observed before durable and native work.</param>
    /// <returns>The resulting entry status. A deferred decision leaves the conflict durable.</returns>
    /// <exception cref="KeyNotFoundException">The conflict no longer exists.</exception>
    /// <exception cref="InvalidDataException">The durable conflict envelope is corrupt.</exception>
    public async ValueTask<CloudRemoteApplyEntryResult> ResolveRemoteConflictAsync(
        Guid conflictId,
        CloudRemoteConflictResolution resolution,
        CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        await _remoteApplyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using CloudFileSystemOperationLease operation = await AcquireOperationAsync(
                    [CloudItemOperationScope.Subtree(SyncRootPath)],
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            operation.EstablishContext();
            return await ResolveRemoteConflictCoreAsync(conflictId, resolution, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _remoteApplyGate.Release();
        }
    }

    private async ValueTask<CloudRemoteApplyEntryResult> ResolveRemoteConflictCoreAsync(
        Guid conflictId,
        CloudRemoteConflictResolution resolution,
        CancellationToken cancellationToken)
    {
        EnsureStarted();
        if (conflictId == Guid.Empty)
        {
            throw new ArgumentException("The conflict identifier cannot be empty.", nameof(conflictId));
        }

        ArgumentNullException.ThrowIfNull(resolution);
        (CloudRemoteConflict conflict, CloudConflictState durableState) =
            await ReadRemoteConflictAsync(conflictId, cancellationToken).ConfigureAwait(false);
        if (resolution.Decision is CloudRemoteConflictDecision.Defer or
            CloudRemoteConflictDecision.KeepLocal)
        {
            return new CloudRemoteApplyEntryResult(
                conflict.Change.ChangeId,
                CloudRemoteApplyEntryStatus.Conflict,
                conflict);
        }

        if (resolution.Decision is CloudRemoteConflictDecision.KeepBoth &&
            (conflict.Change.Kind is not CloudRemoteChangeKind.FileUpsert and
             not CloudRemoteChangeKind.DirectoryUpsert))
        {
            return new CloudRemoteApplyEntryResult(
                conflict.Change.ChangeId,
                CloudRemoteApplyEntryStatus.Conflict,
                conflict);
        }

        CloudRemoteChange retry = resolution.Decision is CloudRemoteConflictDecision.KeepBoth
            ? CreateConflictCopyChange(conflict.Change, resolution.KeepBothRelativePath!)
            : CreateForceApplyChange(conflict.Change);
        CloudRemoteApplyOptions resolutionOptions = CloudRemoteApplyOptions.Default with
        {
            PreserveUnsynchronizedLocalContent = false,
            ConflictResolver = null,
        };
        RemoteChangeRequest? deleteRequest = retry.Kind == CloudRemoteChangeKind.Delete
            ? new(new CloudRemoteChangeBatch("cfsharp.resolve-delete/" + conflictId.ToString("N"), ReadOnlyMemory<byte>.Empty, [retry], ReadOnlyMemory<byte>.Empty), 0, resolutionOptions)
            : null;
        RemoteEntryOutcome outcome = await ApplyRemoteEntryCoreAsync(
                retry,
                resolutionOptions,
                cancellationToken,
                deleteRequest)
            .ConfigureAwait(false);
        if (outcome.Status is not CloudRemoteApplyEntryStatus.Applied)
        {
            return new CloudRemoteApplyEntryResult(
                conflict.Change.ChangeId,
                outcome.Status,
                outcome.Conflict);
        }

        await using ICloudStateTransaction transaction = await (_stateStore ?? throw new InvalidOperationException(
                "The cloud file system has no open state store.")).BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        using RemoteNamespaceParentLease? deleteParents = deleteRequest is not null
            ? HoldRemoteNamespaceParents(ToFullPath(retry.RelativePath), ToFullPath(retry.RelativePath))
            : null;
        if (deleteRequest is not null && deleteParents is not null)
        {
            await ValidateRemoteDeleteOutcomeAsync(transaction, deleteRequest.Batch, 0, deleteParents.Parents, cancellationToken)
                .ConfigureAwait(false);
        }
        await transaction.Conflicts.RemoveAsync(durableState.ConflictId, cancellationToken)
            .ConfigureAwait(false);
        if (deleteRequest is not null)
        {
            await transaction.Checkpoints.RemoveAsync(RemoteDeleteIntent.Name(deleteRequest.Batch.BatchId, 0), cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new CloudRemoteApplyEntryResult(
            conflict.Change.ChangeId,
            CloudRemoteApplyEntryStatus.Applied);
    }

    private async ValueTask<CloudRemoteBatchState> ReadOrInitializeRemoteBatchAsync(
        CloudRemoteChangeBatch batch,
        CloudRemoteApplyOptions options,
        CancellationToken cancellationToken)
    {
        await using ICloudStateTransaction transaction = await (_stateStore ?? throw new InvalidOperationException(
                "The cloud file system has no open state store.")).BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        CloudRemoteBatchState? existing = await transaction.RemoteBatches
            .GetAsync(batch.BatchId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.TotalEntryCount != batch.Changes.Count ||
                (!existing.Fingerprint.IsEmpty &&
                 !existing.Fingerprint.Span.SequenceEqual(batch.Fingerprint.Span)))
            {
                throw new ArgumentException(
                    $"Remote batch '{batch.BatchId}' does not match its durable fingerprint.",
                    nameof(batch));
            }

            if (existing.AppliedEntryCount > batch.Changes.Count)
            {
                throw new InvalidOperationException(
                    $"Remote batch '{batch.BatchId}' has invalid durable progress.");
            }

            if (existing.AppliedEntryCount < batch.Changes.Count &&
                batch.Changes[existing.AppliedEntryCount] is { Kind: CloudRemoteChangeKind.Move, ItemKind: CloudItemKind.File })
            {
                await ReadRemoteMoveIntentCoreAsync(transaction, new(batch, existing.AppliedEntryCount, options), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (existing.AppliedEntryCount < batch.Changes.Count &&
                batch.Changes[existing.AppliedEntryCount].Kind == CloudRemoteChangeKind.Delete)
            {
                await ReadRemoteDeleteIntentCoreAsync(transaction, new(batch, existing.AppliedEntryCount, options), cancellationToken)
                    .ConfigureAwait(false);
            }

            if (existing.Status is CloudRemoteBatchStatus.Applied)
            {
                if (existing.AppliedEntryCount != batch.Changes.Count ||
                    (batch.Changes.Count > 0 &&
                     !string.Equals(
                         existing.LastAppliedChangeId,
                         batch.Changes[^1].ChangeId,
                         StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"Remote batch '{batch.BatchId}' is marked applied without a complete terminal entry.");
                }
            }
            else if (existing.AppliedEntryCount == batch.Changes.Count && batch.Changes.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Remote batch '{batch.BatchId}' has complete progress but is not marked applied.");
            }

            CloudRemoteBatchStatus status = existing.Status is CloudRemoteBatchStatus.Applied
                ? CloudRemoteBatchStatus.Applied
                : CloudRemoteBatchStatus.Applying;
            CloudRemoteBatchState normalized = new(
                existing.BatchId,
                existing.Cursor.Span,
                existing.AppliedEntryCount,
                existing.TotalEntryCount,
                status,
                existing.Payload.Span,
                DateTimeOffset.UtcNow,
                batch.Fingerprint,
                existing.LastAppliedChangeId);
            if (existing.Status is not CloudRemoteBatchStatus.Applied ||
                existing.Fingerprint.IsEmpty)
            {
                await transaction.RemoteBatches.UpsertAsync(normalized, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            return normalized;
        }

        CloudRemoteBatchStatus initialStatus = batch.Changes.Count == 0
            ? CloudRemoteBatchStatus.Applied
            : CloudRemoteBatchStatus.Applying;
        CloudRemoteBatchState initial = new(
            batch.BatchId,
            (initialStatus is CloudRemoteBatchStatus.Applied
                ? batch.FinalCursor
                : batch.InitialCursor).Span,
            0,
            batch.Changes.Count,
            initialStatus,
            [],
            DateTimeOffset.UtcNow,
            batch.Fingerprint);
        await transaction.RemoteBatches.UpsertAsync(initial, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return initial;
    }

    private async ValueTask<RemoteEntryOutcome> ApplyRemoteEntryAsync(
        CloudRemoteChange change,
        CloudRemoteApplyOptions options,
        CancellationToken cancellationToken,
        RemoteChangeRequest? request = null)
    {
        RemoteEntryOutcome outcome = await ApplyRemoteEntryCoreAsync(change, options, cancellationToken, request)
            .ConfigureAwait(false);
        if (outcome.Conflict is null || options.ConflictResolver is null)
        {
            return outcome;
        }

        CloudRemoteConflictResolution resolution = await options.ConflictResolver
            .ResolveAsync(outcome.Conflict, cancellationToken)
            .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(resolution);
        if (resolution.Decision is CloudRemoteConflictDecision.Defer or
            CloudRemoteConflictDecision.KeepLocal)
        {
            return outcome;
        }

        if (resolution.Decision is CloudRemoteConflictDecision.KeepBoth &&
            (change.Kind is not CloudRemoteChangeKind.FileUpsert and
             not CloudRemoteChangeKind.DirectoryUpsert))
        {
            return outcome;
        }

        CloudRemoteChange retry = resolution.Decision is CloudRemoteConflictDecision.KeepBoth
            ? CreateConflictCopyChange(change, resolution.KeepBothRelativePath!)
            : CreateForceApplyChange(change);
        return await ApplyRemoteEntryCoreAsync(
                retry,
                options with
                {
                    PreserveUnsynchronizedLocalContent = false,
                    ConflictResolver = null,
                },
                cancellationToken,
                request)
            .ConfigureAwait(false);
    }

    private async ValueTask<RemoteEntryOutcome> ApplyRemoteEntryCoreAsync(
        CloudRemoteChange change,
        CloudRemoteApplyOptions options,
        CancellationToken cancellationToken,
        RemoteChangeRequest? request = null)
    {
        if (change.Kind == CloudRemoteChangeKind.Delete && request is not null)
        {
            RemoteDeleteIntent? pending = await ReadRemoteDeleteIntentAsync(request, cancellationToken).ConfigureAwait(false);
            if (pending is not null)
            {
                return await CompleteRemoteDeleteAsync(request, pending, cancellationToken).ConfigureAwait(false);
            }
        }

        if (change.Kind is CloudRemoteChangeKind.FileUpsert or CloudRemoteChangeKind.DirectoryUpsert)
        {
            RemoteCreationIntent? pending = await ReadRemoteCreationIntentAsync(change, cancellationToken)
                .ConfigureAwait(false);
            if (pending is not null)
            {
                return await CreateRemotePlaceholderCoordinatedAsync(change, pending.Identity.ItemId,
                    options, pending, cancellationToken).ConfigureAwait(false);
            }
        }

        RemoteEntryContext context = await ReadRemoteEntryContextAsync(change, cancellationToken)
            .ConfigureAwait(false);
        if (change.Kind is CloudRemoteChangeKind.Move)
        {
            return await ApplyRemoteMoveAsync(change, context, options, request, cancellationToken)
                .ConfigureAwait(false);
        }

        if (change.Kind is CloudRemoteChangeKind.Delete)
        {
            return await ApplyRemoteDeleteAsync(change, context, options, request!, cancellationToken)
                .ConfigureAwait(false);
        }

        bool pathExists = File.Exists(ToFullPath(change.RelativePath)) ||
            Directory.Exists(ToFullPath(change.RelativePath));

        bool pathBelongsToAnotherItem = context.ByPath is not null &&
            !context.ByPath.IsTombstone &&
            (context.ByRemoteId is null || context.ByRemoteId.ItemId != context.ByPath.ItemId) &&
            (change.ItemId is null || context.ByItemId is null ||
             context.ByItemId.ItemId != context.ByPath.ItemId);
        if (pathBelongsToAnotherItem ||
            (context.ByPath is not null &&
             context.LocalState is not null &&
             context.ByPath.ItemId != context.LocalState.ItemId &&
             !context.ByPath.IsTombstone))
        {
            return RemoteEntryOutcome.ConflictResult(CreateConflict(
                change,
                context.LocalState,
                CloudRemoteConflictReason.PathCollision));
        }

        if (pathExists && context.LocalState is null && context.ByPath is null)
        {
            return RemoteEntryOutcome.ConflictResult(CreateConflict(
                change,
                localState: null,
                CloudRemoteConflictReason.PathCollision));
        }

        if (change.PreviousRemoteRevision is not null &&
            (context.LocalState is null || !string.Equals(
                context.LocalState.RemoteRevision,
                change.PreviousRemoteRevision,
                StringComparison.Ordinal)))
        {
            return RemoteEntryOutcome.ConflictResult(CreateConflict(
                change,
                context.LocalState,
                CloudRemoteConflictReason.StaleRemoteRevision));
        }

        bool upsert = change.Kind is CloudRemoteChangeKind.FileUpsert or
            CloudRemoteChangeKind.DirectoryUpsert;
        if (options.PreserveUnsynchronizedLocalContent &&
            context.LocalState is not null && context.LocalOperations.Count != 0)
        {
            return RemoteEntryOutcome.ConflictResult(CreateConflict(
                change,
                context.LocalState,
                change.Kind is CloudRemoteChangeKind.MetadataUpdate
                    ? CloudRemoteConflictReason.Metadata
                    : CloudRemoteConflictReason.Content));
        }

        if (!upsert && context.LocalState is null)
        {
            return RemoteEntryOutcome.ConflictResult(CreateConflict(
                change,
                localState: null,
                CloudRemoteConflictReason.MissingItem));
        }

        if (context.LocalState is not null &&
            !string.Equals(
                context.LocalState.RelativePath,
                change.RelativePath,
                StringComparison.OrdinalIgnoreCase))
        {
            return RemoteEntryOutcome.ConflictResult(CreateConflict(
                change,
                context.LocalState,
                CloudRemoteConflictReason.Move));
        }

        Guid itemId = context.LocalState?.ItemId ?? change.ItemId ?? Guid.NewGuid();
        bool localExists = change.ItemKind is CloudItemKind.Directory
            ? Directory.Exists(ToFullPath(change.RelativePath))
            : File.Exists(ToFullPath(change.RelativePath));
        if (context.LocalState is not null && localExists)
        {
            CloudItem item = CreateItemReference(change.RelativePath, change.ItemKind);
            CloudItemSnapshot snapshot = await item.InspectAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!snapshot.IsPlaceholder)
            {
                return RemoteEntryOutcome.ConflictResult(CreateConflict(
                    change,
                    context.LocalState,
                    CloudRemoteConflictReason.Content));
            }

            if (options.PreserveUnsynchronizedLocalContent &&
                snapshot.SynchronizationState is CloudSynchronizationState.NotInSync)
            {
                return RemoteEntryOutcome.ConflictResult(CreateConflict(
                    change,
                    context.LocalState,
                    change.Kind is CloudRemoteChangeKind.MetadataUpdate
                        ? CloudRemoteConflictReason.Metadata
                        : CloudRemoteConflictReason.Content));
            }
        }

        CloudStateOperationKind suppressionKind = context.LocalState is null || !localExists
            ? CloudStateOperationKind.Create
            : change.Kind is CloudRemoteChangeKind.MetadataUpdate
                ? CloudStateOperationKind.MetadataUpdate
                : CloudStateOperationKind.ContentUpdate;
        if (!upsert && (context.LocalState is null || !localExists))
        {
            return RemoteEntryOutcome.ConflictResult(CreateConflict(
                change,
                context.LocalState,
                CloudRemoteConflictReason.MissingItem));
        }

        if (upsert && (context.LocalState is null || !localExists))
        {
            return await CreateRemotePlaceholderCoordinatedAsync(change, itemId, options, pending: null,
                cancellationToken).ConfigureAwait(false);
        }

        // Register suppression only after all conflict checks that can return
        // without mutating the namespace. A conflict must not leave behind a
        // record that could consume an unrelated future local observation.
        Guid? suppressionId = null;
        try
        {
            if (options.SuppressLocalEcho)
            {
                suppressionId = await RegisterRemoteEchoSuppressionAsync(
                        change,
                        itemId,
                        suppressionKind,
                        options,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (context.LocalState is null || !localExists)
            {
                await CreateRemotePlaceholderAsync(change, itemId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await UpdateRemotePlaceholderAsync(change, context.LocalState!, options, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            if (suppressionId is Guid registeredSuppressionId)
            {
                await RemoveRemoteEchoSuppressionAsync(registeredSuppressionId).ConfigureAwait(false);
            }

            throw;
        }

        return RemoteEntryOutcome.AppliedResult;
    }

    private async ValueTask<RemoteEntryOutcome> ApplyRemoteMoveAsync(
        CloudRemoteChange change,
        RemoteEntryContext context,
        CloudRemoteApplyOptions options,
        RemoteChangeRequest? request,
        CancellationToken cancellationToken)
    {
        CloudItemState? localState = context.LocalState;
        if (localState is null)
        {
            return RemoteEntryOutcome.ConflictResult(CreateConflict(
                change,
                localState: null,
                CloudRemoteConflictReason.MissingItem));
        }

        if (change.PreviousRemoteRevision is not null && !string.Equals(
                localState.RemoteRevision,
                change.PreviousRemoteRevision,
                StringComparison.Ordinal))
        {
            return RemoteEntryOutcome.ConflictResult(CreateConflict(
                change,
                localState,
                CloudRemoteConflictReason.StaleRemoteRevision));
        }

        if (!string.Equals(
                localState.RelativePath,
                change.PreviousRelativePath,
                StringComparison.OrdinalIgnoreCase))
        {
            return RemoteEntryOutcome.ConflictResult(CreateConflict(
                change,
                localState,
                CloudRemoteConflictReason.Move));
        }

        if (options.PreserveUnsynchronizedLocalContent && context.LocalOperations.Count != 0)
        {
            return RemoteEntryOutcome.ConflictResult(CreateConflict(
                change,
                localState,
                CloudRemoteConflictReason.Move));
        }

        if (context.ByPath is not null &&
            context.ByPath.ItemId != localState.ItemId &&
            !context.ByPath.IsTombstone)
        {
            return RemoteEntryOutcome.ConflictResult(CreateConflict(
                change,
                localState,
                CloudRemoteConflictReason.PathCollision));
        }

        string sourceFullPath = ToFullPath(change.PreviousRelativePath!);
        bool sourceExists = change.ItemKind is CloudItemKind.Directory
            ? Directory.Exists(sourceFullPath)
            : File.Exists(sourceFullPath);
        if (!sourceExists)
        {
            return RemoteEntryOutcome.ConflictResult(CreateConflict(
                change,
                localState,
                localState.IsTombstone
                    ? CloudRemoteConflictReason.MissingItem
                    : CloudRemoteConflictReason.Move));
        }

        CloudItem source = CreateItemReference(change.PreviousRelativePath!, change.ItemKind);
        CloudItemSnapshot snapshot = await source.InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.IsPlaceholder ||
            (options.PreserveUnsynchronizedLocalContent &&
             snapshot.SynchronizationState is CloudSynchronizationState.NotInSync))
        {
            return RemoteEntryOutcome.ConflictResult(CreateConflict(
                change,
                localState,
                CloudRemoteConflictReason.Move));
        }

        string destinationParentPath = Path.GetDirectoryName(change.RelativePath) ?? string.Empty;
        CloudDirectory destination = GetDirectory(destinationParentPath);
        string destinationName = Path.GetFileName(change.RelativePath);
        if (request is not null && change.ItemKind == CloudItemKind.File)
        {
            RemoteMoveIntent intent = await PrepareRemoteFileMoveAsync(change, localState, options, request,
                cancellationToken).ConfigureAwait(false);
            return await CompleteRemoteFileMoveAsync(change, intent, cancellationToken).ConfigureAwait(false);
        }

        Guid? suppressionId = null;
        if (options.SuppressLocalEcho)
        {
            suppressionId = await RegisterRemoteEchoSuppressionAsync(
                    change,
                    localState.ItemId,
                    CloudStateOperationKind.Move,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            await source.MoveToAsync(
                    destination,
                    destinationName,
                    CloudMoveOptions.Default,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            if (suppressionId is Guid registeredSuppressionId)
            {
                await RemoveRemoteEchoSuppressionAsync(registeredSuppressionId).ConfigureAwait(false);
            }

            throw;
        }
        await UpdateRemoteItemRevisionAsync(
                localState.ItemId,
                change.RemoteId,
                change.RelativePath,
                change.ItemKind,
                change.RemoteRevision,
                isTombstone: false,
                cancellationToken)
            .ConfigureAwait(false);
        return RemoteEntryOutcome.AppliedResult;
    }

    private async ValueTask<RemoteEntryContext> ReadRemoteEntryContextAsync(
        CloudRemoteChange change,
        CancellationToken cancellationToken)
    {
        await using ICloudStateTransaction transaction = await (_stateStore ?? throw new InvalidOperationException(
                "The cloud file system has no open state store.")).BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        CloudItemState? byRemoteId = await transaction.Items
            .GetByRemoteIdAsync(change.RemoteId, cancellationToken)
            .ConfigureAwait(false);
        CloudItemState? byItemId = change.ItemId is Guid itemId
            ? await transaction.Items.GetByItemIdAsync(itemId, cancellationToken).ConfigureAwait(false)
            : null;
        CloudItemState? byPath = await transaction.Items
            .GetByRelativePathAsync(change.RelativePath, cancellationToken)
            .ConfigureAwait(false);
        CloudItemState? byPreviousPath = change.PreviousRelativePath is null
            ? null
            : await transaction.Items
                .GetByRelativePathAsync(change.PreviousRelativePath, cancellationToken)
                .ConfigureAwait(false);
        CloudItemState? localState = byRemoteId ?? byItemId ?? byPreviousPath ?? byPath;
        IReadOnlyList<CloudOperationJournalEntry> operations = localState is null
            ? []
            : await transaction.Operations.ListByItemIdAsync(
                    localState.ItemId,
                    maximumCount: int.MaxValue,
                    cancellationToken)
                .ConfigureAwait(false);
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return new RemoteEntryContext(
            localState,
            byRemoteId,
            byItemId,
            byPath,
            byPreviousPath,
            operations);
    }

    private async ValueTask<Guid> RegisterRemoteEchoSuppressionAsync(
        CloudRemoteChange change,
        Guid itemId,
        CloudStateOperationKind kind,
        CloudRemoteApplyOptions options,
        CancellationToken cancellationToken)
    {
        Guid suppressionId = Guid.NewGuid();
        await using ICloudStateTransaction transaction = await (_stateStore ?? throw new InvalidOperationException(
                "The cloud file system has no open state store.")).BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.EchoSuppressions.UpsertAsync(
            new CloudEchoSuppressionState(
                suppressionId,
                itemId,
                kind,
                change.RelativePath,
                Encoding.UTF8.GetBytes($"remote-change/v1|{change.ChangeId}"),
                DateTimeOffset.UtcNow + options.EchoSuppressionLifetime,
                change.PreviousRelativePath,
                options.EchoSuppressionObservationCount),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return suppressionId;
    }

    private async ValueTask RemoveRemoteEchoSuppressionAsync(Guid suppressionId)
    {
        try
        {
            await using ICloudStateTransaction transaction = await (_stateStore ?? throw new InvalidOperationException(
                    "The cloud file system has no open state store.")).BeginTransactionAsync(CancellationToken.None)
                .ConfigureAwait(false);
            await transaction.EchoSuppressions.RemoveAsync(suppressionId, CancellationToken.None)
                .ConfigureAwait(false);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original namespace failure; a later expiry cleanup can remove a
            // record if the state store is temporarily unavailable during rollback.
        }
    }

    private async ValueTask CreateRemotePlaceholderAsync(
        CloudRemoteChange change,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        CloudPlaceholderIdentity identity = new(itemId, change.RemoteId, change.RemoteRevision);
        string parentPath = Path.GetDirectoryName(change.RelativePath) ?? string.Empty;
        string name = Path.GetFileName(change.RelativePath);
        CloudDirectory parent = GetDirectory(parentPath);
        if (change.Kind is CloudRemoteChangeKind.FileUpsert)
        {
            CloudFilePlaceholderSpec specification = CloudFilePlaceholderSpec
                .CreateBuilder(name, identity, change.Length!.Value)
                .WithMetadata(change.Metadata!)
                .WithInSyncState(true)
                .Build();
            await parent.CreatePlaceholderAsync(specification, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        CloudDirectoryPlaceholderSpec directory = CloudDirectoryPlaceholderSpec
            .CreateBuilder(name, identity)
            .WithMetadata(change.Metadata!)
            .WithInSyncState(true)
            .Build();
        await parent.CreatePlaceholderAsync(directory, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask UpdateRemotePlaceholderAsync(
        CloudRemoteChange change,
        CloudItemState localState,
        CloudRemoteApplyOptions options,
        CancellationToken cancellationToken)
    {
        CloudPlaceholderIdentity identity = new(
            localState.ItemId,
            change.RemoteId,
            change.RemoteRevision);
        CloudPlaceholderPatch.Builder builder = CloudPlaceholderPatch.CreateBuilder()
            .WithIdentity(identity)
            .WithMetadata(change.Metadata!)
            .WithInSyncState(true);
        if (change.Kind is CloudRemoteChangeKind.FileUpsert)
        {
            // Changing the identity alone leaves hydrated ranges readable as the old
            // revision. Invalidate them atomically with the new identity and metadata.
            // Windows rejects dehydration when pin/content policy forbids it; do not
            // silently unpin the file or publish a revision with stale cached bytes.
            builder.WithFileSize(change.Length!.Value);
            if (change.Length.Value != 0)
            {
                builder.WithFullDehydration();
            }
            else
            {
                // The default native metadata filter skips zero-valued fields on some
                // supported builds. Pass through the explicit zero length to truncate.
                builder.WithMetadata(change.Metadata!, CloudMetadataWriteMode.PassThrough);
            }
        }

        if (options.PreserveUnsynchronizedLocalContent)
        {
            builder.WithInSyncVerification();
        }

        CloudItem item = CreateItemReference(change.RelativePath, change.ItemKind);
        await item.UpdatePlaceholderAsync(builder.Build(), cancellationToken).ConfigureAwait(false);
    }

    private string ToFullPath(string relativePath) =>
        CloudItemPathResolver.Resolve(SyncRootPath, relativePath, allowRoot: false).FullPath;

    private async ValueTask UpdateRemoteItemRevisionAsync(
        Guid itemId,
        string remoteId,
        string relativePath,
        CloudItemKind kind,
        string revision,
        bool isTombstone,
        CancellationToken cancellationToken)
    {
        await using ICloudStateTransaction transaction = await (_stateStore ?? throw new InvalidOperationException(
                "The cloud file system has no open state store.")).BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        CloudItemState? existing = await transaction.Items
            .GetByItemIdAsync(itemId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            await transaction.Items.UpsertAsync(
                new CloudItemState(
                    existing.ItemId,
                    remoteId,
                    relativePath,
                    kind,
                    revision,
                    existing.LocalFileId,
                    isTombstone,
                    DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static CloudRemoteConflict CreateConflict(
        CloudRemoteChange change,
        CloudItemState? localState,
        CloudRemoteConflictReason reason) =>
        new(change, localState, reason, DateTimeOffset.UtcNow);

    private static CloudRemoteChange CreateForceApplyChange(CloudRemoteChange change) =>
        new(
            change.ChangeId,
            change.Kind,
            change.RemoteId,
            change.RemoteRevision,
            change.ItemKind,
            change.RelativePath,
            change.ItemId,
            previousRemoteRevision: null,
            change.PreviousRelativePath,
            change.Length,
            change.Metadata,
            change.CursorAfter);

    private static CloudRemoteChange CreateConflictCopyChange(
        CloudRemoteChange change,
        string relativePath) =>
        new(
            change.ChangeId + ":keep-both",
            change.Kind,
            "cfsharp-conflict-" + Guid.NewGuid().ToString("N"),
            change.RemoteRevision,
            change.ItemKind,
            relativePath,
            Guid.NewGuid(),
            previousRemoteRevision: null,
            previousRelativePath: null,
            change.Length,
            change.Metadata,
            change.CursorAfter);

    private async ValueTask<CloudRemoteBatchState> PersistRemoteBatchOutcomeAsync(
        CloudRemoteChangeBatch batch,
        CloudRemoteBatchState previous,
        int appliedEntryCount,
        CloudRemoteChange change,
        RemoteEntryOutcome outcome,
        Guid? conflictId,
        ReadOnlyMemory<byte> cursor,
        CloudRemoteBatchStatus status,
        CancellationToken cancellationToken)
    {
        if (status is CloudRemoteBatchStatus.Applied &&
            (appliedEntryCount != batch.Changes.Count ||
             (batch.Changes.Count > 0 &&
              !string.Equals(change.ChangeId, batch.Changes[^1].ChangeId, StringComparison.Ordinal))))
        {
            throw new InvalidOperationException(
                $"Remote batch '{batch.BatchId}' cannot publish an incomplete applied state.");
        }

        await using ICloudStateTransaction transaction = await (_stateStore ?? throw new InvalidOperationException(
                "The cloud file system has no open state store.")).BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        using RemoteNamespaceParentLease? deleteParents = change.Kind == CloudRemoteChangeKind.Delete && outcome.Conflict is null
            ? HoldRemoteNamespaceParents(ToFullPath(change.RelativePath), ToFullPath(change.RelativePath))
            : null;
        if (deleteParents is not null)
        {
            await ValidateRemoteDeleteOutcomeAsync(transaction, batch, appliedEntryCount - 1, deleteParents.Parents, cancellationToken)
                .ConfigureAwait(false);
        }
        if (outcome.Conflict is not null)
        {
            Guid id = conflictId ?? Guid.NewGuid();
            await transaction.Conflicts.UpsertAsync(
                new CloudConflictState(
                    id,
                    outcome.Conflict.LocalState?.ItemId,
                    ToDurableConflictKind(outcome.Conflict.Reason),
                    EncodeConflict(outcome.Conflict),
                    outcome.Conflict.DetectedAt),
                cancellationToken).ConfigureAwait(false);
        }

        CloudRemoteBatchState next = new(
            batch.BatchId,
            cursor.Span,
            appliedEntryCount,
            batch.Changes.Count,
            status,
            previous.Payload.Span,
            DateTimeOffset.UtcNow,
            batch.Fingerprint,
            change.ChangeId);
        await transaction.RemoteBatches.UpsertAsync(next, cancellationToken).ConfigureAwait(false);
        await transaction.Checkpoints.RemoveAsync(RemoteCreationIntent.Name(change), cancellationToken)
            .ConfigureAwait(false);
        await transaction.Checkpoints.RemoveAsync(RemoteMoveIntent.Name(batch.BatchId, appliedEntryCount - 1), cancellationToken)
            .ConfigureAwait(false);
        await transaction.Checkpoints.RemoveAsync(RemoteDeleteIntent.Name(batch.BatchId, appliedEntryCount - 1), cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return next;
    }

    private async ValueTask<CloudRemoteBatchState> PersistRemoteBatchFailureAsync(
        CloudRemoteChangeBatch batch,
        CloudRemoteBatchState previous,
        Exception exception,
        CancellationToken cancellationToken)
    {
        await using ICloudStateTransaction transaction = await (_stateStore ?? throw new InvalidOperationException(
                "The cloud file system has no open state store.")).BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        CloudRemoteBatchState next = new(
            batch.BatchId,
            previous.Cursor.Span,
            previous.AppliedEntryCount,
            batch.Changes.Count,
            CloudRemoteBatchStatus.Failed,
            Encoding.UTF8.GetBytes($"failure|{exception.GetType().FullName}"),
            DateTimeOffset.UtcNow,
            batch.Fingerprint,
            previous.LastAppliedChangeId);
        await transaction.RemoteBatches.UpsertAsync(next, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return next;
    }

    private static CloudRemoteApplyResult CreateRemoteApplyResult(
        CloudRemoteBatchState progress,
        IReadOnlyList<CloudRemoteApplyEntryResult> entries,
        bool requiresRetry,
        IReadOnlyList<Guid> conflictIds) =>
        new(
            entries,
            progress.Status,
            progress.Cursor.Span,
            requiresRetry,
            progress.AppliedEntryCount,
            conflictIds);

    private async ValueTask<(CloudRemoteConflict Conflict, CloudConflictState DurableState)>
        ReadRemoteConflictAsync(Guid conflictId, CancellationToken cancellationToken)
    {
        await using ICloudStateTransaction transaction = await (_stateStore ?? throw new InvalidOperationException(
                "The cloud file system has no open state store.")).BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        CloudConflictState? durableState = await transaction.Conflicts
            .GetAsync(conflictId, cancellationToken)
            .ConfigureAwait(false);
        if (durableState is null)
        {
            throw new KeyNotFoundException($"Remote conflict '{conflictId}' was not found.");
        }

        CloudRemoteConflict conflict = await DecodeRemoteConflictAsync(
            durableState, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return (conflict, durableState);
    }

    private static async ValueTask<CloudRemoteConflict> DecodeRemoteConflictAsync(
        CloudConflictState durableState,
        ICloudStateTransaction transaction,
        CancellationToken cancellationToken)
    {
        CloudRemoteChange change;
        CloudRemoteConflictReason reason;
        try
        {
            RemoteConflictEnvelope envelope = JsonSerializer.Deserialize(
                    durableState.Payload.Span,
                    RemoteChangesJsonContext.Default.RemoteConflictEnvelope)
                ?? throw new InvalidDataException("The durable remote conflict envelope is empty.");
            if (envelope.Version != RemoteConflictEnvelopeVersion)
            {
                throw new InvalidDataException(
                    $"The durable remote conflict envelope version '{envelope.Version}' is unsupported.");
            }

            if (envelope.CursorAfter is null)
            {
                throw new InvalidDataException("The durable remote conflict cursor is missing.");
            }

            reason = (CloudRemoteConflictReason)envelope.Reason;
            if (!Enum.IsDefined(reason))
            {
                throw new InvalidDataException("The durable remote conflict reason is invalid.");
            }

            change = new CloudRemoteChange(
                envelope.ChangeId ?? throw new InvalidDataException("The conflict change id is missing."),
                (CloudRemoteChangeKind)envelope.Kind,
                envelope.RemoteId ?? throw new InvalidDataException("The conflict remote id is missing."),
                envelope.RemoteRevision ?? throw new InvalidDataException(
                    "The conflict remote revision is missing."),
                (CloudItemKind)envelope.ItemKind,
                envelope.RelativePath ?? throw new InvalidDataException(
                    "The conflict relative path is missing."),
                envelope.ItemId,
                envelope.PreviousRemoteRevision,
                envelope.PreviousRelativePath,
                envelope.Length,
                DecodeMetadata(envelope.Metadata),
                envelope.CursorAfter);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The durable remote conflict envelope is invalid JSON.", exception);
        }
        catch (NotSupportedException exception)
        {
            throw new InvalidDataException("The durable remote conflict envelope is unsupported.", exception);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The durable remote conflict envelope is invalid.", exception);
        }

        CloudItemState? localState = durableState.ItemId is Guid itemId
            ? await transaction.Items.GetByItemIdAsync(itemId, cancellationToken).ConfigureAwait(false)
            : null;
        CloudRemoteConflict conflict = new(change, localState, reason, durableState.CreatedAt);
        return conflict;
    }

    private static CloudPlaceholderMetadata? DecodeMetadata(RemoteMetadataEnvelope? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        CloudItemKind kind = (CloudItemKind)metadata.Kind;
        CloudPlaceholderMetadata.Builder builder = kind is CloudItemKind.Directory
            ? CloudPlaceholderMetadata.CreateDirectoryBuilder()
            : CloudPlaceholderMetadata.CreateFileBuilder();
        builder.WithAttributes((FileAttributes)metadata.Attributes);
        if (metadata.CreationTimeUtcTicks is long creationTime)
        {
            builder.WithCreationTime(ToUtcDateTimeOffset(creationTime));
        }

        if (metadata.LastAccessTimeUtcTicks is long lastAccessTime)
        {
            builder.WithLastAccessTime(ToUtcDateTimeOffset(lastAccessTime));
        }

        if (metadata.LastWriteTimeUtcTicks is long lastWriteTime)
        {
            builder.WithLastWriteTime(ToUtcDateTimeOffset(lastWriteTime));
        }

        if (metadata.ChangeTimeUtcTicks is long changeTime)
        {
            builder.WithChangeTime(ToUtcDateTimeOffset(changeTime));
        }

        return builder.Build();
    }

    private static DateTimeOffset ToUtcDateTimeOffset(long ticks) =>
        new(new DateTime(ticks, DateTimeKind.Utc));

    private static byte[] EncodeConflict(CloudRemoteConflict conflict)
    {
        CloudRemoteChange change = conflict.Change;
        CloudPlaceholderMetadata? metadata = change.Metadata;
        RemoteMetadataEnvelope? metadataEnvelope = metadata is null
            ? null
            : new(
                (int)metadata.Kind,
                (int)metadata.Attributes,
                metadata.CreationTime?.UtcTicks,
                metadata.LastAccessTime?.UtcTicks,
                metadata.LastWriteTime?.UtcTicks,
                metadata.ChangeTime?.UtcTicks);
        RemoteConflictEnvelope envelope = new(
            RemoteConflictEnvelopeVersion,
            change.ChangeId,
            (int)change.Kind,
            change.RemoteId,
            change.RemoteRevision,
            change.PreviousRemoteRevision,
            change.ItemId,
            (int)change.ItemKind,
            change.RelativePath,
            change.PreviousRelativePath,
            change.Length,
            metadataEnvelope,
            change.CursorAfter.ToArray(),
            (int)conflict.Reason);
        return JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            RemoteChangesJsonContext.Default.RemoteConflictEnvelope);
    }

    private static CloudStateConflictKind ToDurableConflictKind(CloudRemoteConflictReason reason) =>
        reason switch
        {
            CloudRemoteConflictReason.Content => CloudStateConflictKind.Content,
            CloudRemoteConflictReason.Move => CloudStateConflictKind.Move,
            CloudRemoteConflictReason.Delete => CloudStateConflictKind.Delete,
            _ => CloudStateConflictKind.Metadata,
        };

    private sealed record RemoteConflictEnvelope(
        int Version,
        string? ChangeId,
        int Kind,
        string? RemoteId,
        string? RemoteRevision,
        string? PreviousRemoteRevision,
        Guid? ItemId,
        int ItemKind,
        string? RelativePath,
        string? PreviousRelativePath,
        long? Length,
        RemoteMetadataEnvelope? Metadata,
        byte[]? CursorAfter,
        int Reason);

    private sealed record RemoteMetadataEnvelope(
        int Kind,
        int Attributes,
        long? CreationTimeUtcTicks,
        long? LastAccessTimeUtcTicks,
        long? LastWriteTimeUtcTicks,
        long? ChangeTimeUtcTicks);

    [JsonSerializable(typeof(RemoteConflictEnvelope))]
    [JsonSerializable(typeof(RemoteMetadataEnvelope))]
    private partial class RemoteChangesJsonContext : JsonSerializerContext
    {
    }

    private sealed record RemoteEntryContext(
        CloudItemState? LocalState,
        CloudItemState? ByRemoteId,
        CloudItemState? ByItemId,
        CloudItemState? ByPath,
        CloudItemState? ByPreviousPath,
        IReadOnlyList<CloudOperationJournalEntry> LocalOperations);

    private sealed record RemoteEntryOutcome(
        CloudRemoteApplyEntryStatus Status,
        CloudRemoteConflict? Conflict)
    {
        internal static RemoteEntryOutcome AppliedResult { get; } = new(
            CloudRemoteApplyEntryStatus.Applied,
            null);

        internal static RemoteEntryOutcome ConflictResult(CloudRemoteConflict conflict) => new(
            CloudRemoteApplyEntryStatus.Conflict,
            conflict);

        internal static RemoteEntryOutcome AlreadyAppliedResult { get; } = new(
            CloudRemoteApplyEntryStatus.AlreadyApplied,
            null);
    }
}
