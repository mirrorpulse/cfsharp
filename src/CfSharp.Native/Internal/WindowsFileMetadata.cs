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

    internal static FileIdentity ReadIdentity(nint handle)
    {
        FileIdentity value;
        Query(handle, 18, &value, (uint)sizeof(FileIdentity));
        return value;
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
