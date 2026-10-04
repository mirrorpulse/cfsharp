using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using CfSharp.Native;

using Microsoft.Win32.SafeHandles;

namespace CfSharp;

internal sealed class LocalCloudItemInspection
{
    internal required bool Exists { get; init; }

    internal required CloudItemKind Kind { get; init; }

    internal FileAttributes? Attributes { get; init; }

    internal long? Length { get; init; }

    internal DateTimeOffset? CreationTime { get; init; }

    internal DateTimeOffset? LastWriteTime { get; init; }

    internal DateTimeOffset? LastAccessTime { get; init; }

    internal CloudPlaceholderState PlaceholderState { get; init; }

    internal CloudContentAvailability ContentAvailability { get; init; }

    internal CloudPinState PinState { get; init; }

    internal CloudSynchronizationState SynchronizationState { get; init; }

    internal long? LocalFileId { get; init; }

    internal long? SyncRootFileId { get; init; }

    internal CloudLocalFileBinding? LocalBinding { get; init; }

    internal long? OnDiskDataSize { get; init; }

    internal long? ValidatedDataSize { get; init; }

    internal long? ModifiedDataSize { get; init; }

    internal long? PropertyDataSize { get; init; }

    internal byte[] PlaceholderIdentity { get; init; } = [];
}

[SupportedOSPlatform("windows10.0.16299")]
internal static partial class CloudItemInspector
{
    private const uint FileShareReadWriteDelete = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int FileAttributeTagInformationClass = 9;

