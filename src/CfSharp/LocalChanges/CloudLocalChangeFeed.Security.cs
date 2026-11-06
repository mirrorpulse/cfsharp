using System.ComponentModel;

using CfSharp.Native;

using Microsoft.Win32.SafeHandles;

namespace CfSharp;

public sealed partial class CloudLocalChangeFeed
{
    private SecurityFeedProjection? TryBindSecurityObservation(CloudItemPath path, CloudItemState? state,
        LocalSecurityChangeIdentity? identity)
    {
        // Official creation/conversion rows can omit LocalFileId. The kernel/native ID match
        // remains mandatory; the placeholder blob binds that object to the official identity.
        // A durable file ID, when available, is an additional constraint rather than a prerequisite.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299) || state is null || state.IsTombstone || identity is null ||
            identity.Value.FileId == 0 || identity.Value.VolumeSerialNumber == 0 || identity.Value.RootFileId == Guid.Empty ||
            (state.LocalFileId is long durableFileId && unchecked((ulong)durableFileId) != identity.Value.FileId) ||
            (state.Kind == CloudItemKind.Directory) != identity.Value.IsDirectory)
        { return null; }

        CloudPathHandleLease? parents = null;
        SafeFileHandle? guard = null;
        try
        {
            parents = CloudPathHandleLease.OpenParentChains(_syncRootPath, [path.FullPath]);
            guard = WindowsFileMetadata.OpenNamespaceGuard(path.FullPath);
            LocalCloudItemInspection observed = _securityInspection?.Invoke(guard, path.FullPath, state.Kind, _syncRootPath)
                ?? CloudItemInspector.Inspect(guard, path.FullPath, state.Kind, _syncRootPath);
            if (observed.LocalFileId is not long nativeFileId || unchecked((ulong)nativeFileId) != identity.Value.FileId ||
                observed.LocalBinding?.VolumeSerialNumber != identity.Value.VolumeSerialNumber ||
                observed.LocalBinding?.SyncRootFileId != identity.Value.RootFileId ||
                !CloudPlaceholderIdentity.TryDecode(observed.PlaceholderIdentity, out CloudPlaceholderIdentity? placeholder) ||
                placeholder.ItemId != state.ItemId || !string.Equals(placeholder.RemoteId, state.RemoteId, StringComparison.Ordinal))
            { return null; }
            SecurityFeedProjection result = new(_syncRootPath, path, state.Kind, observed, guard, parents, _securityInspection);
            guard = null;
            parents = null;
            return result;
        }
        catch (Exception exception) when (exception is CloudFilesException or Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Missing/replaced objects, denied metadata access and unavailable bindings are
            // observations to reconcile, not permission to create an item from a stale path.
            // CfGetPlaceholderInfo can fail after attributes were read if the object reverted.
            return null;
        }
        finally
        {
            guard?.Dispose();
            parents?.Dispose();
        }
    }

    private sealed class SecurityFeedProjection(string root, CloudItemPath path, CloudItemKind kind,
        LocalCloudItemInspection expected, SafeFileHandle guard, IDisposable parents, CloudSecurityInspection? inspection) : IDisposable
    {
        internal void Validate()
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299)) { throw new PlatformNotSupportedException(); }
            try
            {
                LocalCloudItemInspection current = inspection?.Invoke(guard, path.FullPath, kind, root)
                    ?? CloudItemInspector.Inspect(guard, path.FullPath, kind, root);
                if (current.LocalBinding != expected.LocalBinding || current.LocalFileId != expected.LocalFileId ||
                    !current.PlaceholderIdentity.AsSpan().SequenceEqual(expected.PlaceholderIdentity))
                { throw new CloudNamespaceObservationUncertainException(kind == CloudItemKind.Directory); }
            }
            catch (Exception exception) when (exception is CloudFilesException or Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
            { throw new CloudNamespaceObservationUncertainException(kind == CloudItemKind.Directory, exception); }
        }

        public void Dispose()
        {
            guard.Dispose();
            parents.Dispose();
        }
    }
}

internal delegate LocalCloudItemInspection CloudSecurityInspection(SafeFileHandle handle, string path, CloudItemKind kind, string root);
