using System.Runtime.Versioning;

using CfSharp.Native;

namespace CfSharp;

[SupportedOSPlatform("windows10.0.16299")]
internal static class CloudPlaceholderMutationPlatform
{
    internal static unsafe long Convert(
        string path,
        CloudPlaceholderIdentity identity,
        CloudPlaceholderConversionOptions options)
    {
        byte[] encodedIdentity = identity.Encode();
        CfOpenFileFlags openFlags = options.Dehydrate
            ? CfOpenFileFlags.Exclusive | CfOpenFileFlags.WriteAccess
            : CfOpenFileFlags.Foreground | CfOpenFileFlags.WriteAccess;
        using SafeCloudFilesProtectedHandle protectedHandle = SafeCloudFilesProtectedHandle.Open(
            path,
            openFlags,
            "CloudItem.ConvertToPlaceholder.Open");
        using SafeCloudFilesProtectedHandle.CloudFilesHandleReference handle =
            protectedHandle.AcquireReference();
        return Convert(handle.Win32Handle, path, encodedIdentity, options);
    }

    internal static unsafe long Convert(nint fileHandle, string path, byte[] encodedIdentity,
        CloudPlaceholderConversionOptions options)
    {
        fixed (byte* identityPointer = encodedIdentity)
        {
            long operationUsn;
            int result = CfApi.CfConvertToPlaceholder(
                fileHandle,
                identityPointer,
                checked((uint)encodedIdentity.Length),
                CreateConvertFlags(options),
                &operationUsn,
                overlapped: null);
            if (result < 0)
            {
                throw CloudFilesException.FromHResult(
                    "CloudItem.ConvertToPlaceholder",
                    path,
                    result);
            }

            return operationUsn;
        }
    }

    internal static unsafe long Update(
        string path,
        CloudPlaceholderPatch patch,
        long currentFileSize)
    {
        byte[] identity = patch.ReplacementIdentity?.Encode() ?? [];
        CfFileRange[] ranges = patch.DehydrateRanges
            .Select(static range => new CfFileRange
            {
                StartingOffset = range.Offset,
                Length = range.ExtendsToEnd ? CfApi.EndOfFile : range.Length,
            })
            .ToArray();
        CfFsMetadata metadata = CreateMetadata(patch.Metadata, patch.FileSize ?? currentFileSize);
        bool dehydrates = patch.DehydrateWholeFile || ranges.Length != 0;
        CfOpenFileFlags openFlags = dehydrates
            ? CfOpenFileFlags.Exclusive | CfOpenFileFlags.WriteAccess
            : CfOpenFileFlags.Foreground | CfOpenFileFlags.WriteAccess;
        using SafeCloudFilesProtectedHandle protectedHandle = SafeCloudFilesProtectedHandle.Open(
            path,
            openFlags,
            "CloudItem.UpdatePlaceholder.Open");
        using SafeCloudFilesProtectedHandle.CloudFilesHandleReference handle =
            protectedHandle.AcquireReference();
        fixed (byte* identityPointer = identity)
        fixed (CfFileRange* rangesPointer = ranges)
        {
            long operationUsn = patch.ExpectedUsn ?? 0;
            int result = CfApi.CfUpdatePlaceholder(
                handle.Win32Handle,
                patch.Metadata is null ? null : &metadata,
                identityPointer,
                checked((uint)identity.Length),
                rangesPointer,
                checked((uint)ranges.Length),
                CreateUpdateFlags(patch),
                &operationUsn,
                overlapped: null);
            if (result < 0)
            {
                throw CloudFilesException.FromHResult(
                    "CloudItem.UpdatePlaceholder",
                    path,
                    result);
            }

            return operationUsn;
        }
    }

