using System.ComponentModel;
using System.Diagnostics;

using CfSharp.Native;

using Microsoft.Win32.SafeHandles;

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

    private static async ValueTask<RemoteMetadataIntent[]> ReadMetadataObservationIntentsAsync(ICloudStateTransaction transaction,
        string path, Guid? itemId, CancellationToken cancellationToken)
    {
        RemoteMetadataIntent[] owned = itemId is Guid id
            ? await ReadMetadataIntentsAsync(transaction, cancellationToken, id).ConfigureAwait(false) : [];
        IReadOnlyList<CloudStateCheckpoint> indexed = await transaction.Checkpoints.ListAsync(
            RemoteMetadataIntent.PathPrefix(path), cancellationToken).ConfigureAwait(false);
        return owned.Concat(indexed.Select(RemoteMetadataIntent.DecodePathIndex)
            .Where(intent => intent.ItemId != itemId && intent.Covers(path))).ToArray();
    }

    private static async ValueTask RetainMetadataObservationProtectionAsync(ICloudCheckpointRepository checkpoints,
        string path, CloudItemState? owner, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        // Unbound rows cannot protect an occupant through the journal's item lookup.
        await checkpoints.UpsertAsync(new(RemoteMetadataIntent.PendingObservationPathName(path), [1], observedAt),
            cancellationToken).ConfigureAwait(false);
        if (owner is { IsTombstone: false })
        {
            await checkpoints.UpsertAsync(new(RemoteMetadataIntent.PendingObservationItemName(owner.ItemId), [1], observedAt),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private bool OwnsMetadataObservation(RemoteMetadataIntent intent, LocalChangeSourceEvent? observation, Guid? itemId) =>
        _metadataEchoAuthority?.IsAuthorized(intent) == true &&
        observation is { Action: LocalChangeSourceAction.AttributesModified, SecurityIdentity: { } identity } &&
        intent.Matches(identity, itemId);

    private SecurityFeedProjection? CaptureDirectoryPassthroughOwner(CloudItemPath path, CloudItemState? owner, RemoteMetadataIntent[] intents)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299) ||
            owner is not { IsTombstone: false, Kind: CloudItemKind.Directory })
        { return null; }
        try
        {
            using SafeFileHandle reference = WindowsFileMetadata.OpenMetadataReference(path.FullPath);
            if (!MatchesAttributePath(reference, path)) { return null; }
            LocalCloudItemInspection native = _securityInspection?.Invoke(reference, path.FullPath, owner.Kind, _syncRootPath)
                ?? CloudItemInspector.Inspect(reference, path.FullPath, owner.Kind, _syncRootPath);
            if (native.LocalBinding is { } binding &&
                CloudPlaceholderIdentity.TryDecode(native.PlaceholderIdentity, out CloudPlaceholderIdentity? identity) &&
                identity.ItemId == owner.ItemId && identity.RemoteId == owner.RemoteId &&
                intents.Any(intent => intent.ItemId == owner.ItemId && intent.IsConfirmedPassthrough && intent.Binding == binding))
            {
                return new(_syncRootPath, path, owner.Kind, native, guard: null, parents: null, _securityInspection, allowRename: true);
            }
            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or
            UnauthorizedAccessException or CloudFilesException or Win32Exception)
        {
            return null;
        }
    }

    private async ValueTask<bool> RequiresMetadataReconciliationAsync(ICloudStateTransaction transaction,
        CancellationToken cancellationToken) => (await transaction.Checkpoints.ListAsync(RemoteMetadataIntent.PendingPrefix,
            cancellationToken).ConfigureAwait(false)).Select(RemoteMetadataIntent.DecodePending).Any(NeedsMetadataReconciliation);

    private bool NeedsMetadataReconciliation(RemoteMetadataIntent intent) => !intent.Reconciled &&
        (intent.RequiresReconciliation || (_metadataEchoAuthority?.IsApplying(intent.CallId) != true &&
            (intent.HasUnconfirmedOutcome || intent.UsesReconciliationPolicy ||
                (intent.Coordinated && _metadataEchoAuthority?.IsAuthorized(intent) != true))));

    private async ValueTask<bool> RetireFileTimeObservationsBeforeStartingAsync(ICloudCheckpointRepository checkpoints,
        CancellationToken cancellationToken)
    {
        if (_metadataEchoAuthority is not { } authority) { return false; }
        bool retired = false;
        // The owner admits one feed. A new feed has no queued notifications from a
        // previous source. An active remote call can still write into the new source.
        foreach (CloudStateCheckpoint marker in await checkpoints.ListAsync(
            RemoteMetadataIntent.FileTimeObservationPrefix, cancellationToken).ConfigureAwait(false))
        {
            if (marker.Value.Length == 16 && authority.IsApplying(new Guid(marker.Value.Span))) { continue; }
            await checkpoints.RemoveAsync(marker.Name, cancellationToken).ConfigureAwait(false);
            retired = true;
        }
        return retired;
    }

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
        Dictionary<string, CloudStateCheckpoint> stalePaths = (await checkpoints.ListAsync(RemoteMetadataIntent.PathIndexPrefix,
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
                    stalePaths.Remove(intent.PathIndexName);
                    changed = true;
                    continue;
                }
            }
            byte[] encoded = intent.Encode();
            if (!stalePaths.Remove(intent.PathIndexName, out CloudStateCheckpoint? indexedPath) ||
                !indexedPath.Value.Span.SequenceEqual(encoded))
            {
                await checkpoints.UpsertAsync(new(intent.PathIndexName, encoded, now), cancellationToken).ConfigureAwait(false);
                changed = true;
            }
            if (intent.MayRequireReconciliation &&
                (!stale.Remove(intent.PendingName, out CloudStateCheckpoint? indexed) || !indexed.Value.Span.SequenceEqual(encoded)))
            {
                await checkpoints.UpsertAsync(new(intent.PendingName, encoded, now), cancellationToken).ConfigureAwait(false);
                changed = true;
            }
        }
        foreach (string name in stale.Keys.Concat(stalePaths.Keys))
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
        HashSet<string> retainedProjections = await RemoteMetadataProjection.ReadRetainedReceiptNamesAsync(
            transaction.Checkpoints, cancellationToken).ConfigureAwait(false);
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
                    if (intent.Id != keep.Id && !retainedProjections.Contains(intent.Name))
                    {
                        await intent.RemoveAsync(transaction.Checkpoints, cancellationToken).ConfigureAwait(false);
                    }
                    // Keep one historical marker per generation/path, plus any receipt
                    // that still correlates an unsettled remote batch.
                    else if (!intent.Reconciled || intent.RequiresReconciliation)
                    {
                        RemoteMetadataIntent reconciled = intent with { Reconciled = true, RequiresReconciliation = false };
                        await reconciled.PersistAsync(transaction.Checkpoints, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
    }
}
