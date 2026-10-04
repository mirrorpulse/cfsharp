using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CfSharp.Native.Tests;

public sealed class ProtectedFileReaderTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 17)]
    public void CompletesImmediateAndPendingReadsBeforeDisposal(int pendingPolls, int bytes)
    {
        FakeRead operation = new(pendingPolls, bytes);
        Assert.Equal(bytes, ProtectedFileReader.Read(() => operation, TimeSpan.FromSeconds(1), default));
        Assert.True(operation.DrainedAtDispose);
        Assert.False(operation.Canceled);
    }

    [Fact]
    public void CancellationDrainsEvenWhenTheRequestCompletesNormally()
    {
        using CancellationTokenSource source = new();
        FakeRead operation = new(3, 17) { OnWait = source.Cancel };
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ProtectedFileReader.Read(() => operation, TimeSpan.FromSeconds(1), source.Token));
        Assert.True(operation.Canceled);
        Assert.True(operation.DrainedAtDispose);
    }

    [Fact]
    public void TimeoutDrainsBeforeReturning()
    {
        FakeRead operation = new(3, 17) { OnWait = () => Thread.Sleep(2) };
        Assert.Throws<TimeoutException>(() =>
            ProtectedFileReader.Read(() => operation, TimeSpan.FromMilliseconds(1), default));
        Assert.True(operation.Canceled);
        Assert.True(operation.DrainedAtDispose);
    }

    [Fact]
    public void NativeFailurePreservesTheErrorAfterCompletion()
    {
        FakeRead operation = new(2, 0) { Error = 995 };
        Win32Exception exception = Assert.Throws<Win32Exception>(() =>
            ProtectedFileReader.Read(() => operation, TimeSpan.FromSeconds(1), default));
        Assert.Equal(995, exception.NativeErrorCode);
        Assert.True(operation.DrainedAtDispose);
    }

    [Fact]
    public void OverlappedLayoutMatchesWin32()
    {
        Assert.Equal(IntPtr.Size == 8 ? 32 : 20, Marshal.SizeOf<OverlappedRead.Overlapped>());
        Assert.Equal(IntPtr.Size * 2, Marshal.OffsetOf<OverlappedRead.Overlapped>("Offset").ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 24 : 16, Marshal.OffsetOf<OverlappedRead.Overlapped>("Event").ToInt32());
    }

    private sealed class FakeRead(int pendingPolls, int bytes) : IProtectedReadOperation
    {
        private int _remaining = pendingPolls;

        internal Action? OnWait { get; init; }

        internal int Error { get; init; }

        internal bool Canceled { get; private set; }

        internal bool DrainedAtDispose { get; private set; }

        public void Start() { }

        public bool TryComplete(out int transferred, out int error)
        {
            transferred = bytes;
            error = Error;
            return _remaining-- <= 0;
        }

        public void Cancel() => Canceled = true;

        public void Wait() => OnWait?.Invoke();

        public void Dispose() => DrainedAtDispose = _remaining < 0;
    }
}
