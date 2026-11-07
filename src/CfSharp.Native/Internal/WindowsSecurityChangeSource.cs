using System.ComponentModel;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace CfSharp.Native;

// Extended NTFS records retain the kernel's file ID. Reopening a FILE_NOTIFY_INFORMATION
// path in a FileSystemWatcher callback cannot recover that identity after a rename/replacement.
internal sealed partial class WindowsSecurityChangeSource : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SafeFileHandle _directory;
    private readonly ManualResetEvent _completed = new(false);
    private readonly Action<WindowsSecurityChange> _changed;
    private readonly Action<int, bool> _error;
    private readonly int _capacity;
    private readonly nint _buffer;
    private readonly nint _overlapped;
    private readonly Task _pump = Task.CompletedTask;
    private Task? _disposal;
    private bool _pending;
    private bool _stopping;

    internal WindowsSecurityChangeSource(string path, int capacity, Action<WindowsSecurityChange> changed, Action<int, bool> error)
    {
        _changed = changed;
        _error = error;
        _capacity = Math.Clamp(capacity, 4096, 64 * 1024);
        // FILE_LIST_DIRECTORY, all sharing, BACKUP_SEMANTICS | OVERLAPPED. No content read.
        _directory = CreateFile(path, 0x81, 7, 0, 3, 0x42000000, 0);
        try
        {
            if (_directory.IsInvalid) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
            WindowsFileMetadata.FileIdentity root = WindowsFileMetadata.ReadIdentity(_directory.DangerousGetHandle());
            VolumeSerialNumber = root.VolumeSerialNumber;
            RootFileId = root.FileId;
            (_buffer, _overlapped) = Allocate(_capacity, _completed.SafeWaitHandle.DangerousGetHandle());
            // Windows can cancel a directory read when its issuing thread exits. Keep one
            // owned issuer alive until cancellation completes, independently of the caller's
            // startup thread or synchronization context. Disposal still awaits it asynchronously.
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _pump = Task.Factory.StartNew(() => Pump(started), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            started.Task.GetAwaiter().GetResult();
        }
        catch
        {
            _pump.GetAwaiter().GetResult();
            Release(_buffer, _overlapped);
            _directory.Dispose();
            _completed.Dispose();
            throw;
        }
    }

    internal ulong VolumeSerialNumber { get; }
    internal Guid RootFileId { get; }

    private unsafe void IssueRead()
    {
        // One stable, DWORD-aligned buffer and OVERLAPPED belong to this handle until its
        // last completion is observed. Reset/reuse only after GetOverlappedResult completes.
        if (!ReadDirectoryChangesEx(_directory, (void*)_buffer, (uint)_capacity, 1, 0x100,
            null, (NativeOverlapped*)_overlapped, 0, 2))
        {
            int code = Marshal.GetLastPInvokeError();
            if (code != 997) { throw new Win32Exception(code); }
        }
        _pending = true;
    }

    private void Pump(TaskCompletionSource started)
    {
        try { lock (_gate) { IssueRead(); } }
        catch (Exception exception)
        {
            started.TrySetException(exception);
            return;
        }
        started.TrySetResult();
        while (true)
        {
            _completed.WaitOne();
            WindowsSecurityChange[] changes = [];
            int error = 0;
            bool terminal;
            lock (_gate)
            {
                bool success = GetResult(out uint bytes);
                _pending = false;
                if (_stopping) { return; }
                if (!success) { error = Marshal.GetLastPInvokeError(); }
                else if (bytes == 0) { error = 1022; }
                else
                {
                    try { changes = ReadChanges(bytes); }
                    catch (InvalidDataException) { error = 13; }
                }
                if (error is 0 or 1022 or 13)
                {
                    _completed.Reset();
                    try { IssueRead(); }
                    catch (Win32Exception exception) { error = exception.NativeErrorCode; }
                }
                terminal = !_pending;
                if (terminal) { _stopping = true; }
            }
            if (terminal || error != 0)
            {
                Deliver(() => _error(error, terminal));
                if (terminal) { return; }
                continue;
            }
            // Rearm before publishing managed copies. Slow consumers cannot retain a native
            // buffer or release it while Windows is still completing an outstanding request.
            foreach (WindowsSecurityChange change in changes) { Deliver(() => _changed(change)); }
        }
    }

    private static void Deliver(Action callback)
    {
        try { callback(); }
        catch
        {
            // The owning feed records delivery failures. Never let a consumer fault strand
            // an outstanding native request or bypass cancellation/completion ownership.
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) { return new(_disposal ??= StopAsync()); }
    }

    private async Task StopAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            if (_pending && !CancelRead())
            {
                int error = Marshal.GetLastPInvokeError();
                if (error != 1168) { throw new Win32Exception(error); }
            }
        }
        // Cancellation requests do not complete I/O. Keep the handle, event, buffer and
        // OVERLAPPED alive until the pump has consumed the terminal completion.
        await _pump.ConfigureAwait(false);
        _directory.Dispose();
        _completed.Dispose();
        Release(_buffer, _overlapped);
    }

    private unsafe bool CancelRead() => CancelIoEx(_directory, (NativeOverlapped*)_overlapped);
    private unsafe bool GetResult(out uint bytes) => GetOverlappedResult(_directory, (NativeOverlapped*)_overlapped, out bytes, 0);
    private unsafe WindowsSecurityChange[] ReadChanges(uint bytes)
    {
        if (bytes > _capacity) { throw new InvalidDataException("The security notification buffer is truncated."); }
        return Decode(new ReadOnlySpan<byte>((void*)_buffer, (int)bytes));
    }

    internal static unsafe WindowsSecurityChange[] Decode(ReadOnlySpan<byte> buffer)
    {
        List<WindowsSecurityChange> changes = [];
        const int header = 84; // FIELD_OFFSET(FILE_NOTIFY_EXTENDED_INFORMATION, FileName), not sizeof.
        while (true)
        {
            if (buffer.Length < header) { throw new InvalidDataException("The security notification header is truncated."); }
            FileNotifyExtendedInformation record = default;
            buffer[..header].CopyTo(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref record, 1)));
            if (record.FileNameLength == 0 || (record.FileNameLength & 1) != 0 || record.FileNameLength > buffer.Length - header)
            { throw new InvalidDataException("The security notification name is truncated."); }
            int end = checked(header + (int)record.FileNameLength);
            if (record.Action != 3) { throw new InvalidDataException("Unexpected action in a security-only subscription."); }
            changes.Add(new(new string(MemoryMarshal.Cast<byte, char>(buffer[header..end])), record.FileId,
                record.ParentFileId, (record.FileAttributes & 0x10) != 0));
            if (record.NextEntryOffset == 0) { return changes.ToArray(); }
            if (record.NextEntryOffset < end || record.NextEntryOffset > buffer.Length || (record.NextEntryOffset & 3) != 0)
            { throw new InvalidDataException("The security notification link is invalid."); }
            buffer = buffer[(int)record.NextEntryOffset..];
        }
    }

    private static unsafe (nint Buffer, nint Overlapped) Allocate(int capacity, nint completion)
    {
        void* buffer = NativeMemory.Alloc((nuint)capacity);
        try
        {
            NativeOverlapped* overlapped = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
            overlapped->EventHandle = completion;
            return ((nint)buffer, (nint)overlapped);
        }
        catch
        {
            NativeMemory.Free(buffer);
            throw;
        }
    }

    private static unsafe void Release(nint buffer, nint overlapped)
    {
        NativeMemory.Free((void*)buffer);
        NativeMemory.Free((void*)overlapped);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileNotifyExtendedInformation
    {
        internal uint NextEntryOffset, Action;
        internal long CreationTime, LastModificationTime, LastChangeTime, LastAccessTime, AllocatedLength, FileSize;
        internal uint FileAttributes, ReparsePointTag;
        internal ulong FileId, ParentFileId;
        internal uint FileNameLength;
        internal char FileName;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [LibraryImport("kernel32.dll", EntryPoint = "ReadDirectoryChangesExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReadDirectoryChangesEx(SafeFileHandle directory, void* buffer, uint length, int subtree, uint filter,
        uint* bytes, NativeOverlapped* overlapped, nint completion, int informationClass);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetOverlappedResult(SafeFileHandle directory, NativeOverlapped* overlapped, out uint bytes, int wait);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CancelIoEx(SafeFileHandle directory, NativeOverlapped* overlapped);
}

internal readonly record struct WindowsSecurityChange(string RelativePath, ulong FileId, ulong ParentFileId, bool IsDirectory);
