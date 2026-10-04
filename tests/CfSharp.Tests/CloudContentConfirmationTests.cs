using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace CfSharp.Tests;

public sealed class CloudContentConfirmationTests
{
    private static readonly CloudLocalFileBinding Binding = new(7, Guid.NewGuid(), Guid.NewGuid());
    private static readonly CloudPlaceholderIdentity Identity = new(Guid.NewGuid(), "accepted", "revision-2");
    private static readonly byte[] Content = "accepted full content"u8.ToArray();

    [Fact]
    public void RequestOwnsProofBytesIncludingMemoryReturnedToTheCaller()
    {
        byte[] digest = SHA256.HashData(Content);
        byte[] previous = [1, 2, 3];
        CloudContentConfirmationRequest request = new(Binding, Identity, Content.Length, digest,
            CloudContentPreparation.ReplacePlaceholderIdentity, previous);
        digest[0] ^= 0xff;
        previous[0] = 9;
        Assert.Equal(SHA256.HashData(Content), request.ExpectedSha256.ToArray());
        Assert.Equal(new byte[] { 1, 2, 3 }, request.ExpectedPlaceholderIdentity.ToArray());
        Assert.True(MemoryMarshal.TryGetArray(request.ExpectedSha256, out ArraySegment<byte> returned));
        returned.Array![0] ^= 0xff;
        Assert.Equal(SHA256.HashData(Content), request.ExpectedSha256.ToArray());
    }

