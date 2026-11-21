using System.Diagnostics;

namespace CfSharp;

public sealed partial class CloudLocalChangeFeed
{
    private async ValueTask<IDisposable?> AcquirePublicationAsync(CancellationToken cancellationToken) =>
        _metadataEchoAuthority is null ? null :
            await _metadataEchoAuthority.AcquirePublicationAsync(cancellationToken).ConfigureAwait(false);

    private static async ValueTask<RemoteMetadataIntent[]> ReadMetadataIntentsAsync(ICloudStateTransaction transaction,
        CancellationToken cancellationToken, Guid? itemId = null) => (await transaction.Checkpoints.ListAsync(
            itemId is Guid id ? RemoteMetadataIntent.ItemPrefix(id) : RemoteMetadataIntent.Prefix,
            cancellationToken).ConfigureAwait(false)).Select(RemoteMetadataIntent.Decode).ToArray();

    private bool OwnsMetadataObservation(RemoteMetadataIntent intent, LocalChangeSourceEvent? observation, Guid? itemId) =>
        _metadataEchoAuthority?.IsAuthorized(intent) == true &&
        observation is { Action: LocalChangeSourceAction.AttributesModified, SecurityIdentity: { } identity } &&
        intent.Matches(identity, itemId);

    private async ValueTask<bool> RequiresMetadataReconciliationAsync(ICloudStateTransaction transaction,
        CancellationToken cancellationToken) => (await transaction.Checkpoints.ListAsync(RemoteMetadataIntent.PendingPrefix,
            cancellationToken).ConfigureAwait(false)).Select(RemoteMetadataIntent.DecodePending).Any(NeedsMetadataReconciliation);

    private bool NeedsMetadataReconciliation(RemoteMetadataIntent intent) => !intent.Reconciled &&
        (intent.RequiresReconciliation || (_metadataEchoAuthority?.IsApplying(intent.CallId) != true &&
            (intent.HasUnconfirmedOutcome || intent.UsesReconciliationPolicy ||
                (intent.Coordinated && _metadataEchoAuthority?.IsAuthorized(intent) != true))));

    internal void NotifyRemoteMetadataCallEnded() => SignalAvailable();

    private async ValueTask WaitForAvailabilityAsync(long? metadataExpiration, CancellationToken cancellationToken)
    {
        if (metadataExpiration is not long expires)
        {
            await _availability.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        TimeSpan remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), expires);
        if (remaining <= TimeSpan.Zero) { return; }

        // Cancel the channel read at expiry so it cannot consume a later signal
        // after this reader has begun another durable scan.
        using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(remaining + TimeSpan.FromMilliseconds(1));
        try { await _availability.Reader.ReadAsync(wait.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (wait.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The next iteration checks the actual monotonic deadline and receipts.
        }
    }

    private async ValueTask<(bool Changed, bool RequiresReconciliation)> RecoverMetadataIntentsAsync(ICloudStateTransaction transaction,
        CancellationToken cancellationToken)
    {
        bool reconcile = false;
        bool changed = false;
        ICloudCheckpointRepository checkpoints = transaction.Checkpoints;
        // Rebuild from canonical receipts at startup to cover older stores and detect
        // corrupt receipts before the feed begins serving pages from the pending index.
        Dictionary<string, CloudStateCheckpoint> stale = (await checkpoints.ListAsync(RemoteMetadataIntent.PendingPrefix,
            cancellationToken).ConfigureAwait(false)).ToDictionary(checkpoint => checkpoint.Name, StringComparer.Ordinal);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (RemoteMetadataIntent original in await ReadMetadataIntentsAsync(transaction, cancellationToken).ConfigureAwait(false))
        {
            RemoteMetadataIntent intent = original;
            if (NeedsMetadataReconciliation(intent))
            {
                reconcile = true;
                if (!intent.RequiresReconciliation)
                {
                    intent = intent with { RequiresReconciliation = true };
                    await intent.PersistAsync(checkpoints, now, cancellationToken).ConfigureAwait(false);
                    stale.Remove(intent.PendingName);
                    changed = true;
                    continue;
                }
            }
            if (!intent.MayRequireReconciliation) { continue; }
            byte[] encoded = intent.Encode();
            if (!stale.Remove(intent.PendingName, out CloudStateCheckpoint? indexed) || !indexed.Value.Span.SequenceEqual(encoded))
            {
                await checkpoints.UpsertAsync(new(intent.PendingName, encoded, now), cancellationToken).ConfigureAwait(false);
                changed = true;
            }
        }
        foreach (string name in stale.Keys)
        {
            await checkpoints.RemoveAsync(name, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask ClearReconciledMetadataIntentsAsync(ICloudStateTransaction transaction, Func<RemoteMetadataIntent, bool>? retainReceipt,
        CancellationToken cancellationToken)
    {
        RemoteMetadataIntent[] intents = await ReadMetadataIntentsAsync(transaction, cancellationToken).ConfigureAwait(false);
        if (intents.Any(intent => _metadataEchoAuthority?.IsApplying(intent.CallId) == true))
        { throw new InvalidOperationException("Finish the active remote metadata call before acknowledging reconciliation."); }
        // A scan cannot cover a remote call still in flight at admission. Retain its
        // receipts as well as completed grants whose window had not yet expired.
        IEnumerable<RemoteMetadataIntent> retired = intents.Where(intent => retainReceipt?.Invoke(intent) != true &&
            _metadataEchoAuthority?.IsAuthorized(intent) != true);
        foreach (IGrouping<(Guid ItemId, CloudLocalFileBinding? Binding), RemoteMetadataIntent> generation in
            retired.GroupBy(intent => (intent.ItemId, intent.Binding)))
        {
            foreach (IGrouping<string, RemoteMetadataIntent> path in generation.GroupBy(intent => intent.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                RemoteMetadataIntent[] history = path.ToArray();
                RemoteMetadataIntent keep = history.FirstOrDefault(intent => !intent.Superseded) ?? history[0];
                foreach (RemoteMetadataIntent intent in history)
                {
                    if (intent.Id != keep.Id) { await intent.RemoveAsync(transaction.Checkpoints, cancellationToken).ConfigureAwait(false); }
                }
                // A timeout is not origin evidence. Keep one retired marker per native
                // generation/path so late callbacks cannot regain a false local association.
                RemoteMetadataIntent reconciled = keep with { Reconciled = true, RequiresReconciliation = false };
                await reconciled.PersistAsync(transaction.Checkpoints, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
