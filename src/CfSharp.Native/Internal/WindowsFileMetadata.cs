using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Win32.SafeHandles;

namespace CfSharp.Native;

// All queries use the supplied file object. Metadata-only opens do not hydrate content,
// follow the final reparse point, or take ownership of a CFAPI-borrowed Win32 handle.
internal static unsafe partial class WindowsFileMetadata
{
    internal static SafeFileHandle Open(string path, bool preventDelete = false)
    {
        SafeFileHandle handle = CreateFile(path, 0x80, preventDelete ? 3u : 7u,
            0, 3, 0x02200000, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }

        return handle;
    }

    internal static SafeFileHandle OpenLocalAccess(string path, bool directory)
    {
        // A single file object owns both data exclusion and security access. A separate
        // WRITE_DAC opener can break a CFAPI oplock and wait for our own referenced owner.
        // READ/WRITE_DATA with share-none rejects existing writers and writable sections;
        // FILE_LIST_DIRECTORY makes no-delete directory sharing effective on Windows,
        // unlike an attribute/security-only open. It performs no enumeration or hydration.
        // Directory members remain free to change.
        // OPEN_REPARSE_POINT prevents a final symbolic link from redirecting this access.
        SafeFileHandle handle = CreateFile(path, directory ? 0x60081u : 0x60083u,
            directory ? 3u : 0u, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        return handle;
    }

    internal static FileBasicInfo ReadBasic(nint handle)
    {
        FileBasicInfo value;
        Query(handle, 0, &value, (uint)sizeof(FileBasicInfo));
        return value;
    }

    internal static long ReadLength(nint handle)
    {
        FileStandardInfo value;
        Query(handle, 1, &value, (uint)sizeof(FileStandardInfo));
        return value.EndOfFile;
    }

    internal static FileIdentity ReadIdentity(nint handle)
    {
        FileIdentity value;
        Query(handle, 18, &value, (uint)sizeof(FileIdentity));
        return value;
    }

    internal static string ReadFinalPath(nint handle)
    {
        char[] buffer = new char[260];
        while (true)
        {
            uint length;
            fixed (char* pointer = buffer)
            {
                // Normalized DOS spelling observes the current namespace through the existing
                // object handle, including a case-only rename. Successful lengths exclude NUL;
                // insufficient-buffer lengths include it. Never close a borrowed handle here.
                // https://learn.microsoft.com/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew
                length = GetFinalPathNameByHandle(handle, pointer, (uint)buffer.Length, 0);
            }

            if (length == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            if (length < buffer.Length)
            {
                string path = new(buffer, 0, (int)length);
                if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                {
                    path = @"\\" + path[8..];
                }
                else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                {
                    path = path[4..];
                }

                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }

            // Bound repeated growth even if a concurrent external rename changes the required
            // length between calls. This query never follows a final link opened as reparse data.
            if (length > 32768 || buffer.Length == 32768)
            {
                throw new PathTooLongException("The native namespace path exceeds the Windows path limit.");
            }

            buffer = new char[Math.Min(32768, checked((int)length + 1))];
        }
    }

    [SupportedOSPlatform("windows10.0.16299")]
    internal static NativeFileMetadata Read(nint handle, nint protectedHandle = 0)
    {
        FileAttributeTagInfo attributes;
        FileStandardInfo standard;
        Query(handle, 9, &attributes, (uint)sizeof(FileAttributeTagInfo));
        Query(handle, 1, &standard, (uint)sizeof(FileStandardInfo));
        CfPlaceholderState state = CfApi.CfGetPlaceholderStateFromAttributeTag(
            attributes.Attributes, attributes.ReparseTag);
        byte[] identity = [];
        long onDisk = 0;
        bool inSync = false;
        if (state != CfPlaceholderState.Invalid && state.HasFlag(CfPlaceholderState.Placeholder))
        {
            byte[] buffer = new byte[sizeof(CfPlaceholderStandardInfo) + CfApi.MaxFileIdentityLength];
            fixed (byte* pointer = buffer)
            {
                uint returned = 0;
                // Preserve an available opaque owner for CFAPI calls. Its borrowed Win32
                // handle is used by the general file-information queries above/below.
                int result = CfApi.CfGetPlaceholderInfo(protectedHandle == 0 ? handle : protectedHandle, CfPlaceholderInfoClass.Standard,
                    pointer, (uint)buffer.Length, &returned);
                if (result < 0)
                {
                    throw new NativeFileException("CfGetPlaceholderInfo", result);
                }

                CfPlaceholderStandardInfo* info = (CfPlaceholderStandardInfo*)pointer;
                int offset = checked((int)(info->FileIdentity - pointer));
                if (returned < offset || returned > buffer.Length ||
                    info->FileIdentityLength > CfApi.MaxFileIdentityLength ||
                    info->FileIdentityLength > returned - offset)
                {
                    throw new InvalidDataException("Windows returned truncated placeholder metadata.");
                }

                identity = new ReadOnlySpan<byte>(info->FileIdentity, (int)info->FileIdentityLength).ToArray();
                onDisk = info->OnDiskDataSize;
                inSync = info->InSyncState == CfInSyncState.InSync;
            }
        }

        return new NativeFileMetadata(ReadIdentity(handle), standard.EndOfFile,
            standard.NumberOfLinks, standard.DeletePending != 0, standard.Directory != 0,
            attributes.Attributes, attributes.ReparseTag, state, onDisk, inSync, identity);
    }

    private static void Query(nint handle, int informationClass, void* value, uint size)
    {
        if (!GetFileInformationByHandleEx(handle, informationClass, value, size))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    // FILE_ID_128 is an opaque 16-byte identifier, not a UUID. Guid preserves its complete
    // bytes without architecture-dependent pointer fields; compare only with the volume ID.
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileIdentity
    {
        internal ulong VolumeSerialNumber;
        internal Guid FileId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileBasicInfo
    {
        internal long CreationTime;
        internal long LastAccessTime;
        internal long LastWriteTime;
        internal long ChangeTime;
        internal uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileStandardInfo
    {
        internal long AllocationSize;
        internal long EndOfFile;
        internal uint NumberOfLinks;
        internal byte DeletePending;
        internal byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileAttributeTagInfo
    {
        internal uint Attributes;
        internal uint ReparseTag;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share,
        nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(nint handle, int informationClass, void* value, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static partial uint GetFinalPathNameByHandle(nint handle, char* path, uint capacity, uint flags);
}

internal sealed record NativeFileMetadata(WindowsFileMetadata.FileIdentity Identity, long Length,
    uint Links, bool DeletePending, bool Directory, uint Attributes, uint ReparseTag,
    CfPlaceholderState PlaceholderState, long OnDiskDataSize, bool InSync, byte[] PlaceholderIdentity);

internal sealed class NativeFileException : IOException
{
    internal NativeFileException(string operation, int hresult) : base($"{operation} failed.")
    {
        Operation = operation;
        HResult = hresult;
    }

    internal string Operation { get; }
}