    [Fact]
    public void InvalidProofAndUnboundedBudgetsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CloudContentConfirmationRequest(Binding, Identity, -1, new byte[32]));
        Assert.Throws<ArgumentException>(() => new CloudContentConfirmationRequest(Binding, Identity, 0, new byte[31]));
        Assert.Throws<ArgumentException>(() => new CloudContentConfirmationRequest(Binding, Identity, 0, new byte[32],
            CloudContentPreparation.ReplacePlaceholderIdentity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CloudContentConfirmationRequest(Binding, Identity, 0, new byte[32], segmentSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CloudContentConfirmationRequest(Binding, Identity, 0, new byte[32],
            referenceBudget: Timeout.InfiniteTimeSpan));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CloudContentConfirmationRequest(Binding, Identity, 0, new byte[32],
            deadline: TimeSpan.MaxValue));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(21, false)]
    [InlineData(21, true)]
    public async Task VerifiesWholeContentIncludingEmptyAndAlreadyInSyncFiles(int length, bool inSync)
    {
        byte[] content = Content.AsSpan(0, length).ToArray();
        FakeSession session = new(content) { InSync = inSync, ShortReads = true };
        CloudContentConfirmationResult result = await RunAsync(session, Proof(content));
        Assert.Equal(inSync ? CloudContentConfirmationOutcome.AlreadyConfirmed : CloudContentConfirmationOutcome.Confirmed, result.Outcome);
        Assert.Equal(!inSync, result.NativeApplied);
        Assert.True(result.NativeConfirmationVerified);
        Assert.Equal(length, result.BytesVerified);
        Assert.True(session.Disposed);
        Assert.Equal(0, session.ActiveReferences);
        Assert.True(result.LongestReference > TimeSpan.Zero);
    }

    [Theory]
    [InlineData("file", CloudContentConfirmationOutcome.LocalObjectMismatch)]
    [InlineData("root", CloudContentConfirmationOutcome.LocalObjectMismatch)]
    [InlineData("volume", CloudContentConfirmationOutcome.LocalObjectMismatch)]
    [InlineData("identity", CloudContentConfirmationOutcome.IdentityMismatch)]
    [InlineData("length", CloudContentConfirmationOutcome.ContentMismatch)]
    [InlineData("partial", CloudContentConfirmationOutcome.NotFullyLocal)]
    [InlineData("directory", CloudContentConfirmationOutcome.NotApplicable)]
    [InlineData("hardlink", CloudContentConfirmationOutcome.NotApplicable)]
    public async Task RejectionsNeverReadPrepareOrMark(string mismatch, CloudContentConfirmationOutcome outcome)
    {
        FakeSession session = new(Content);
        session.Facts = mismatch switch
        {
            "file" => session.Facts with { Binding = new(7, Binding.SyncRootFileId, Guid.NewGuid()) },
            "root" => session.Facts with { Binding = new(7, Guid.NewGuid(), Binding.LocalFileId) },
            "volume" => session.Facts with { Binding = new(8, Binding.SyncRootFileId, Binding.LocalFileId) },
            "identity" => session.Facts with { Identity = [9] },
            "length" => session.Facts with { Length = Content.Length + 1 },
            "partial" => session.Facts with { IsFullyLocal = false },
            "directory" => session.Facts with { IsDirectory = true },
            _ => session.Facts with { IsSupported = false },
        };
        CloudContentConfirmationResult result = await RunAsync(session, Proof(Content));
        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(0, session.Reads);
        Assert.Equal(0, session.Preparations);
        Assert.Equal(0, session.Marks);
        Assert.False(result.NativeApplied);
        Assert.True(session.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardedPreparationRequiresTwoCompleteVerifications(bool convert)
    {
        byte[] previous = [1, 2, 3];
        FakeSession session = new(Content);
        session.Facts = session.Facts with { IsPlaceholder = !convert, Identity = convert ? [] : previous };
        CloudContentConfirmationRequest request = new(Binding, Identity, Content.Length, SHA256.HashData(Content),
            convert ? CloudContentPreparation.ConvertRegularFile : CloudContentPreparation.ReplacePlaceholderIdentity,
            convert ? default : previous, segmentSize: 4);
        CloudContentConfirmationResult result = await RunAsync(session, request);
        Assert.Equal(CloudContentConfirmationOutcome.Confirmed, result.Outcome);
        Assert.True(result.NativeIdentityPrepared);
        Assert.Equal(0, result.PreparationUsn);
        Assert.Equal(Content.Length * 2, result.BytesVerified);
        Assert.Equal(1, session.Preparations);
        Assert.Equal(1, session.Marks);
        Assert.Equal(0, session.ActiveReferences);
    }

    [Fact]
    public async Task WrongHashAndEarlyOrExtraEofCannotConfirmOrPrepare()
    {
        foreach (byte[] actual in new[] { "wrong full content!!!"u8.ToArray(), Content[..^1], Content.Concat(new byte[] { 1 }).ToArray() })
        {
            FakeSession session = new(actual);
            session.Facts = session.Facts with { Length = Content.Length };
            CloudContentConfirmationResult result = await RunAsync(session, Proof(Content));
            Assert.Equal(CloudContentConfirmationOutcome.ContentMismatch, result.Outcome);
            Assert.Equal(0, session.Marks);
            Assert.Equal(0, session.Preparations);
        }
    }

    [Fact]
    public async Task BrokenProtectionDoesNotReopenOrContinueTheDigest()
    {
        FakeSession session = new(Content) { BreakAtReference = 2 };
        CloudContentConfirmationResult result = await RunAsync(session, Proof(Content));
        Assert.Equal(CloudContentConfirmationOutcome.ProtectionLost, result.Outcome);
        Assert.Equal(4, result.BytesVerified);
        Assert.Equal(1, session.Reads);
        Assert.Equal(0, session.Marks);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task CancellationAndFaultAfterPreparationRetainTheirReceipt()
    {
        using CancellationTokenSource cancellation = new();
        FakeSession session = new(Content) { OnPrepare = cancellation.Cancel };
        session.Facts = session.Facts with { IsPlaceholder = false, Identity = [] };
        CloudContentConfirmationRequest request = new(Binding, Identity, Content.Length, SHA256.HashData(Content),
            CloudContentPreparation.ConvertRegularFile, segmentSize: 4);
        CloudContentConfirmationResult result = await RunAsync(session, request, cancellation.Token);
        Assert.Equal(CloudContentConfirmationOutcome.Canceled, result.Outcome);
        Assert.True(result.NativeIdentityPrepared);
        Assert.False(result.NativeApplied);
        Assert.Equal(0, session.ActiveReferences);
        Assert.Equal(0, session.Marks);
    }

    [Fact]
    public async Task CancellationAfterMarkDoesNotUndoOrMisreportTheCommit()
    {
        using CancellationTokenSource cancellation = new();
        FakeSession session = new(Content) { OnMark = cancellation.Cancel };
        CloudContentConfirmationResult result = await RunAsync(session, Proof(Content), cancellation.Token);
        Assert.Equal(CloudContentConfirmationOutcome.Confirmed, result.Outcome);
        Assert.True(result.NativeApplied);
        Assert.True(result.NativeConfirmationVerified);
        Assert.Equal(1, session.Marks);
    }

    [Fact]
    public async Task ExactPreMarkFaultIsPreservedAfterCompleteHashing()
    {
        VerificationFault fault = new();
        FakeSession session = new(Content) { MarkFault = fault };
        CloudContentConfirmationResult result = await RunAsync(session, Proof(Content));
        Assert.Same(fault, result.Error);
        Assert.Equal(CloudContentConfirmationStage.Mark, result.Stage);
        Assert.Equal(Content.Length, result.BytesVerified);
        Assert.False(result.NativeApplied);
        Assert.Equal(0, session.ActiveReferences);
    }

    [Fact]
    public async Task DeadlineStopsFurtherSegmentsBeforeMark()
    {
        FakeSession session = new(Content) { OnRead = () => Thread.Sleep(20) };
        CloudContentConfirmationRequest request = new(Binding, Identity, Content.Length, SHA256.HashData(Content),
            segmentSize: 4, referenceBudget: TimeSpan.FromMilliseconds(10), deadline: TimeSpan.FromMilliseconds(10));
        CloudContentConfirmationResult result = await RunAsync(session, request);
        Assert.Equal(CloudContentConfirmationOutcome.DeadlineExceeded, result.Outcome);
        Assert.Equal(0, session.Marks);
        Assert.Equal(0, session.ActiveReferences);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredEntryDeadlinePreventsOpeningPreparingAndMarkingWithoutTimerCancellation(bool convert)
    {
        FakeSession session = new([]);
        session.Facts = session.Facts with { IsPlaceholder = !convert, Identity = convert ? [] : Identity.Encode() };
        CloudContentConfirmationRequest request = new(Binding, Identity, 0, SHA256.HashData([]),
            convert ? CloudContentPreparation.ConvertRegularFile : CloudContentPreparation.None,
            referenceBudget: TimeSpan.FromMilliseconds(500), deadline: TimeSpan.FromMilliseconds(500));
        int opens = 0;
        // Seed the public entry before the deadline without relying on any timer or captured
        // continuation. The isolated native subprocess separately delays actual pool scheduling.
        long entry = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
        CloudContentConfirmationResult result = await CloudProtectedContentConfirmation.RunAsync(() =>
        {
            opens++;
            return session;
        }, request, "owned-test.bin", entry, CancellationToken.None);
        Assert.Equal(CloudContentConfirmationOutcome.DeadlineExceeded, result.Outcome);
        Assert.Equal(CloudContentConfirmationStage.Open, result.Stage);
        Assert.True(result.Elapsed >= request.Deadline);
        Assert.Equal(0, opens);
        Assert.Equal(0, session.Reads);
        Assert.Equal(0, session.Preparations);
        Assert.Equal(0, session.Marks);
        Assert.False(result.NativeIdentityPrepared);
        Assert.False(result.NativeApplied);
        Assert.False(result.NativeConfirmationVerified);
        Assert.False(result.DurableProjectionCommitted);
    }

    private static CloudContentConfirmationRequest Proof(byte[] content) =>
        new(Binding, Identity, content.Length, SHA256.HashData(content), segmentSize: 4);

    [Fact]
    public async Task EverySegmentRunsOutsideTheCallingTaskScheduler()
    {
        bool detached = true;
        FakeSession session = new(Content)
        {
            OnRead = () => detached &= TaskScheduler.Current == TaskScheduler.Default && SynchronizationContext.Current is null,
        };
        ConcurrentExclusiveSchedulerPair scheduler = new(TaskScheduler.Default, maxConcurrencyLevel: 1);
        try
        {
            CloudContentConfirmationResult result = await Task.Factory.StartNew(() => RunAsync(session, Proof(Content)),
                CancellationToken.None, TaskCreationOptions.None, scheduler.ExclusiveScheduler).Unwrap().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(CloudContentConfirmationOutcome.Confirmed, result.Outcome);
            Assert.True(session.Reads > 1);
            Assert.True(detached, "A protected segment inherited the caller's task scheduler or synchronization context.");
        }
        finally
        {
            scheduler.Complete();
            await scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void ProjectionObservationsOwnTheirCompleteIdentityBytes()
    {
        byte[] identity = Identity.Encode();
        CloudContentConfirmationResult native = new(Proof(Content), CloudContentConfirmationOutcome.Confirmed,
            CloudContentConfirmationStage.Mark, false, true, true, false, Content.Length, 1,
            TimeSpan.Zero, TimeSpan.Zero, null, null);
        CloudContentConfirmationResult projected = native.WithProjection(CloudContentConfirmationOutcome.Confirmed,
            true, TimeSpan.Zero, CloudSynchronizationState.InSync, identity);
        identity[0] ^= 0xff;
        Assert.Equal(Identity.Encode(), projected.ObservedPlaceholderIdentity!.Value.ToArray());
        Assert.True(MemoryMarshal.TryGetArray(projected.ObservedPlaceholderIdentity.Value, out ArraySegment<byte> returned));
        returned.Array![0] ^= 0xff;
        Assert.Equal(Identity.Encode(), projected.ObservedPlaceholderIdentity.Value.ToArray());
        Assert.Null(native.ObservedPlaceholderIdentity);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedProjectionPreservesAnEarlierNativeFailureAndPreparation(bool failDuringRead)
    {
        System.ComponentModel.Win32Exception nativeFault = new(5);
        FakeSession session = null!;
        session = new(Content)
        {
            MarkFault = failDuringRead ? null : nativeFault,
            OnRead = () =>
            {
                if (failDuringRead && session.Preparations > 0)
                {
                    throw nativeFault;
                }
            },
        };
        session.Facts = session.Facts with { IsPlaceholder = false, Identity = [] };
        CloudContentConfirmationRequest request = new(Binding, Identity, Content.Length, SHA256.HashData(Content),
            CloudContentPreparation.ConvertRegularFile, segmentSize: 4);
        CloudContentConfirmationResult native = await RunAsync(session, request);
        CloudFilesException original = Assert.IsType<CloudFilesException>(native.Error);
        IOException projectionFault = new("Injected transaction failure.");
        CloudContentConfirmationResult projected = native.WithProjection(native.Outcome, false,
            TimeSpan.Zero, CloudSynchronizationState.NotInSync, Identity.Encode(), projectionFault);
        Assert.Equal(CloudContentConfirmationOutcome.Failed, projected.Outcome);
        Assert.Equal(CloudContentConfirmationStage.Projection, projected.Stage);
        Assert.Equal(failDuringRead ? CloudContentConfirmationStage.Read : CloudContentConfirmationStage.Mark, projected.NativeStage);
        Assert.Same(original, projected.NativeError);
        Assert.Equal(unchecked((int)0x80070005), projected.NativeError!.HResult);
        Assert.Same(projectionFault, projected.ProjectionError);
        Assert.Equal(new Exception[] { original, projectionFault }, Assert.IsType<AggregateException>(projected.Error).InnerExceptions);
        Assert.True(projected.NativeIdentityPrepared);
        Assert.Equal(0, projected.PreparationHResult);
        Assert.False(projected.NativeApplied);
        Assert.False(projected.NativeConfirmationVerified);
        Assert.False(projected.DurableProjectionCommitted);
        Assert.Equal(0, session.ActiveReferences);
        Assert.True(session.Disposed);
    }

    [Fact]
    public async Task WrappedPathIoErrorsRetainTheirNativeCodeAndUncommittedReceipt()
    {
        foreach (int code in new[] { 5, 32 })
        {
            IOException error = new("Injected path component open failure.", new System.ComponentModel.Win32Exception(code));
            CloudContentConfirmationResult result = await CloudProtectedContentConfirmation.RunAsync(
                () => throw error, Proof(Content), "owned-test.bin", Stopwatch.GetTimestamp(), default);
            Assert.Equal(code == 32 ? CloudContentConfirmationOutcome.Busy : CloudContentConfirmationOutcome.Failed, result.Outcome);
            Assert.Equal(CloudContentConfirmationStage.Open, result.Stage);
            CloudFilesException native = Assert.IsType<CloudFilesException>(result.Error);
            Assert.Equal(code, native.Win32ErrorCode);
            Assert.Equal(unchecked((int)(0x80070000u | (uint)code)), native.HResult);
            Assert.Same(native, result.NativeError);
            Assert.False(result.NativeIdentityPrepared);
            Assert.False(result.NativeApplied);
            Assert.False(result.DurableProjectionCommitted);
        }
    }

    [Fact]
    public async Task AnExpiredEarlierReferenceStopsBeforeTheTotalDeadline()
    {
        int reads = 0;
        FakeSession session = new(Content) { OnRead = () => { if (++reads == 1) { Thread.Sleep(50); } } };
        CloudContentConfirmationRequest request = new(Binding, Identity, Content.Length, SHA256.HashData(Content),
            segmentSize: 4, referenceBudget: TimeSpan.FromMilliseconds(10), deadline: TimeSpan.FromSeconds(5));
        CloudContentConfirmationResult result = await RunAsync(session, request);
        Assert.Equal(CloudContentConfirmationOutcome.DeadlineExceeded, result.Outcome);
        Assert.Equal(CloudContentConfirmationStage.Read, result.Stage);
        Assert.IsType<TimeoutException>(result.Error);
        Assert.Equal(1, session.Reads);
        Assert.Equal(0, session.Marks);
        Assert.True(result.LongestReference >= request.ReferenceBudget);
        Assert.True(session.Disposed);
        Assert.Equal(0, session.ActiveReferences);
    }

    [Fact]
    public async Task PreparationThatReturnsAfterItsBudgetRetainsTheMutationAndStops()
    {
        FakeSession session = new(Content) { OnPrepare = () => Thread.Sleep(50) };
        session.Facts = session.Facts with { IsPlaceholder = false, Identity = [] };
        CloudContentConfirmationRequest request = new(Binding, Identity, Content.Length, SHA256.HashData(Content),
            CloudContentPreparation.ConvertRegularFile, segmentSize: 4,
            referenceBudget: TimeSpan.FromMilliseconds(10), deadline: TimeSpan.FromSeconds(5));
        CloudContentConfirmationResult result = await RunAsync(session, request);
        Assert.Equal(CloudContentConfirmationOutcome.DeadlineExceeded, result.Outcome);
        Assert.Equal(CloudContentConfirmationStage.Prepare, result.Stage);
        Assert.True(result.NativeIdentityPrepared);
        Assert.Equal(0, result.PreparationHResult);
        Assert.Equal(0, result.PreparationUsn);
        Assert.False(result.NativeApplied);
        Assert.Equal(1, session.Preparations);
        Assert.Equal(0, session.Marks);
        Assert.Equal(0, session.ActiveReferences);
    }

    [Fact]
    public async Task MarkThatSucceedsAfterItsBudgetStillRetainsTheCommit()
    {
        FakeSession session = new(Content) { OnMark = () => Thread.Sleep(50) };
        CloudContentConfirmationRequest request = new(Binding, Identity, Content.Length, SHA256.HashData(Content),
            segmentSize: 4, referenceBudget: TimeSpan.FromMilliseconds(10), deadline: TimeSpan.FromSeconds(5));
        CloudContentConfirmationResult result = await RunAsync(session, request);
        Assert.Equal(CloudContentConfirmationOutcome.Confirmed, result.Outcome);
        Assert.True(result.NativeApplied);
        Assert.True(result.NativeConfirmationVerified);
        Assert.Equal(0, result.NativeMarkHResult);
        Assert.True(result.LongestReference >= request.ReferenceBudget);
        Assert.Null(result.Error);
    }

    private static Task<CloudContentConfirmationResult> RunAsync(FakeSession session,
        CloudContentConfirmationRequest proof, CancellationToken token = default) =>
        CloudProtectedContentConfirmation.RunAsync(() => session, proof, "owned-test.bin", Stopwatch.GetTimestamp(), token);

    private sealed class VerificationFault : Exception;

    private sealed class FakeSession(byte[] content) : ICloudProtectedContentSession
    {
        internal CloudProtectedFileFacts Facts { get; set; } = new(Binding, content.Length, false,
            true, true, true, false, Identity.Encode());
        internal bool InSync { get => Facts.InSync; init => Facts = Facts with { InSync = value }; }
        internal bool ShortReads { get; init; }
        internal int BreakAtReference { get; init; }
        internal Action? OnPrepare { get; init; }
        internal Action? OnRead { get; init; }
        internal Action? OnMark { get; init; }
        internal Exception? MarkFault { get; init; }
        internal int Reads { get; private set; }
        internal int Preparations { get; private set; }
        internal int Marks { get; private set; }
        internal int ActiveReferences { get; private set; }
        internal bool Disposed { get; private set; }
        private int _references;

        public ICloudProtectedContentReference Reference()
        {
            if (++_references == BreakAtReference)
            {
                throw new InvalidOperationException("Injected oplock break.");
            }

            Assert.Equal(0, ActiveReferences++);
            return new ReferenceOwner(this, content);
        }

        public void Dispose()
        {
            Assert.Equal(0, ActiveReferences);
            Disposed = true;
        }

        private sealed class ReferenceOwner(FakeSession owner, byte[] content) : ICloudProtectedContentReference
        {
            public CloudProtectedFileFacts Inspect() => owner.Facts;
            public int Read(byte[] buffer, int count, long offset, TimeSpan budget, CancellationToken cancellationToken)
            {
                owner.Reads++;
                owner.OnRead?.Invoke();
                int read = (int)Math.Min(count, Math.Max(0, content.Length - offset));
                if (owner.ShortReads)
                {
                    read = Math.Min(read, 2);
                }

                content.AsSpan((int)Math.Min(offset, content.Length), read).CopyTo(buffer);
                return read;
            }

            public (int HResult, long Usn) Prepare(CloudContentConfirmationRequest request, bool convert)
            {
                owner.Preparations++;
                owner.Facts = owner.Facts with { IsPlaceholder = true, Identity = request.AcceptedIdentity.Encode(), InSync = false };
                owner.OnPrepare?.Invoke();
                return (0, 0);
            }

            public int Mark()
            {
                if (owner.MarkFault is not null)
                {
                    throw owner.MarkFault;
                }

                owner.Marks++;
                owner.OnMark?.Invoke();
                return 0;
            }

            public void Dispose() => owner.ActiveReferences--;
        }
    }
}