    internal static unsafe LocalCloudItemInspection Inspect(
        string path,
        CloudItemKind expectedKind,
        string? syncRootPath = null)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return new LocalCloudItemInspection
            {
                Exists = false,
                Kind = expectedKind,
                ContentAvailability = CloudContentAvailability.NotApplicable,
                PinState = CloudPinState.Unspecified,
                SynchronizationState = CloudSynchronizationState.NotApplicable,
            };
        }

        using SafeFileHandle handle = CreateFile(
            path,
            desiredAccess: 0,
            FileShareReadWriteDelete,
            securityAttributes: 0,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            templateFile: 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            if (error is 2 or 3)
            {
                return new LocalCloudItemInspection
                {
                    Exists = false,
                    Kind = expectedKind,
                    ContentAvailability = CloudContentAvailability.NotApplicable,
                    PinState = CloudPinState.Unspecified,
                    SynchronizationState = CloudSynchronizationState.NotApplicable,
                };
            }

            throw CloudFilesException.FromHResult(
                "CloudItem.Inspect.Open",
                path,
                Marshal.GetHRForLastWin32Error());
        }

        FileAttributeTagInfo attributeTagInfo;
        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInformationClass,
                &attributeTagInfo,
                (uint)sizeof(FileAttributeTagInfo)))
        {
            throw CloudFilesException.FromHResult(
                "CloudItem.Inspect.Attributes",
                path,
                Marshal.GetHRForLastWin32Error());
        }

        FileAttributes attributes = (FileAttributes)attributeTagInfo.FileAttributes;
        CloudItemKind actualKind = attributes.HasFlag(FileAttributes.Directory)
            ? CloudItemKind.Directory
            : CloudItemKind.File;
        if (actualKind != expectedKind)
        {
            throw new InvalidOperationException(
                $"The item at '{path}' is a {actualKind.ToString().ToLowerInvariant()}, " +
                $"not a {expectedKind.ToString().ToLowerInvariant()}.");
        }

        CfPlaceholderState nativeState = CfApi.CfGetPlaceholderStateFromAttributeTag(
            attributeTagInfo.FileAttributes,
            attributeTagInfo.ReparseTag);
        if (nativeState == CfPlaceholderState.Invalid)
        {
            int error = Marshal.GetLastPInvokeError();
            int hresult = error == 0
                ? unchecked((int)0x8007000D)
                : Marshal.GetHRForLastWin32Error();
            throw CloudFilesException.FromHResult("CloudItem.Inspect.State", path, hresult);
        }

        FileSystemInfo fileSystemInfo = actualKind == CloudItemKind.Directory
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        fileSystemInfo.Refresh();
        long? length = actualKind == CloudItemKind.File ? ((FileInfo)fileSystemInfo).Length : null;
        PlaceholderInformation? placeholder = nativeState.HasFlag(CfPlaceholderState.Placeholder)
            ? ReadPlaceholderInformation(handle, path)
            : null;
        CloudPlaceholderState placeholderState = (CloudPlaceholderState)(uint)nativeState;
        return new LocalCloudItemInspection
        {
            Exists = true,
            Kind = actualKind,
            Attributes = attributes,
            Length = length,
            CreationTime = ToDateTimeOffset(fileSystemInfo.CreationTimeUtc),
            LastWriteTime = ToDateTimeOffset(fileSystemInfo.LastWriteTimeUtc),
            LastAccessTime = ToDateTimeOffset(fileSystemInfo.LastAccessTimeUtc),
            PlaceholderState = placeholderState,
            ContentAvailability = GetContentAvailability(
                actualKind,
                length,
                placeholderState,
                placeholder),
            PinState = placeholder?.PinState ?? CloudPinState.Unspecified,
            SynchronizationState = placeholder?.SynchronizationState ??
                (placeholderState.HasFlag(CloudPlaceholderState.InSync)
                    ? CloudSynchronizationState.InSync
                    : CloudSynchronizationState.NotApplicable),
            LocalFileId = placeholder?.FileId,
            SyncRootFileId = placeholder?.SyncRootFileId,
            LocalBinding = actualKind == CloudItemKind.File && syncRootPath is not null &&
                (!attributes.HasFlag(FileAttributes.ReparsePoint) || placeholder is not null)
                ? CloudLocalFileBindingPlatform.TryRead(handle.DangerousGetHandle(), syncRootPath)
                : null,
            OnDiskDataSize = placeholder?.OnDiskDataSize,
            ValidatedDataSize = placeholder?.ValidatedDataSize,
            ModifiedDataSize = placeholder?.ModifiedDataSize,
            PropertyDataSize = placeholder?.PropertyDataSize,
            PlaceholderIdentity = placeholder?.Identity ?? [],
        };
    }

    internal static unsafe bool IsCloudPlaceholder(string path)
    {
        using SafeFileHandle handle = CreateFile(
            path,
            desiredAccess: 0,
            FileShareReadWriteDelete,
            securityAttributes: 0,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            templateFile: 0);
        if (handle.IsInvalid)
        {
            return false;
        }

        FileAttributeTagInfo attributeTagInfo;
        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInformationClass,
                &attributeTagInfo,
                (uint)sizeof(FileAttributeTagInfo)) ||
            !((FileAttributes)attributeTagInfo.FileAttributes).HasFlag(FileAttributes.ReparsePoint))
        {
            return false;
        }

        CfPlaceholderState state = CfApi.CfGetPlaceholderStateFromAttributeTag(
            attributeTagInfo.FileAttributes,
            attributeTagInfo.ReparseTag);
        return state != CfPlaceholderState.Invalid &&
            state.HasFlag(CfPlaceholderState.Placeholder);
    }

    private static unsafe PlaceholderInformation ReadPlaceholderInformation(
        SafeFileHandle handle,
        string path)
    {
        int bufferSize = checked(sizeof(CfPlaceholderStandardInfo) + CfApi.MaxFileIdentityLength);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(bufferSize);
        fixed (byte* bufferPointer = buffer)
        {
            uint returnedLength;
            int result = CfApi.CfGetPlaceholderInfo(
                handle.DangerousGetHandle(),
                CfPlaceholderInfoClass.Standard,
                bufferPointer,
                checked((uint)bufferSize),
                &returnedLength);
            if (result < 0)
            {
                throw CloudFilesException.FromHResult(
                    "CloudItem.Inspect.PlaceholderInfo",
                    path,
                    result);
            }

            CfPlaceholderStandardInfo* info = (CfPlaceholderStandardInfo*)bufferPointer;
            int identityLength = checked((int)info->FileIdentityLength);
            int identityOffset = Marshal.OffsetOf<CfPlaceholderStandardInfo>(
                nameof(CfPlaceholderStandardInfo.FileIdentity)).ToInt32();
            int requiredLength = checked(identityOffset + identityLength);
            if (identityLength > CfApi.MaxFileIdentityLength ||
                returnedLength > (uint)bufferSize ||
                returnedLength < checked((uint)requiredLength))
            {
                throw new InvalidDataException(
                    "Windows returned invalid Cloud Files placeholder information lengths.");
            }

            return new PlaceholderInformation(
                info->OnDiskDataSize,
                info->ValidatedDataSize,
                info->ModifiedDataSize,
                info->PropertiesSize,
                MapPinState(info->PinState),
                info->InSyncState == CfInSyncState.InSync
                    ? CloudSynchronizationState.InSync
                    : CloudSynchronizationState.NotInSync,
                info->FileId,
                info->SyncRootFileId,
                new ReadOnlySpan<byte>(info->FileIdentity, identityLength).ToArray());
        }
    }

    private static CloudContentAvailability GetContentAvailability(
        CloudItemKind kind,
        long? logicalLength,
        CloudPlaceholderState state,
        PlaceholderInformation? placeholder)
    {
        if (kind != CloudItemKind.File || !state.HasFlag(CloudPlaceholderState.Placeholder))
        {
            return CloudContentAvailability.NotApplicable;
        }

        if (logicalLength == 0)
        {
            return CloudContentAvailability.FullyAvailable;
        }

        if (placeholder is { OnDiskDataSize: <= 0 })
        {
            return CloudContentAvailability.OnlineOnly;
        }

        if (state.HasFlag(CloudPlaceholderState.Partial) ||
            state.HasFlag(CloudPlaceholderState.PartiallyOnDisk) ||
            placeholder is not null && placeholder.OnDiskDataSize < logicalLength)
        {
            return CloudContentAvailability.PartiallyAvailable;
        }

        return CloudContentAvailability.FullyAvailable;
    }

    private static CloudPinState MapPinState(CfPinState state) => state switch
    {
        CfPinState.Pinned => CloudPinState.Pinned,
        CfPinState.Unpinned => CloudPinState.Unpinned,
        CfPinState.Excluded => CloudPinState.Excluded,
        _ => CloudPinState.Unspecified,
    };

    private static DateTimeOffset ToDateTimeOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        void* fileInformation,
        uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        internal uint FileAttributes;
        internal uint ReparseTag;
    }

    private sealed record PlaceholderInformation(
        long OnDiskDataSize,
        long ValidatedDataSize,
        long ModifiedDataSize,
        long PropertyDataSize,
        CloudPinState PinState,
        CloudSynchronizationState SynchronizationState,
        long FileId,
        long SyncRootFileId,
        byte[] Identity);
}
