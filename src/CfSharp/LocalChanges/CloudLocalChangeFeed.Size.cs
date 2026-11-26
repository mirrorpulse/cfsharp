using System.ComponentModel;

using CfSharp.Native;

using Microsoft.Win32.SafeHandles;

namespace CfSharp;

public sealed partial class CloudLocalChangeFeed
{
    private enum SizeObservationDisposition { Content, CleanPlaceholder, Unbound }

    private sealed record SizeObservationBinding(CloudLocalFileBinding Local, CloudItemKind Kind,
        CloudPlaceholderIdentity? Placeholder, LocalSizeChange? ContentObservation = null);

    private sealed record StagedSizeObservation(CloudItemPath Path, SizeObservationBinding Binding,
        CloudOperationJournalEntry Entry) : IStagedObservation
    {
        public ValueTask PublishAsync(CloudLocalChangeFeed feed, CancellationToken cancellationToken)
        {
            feed.ValidateSizeObservationBinding(Path, Binding);
            return feed.CompleteValidatedObservationAsync(Entry.OperationId, Entry.Sequence, cancellationToken);
        }

        public void Dispose() { }
    }

    private SizeObservationDisposition InspectSizeObservation(CloudItemPath path, CloudItemState owner, LocalSizeChange size,
        out SizeObservationBinding? binding)
    {
        binding = null;
        LocalSecurityChangeIdentity identity = size.Identity;
        CloudItemKind kind = identity.IsDirectory ? CloudItemKind.Directory : CloudItemKind.File;
        bool capturedPlaceholder = size.PlaceholderState.HasFlag(CloudPlaceholderState.Placeholder);
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
            // Match the notice to the current object. Reusing an ordinary item's
            // existing row also requires its recorded FileId to identify that object.
            if (NtfsFileId(item.LocalBinding) is not long fileId || unchecked((ulong)fileId) != identity.FileId ||
                item.LocalBinding?.VolumeSerialNumber != identity.VolumeSerialNumber ||
                item.LocalBinding.SyncRootFileId != identity.RootFileId ||
                owner.IsTombstone || owner.Kind != kind ||
                (!capturedPlaceholder && owner.LocalFileId is null) ||
                (owner.LocalFileId is long recorded && recorded != fileId) ||
                !MatchesAttributePath(reference, path))
            {
                throw new CloudNamespaceObservationUncertainException(identity.IsDirectory);
            }

            if (capturedPlaceholder != item.PlaceholderState.HasFlag(CloudPlaceholderState.Placeholder))
            {
                throw new CloudNamespaceObservationUncertainException(identity.IsDirectory);
            }
            CloudPlaceholderIdentity? placeholder = null;
            // A revision ACK can advance the official row without rewriting the native
            // revision. Capture the actual native value for both publication checks.
            if (capturedPlaceholder &&
                (!CloudPlaceholderIdentity.TryDecode(item.PlaceholderIdentity, out placeholder) ||
                    placeholder.ItemId != owner.ItemId || placeholder.RemoteId != owner.RemoteId))
            {
                throw new CloudNamespaceObservationUncertainException(identity.IsDirectory);
            }
            if (kind != CloudItemKind.File || !capturedPlaceholder)
            {
                binding = new(item.LocalBinding, kind, placeholder);
                return SizeObservationDisposition.Content;
            }

            // Hydration can signal Size when allocation grows. Metadata changes can
            // already have cleared in-sync, so that flag alone cannot prove a data edit.
            // Keep last-write notifications independent, including overlapping writes.
            bool capturedInSync = size.PlaceholderState.HasFlag(CloudPlaceholderState.InSync);
            bool inSyncNow = item.SynchronizationState == CloudSynchronizationState.InSync;
            if (capturedInSync != inSyncNow || item.Length != size.Length ||
                (inSyncNow
                    ? item.ModifiedDataSize != 0 || item.OnDiskDataSize != item.ValidatedDataSize
                    : !MatchesSizeContentObservation(item, size)))
            {
                // The current dirty ranges may belong to a later write. Preserve the
                // original for reconciliation if its native timestamps disagree.
                throw new CloudNamespaceObservationUncertainException(isDirectory: false);
            }
            if (inSyncNow) { return SizeObservationDisposition.CleanPlaceholder; }

            binding = new(item.LocalBinding, kind, placeholder, size);
            return SizeObservationDisposition.Content;
        }
        catch (Win32Exception exception) when (kind == CloudItemKind.File && !capturedPlaceholder && exception.NativeErrorCode is 2 or 3)
        {
            return SizeObservationDisposition.Unbound;
        }
        catch (CloudNamespaceObservationUncertainException) when (kind == CloudItemKind.File && !capturedPlaceholder &&
            IsMissingSizeObservationPath(path.FullPath))
        {
            return SizeObservationDisposition.Unbound;
        }
        catch (Exception exception) when (exception is not CloudNamespaceObservationUncertainException &&
            exception is (CloudPathReparsePointException or CloudFilesException or Win32Exception or
                UnauthorizedAccessException or InvalidOperationException or IOException))
        {
            throw new CloudNamespaceObservationUncertainException(identity.IsDirectory, exception);
        }
    }

    private void ValidateSizeObservationBinding(CloudItemPath path, SizeObservationBinding binding)
    {
        bool isDirectory = binding.Kind == CloudItemKind.Directory;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            throw new CloudNamespaceObservationUncertainException(isDirectory);
        }
        try
        {
            using SafeFileHandle reference = WindowsFileMetadata.OpenMetadataReference(path.FullPath);
            LocalCloudItemInspection item = CloudItemInspector.Inspect(reference, path.FullPath, binding.Kind, _syncRootPath);
            // An in-place CFAPI update can change the cloud identity while preserving the NTFS file ID.
            bool sameIdentity = binding.Placeholder is null
                ? !item.PlaceholderState.HasFlag(CloudPlaceholderState.Placeholder)
                : CloudPlaceholderIdentity.TryDecode(item.PlaceholderIdentity, out CloudPlaceholderIdentity? current) &&
                    binding.Placeholder.Equals(current);
            if (item.LocalBinding == binding.Local && sameIdentity && MatchesAttributePath(reference, path) &&
                (binding.ContentObservation is not { } observation || MatchesSizeContentObservation(item, observation)))
            {
                return;
            }
        }
        catch (Exception exception) when (exception is CloudPathReparsePointException or CloudFilesException or
            Win32Exception or UnauthorizedAccessException or InvalidOperationException or IOException)
        {
            throw new CloudNamespaceObservationUncertainException(isDirectory, exception);
        }
        throw new CloudNamespaceObservationUncertainException(isDirectory);
    }

    private static bool MatchesSizeContentObservation(LocalCloudItemInspection item, LocalSizeChange size) =>
        item.SynchronizationState == CloudSynchronizationState.NotInSync &&
        item.Length == size.Length && item.ModifiedDataSize is > 0 &&
        size.LastWriteTime > 0 && item.LastWriteTime?.ToFileTime() == size.LastWriteTime &&
        size.ChangeTime > 0 && item.ChangeTime == size.ChangeTime;

    private static bool IsMissingSizeObservationPath(string path)
    {
        try { _ = File.GetAttributes(path); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return true; }
        return false;
    }
}
