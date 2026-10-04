using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

namespace CfSharp.Native;

/// <summary>Completes one bounded read without binding a borrowed CFAPI handle to another IOCP.</summary>
internal static class ProtectedFileReader
{
    internal static int Read(nint borrowedHandle, byte[] buffer, int count, long offset,
        TimeSpan timeout, CancellationToken cancellationToken) =>
        Read(() => new OverlappedRead(borrowedHandle, buffer, count, offset), timeout, cancellationToken);

    // The operation owns its buffer pin, OVERLAPPED, and event. Dispose must drain even when
    // polling, cancellation, or timeout throws. The caller retains the protected reference
    // until this method returns; neither this method nor the operation owns the file handle.
    internal static int Read(Func<IProtectedReadOperation> open, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        using IProtectedReadOperation operation = open();
        operation.Start();
        bool canceled = false;
        bool timedOut = false;
        int bytes;
        int error;
        while (!operation.TryComplete(out bytes, out error))
        {
            if (!canceled && !timedOut &&
                (cancellationToken.IsCancellationRequested || Stopwatch.GetElapsedTime(started) >= timeout))
            {
                canceled = cancellationToken.IsCancellationRequested;
                timedOut = !canceled;
                operation.Cancel();
            }

            operation.Wait();
        }

        // CancelIoEx only requests cancellation. Even ERROR_NOT_FOUND requires observing
        // completion. A deadline bounds new work, not the kernel's cancellation-drain time.
        if (canceled || cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (timedOut)
        {
            throw new TimeoutException("The protected file read exceeded its time budget.");
        }

        if (error != 0)
        {
            throw new Win32Exception(error);
        }

        return bytes;
    }
}

internal interface IProtectedReadOperation : IDisposable
{
    void Start();

    bool TryComplete(out int bytes, out int error);

    void Cancel();

    void Wait();
}

internal sealed unsafe partial class OverlappedRead : IProtectedReadOperation
{
    private readonly nint _file;
    private readonly int _count;
    private readonly SafeWaitHandle _event;
    private GCHandle _bufferPin;
    private Overlapped* _overlapped;
    private bool _started;
    private bool _complete;
    private uint _bytes;
    private int _error;

    internal OverlappedRead(nint borrowedHandle, byte[] buffer, int count, long offset)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, buffer.Length);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        _file = borrowedHandle;
        _count = count;
        _event = CreateEvent(0, true, false, 0);
        if (_event.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            _event.Dispose();
            throw new Win32Exception(error);
        }

        try
        {
            _bufferPin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            _overlapped = (Overlapped*)NativeMemory.AllocZeroed((nuint)sizeof(Overlapped));
            _overlapped->Offset = (uint)offset;
            _overlapped->OffsetHigh = (uint)((ulong)offset >> 32);
            // CFAPI owns the handle's completion port. The event's low bit suppresses only
            // this read's completion packet; never use ThreadPoolBoundHandle/RandomAccess.
            _overlapped->Event = _event.DangerousGetHandle() | 1;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Start()
    {
        if (_started)
        {
            throw new InvalidOperationException("The read has already started.");
        }

        _started = true;
        bool success = ReadFile(_file, (void*)_bufferPin.AddrOfPinnedObject(), (uint)_count,
            out _bytes, _overlapped);
        _error = success ? 0 : Marshal.GetLastPInvokeError();
        _complete = success || _error != 997;
        NormalizeEof();
    }

    public bool TryComplete(out int bytes, out int error)
    {
        if (!_complete)
        {
            bool success = GetOverlappedResult(_file, _overlapped, out _bytes, false);
            _error = success ? 0 : Marshal.GetLastPInvokeError();
            _complete = success || _error != 996;
            NormalizeEof();
        }

        bytes = _complete ? checked((int)_bytes) : 0;
        error = _complete ? _error : 0;
        return _complete;
    }

    public void Cancel()
    {
        // Do not throw on ERROR_NOT_FOUND (completion race), or free the OVERLAPPED on
        // another cancellation error. The original request must still reach a terminal state.
        _ = CancelIoEx(_file, _overlapped);
    }

    public void Wait() => _ = WaitForSingleObject(_event, 10);

    public void Dispose()
    {
        if (_overlapped is null)
        {
            if (_bufferPin.IsAllocated)
            {
                _bufferPin.Free();
            }

            _event.Dispose();
            return;
        }

        if (_started && !_complete)
        {
            Cancel();
            while (!TryComplete(out _, out _))
            {
                Wait();
            }
        }

        NativeMemory.Free(_overlapped);
        _overlapped = null;
        _bufferPin.Free();
        _event.Dispose();
    }

    private void NormalizeEof()
    {
        if (_complete && _error == 38)
        {
            _error = 0;
            _bytes = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Overlapped
    {
        internal nuint Internal;
        internal nuint InternalHigh;
        internal uint Offset;
        internal uint OffsetHigh;
        internal nint Event;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static partial SafeWaitHandle CreateEvent(nint attributes,
        [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadFile(nint file, void* buffer, uint count, out uint bytes, Overlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetOverlappedResult(nint file, Overlapped* overlapped, out uint bytes,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CancelIoEx(nint file, Overlapped* overlapped);

    [LibraryImport("kernel32.dll")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static partial uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
