namespace CfSharp;

public sealed partial class CloudFileSystem
{
    private async ValueTask<RemoteEntryOutcome?> RecoverRemoteMetadataAsync(CloudRemoteChange change,
        RemoteChangeRequest request, CancellationToken cancellationToken)
    {
        await using ICloudStateTransaction transaction = await _stateStore!.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        string name = RemoteMetadataProjection.Name(request.Batch, request.EntryIndex);
        CloudStateCheckpoint? checkpoint = await transaction.Checkpoints.GetAsync(name, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null) { return null; }

        RemoteMetadataProjection projection = RemoteMetadataProjection.Decode(checkpoint);
        if (projection.ProjectedAtTicks == 0) { throw MetadataProjectionUnresolved(change, "the native write was not projected"); }
        CloudStateCheckpoint receipt = await transaction.Checkpoints.GetAsync(projection.ReceiptName, cancellationToken)
            .ConfigureAwait(false) ?? throw MetadataProjectionUnresolved(change, "the attribution preparation is missing");
        RemoteMetadataIntent intent = RemoteMetadataIntent.Decode(receipt);
        if (projection.ProjectedAtTicks == RemoteMetadataProjection.NativeWriteNotAttempted)
        {
            if (intent.Reconciled && intent.Completed && !intent.NativeWriteApplied && !intent.OutcomeConfirmed)
            { return null; }
            throw MetadataProjectionUnresolved(change, "the unattempted write still needs reconciliation");
        }
        await ValidateRemoteMetadataProjectionAsync(transaction, change, projection, intent, cancellationToken).ConfigureAwait(false);
        return RemoteEntryOutcome.AppliedResult with
        {
            MetadataIntent = intent with { RequiresReconciliation = !intent.Reconciled },
            RecoveredMetadataProjection = projection,
        };
    }

    private async ValueTask<bool> RecordUnattemptedRemoteMetadataAsync(RemoteChangeRequest request)
    {
        using IDisposable publication = await _metadataEchoAuthority.AcquirePublicationAsync(CancellationToken.None).ConfigureAwait(false);
        await using ICloudStateTransaction transaction = await _stateStore!.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
        string name = RemoteMetadataProjection.Name(request.Batch, request.EntryIndex);
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
        RemoteChangeRequest request, RemoteMetadataIntent intent, DateTimeOffset projectedAt, CancellationToken cancellationToken)
    {
        string name = RemoteMetadataProjection.Name(request.Batch, request.EntryIndex);
        CloudStateCheckpoint checkpoint = await transaction.Checkpoints.GetAsync(name, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The remote metadata projection preparation is missing.");
        RemoteMetadataProjection projection = RemoteMetadataProjection.Decode(checkpoint);
        if (projection.ReceiptName != intent.Name || projection.ProjectedAtTicks != 0)
        { throw new InvalidDataException("The remote metadata projection preparation changed."); }
        await transaction.Checkpoints.UpsertAsync((projection with { ProjectedAtTicks = projectedAt.UtcTicks })
            .ToCheckpoint(name, projectedAt), cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ValidateRemoteMetadataProjectionAsync(ICloudStateTransaction transaction, CloudRemoteChange change,
        RemoteMetadataProjection projection, RemoteMetadataIntent intent, CancellationToken cancellationToken)
    {
        if (intent.Completed || intent.Superseded || intent.Binding is null || !intent.Covers(change.RelativePath) ||
            (change.ItemId is Guid explicitId && explicitId != projection.ItemId))
        { throw MetadataProjectionUnresolved(change, "the original attribution changed"); }

        CloudItemState? owner = await transaction.Items.GetByRelativePathAsync(change.RelativePath, cancellationToken).ConfigureAwait(false);
        if (owner is null || owner.IsTombstone || owner.ItemId != projection.ItemId || owner.Kind != change.ItemKind ||
            owner.RemoteId != change.RemoteId || owner.RemoteRevision != change.RemoteRevision ||
            owner.UpdatedAt.UtcTicks != projection.ProjectedAtTicks)
        { throw MetadataProjectionUnresolved(change, "the official projection changed"); }

        LocalCloudItemInspection native = CloudItemInspector.Inspect(ToFullPath(change.RelativePath), change.ItemKind, SyncRootPath);
        if (!native.Exists || native.Kind != change.ItemKind || native.LocalBinding != intent.Binding ||
            !CloudPlaceholderIdentity.TryDecode(native.PlaceholderIdentity, out CloudPlaceholderIdentity? identity) ||
            identity.ItemId != owner.ItemId || identity.RemoteId != change.RemoteId || identity.RemoteRevision != change.RemoteRevision)
        { throw MetadataProjectionUnresolved(change, "the original native binding or identity changed"); }
    }

    private static IOException MetadataProjectionUnresolved(CloudRemoteChange change, string reason) =>
        new($"Remote metadata change '{change.ChangeId}' requires reconciliation because {reason}. Retain the original batch for retry.");
}
