using System.ComponentModel;
using System.Runtime.Versioning;

using CfSharp.Native;

using Microsoft.Win32.SafeHandles;

namespace CfSharp;

[SupportedOSPlatform("windows10.0.16299")]
internal static class CloudLocalFileBindingPlatform
{
    internal static CloudLocalFileBinding? TryRead(nint file, string rootPath)
    {
        try
        {
            return Read(file, rootPath);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 1 or 50 or 87)
        {
            // Additive snapshot metadata must not break inspection on storage that cannot
            // supply complete IDs. Such observations cannot be used to construct a proof.
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    internal static CloudLocalFileBinding Read(nint file, string rootPath)
    {
        WindowsFileMetadata.FileIdentity local = WindowsFileMetadata.ReadIdentity(file);
        using SafeFileHandle root = WindowsFileMetadata.Open(rootPath);
        WindowsFileMetadata.FileIdentity parent = WindowsFileMetadata.ReadIdentity(root.DangerousGetHandle());
        if (local.VolumeSerialNumber != parent.VolumeSerialNumber)
        {
            throw new NotSupportedException("The file and sync root must belong to the same native volume.");
        }

        if (local.VolumeSerialNumber == 0 || parent.FileId == Guid.Empty || local.FileId == Guid.Empty)
        {
            throw new NotSupportedException("The storage did not provide a complete native object binding.");
        }

        return new CloudLocalFileBinding(local.VolumeSerialNumber, parent.FileId, local.FileId);
    }
}