    internal static unsafe void Revert(string path)
    {
        using SafeCloudFilesProtectedHandle protectedHandle = SafeCloudFilesProtectedHandle.Open(
            path,
            CfOpenFileFlags.Foreground | CfOpenFileFlags.WriteAccess,
            "CloudItem.RevertToRegularItem.Open");
        using SafeCloudFilesProtectedHandle.CloudFilesHandleReference handle =
            protectedHandle.AcquireReference();
        int result = CfApi.CfRevertPlaceholder(
            handle.Win32Handle,
            CfRevertFlags.None,
            overlapped: null);
        if (result < 0)
        {
            throw CloudFilesException.FromHResult(
                "CloudItem.RevertToRegularItem",
                path,
                result);
        }
    }

    private static CfConvertFlags CreateConvertFlags(
        CloudPlaceholderConversionOptions options)
    {
        CfConvertFlags flags = CfConvertFlags.None;
        if (options.MarkInSync)
        {
            flags |= CfConvertFlags.MarkInSync;
        }

        if (options.Dehydrate)
        {
            flags |= CfConvertFlags.Dehydrate;
        }

        if (options.PopulationState is CloudDirectoryPopulationState.Partial)
        {
            flags |= CfConvertFlags.EnableOnDemandPopulation;
        }

        if (options.ContentMode is CloudFileContentMode.AlwaysFull)
        {
            flags |= CfConvertFlags.AlwaysFull;
        }

        if (options.ForceConversion)
        {
            flags |= CfConvertFlags.ForceConvertToCloudFile;
        }

        return flags;
    }

    private static CfUpdateFlags CreateUpdateFlags(CloudPlaceholderPatch patch)
    {
        CfUpdateFlags flags = CfUpdateFlags.None;
        if (patch.RequireInSync)
        {
            flags |= CfUpdateFlags.VerifyInSync;
        }

        flags |= patch.SynchronizationChange switch
        {
            CloudPlaceholderSynchronizationChange.MarkInSync => CfUpdateFlags.MarkInSync,
            CloudPlaceholderSynchronizationChange.MarkNotInSync => CfUpdateFlags.ClearInSync,
            _ => CfUpdateFlags.None,
        };
        if (patch.DehydrateWholeFile)
        {
            flags |= CfUpdateFlags.Dehydrate;
        }

        flags |= patch.PopulationState switch
        {
            CloudDirectoryPopulationState.Partial => CfUpdateFlags.EnableOnDemandPopulation,
            CloudDirectoryPopulationState.Complete => CfUpdateFlags.DisableOnDemandPopulation,
            _ => CfUpdateFlags.None,
        };
        if (patch.IdentityChange is CloudPlaceholderIdentityChange.Remove)
        {
            flags |= CfUpdateFlags.RemoveFileIdentity;
        }

        if (patch.RemoveExtrinsicProperties)
        {
            flags |= CfUpdateFlags.RemoveProperty;
        }

        if (patch.Metadata is not null &&
            patch.MetadataWriteMode is CloudMetadataWriteMode.PassThrough)
        {
            flags |= CfUpdateFlags.PassthroughFsMetadata;
        }

        flags |= patch.ContentMode switch
        {
            CloudFileContentMode.AlwaysFull => CfUpdateFlags.AlwaysFull,
            CloudFileContentMode.AllowPartial => CfUpdateFlags.AllowPartial,
            _ => CfUpdateFlags.None,
        };
        return flags;
    }

    private static CfFsMetadata CreateMetadata(
        CloudPlaceholderMetadata? metadata,
        long currentFileSize) =>
        metadata is null
            ? default
            : new CfFsMetadata
            {
                BasicInfo = new CfFileBasicInfo
                {
                    CreationTime = metadata.CreationTime?.ToFileTime() ?? 0,
                    LastAccessTime = metadata.LastAccessTime?.ToFileTime() ?? 0,
                    LastWriteTime = metadata.LastWriteTime?.ToFileTime() ?? 0,
                    ChangeTime = metadata.ChangeTime?.ToFileTime() ?? 0,
                    FileAttributes = (uint)metadata.Attributes,
                },
                FileSize = metadata.Kind is CloudItemKind.File ? currentFileSize : 0,
            };
}
