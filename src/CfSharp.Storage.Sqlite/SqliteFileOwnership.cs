using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CfSharp.Storage.Sqlite;

// Ownership is attached to the kernel file object, not a pathname, metadata row or process-local
// registry. Every provider owner must hold the same exclusive byte on main/WAL/SHM handles before
// SQLite can open them. An alias added after the link-count check therefore cannot admit a peer.
internal static class SqliteFileOwnership
{
    // 2^62 is beyond SQLite's maximum main/WAL offsets and its byte-range locking protocol.
    // Windows permits locking beyond EOF without allocating bytes or changing the file length.
    // Keep this protocol offset stable across releases; closing the owning handle releases it.
    private const uint OwnershipOffsetHigh = 0x40000000;

    internal static void Acquire(SafeFileHandle handle, string databasePath, string path)
    {
        FileOverlapped overlapped = new() { OffsetHigh = OwnershipOffsetHigh };
        if (!LockFileEx(handle, 0x00000003, 0, 1, 0, ref overlapped))
        {
            int error = Marshal.GetLastWin32Error();
            throw new SqliteCloudStateStoreException(
                error is 32 or 33 ? SqliteCloudStateStoreError.AlreadyInUse : SqliteCloudStateStoreError.InvalidPath,
                databasePath, "The SQLite physical file ownership lock could not be acquired.", new Win32Exception(error));
        }
        EnsureSingleLink(handle, path);
    }

    internal static void EnsureSingleLink(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandleEx(handle, 1, out FileStandardInfo info, (uint)Marshal.SizeOf<FileStandardInfo>()))
        {
            throw new IOException($"The SQLite file '{path}' link state could not be read.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }
        if (info.NumberOfLinks != 1 || info.DeletePending != 0 || info.Directory != 0)
        {
            throw new IOException($"The SQLite file '{path}' must be a single-link, non-deleting regular file.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileStandardInfo
    {
        internal long AllocationSize;
        internal long EndOfFile;
        internal uint NumberOfLinks;
        // Native BOOLEAN fields are one byte; BOOL marshalling would change the ABI.
        internal byte DeletePending;
        internal byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileOverlapped
    {
        internal nint Internal;
        internal nint InternalHigh;
        internal uint Offset;
        internal uint OffsetHigh;
        internal nint Event;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockFileEx(SafeFileHandle file, uint flags, uint reserved,
        uint lengthLow, uint lengthHigh, ref FileOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out FileStandardInfo information, uint size);
}
