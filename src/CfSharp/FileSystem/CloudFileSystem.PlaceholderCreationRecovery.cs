using System.ComponentModel;
using CfSharp.Native;
using Microsoft.Win32.SafeHandles;
using Status = CfSharp.CloudPlaceholderCreationRecoveryStatus;

namespace CfSharp;

public sealed partial class CloudFileSystem
{
    internal async ValueTask<IReadOnlyList<CloudPlaceholderCreationRecoveryResult>> CreateRecoverablePlaceholdersAsync(
        CloudDirectory directory, IEnumerable<CloudPlaceholderCreationRequest> requests, CloudPlaceholderBatchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        CloudPlaceholderCreationRequest[] entries = requests.ToArray();
        HashSet<Guid> operationIds = [];
        HashSet<Guid> itemIds = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> remoteIds = new(StringComparer.Ordinal);
        if (entries.Length == 0 || entries.Any(request => request is null || !operationIds.Add(request.OperationId) ||
            !names.Add(request.Specification.Name) || !itemIds.Add(request.Specification.Identity.ItemId) ||
            !remoteIds.Add(request.Specification.Identity.RemoteId)))
        {
            throw new ArgumentException("A recoverable batch requires distinct non-null entries, names and identities.", nameof(requests));
        }

        List<CloudPlaceholderCreationRecoveryResult> results = new(entries.Length);
        bool stopped = false;
        foreach (CloudPlaceholderCreationRequest request in entries)
        {
            CloudPlaceholderCreationRecoveryResult result = stopped
                ? new(request.OperationId, Status.NotProcessed)
                : await RunRecoverablePlaceholderAsync(directory, request, create: true, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            stopped = options.StopOnFirstFailure && result.Status is not (Status.Created or Status.Recovered);
        }

        return results.AsReadOnly();
    }

    internal async ValueTask<CloudPlaceholderCreationRecoveryResult> RunRecoverablePlaceholderAsync(CloudDirectory directory,
        CloudPlaceholderCreationRequest request, bool create, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        CloudPlaceholderSpec specification = request.Specification;
        string path = Path.Combine(directory.FullPath, specification.Name);
        string relativePath = Path.Combine(directory.RelativePath, specification.Name);
        try
        {
            using CloudFileSystemOperationLease operation = await AcquireOperationAsync(
                [CloudItemOperationScope.Exact(path)], cancellationToken).ConfigureAwait(false);
            // Attribute-only handles do not enforce all sharing checks. The creation call uses a
            // string path, so keep rename protection on every ancestor while authenticating it.
            using CloudPathHandleLease ancestry = CloudPathHandleLease.OpenDirectoryChain(SyncRootPath,
                directory.FullPath, protectRename: true);
            return await RunRecoverablePlaceholderUnderLeaseAsync(operation.StateStore, directory, request, path,
                relativePath, create, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is Win32Exception { NativeErrorCode: 2 or 3 } or
            IOException { InnerException: Win32Exception { NativeErrorCode: 2 or 3 } })
        {
            return new(request.OperationId, Status.Missing, error: exception);
        }
        catch (Exception exception) when (exception is Win32Exception or CloudPathReparsePointException or NativeFileException or
            CloudFilesException or UnauthorizedAccessException or IOException { InnerException: Win32Exception })
        {
            return new(request.OperationId, Status.Unavailable, error: exception);
        }
    }

    private async ValueTask<CloudPlaceholderCreationRecoveryResult> RunRecoverablePlaceholderUnderLeaseAsync(
        ICloudStateStore store, CloudDirectory directory, CloudPlaceholderCreationRequest request, string path,
        string relativePath, bool create, CancellationToken cancellationToken)
    {
        CloudPlaceholderSpec specification = request.Specification;
        Guid id = request.OperationId;
        string checkpointName = PlaceholderCreationEvidence.Name(id);
        byte[] identity = specification.Identity.Encode();
        byte[] fingerprint = PlaceholderCreationEvidence.Fingerprint(specification);
        using SafeFileHandle root = WindowsFileMetadata.OpenDirectoryGuard(SyncRootPath);
        using SafeFileHandle parent = WindowsFileMetadata.OpenDirectoryGuard(directory.FullPath);
        CloudLocalFileBinding parentBinding = ReadCreationParentBinding(root, parent);
        if (!CloudDirectoryStateProjection.SamePath(WindowsFileMetadata.ReadFinalPath(root.DangerousGetHandle()), SyncRootPath))
        {
            return new(id, Status.WrongRoot);
        }

        if (!CloudDirectoryStateProjection.SamePath(WindowsFileMetadata.ReadFinalPath(parent.DangerousGetHandle()), directory.FullPath))
        {
            return new(id, Status.Replaced);
        }

        PlaceholderCreationEvidence evidence;
        await using (ICloudStateTransaction preparation = await store.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            CloudStateCheckpoint? checkpoint = await preparation.Checkpoints.GetAsync(checkpointName, cancellationToken).ConfigureAwait(false);
            if (checkpoint is not null)
            {
                evidence = PlaceholderCreationEvidence.Decode(checkpoint.Value, id);
                Guid scope = await CloudDirectoryMoveEvidence.GetScopeAsync(preparation, false, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return VerifyRetainedCreation(evidence, scope, parentBinding, relativePath, specification.Kind,
                    identity, fingerprint, path, root);
            }

            if (!create)
            {
                return new(id, Status.NotFound);
            }

            using SafeFileHandle? existing = TryOpenCreationTarget(path);
            if (existing is not null || await HasRecoverableCreationCollisionAsync(preparation, specification, relativePath, cancellationToken)
                    .ConfigureAwait(false))
            {
                return new(id, Status.Conflict);
            }

            if (!IsSameRegisteredCreationRoot(root, parent))
            {
                return new(id, Status.WrongRoot);
            }

            // Check that this filesystem can query the independent USN before attempting a birth.
            try
            {
                _ = WindowsFileUsn.Read(parent, expectedDirectory: true);
            }
            catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException)
            {
                return new(id, Status.Unavailable, error: exception);
            }

            Guid storeScope = await CloudDirectoryMoveEvidence.GetScopeAsync(preparation, true, cancellationToken).ConfigureAwait(false);
            evidence = new(id, storeScope, SyncRootPath, relativePath, specification.Kind, parentBinding, identity, fingerprint);
            await preparation.Checkpoints.UpsertAsync(new(checkpointName, evidence.Encode(), DateTimeOffset.UtcNow), cancellationToken)
                .ConfigureAwait(false);
            await preparation.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        // The preparation is the irreversible boundary. A lost result from either transaction
        // must leave either the pending request or the completed receipt available through replay.
        await using ICloudStateTransaction projection = await store.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
        if (await HasRecoverableCreationCollisionAsync(projection, specification, relativePath, CancellationToken.None).ConfigureAwait(false))
        {
            return new(id, Status.Conflict);
        }

        CloudPlaceholderNativeBatchResult native = CloudPlaceholderPlatform.CreatePlaceholders(directory.FullPath, [specification], true);
        int error = native.EntriesProcessed == 1 && native.Entries[0].HResult < 0 ? native.Entries[0].HResult : native.HResult;
        if (error < 0)
        {
            return new(id, Status.Failed, error: CloudFilesException.FromHResult("CloudDirectory.CreateRecoverablePlaceholders", path, error));
        }

        if (native.EntriesProcessed != 1 || native.Entries[0].CreateUsn <= 0)
        {
            return new(id, Status.Unavailable);
        }

        using SafeFileHandle? target = TryOpenCreationTarget(path);
        if (target is null)
        {
            return new(id, Status.Missing);
        }

        long createUsn = native.Entries[0].CreateUsn;
        Guid objectId = ReadOriginalCreationTarget(target, evidence, path, root);
        // CfCreatePlaceholders gives a USN rather than a handle. Query that USN through the very
        // handle that supplies the file ID and identity bytes to reject a same-name replacement
        // between the native call and our open. Keep the handle through the official commit.
        try
        {
            if (objectId == Guid.Empty || WindowsFileUsn.Read(target, specification.Kind == CloudItemKind.Directory) != createUsn)
            {
                return new(id, Status.Unavailable);
            }
        }
        catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException)
        {
            return new(id, Status.Unavailable, error: exception);
        }

        evidence = evidence with { ObjectId = objectId, CreateUsn = createUsn };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        // LocalFileId on the item row is the legacy Cloud Files projection. The receipt retains
        // the full FILE_ID_INFO binding without truncation or dependence on that mutable row.
        await projection.Items.UpsertAsync(new(specification.Identity.ItemId, specification.Identity.RemoteId,
            relativePath, specification.Kind, specification.Identity.RemoteRevision, localFileId: null, isTombstone: false, now),
            CancellationToken.None).ConfigureAwait(false);
        await CloudDirectoryProvenance.RetainProjectionAsync(projection, SyncRootPath, relativePath, specification.Kind,
            CancellationToken.None).ConfigureAwait(false);
        await projection.Checkpoints.UpsertAsync(new(checkpointName, evidence.Encode(), now), CancellationToken.None).ConfigureAwait(false);
        if (ReadOriginalCreationTarget(target, evidence, path, root) != objectId)
        {
            return new(id, Status.Unavailable);
        }

        await projection.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        return new(id, Status.Created, evidence.ToReceipt());
    }

    private CloudPlaceholderCreationRecoveryResult VerifyRetainedCreation(PlaceholderCreationEvidence evidence, Guid scope,
        CloudLocalFileBinding parent, string relativePath, CloudItemKind kind, byte[] identity, byte[] fingerprint,
        string path, SafeFileHandle root)
    {
        Status status;
        if (!CloudDirectoryStateProjection.SamePath(evidence.RootPath, SyncRootPath) || evidence.StoreScope != scope ||
            evidence.Parent.SyncRootFileId != parent.SyncRootFileId || evidence.Parent.VolumeSerialNumber != parent.VolumeSerialNumber)
        {
            status = Status.WrongRoot;
        }
        else if (!CloudDirectoryStateProjection.SamePath(evidence.RelativePath, relativePath) || evidence.Kind != kind ||
            !evidence.Identity.AsSpan().SequenceEqual(identity) || !evidence.SpecificationHash.AsSpan().SequenceEqual(fingerprint))
        {
            status = Status.Conflict;
        }
        else if (evidence.Parent.LocalFileId != parent.LocalFileId)
        {
            status = Status.Replaced;
        }
        else if (!evidence.IsCommitted)
        {
            status = Status.Pending;
        }
        else
        {
            using SafeFileHandle? target = TryOpenCreationTarget(path);
            if (target is null)
            {
                status = Status.Missing;
            }
            else if (ReadOriginalCreationTarget(target, evidence, path, root) != evidence.ObjectId)
            {
                status = Status.Replaced;
            }
            else
            {
                return new(evidence.OperationId, Status.Recovered, evidence.ToReceipt());
            }
        }

        return new(evidence.OperationId, status);
    }

    private static SafeFileHandle? TryOpenCreationTarget(string path)
    {
        try
        {
            return WindowsFileMetadata.OpenNamespaceGuard(path);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return null;
        }
    }

    private static CloudLocalFileBinding ReadCreationParentBinding(SafeFileHandle root, SafeFileHandle parent)
    {
        WindowsFileMetadata.FileIdentity rootId = WindowsFileMetadata.ReadIdentity(root.DangerousGetHandle());
        WindowsFileMetadata.FileIdentity parentId = WindowsFileMetadata.ReadIdentity(parent.DangerousGetHandle());
        if (rootId.VolumeSerialNumber == 0 || rootId.VolumeSerialNumber != parentId.VolumeSerialNumber ||
            rootId.FileId == Guid.Empty || parentId.FileId == Guid.Empty)
        {
            throw new Win32Exception(50, "Recoverable placeholder creation requires full file IDs on one native volume.");
        }

        return new(rootId.VolumeSerialNumber, rootId.FileId, parentId.FileId);
    }

    private static Guid ReadOriginalCreationTarget(SafeFileHandle target, PlaceholderCreationEvidence evidence, string path, SafeFileHandle root)
    {
        nint handle = target.DangerousGetHandle();
        NativeFileMetadata native = WindowsFileMetadata.Read(handle);
        if (native.Identity.VolumeSerialNumber != evidence.Parent.VolumeSerialNumber || native.DeletePending || native.Links != 1 ||
            native.Directory != (evidence.Kind == CloudItemKind.Directory) ||
            (evidence.IsCommitted && native.Identity.FileId != evidence.ObjectId) ||
            !native.PlaceholderIdentity.AsSpan().SequenceEqual(evidence.Identity) ||
            !CloudDirectoryStateProjection.SamePath(WindowsFileMetadata.ReadFinalPath(handle), path) ||
            !IsSameRegisteredCreationRoot(root, target))
        {
            return Guid.Empty;
        }

        return native.Identity.FileId;
    }

    private static bool IsSameRegisteredCreationRoot(SafeFileHandle root, SafeFileHandle item) =>
        ReadRegisteredCreationRoot(root) == ReadRegisteredCreationRoot(item);

    private static long ReadRegisteredCreationRoot(SafeFileHandle handle)
    {
        int result = NativeSyncRoot.Query(handle.DangerousGetHandle(), out NativeSyncRootInfo? root);
        if (result < 0)
        {
            throw new NativeFileException("CfGetSyncRootInfoByHandle", result);
        }

        if (root is null || root.FileId == 0)
        {
            throw new Win32Exception(50, "Windows did not provide a registered sync-root identifier.");
        }

        return root.FileId;
    }

    private static async ValueTask<bool> HasRecoverableCreationCollisionAsync(ICloudStateTransaction transaction,
        CloudPlaceholderSpec specification, string relativePath, CancellationToken cancellationToken)
    {
        CloudPlaceholderIdentity identity = specification.Identity;
        CloudItemState? atPath = await transaction.Items.GetByRelativePathAsync(relativePath, cancellationToken).ConfigureAwait(false);
        CloudItemState? atId = await transaction.Items.GetByItemIdAsync(identity.ItemId, cancellationToken).ConfigureAwait(false);
        CloudItemState? atRemote = await transaction.Items.GetByRemoteIdAsync(identity.RemoteId, cancellationToken).ConfigureAwait(false);
        return (atPath is { IsTombstone: false } && !Matches(atPath)) ||
            (atId is not null && !Matches(atId)) || (atRemote is not null && !Matches(atRemote));

        bool Matches(CloudItemState item) => !item.IsTombstone && item.ItemId == identity.ItemId && item.RemoteId == identity.RemoteId &&
            item.RemoteRevision == identity.RemoteRevision && item.Kind == specification.Kind && item.LocalFileId is null &&
            CloudDirectoryStateProjection.SamePath(item.RelativePath, relativePath);
    }
}
