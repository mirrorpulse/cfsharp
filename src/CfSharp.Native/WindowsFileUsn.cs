using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Win32.SafeHandles;

namespace CfSharp.Native;

/// <summary>Reads the file-system USN independently of Cloud Files mutation outputs.</summary>
internal static partial class WindowsFileUsn
{
    internal const uint ReadFileUsnDataControlCode = 0x000900eb;
    internal const int Version2UsnOffset = 24;
    internal const int Version3UsnOffset = 40;
    internal const int Version2HeaderLength = 60;
    internal const int Version3HeaderLength = 76;

    [SupportedOSPlatform("windows10.0.16299")]
    internal static long Read(string path, bool expectedDirectory)
    {
        // Attribute-only access and OPEN_REPARSE_POINT avoid hydrating file content or following
        // a final symlink. BACKUP_SEMANTICS permits the same query for files and directories.
        using SafeFileHandle handle = CreateFile(
            path,
            desiredAccess: 0x00000080,
            shareMode: 0x00000007,
            securityAttributes: 0,
            creationDisposition: 3,
            flagsAndAttributes: 0x00200000 | 0x02000000,
            templateFile: 0);
        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return Read(handle, expectedDirectory);
    }

    [SupportedOSPlatform("windows10.0.16299")]
    internal static unsafe long Read(SafeFileHandle handle, bool expectedDirectory)
    {
        FileAttributeTagInfo attributes = default;
        if (!GetFileInformationByHandleEx(handle, 9, &attributes, (uint)sizeof(FileAttributeTagInfo)))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        if (((attributes.FileAttributes & 0x00000010) != 0) != expectedDirectory)
        {
            throw new InvalidOperationException("The existing item has another file-system kind.");
        }

        if ((attributes.FileAttributes & 0x00000400) != 0 &&
            (CfApi.CfGetPlaceholderStateFromAttributeTag(attributes.FileAttributes, attributes.ReparseTag) &
             CfPlaceholderState.Placeholder) == 0)
        {
            throw new InvalidOperationException("USN queries do not support non-Cloud Files reparse points.");
        }

        ReadFileUsnData versions = new() { MinMajorVersion = 2, MaxMajorVersion = 3 };
        // READ_FILE_USN_DATA requests both layouts: ReFS uses 128-bit file identifiers, which
        // shift the USN from byte 24 (v2) to byte 40 (v3). The variable file name is not consumed.
        // 4 KiB accommodates the fixed header and a maximum-length Windows path component.
        byte[] buffer = new byte[4096];
        fixed (byte* bufferPointer = buffer)
        {
            if (!DeviceIoControl(
                    handle,
                    ReadFileUsnDataControlCode,
                    &versions,
                    (uint)sizeof(ReadFileUsnData),
                    bufferPointer,
                    (uint)buffer.Length,
                    out uint bytesReturned,
                    overlapped: 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            if (bytesReturned > buffer.Length)
            {
                throw new InvalidDataException("Windows returned an invalid USN buffer length.");
            }

            return Parse(buffer.AsSpan(0, checked((int)bytesReturned)));
        }
    }

    internal static long Parse(ReadOnlySpan<byte> record)
    {
        // This FSCTL returns a single USN_RECORD, without the leading next-USN value used by
        // FSCTL_READ_USN_JOURNAL. Validate its version and length before reading any field.
        if (record.Length < 8)
        {
            throw new InvalidDataException("Windows returned a truncated USN record header.");
        }

        uint recordLength = BinaryPrimitives.ReadUInt32LittleEndian(record);
        ushort majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        (int headerLength, int usnOffset) = majorVersion switch
        {
            2 => (Version2HeaderLength, Version2UsnOffset),
            3 => (Version3HeaderLength, Version3UsnOffset),
            _ => throw new NotSupportedException($"USN record version {majorVersion} is not supported."),
        };
        if (recordLength < headerLength || recordLength > record.Length)
        {
            throw new InvalidDataException("Windows returned an invalid USN record length.");
        }

        return BinaryPrimitives.ReadInt64LittleEndian(record[usnOffset..]);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ReadFileUsnData
    {
        internal ushort MinMajorVersion;
        internal ushort MaxMajorVersion;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        internal uint FileAttributes;
        internal uint ReparseTag;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        void* fileInformation,
        uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool DeviceIoControl(
        SafeFileHandle fileHandle,
        uint controlCode,
        void* inputBuffer,
        uint inputBufferSize,
        void* outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        nint overlapped);
}
