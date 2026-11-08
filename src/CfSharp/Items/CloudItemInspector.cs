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

        return Inspect(handle, path, expectedKind, syncRootPath);
    }

    // The caller retains the same owned file object throughout this synchronous query.
    // No pathname stat or reopen is allowed on a protected local operation's item.
    internal static unsafe LocalCloudItemInspection Inspect(
        SafeFileHandle handle, string path, CloudItemKind expectedKind, string? syncRootPath = null)
    {
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

        WindowsFileMetadata.FileBasicInfo basic = WindowsFileMetadata.ReadBasic(handle.DangerousGetHandle());
        long? length = actualKind == CloudItemKind.File ? WindowsFileMetadata.ReadLength(handle.DangerousGetHandle()) : null;
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
            CreationTime = ToDateTimeOffset(DateTime.FromFileTimeUtc(basic.CreationTime)),
            LastWriteTime = ToDateTimeOffset(DateTime.FromFileTimeUtc(basic.LastWriteTime)),
            LastAccessTime = ToDateTimeOffset(DateTime.FromFileTimeUtc(basic.LastAccessTime)),
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
            LocalBinding = syncRootPath is not null &&
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

    internal static bool IsCloudPlaceholder(string path) =>
        TryReadAttributeTag(path, out FileAttributeTagInfo info, out _) && IsCloudPlaceholder(info);

    // Query the object itself, not a generic final-link target: even an enumeration used
    // by ResolveLinkTarget can recall Cloud Files metadata when placeholders are exposed.
    // Only name-surrogate tags understood by the BCL may enter link traversal. Unknown
    // reparse points must not silently acquire the containment authority of ordinary files.
    internal static FileSystemInfo? ReadFileSystemLink(string path, out bool exists)
    {
        exists = true;
        try
        {
            if (!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) { return null; }
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Opening a missing name beneath a partial Cloud Files directory may request
            // population. An attribute query can establish absence without that open.
            exists = false;
            return null;
        }
        if (!TryReadAttributeTag(path, out FileAttributeTagInfo info, out int error))
        {
            if (error is 2 or 3) { exists = false; return null; }
            string message = $"The reparse attributes of path component '{path}' could not be read (native error 0x{unchecked((uint)error):X8}).";
            if (error == 5) { throw new UnauthorizedAccessException(message); }
            throw new IOException(message, error < 0 ? error : unchecked((int)(0x80070000u | (uint)error)));
        }

        if (((info.FileAttributes & (uint)FileAttributes.ReparsePoint) == 0 && info.ReparseTag == 0) ||
            IsCloudPlaceholder(info))
        {
            return null;
        }

        // IO_REPARSE_TAG_SYMLINK and IO_REPARSE_TAG_MOUNT_POINT redirect names;
        // Cloud Files tags describe the object and never supply a substitute pathname.
        if (info.ReparseTag is not (0xA000000C or 0xA0000003))
        {
            throw new CloudPathReparsePointException(path);
        }

        return (info.FileAttributes & (uint)FileAttributes.Directory) != 0
            ? new DirectoryInfo(path) : new FileInfo(path);
    }

    private static bool IsCloudPlaceholder(FileAttributeTagInfo info)
    {
        if ((info.FileAttributes & (uint)FileAttributes.ReparsePoint) == 0) { return false; }
        CfPlaceholderState state = CfApi.CfGetPlaceholderStateFromAttributeTag(info.FileAttributes, info.ReparseTag);
        return state != CfPlaceholderState.Invalid && state.HasFlag(CfPlaceholderState.Placeholder);
    }

    private static unsafe bool TryReadAttributeTag(string path, out FileAttributeTagInfo info, out int error)
    {
        info = default;
        error = 0;
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
            error = Marshal.GetLastPInvokeError();
            return false;
        }

        FileAttributeTagInfo attributeTagInfo = default;
        if (!GetFileInformationByHandleEx(
                handle,
                FileAttributeTagInformationClass,
                &attributeTagInfo,
                (uint)sizeof(FileAttributeTagInfo)))
        {
            error = Marshal.GetLastPInvokeError();
            return false;
        }

        info = attributeTagInfo;
        return true;
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
