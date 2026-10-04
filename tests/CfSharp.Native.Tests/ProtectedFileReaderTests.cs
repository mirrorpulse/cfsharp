using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace CfSharp.Native.Tests;

public sealed class ProtectedFileReaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingKernelReadIsCanceledAndDrainedWithoutClosingTheBorrowedHandle(bool timeout)
    {
        string name = $"CfSharp-reader-{Guid.NewGuid():N}";
        using NamedPipeServerStream server = new(name, PipeDirection.Out, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using NamedPipeClientStream client = new(".", name, PipeDirection.In, PipeOptions.Asynchronous);
        Task connection = server.WaitForConnectionAsync();
        await client.ConnectAsync();
        await connection;
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource pending = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // A connected pipe with no writer data deterministically enters IO_PENDING. This
        // tests the real CancelIoEx/completion race; CFAPI-file reads are covered separately
        // by public confirmation acceptance. Neither managed completion port is rebound.
        Task<int> read = Task.Run(() => ProtectedFileReader.Read(() => new ObservedPendingRead(
            new OverlappedRead(client.SafePipeHandle.DangerousGetHandle(), new byte[4], 4, 0), pending),
            timeout ? TimeSpan.FromMilliseconds(100) : TimeSpan.FromSeconds(5), cancellation.Token));
        await pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (!timeout)
        {
            cancellation.Cancel();
        }
        if (timeout)
        {
            await Assert.ThrowsAsync<TimeoutException>(() => read);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        }

        Assert.False(client.SafePipeHandle.IsClosed);
        Task write = server.WriteAsync(new byte[] { 1, 2, 3, 4 }).AsTask();
        byte[] next = new byte[4];
        using CancellationTokenSource retryDeadline = new(TimeSpan.FromSeconds(5));
        await client.ReadExactlyAsync(next, retryDeadline.Token);
        await write.WaitAsync(retryDeadline.Token);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, next);
    }

    private sealed class ObservedPendingRead(IProtectedReadOperation inner, TaskCompletionSource pending) : IProtectedReadOperation
    {
        public void Start()
        {
            inner.Start();
            Assert.False(inner.TryComplete(out _, out _));
            pending.SetResult();
        }

        public bool TryComplete(out int bytes, out int error) => inner.TryComplete(out bytes, out error);
        public void Cancel() => inner.Cancel();
        public void Wait() => inner.Wait();
        public void Dispose() => inner.Dispose();
    }

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
    public void SynchronousCompletionStillHonorsTheReadBudget()
    {
        FakeRead operation = new(0, 17) { OnStart = () => Thread.Sleep(50) };
        Assert.Throws<TimeoutException>(() =>
            ProtectedFileReader.Read(() => operation, TimeSpan.FromMilliseconds(10), default));
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

        internal Action? OnStart { get; init; }

        internal int Error { get; init; }

        internal bool Canceled { get; private set; }

        internal bool DrainedAtDispose { get; private set; }

        public void Start() => OnStart?.Invoke();

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
