namespace CfSharp;

public sealed partial class CloudFileSystem
{
    private async ValueTask<RemoteCreationIntent?> ReadRemoteCreationIntentAsync(
        CloudRemoteChange change, CancellationToken cancellationToken)
    {
        await using ICloudStateTransaction transaction = await _stateStore!
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        CloudStateCheckpoint? checkpoint = await transaction.Checkpoints
            .GetAsync(RemoteCreationIntent.Name(change), cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
        {
            return null;
        }

        RemoteCreationIntent intent = RemoteCreationIntent.Decode(checkpoint.Value);
        if (!intent.Fingerprint.AsSpan().SequenceEqual(RemoteCreationIntent.GetFingerprint(change)))
        {
            throw new ArgumentException("The remote change differs from its pending creation intent.", nameof(change));
        }

        return intent;
    }

    private async ValueTask<RemoteEntryOutcome> CreateRemotePlaceholderCoordinatedAsync(
        CloudRemoteChange change, Guid itemId, CloudRemoteApplyOptions options,
        RemoteCreationIntent? pending, CancellationToken cancellationToken)
    {
        using CloudFileSystemOperationLease operation = await AcquireOperationAsync(
            [CloudItemOperationScope.Exact(ToFullPath(change.RelativePath))], cancellationToken).ConfigureAwait(false);
        RemoteCreationIntent intent = pending ?? new(change.RelativePath, change.ItemKind,
            new CloudPlaceholderIdentity(itemId, change.RemoteId, change.RemoteRevision),
            RemoteCreationIntent.GetFingerprint(change), Committed: false);
        string name = RemoteCreationIntent.Name(change);
        if (pending is null)
        {
            // This record does not reference a nonexistent Item. It survives native success
            // followed by a failed SQL commit and keeps the retry identity stable.
            await using ICloudStateTransaction preparation = await _stateStore!
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await preparation.Checkpoints.UpsertAsync(new CloudStateCheckpoint(name, intent.Encode(), DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
            await preparation.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        string fullPath = ToFullPath(change.RelativePath);
        LocalCloudItemInspection local = CloudItemInspector.Inspect(fullPath, change.ItemKind);
        await using ICloudStateTransaction transaction = await _stateStore!
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        CloudItemState? byPath = await transaction.Items.GetByRelativePathAsync(change.RelativePath, cancellationToken)
            .ConfigureAwait(false);
        CloudItemState? byId = await transaction.Items.GetByItemIdAsync(intent.Identity.ItemId, cancellationToken)
            .ConfigureAwait(false);
        CloudItemState? byRemote = await transaction.Items.GetByRemoteIdAsync(change.RemoteId, cancellationToken)
            .ConfigureAwait(false);
        if ((intent.Committed && !local.Exists) ||
            (byPath is not null && byPath.ItemId != intent.Identity.ItemId) ||
            (byRemote is not null && byRemote.ItemId != intent.Identity.ItemId) ||
            (byId is not null && !string.Equals(byId.RelativePath, change.RelativePath, StringComparison.OrdinalIgnoreCase)) ||
            (local.Exists && (!local.PlaceholderIdentity.AsSpan().SequenceEqual(intent.Identity.Encode()) ||
                (options.PreserveUnsynchronizedLocalContent && local.SynchronizationState != CloudSynchronizationState.InSync))) ||
            (options.PreserveUnsynchronizedLocalContent &&
                (await transaction.Operations.ListByItemIdAsync(intent.Identity.ItemId, 1, cancellationToken)
                    .ConfigureAwait(false)).Count != 0))
        {
            return RemoteEntryOutcome.ConflictResult(CreateConflict(change, byPath, CloudRemoteConflictReason.Content));
        }

        if (!local.Exists)
        {
            string parent = Path.GetDirectoryName(fullPath)!;
            string leaf = Path.GetFileName(fullPath);
            CloudPlaceholderSpec spec = change.ItemKind is CloudItemKind.Directory
                ? CloudDirectoryPlaceholderSpec.CreateBuilder(leaf, intent.Identity)
                    .WithMetadata(change.Metadata!).WithInSyncState(true).Build()
                : CloudFilePlaceholderSpec.CreateBuilder(leaf, intent.Identity, change.Length!.Value)
                    .WithMetadata(change.Metadata!).WithInSyncState(true).Build();
            cancellationToken.ThrowIfCancellationRequested();
            // Creation does not hydrate or invoke application provider code. Keep only this
            // synchronous native call within the transaction, so feed/state writers cannot
            // install a competing identity between creation and the Item/suppression commit.
            CloudPlaceholderNativeBatchResult native = CloudPlaceholderPlatform.CreatePlaceholders(parent, [spec], true);
            int error = native.HResult < 0 ? native.HResult : native.EntriesProcessed == 1
                ? native.Entries[0].HResult : unchecked((int)0x80004005);
            if (error < 0)
            {
                throw CloudFilesException.FromHResult("CloudFileSystem.CreateRemotePlaceholder", fullPath, error);
            }
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await transaction.Items.UpsertAsync(new CloudItemState(intent.Identity.ItemId, change.RemoteId,
            change.RelativePath, change.ItemKind, change.RemoteRevision, localFileId: null,
            isTombstone: false, now), CancellationToken.None).ConfigureAwait(false);
        await CloudDirectoryProvenance.RetainProjectionAsync(transaction, SyncRootPath,
            change.RelativePath, change.ItemKind, CancellationToken.None).ConfigureAwait(false);
        if (options.SuppressLocalEcho && !intent.Committed)
        {
            await transaction.EchoSuppressions.UpsertAsync(new CloudEchoSuppressionState(
                Guid.NewGuid(), intent.Identity.ItemId, CloudStateOperationKind.Create, change.RelativePath,
                RemoteCreationIntent.EchoPayload(intent.Identity), now + options.EchoSuppressionLifetime,
                remainingObservations: options.EchoSuppressionObservationCount), CancellationToken.None).ConfigureAwait(false);
        }

        await transaction.Checkpoints.UpsertAsync(new CloudStateCheckpoint(name,
            (intent with { Committed = true }).Encode(), now), CancellationToken.None).ConfigureAwait(false);
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return RemoteEntryOutcome.AppliedResult;
    }
}
