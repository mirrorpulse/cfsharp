namespace CfSharp;

public sealed partial class CloudLocalChangeFeed
{
    private static async ValueTask<RemoteMetadataIntent[]> ReadMetadataIntentsAsync(ICloudStateTransaction transaction,
        CancellationToken cancellationToken, Guid? itemId = null) => (await transaction.Checkpoints.ListAsync(
            itemId is Guid id ? RemoteMetadataIntent.ItemPrefix(id) : RemoteMetadataIntent.Prefix,
            cancellationToken).ConfigureAwait(false)).Select(RemoteMetadataIntent.Decode).ToArray();

    private bool OwnsMetadataObservation(RemoteMetadataIntent intent, LocalChangeSourceEvent? observation, Guid? itemId) =>
        _metadataEchoAuthority?.IsAuthorized(intent) == true &&
        observation is { Action: LocalChangeSourceAction.AttributesModified, SecurityIdentity: { } identity } &&
        intent.Matches(identity, itemId);

    private async ValueTask<bool> RequiresMetadataReconciliationAsync(ICloudStateTransaction transaction,
        CancellationToken cancellationToken) => (await ReadMetadataIntentsAsync(transaction, cancellationToken).ConfigureAwait(false))
            .Any(NeedsMetadataReconciliation);

    private bool NeedsMetadataReconciliation(RemoteMetadataIntent intent) => !intent.Reconciled &&
        (intent.RequiresReconciliation || (_metadataEchoAuthority?.IsApplying(intent.CallId) != true &&
            (intent.HasUnconfirmedOutcome || intent.UsesReconciliationPolicy ||
                (intent.Coordinated && _metadataEchoAuthority?.IsAuthorized(intent) != true))));

    internal void NotifyRemoteMetadataCallEnded() => SignalAvailable();

    private async ValueTask<(bool Changed, bool RequiresReconciliation)> RecoverMetadataIntentsAsync(ICloudStateTransaction transaction,
        CancellationToken cancellationToken)
    {
        bool reconcile = false;
        bool changed = false;
        foreach (RemoteMetadataIntent intent in await ReadMetadataIntentsAsync(transaction, cancellationToken).ConfigureAwait(false))
        {
            if (intent.IsConfirmedPassthrough) { continue; }
            if (intent.Reconciled && !intent.RequiresReconciliation)
            {
                continue;
            }
            if (_metadataEchoAuthority?.IsApplying(intent.CallId) == true) { continue; }
            if (_metadataEchoAuthority?.IsAuthorized(intent) == true) { continue; }
            RemoteMetadataIntent retained = intent with { RequiresReconciliation = true };
            await transaction.Checkpoints.UpsertAsync(new(retained.Name, retained.Encode(), DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
            reconcile = true;
            changed = true;
        }
        return (changed, reconcile);
    }

    private async ValueTask<bool> TrySuppressCoordinatedMetadataAsync(ICloudStateTransaction transaction,
        CloudOperationJournalEntry entry, LocalChangeSourceEvent observation, CancellationToken cancellationToken)
    {
        if (observation.Action != LocalChangeSourceAction.AttributesModified || observation.SecurityIdentity is not { } identity) { return false; }
        LocalChangePayload payload = LocalChangePayload.Decode(entry.Payload);
        RemoteMetadataIntent[] candidates = (await ReadMetadataIntentsAsync(transaction, cancellationToken, entry.ItemId).ConfigureAwait(false))
            .Where(intent => intent.Matches(identity, intent.ItemId) ||
                ((intent.Binding is null || intent.RequiresReconciliation) && intent.Covers(payload.RelativePath))).ToArray();
        if (!candidates.Any(NeedsMetadataReconciliation))
        {
            if (candidates.Any(intent => intent.IsConfirmedPassthrough)) { return false; }
            if (candidates.Any(intent => OwnsMetadataObservation(intent, observation, entry.ItemId))) { return true; }
        }
        if (candidates.Length != 0)
        {
            // Authority can expire or be invalidated after staging. Keep the same original
            // behind reconciliation rather than publishing a purported local metadata edit.
            throw new CloudNamespaceObservationUncertainException(payload.IsDirectory);
        }
        return false;
    }

    private async ValueTask ClearReconciledMetadataIntentsAsync(ICloudStateTransaction transaction,
        CancellationToken cancellationToken)
    {
        RemoteMetadataIntent[] intents = await ReadMetadataIntentsAsync(transaction, cancellationToken).ConfigureAwait(false);
        if (intents.Any(intent => _metadataEchoAuthority?.IsApplying(intent.CallId) == true))
        { throw new InvalidOperationException("Finish the active remote metadata call before acknowledging reconciliation."); }
        foreach (IGrouping<(Guid ItemId, CloudLocalFileBinding? Binding), RemoteMetadataIntent> generation in
            intents.GroupBy(intent => (intent.ItemId, intent.Binding)))
        {
            foreach (IGrouping<string, RemoteMetadataIntent> path in generation.GroupBy(intent => intent.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                RemoteMetadataIntent[] history = path.ToArray();
                RemoteMetadataIntent keep = history.FirstOrDefault(intent => !intent.Superseded) ?? history[0];
                foreach (RemoteMetadataIntent intent in history)
                {
                    if (intent.Id != keep.Id) { await transaction.Checkpoints.RemoveAsync(intent.Name, cancellationToken).ConfigureAwait(false); }
                }
                // A timeout is not origin evidence. Keep one retired marker per native
                // generation/path so late callbacks cannot regain a false local association.
                RemoteMetadataIntent reconciled = keep with { Reconciled = true, RequiresReconciliation = false };
                await transaction.Checkpoints.UpsertAsync(new(reconciled.Name, reconciled.Encode(), DateTimeOffset.UtcNow),
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
