using System.ComponentModel;

using CfSharp.Native;

using Microsoft.Win32.SafeHandles;

namespace CfSharp;

public sealed partial class CloudLocalChangeFeed
{
    private bool IsCleanPlaceholderSizeObservation(CloudItemPath path, CloudItemState? owner, LocalSizeChange size)
    {
        LocalSecurityChangeIdentity identity = size.Identity;
        CloudItemKind kind = identity.IsDirectory ? CloudItemKind.Directory : CloudItemKind.File;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            throw new CloudNamespaceObservationUncertainException(identity.IsDirectory);
        }
        try
        {
            using SafeFileHandle reference = WindowsFileMetadata.OpenMetadataReference(path.FullPath);
            if (!MatchesAttributePath(reference, path))
            {
                throw new CloudNamespaceObservationUncertainException(identity.IsDirectory);
            }
            LocalCloudItemInspection item = CloudItemInspector.Inspect(reference, path.FullPath, kind, _syncRootPath);
            if (NtfsFileId(item.LocalBinding) is not long fileId || unchecked((ulong)fileId) != identity.FileId ||
                item.LocalBinding?.VolumeSerialNumber != identity.VolumeSerialNumber ||
                item.LocalBinding.SyncRootFileId != identity.RootFileId ||
                (owner?.LocalFileId is long recorded && recorded != fileId) || owner?.IsTombstone == true ||
                !MatchesAttributePath(reference, path))
            {
                throw new CloudNamespaceObservationUncertainException(identity.IsDirectory);
            }

            bool capturedPlaceholder = size.PlaceholderState.HasFlag(CloudPlaceholderState.Placeholder);
            if (capturedPlaceholder != item.PlaceholderState.HasFlag(CloudPlaceholderState.Placeholder))
            {
                throw new CloudNamespaceObservationUncertainException(identity.IsDirectory);
            }
            if (kind != CloudItemKind.File || !capturedPlaceholder) { return false; }
            if (owner is not { Kind: CloudItemKind.File } ||
                !CloudPlaceholderIdentity.TryDecode(item.PlaceholderIdentity, out CloudPlaceholderIdentity? placeholder) ||
                placeholder.ItemId != owner.ItemId || placeholder.RemoteId != owner.RemoteId ||
                placeholder.RemoteRevision != owner.RemoteRevision)
            {
                throw new CloudNamespaceObservationUncertainException(isDirectory: false);
            }

            // On Windows, hydration can change allocation and signal Size while the
            // placeholder remains in sync. A logical data modification clears in-sync.
            // Keep last-write notifications independent, including overlapping writes.
            bool capturedInSync = size.PlaceholderState.HasFlag(CloudPlaceholderState.InSync);
            bool inSyncNow = item.SynchronizationState == CloudSynchronizationState.InSync;
            if (capturedInSync != inSyncNow || item.Length != size.Length ||
                (inSyncNow && (item.ModifiedDataSize != 0 || item.OnDiskDataSize != item.ValidatedDataSize)))
            {
                // This can be a delayed allocation notice delivered after a genuine edit.
                // Preserve the original without letting it authorize an upload.
                throw new CloudNamespaceObservationUncertainException(isDirectory: false);
            }
            return inSyncNow;
        }
        catch (Exception exception) when (exception is not CloudNamespaceObservationUncertainException &&
            exception is (CloudPathReparsePointException or CloudFilesException or Win32Exception or
                UnauthorizedAccessException or InvalidOperationException or IOException))
        {
            throw new CloudNamespaceObservationUncertainException(identity.IsDirectory, exception);
        }
    }
}
