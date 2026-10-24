using CfSharp.Native;

using Microsoft.Win32.SafeHandles;

namespace CfSharp;

public sealed partial class CloudFileSystem
{
    private async ValueTask<RemoteCreationIntent?> ReadRemoteCreationIntentAsync(
        CloudRemoteChange change, CancellationToken cancellationToken)
    {
        await using ICloudStateTransaction transaction = await _stateStore!
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRemoteCreationIntentCoreAsync(transaction, change, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RemoteCreationIntent?> ReadRemoteCreationIntentCoreAsync(
        ICloudStateTransaction transaction, CloudRemoteChange change, CancellationToken cancellationToken)
    {
        CloudStateCheckpoint? checkpoint = await transaction.Checkpoints
            .GetAsync(RemoteCreationIntent.Name(change), cancellationToken).ConfigureAwait(false);
        if (checkpoint is null) { return null; }
        RemoteCreationIntent intent = RemoteCreationIntent.Decode(checkpoint.Value);
        if (!intent.Fingerprint.AsSpan().SequenceEqual(RemoteCreationIntent.GetFingerprint(change)))
        {
            throw new ArgumentException("The remote change differs from its pending creation intent.", nameof(change));
        }
        bool copy = intent.Resolution == CloudRemoteConflictDecision.KeepBoth;
        if ((!copy && ((change.ItemId.HasValue && change.ItemId != intent.Identity.ItemId) ||
                intent.RelativePath != change.RelativePath || intent.Identity.RemoteId != change.RemoteId)) ||
            (copy && (intent.Identity.RemoteId == change.RemoteId || intent.Identity.ItemId == change.ItemId)) ||
            intent.Kind != change.ItemKind || intent.Identity.RemoteRevision != change.RemoteRevision ||
            intent.Parents is null || !string.Equals(intent.RootPath, SyncRootPath, StringComparison.OrdinalIgnoreCase) ||
            intent.StoreScope != await CloudDirectoryMoveEvidence.GetScopeAsync(transaction, false, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The retained creation identity or provenance is incompatible; retain its evidence and explicitly reconcile the original change.");
        }
        return intent;
    }

    private async ValueTask<RemoteEntryOutcome> CreateRemotePlaceholderCoordinatedAsync(
        CloudRemoteChange change, Guid itemId, CloudRemoteApplyOptions options,
        RemoteCreationIntent? pending, CloudRemoteChange original,
        CloudRemoteConflictDecision? resolution, CancellationToken cancellationToken)
    {
        string fullPath = ToFullPath(change.RelativePath);
        using CloudFileSystemOperationLease operation = await AcquireOperationAsync(
            [CloudItemOperationScope.Exact(fullPath)], cancellationToken).ConfigureAwait(false);
        using RemoteNamespaceParentLease parents = HoldRemoteNamespaceParents(fullPath, fullPath);
        if (pending is null && (File.Exists(fullPath) || Directory.Exists(fullPath)))
        {
            // A known pre-existing occupant is a conflict, not an uncertain result of this
            // new birth. Once preparation exists, only its native receipt can establish ownership.
            return RemoteEntryOutcome.ConflictResult(CreateConflict(change, null, CloudRemoteConflictReason.PathCollision));
        }
        RemoteCreationIntent intent;
        string name = RemoteCreationIntent.Name(original);
        if (pending is null)
        {
            await using ICloudStateTransaction preparation = await _stateStore!
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            intent = new(change.RelativePath, change.ItemKind,
                new CloudPlaceholderIdentity(itemId, change.RemoteId, change.RemoteRevision),
                RemoteCreationIntent.GetFingerprint(original), Committed: false,
                await CloudDirectoryMoveEvidence.GetScopeAsync(preparation, true, cancellationToken).ConfigureAwait(false),
                SyncRootPath, parents.Parents, PreserveContent: options.PreserveUnsynchronizedLocalContent, Resolution: resolution);
            await preparation.Checkpoints.UpsertAsync(new(name, intent.Encode(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            await preparation.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            intent = pending;
        }
        RequireRemoteCreationParents(intent, parents.Parents);
        using SafeFileHandle nativeObject = await AcceptRemoteCreationAsync(change, intent, name, fullPath, cancellationToken).ConfigureAwait(false);
        // Acceptance is committed separately from projection. A failed projection cannot erase
        // the file-ID proof, and a path-equal replacement can never inherit that proof on retry.
        await using ICloudStateTransaction transaction = await _stateStore!
            .BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
        intent = (await ReadRemoteCreationIntentCoreAsync(transaction, original, CancellationToken.None).ConfigureAwait(false))!;
        if (await HasRemoteCreationCollisionAsync(transaction, change, intent, CancellationToken.None).ConfigureAwait(false))
        {
            throw new IOException("The original creation projection is blocked by incompatible live ownership; retain its preparation.");
        }
        ValidateRemoteCreationObject(change, intent, nativeObject);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await transaction.Items.UpsertAsync(new(intent.Identity.ItemId, change.RemoteId,
            change.RelativePath, change.ItemKind, change.RemoteRevision, localFileId: null,
            isTombstone: false, now), CancellationToken.None).ConfigureAwait(false);
        await CloudDirectoryProvenance.RetainProjectionAsync(transaction, SyncRootPath,
            change.RelativePath, change.ItemKind, CancellationToken.None).ConfigureAwait(false);
        if (options.SuppressLocalEcho && !intent.Committed)
        {
            await transaction.EchoSuppressions.UpsertAsync(new(Guid.NewGuid(), intent.Identity.ItemId,
                CloudStateOperationKind.Create, change.RelativePath, RemoteCreationIntent.EchoPayload(intent.Identity),
                now + options.EchoSuppressionLifetime, remainingObservations: options.EchoSuppressionObservationCount), CancellationToken.None).ConfigureAwait(false);
        }
        await transaction.Checkpoints.UpsertAsync(new(name, (intent with { Committed = true }).Encode(), now), CancellationToken.None).ConfigureAwait(false);
        ValidateRemoteCreationObject(change, intent, nativeObject);
        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return RemoteEntryOutcome.AppliedResult;
    }

    private async ValueTask<SafeFileHandle> AcceptRemoteCreationAsync(CloudRemoteChange change,
        RemoteCreationIntent intent, string name, string fullPath, CancellationToken cancellationToken)
    {
        await using ICloudStateTransaction transaction = await _stateStore!
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (await HasRemoteCreationCollisionAsync(transaction, change, intent, cancellationToken).ConfigureAwait(false))
        {
            throw new IOException("The original creation is blocked by incompatible live ownership or pending local work; retain its preparation.");
        }
        LocalCloudItemInspection local = CloudItemInspector.Inspect(fullPath, change.ItemKind);
        long? createUsn = null;
        if (intent.CreatedObject is null)
        {
            if (local.Exists)
            {
                throw new IOException("A native occupant has no original creation receipt; explicit reconciliation is required.");
            }
            string parent = Path.GetDirectoryName(fullPath)!;
            string leaf = Path.GetFileName(fullPath);
            CloudPlaceholderSpec spec = change.ItemKind is CloudItemKind.Directory
                ? CloudDirectoryPlaceholderSpec.CreateBuilder(leaf, intent.Identity).WithMetadata(change.Metadata!).WithInSyncState(true).Build()
                : CloudFilePlaceholderSpec.CreateBuilder(leaf, intent.Identity, change.Length!.Value).WithMetadata(change.Metadata!).WithInSyncState(true).Build();
            cancellationToken.ThrowIfCancellationRequested();
            CloudPlaceholderNativeBatchResult result = CloudPlaceholderPlatform.CreatePlaceholders(parent, [spec], true);
            int error = result.HResult < 0 ? result.HResult : result.EntriesProcessed == 1
                ? result.Entries[0].HResult : unchecked((int)0x80004005);
            if (error < 0) { throw CloudFilesException.FromHResult("CloudFileSystem.CreateRemotePlaceholder", fullPath, error); }
            createUsn = result.Entries[0].CreateUsn;
        }
        else if (!local.Exists)
        {
            throw new IOException("The originally created native object is absent; replay cannot create another generation.");
        }
        SafeFileHandle handle = WindowsFileMetadata.OpenNamespaceGuard(fullPath);
        try
        {
            // CF_PLACEHOLDER_CREATE_INFO returns the final create USN. Compare it after
            // acquiring the namespace guard, before accepting object evidence, so a new
            // same-identity occupant installed between create and open is not adopted.
            if (createUsn.HasValue && (createUsn <= 0 ||
                WindowsFileUsn.Read(fullPath, change.ItemKind == CloudItemKind.Directory) != createUsn))
            {
                throw new IOException("The native object changed before original creation acceptance; retain its preparation.");
            }
            if (intent.CreatedObject is null)
            {
                intent = intent with
                {
                    CreatedObject = ObserveRemoteNamespaceObject(handle,
                    change.ItemKind == CloudItemKind.Directory, intent.PreserveContent, intent.PreserveContent)
                };
                ValidateRemoteCreationObject(change, intent, handle);
                await transaction.Checkpoints.UpsertAsync(new(name, intent.Encode(), DateTimeOffset.UtcNow),
                    CancellationToken.None).ConfigureAwait(false);
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                ValidateRemoteCreationObject(change, intent, handle);
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static async ValueTask<bool> HasRemoteCreationCollisionAsync(ICloudStateTransaction transaction,
        CloudRemoteChange change, RemoteCreationIntent intent, CancellationToken cancellationToken)
    {
        CloudItemState? byPath = await transaction.Items.GetByRelativePathAsync(change.RelativePath, cancellationToken).ConfigureAwait(false);
        CloudItemState? byId = await transaction.Items.GetByItemIdAsync(intent.Identity.ItemId, cancellationToken).ConfigureAwait(false);
        CloudItemState? byRemote = await transaction.Items.GetByRemoteIdAsync(change.RemoteId, cancellationToken).ConfigureAwait(false);
        return (byPath is { IsTombstone: false } && byPath.ItemId != intent.Identity.ItemId) ||
            (byRemote is not null && byRemote.ItemId != intent.Identity.ItemId) ||
            (byId is not null && (byId.RemoteId != change.RemoteId || byId.Kind != change.ItemKind ||
                !CloudDirectoryStateProjection.SamePath(byId.RelativePath, change.RelativePath))) ||
            (intent.Committed && (byId is null || byId.IsTombstone || byId.RemoteRevision != change.RemoteRevision)) ||
            (!intent.Committed && byId is not null && change.PreviousRemoteRevision is not null &&
                byId.RemoteRevision != change.PreviousRemoteRevision) ||
            (intent.PreserveContent && (await transaction.Operations.ListByItemIdAsync(intent.Identity.ItemId, 1, cancellationToken).ConfigureAwait(false)).Count != 0);
    }

    private static void RequireRemoteCreationParents(RemoteCreationIntent intent, RemoteNamespaceObject[] parents)
    {
        if (intent.Parents is null || parents.Length != intent.Parents.Count || parents.Where((parent, index) =>
            !CloudDirectoryStateProjection.SamePath(parent.RelativePath, intent.Parents[index].RelativePath) ||
            parent.Binding != intent.Parents[index].Binding || parent.CreationTime != intent.Parents[index].CreationTime ||
            !parent.Identity.AsSpan().SequenceEqual(intent.Parents[index].Identity)).Any())
        {
            throw new IOException("The original creation parent/root provenance changed; retain its preparation.");
        }
    }

    private void ValidateRemoteCreationObject(CloudRemoteChange change, RemoteCreationIntent intent, SafeFileHandle handle)
    {
        // This handle was opened at the effective requested path and excludes namespace
        // replacement. Authenticate its object binding rather than reconstructing a durable
        // path from native spelling, which may include different casing or expanded aliases.
        RemoteNamespaceObject actual = ObserveRemoteNamespaceObject(handle, change.ItemKind == CloudItemKind.Directory,
            intent.PreserveContent, intent.PreserveContent);
        if (intent.CreatedObject is null || !SameRemoteNamespaceObject(intent.CreatedObject, actual) ||
            !actual.Identity.AsSpan().SequenceEqual(intent.Identity.Encode()) ||
            (change.ItemKind == CloudItemKind.File && actual.Length != change.Length))
        {
            throw new IOException("The originally created native identity, binding or metadata changed; retain its preparation.");
        }
    }

    private async ValueTask<RemoteCreationOutcomeLease?> HoldRemoteCreationOutcomeAsync(
        ICloudStateTransaction transaction, CloudRemoteChange change, CancellationToken cancellationToken)
    {
        RemoteCreationIntent? intent = await ReadRemoteCreationIntentCoreAsync(transaction, change, cancellationToken).ConfigureAwait(false);
        if (intent is null) { return null; }
        change = intent.EffectiveChange(change);
        CloudItemState? state = await transaction.Items.GetByItemIdAsync(intent.Identity.ItemId, cancellationToken).ConfigureAwait(false);
        if (!intent.Committed || state is null || state.IsTombstone || state.RemoteId != change.RemoteId ||
            state.RemoteRevision != change.RemoteRevision || state.Kind != change.ItemKind ||
            !CloudDirectoryStateProjection.SamePath(state.RelativePath, change.RelativePath) ||
            await HasRemoteCreationCollisionAsync(transaction, change, intent, cancellationToken).ConfigureAwait(false))
        {
            throw new IOException("The original creation projection changed before outcome publication; retain its preparation.");
        }
        string path = ToFullPath(change.RelativePath);
        RemoteNamespaceParentLease parents = HoldRemoteNamespaceParents(path, path);
        SafeFileHandle? handle = null;
        try
        {
            RequireRemoteCreationParents(intent, parents.Parents);
            handle = WindowsFileMetadata.OpenNamespaceGuard(path);
            ValidateRemoteCreationObject(change, intent, handle);
            return new(this, change, intent, parents, handle);
        }
        catch
        {
            handle?.Dispose();
            parents.Dispose();
            throw;
        }
    }

    private sealed class RemoteCreationOutcomeLease(CloudFileSystem owner, CloudRemoteChange change,
        RemoteCreationIntent intent, RemoteNamespaceParentLease parents, SafeFileHandle handle) : IDisposable
    {
        internal bool IsResolved => intent.Resolution.HasValue;
        internal void Validate() => owner.ValidateRemoteCreationObject(change, intent, handle);
        public void Dispose()
        {
            handle.Dispose();
            parents.Dispose();
        }
    }
}
