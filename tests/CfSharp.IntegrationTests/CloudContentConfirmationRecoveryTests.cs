using System.Security.Cryptography;

using CfSharp.Native;
using CfSharp.Storage.Sqlite;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Theory]
    [InlineData("move")]
    [InlineData("dehydrate")]
    [InlineData("remote")]
    public async Task SameItemMutationsWaitUntilConfirmationProjectionCompletes(string mutation)
    {
        FaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new FaultFactory(path));
        byte[] content = "upload"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudContentConfirmationRequest proof = Proof(binding, content, CloudContentPreparation.ConvertRegularFile);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        factory!.BeforeCommit = () =>
        {
            entered.SetResult();
            return new ValueTask(release.Task);
        };
        Task<CloudContentConfirmationResult> confirming = fixture.File.ConfirmUploadedContentAsync(proof).AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            CloudRemoteChange change = new("update", CloudRemoteChangeKind.FileUpsert,
                proof.AcceptedIdentity.RemoteId, "revision-3", CloudItemKind.File, "content.bin",
                previousRemoteRevision: proof.AcceptedIdentity.RemoteRevision, length: content.Length,
                metadata: CloudPlaceholderMetadata.CreateFileBuilder().Build());
            Task subsequent = mutation switch
            {
                "move" => fixture.File.MoveToAsync(fixture.System.Root, "moved.bin").AsTask(),
                "dehydrate" => fixture.File.DehydrateAsync(CloudFileRange.ToEnd(0)).AsTask(),
                _ => fixture.System.ApplyRemoteChangesAsync(new CloudRemoteChangeBatch("update-batch", Array.Empty<byte>(), [change], new byte[] { 1 })).AsTask(),
            };
            await Task.Delay(50);
            Assert.False(subsequent.IsCompleted);
            release.SetResult();
            Assert.Equal(CloudContentConfirmationOutcome.Confirmed,
                (await confirming.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
            await subsequent.WaitAsync(TimeSpan.FromSeconds(10));
            if (mutation == "move")
            {
                Assert.Equal(binding, (await fixture.System.GetFile("moved.bin").InspectAsync()).LocalBinding);
                Assert.False((await fixture.File.InspectAsync()).Exists);
            }
            else if (mutation == "dehydrate")
            {
                Assert.Equal(CloudContentAvailability.OnlineOnly, (await fixture.File.InspectAsync()).ContentAvailability);
            }
            else
            {
                CloudRemoteApplyResult applied = await (Task<CloudRemoteApplyResult>)subsequent;
                Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single(applied.Entries).Status);
                Assert.Equal("revision-3", (await fixture.File.InspectAsync()).RemoteRevision);
            }

            Assert.Equal(0, fixture.Provider.Fetches);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task DeadlineAndCancellationCoverWaitingForTheItemLease()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        byte[] content = "upload"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudContentConfirmationRequest proof = Proof(binding, content, CloudContentPreparation.ConvertRegularFile);
        await using CloudItemLease lease = await fixture.File.AcquireLeaseAsync(CloudItemLeaseOptions.ExclusiveWrite);
        CloudContentConfirmationRequest shortDeadline = new(binding, proof.AcceptedIdentity, content.Length,
            proof.ExpectedSha256.Span, proof.Preparation, referenceBudget: TimeSpan.FromMilliseconds(10),
            deadline: TimeSpan.FromMilliseconds(50));
        CloudContentConfirmationResult expired = await fixture.File.ConfirmUploadedContentAsync(shortDeadline)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CloudContentConfirmationOutcome.DeadlineExceeded, expired.Outcome);
        Assert.Equal(CloudContentConfirmationStage.Open, expired.Stage);
        Assert.IsType<TimeoutException>(expired.Error);
        Assert.False(expired.NativeIdentityPrepared);
        Assert.False(expired.NativeApplied);
        using CancellationTokenSource cancellation = new();
        Task<CloudContentConfirmationResult> waiting = fixture.File.ConfirmUploadedContentAsync(proof, cancellation.Token).AsTask();
        cancellation.Cancel();
        CloudContentConfirmationResult canceled = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CloudContentConfirmationOutcome.Canceled, canceled.Outcome);
        Assert.Equal(0, canceled.BytesVerified);
        Assert.False(canceled.NativeApplied);
        await lease.DisposeAsync();
        Assert.Equal(CloudContentConfirmationOutcome.Confirmed, (await fixture.File.ConfirmUploadedContentAsync(proof)).Outcome);
    }

    [Fact]
    public async Task NativeCommitAndFailedProjectionReplayAfterReopeningTheOfficialStore()
    {
        FaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new FaultFactory(path));
        byte[] content = "upload"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudContentConfirmationRequest request = Proof(binding, content, CloudContentPreparation.ConvertRegularFile);
        ProjectionFault fault = new();
        factory!.NextCommitFault = fault;
        CloudContentConfirmationResult pending = await fixture.File.ConfirmUploadedContentAsync(request);
        Assert.Equal(CloudContentConfirmationOutcome.NativeAppliedProjectionPending, pending.Outcome);
        Assert.True(pending.NativeIdentityPrepared);
        Assert.True(pending.NativeApplied);
        Assert.False(pending.DurableProjectionCommitted);
        Assert.Same(fault, pending.Error);
        Assert.Equal(CloudContentConfirmationStage.Projection, pending.Stage);
        Assert.Equal(CloudSynchronizationState.InSync, (await fixture.File.InspectAsync()).SynchronizationState);
        Assert.Null((await fixture.File.InspectAsync()).RemoteRevision);
        await fixture.RestartAsync();
        CloudContentConfirmationResult recovered = await fixture.File.ConfirmUploadedContentAsync(request);
        Assert.Equal(CloudContentConfirmationOutcome.AlreadyConfirmed, recovered.Outcome);
        Assert.False(recovered.NativeApplied);
        Assert.True(recovered.DurableProjectionCommitted);
        Assert.Equal(binding, (await fixture.File.InspectAsync()).LocalBinding);
        Assert.Equal(request.AcceptedIdentity.RemoteRevision, (await fixture.File.InspectAsync()).RemoteRevision);
    }

    [Fact]
    public async Task CancellationAtProjectionCannotReportAnUnappliedMark()
    {
        FaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new FaultFactory(path));
        byte[] content = "upload"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        using CancellationTokenSource cancellation = new();
        factory!.BeforeCommit = () =>
        {
            cancellation.Cancel();
            return ValueTask.CompletedTask;
        };
        CloudContentConfirmationResult result = await fixture.File.ConfirmUploadedContentAsync(
            Proof(binding, content, CloudContentPreparation.ConvertRegularFile), cancellation.Token);
        Assert.Equal(CloudContentConfirmationOutcome.Confirmed, result.Outcome);
        Assert.True(result.NativeApplied);
        Assert.True(result.DurableProjectionCommitted);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task WriteAfterMarkRemainsNotInSyncDuringDurableProjection()
    {
        FaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new FaultFactory(path));
        byte[] content = "upload"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        factory!.BeforeCommit = async () =>
        {
            await using FileStream writer = new(fixture.File.FullPath, FileMode.Open, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete);
            await writer.WriteAsync("change"u8.ToArray());
        };
        CloudContentConfirmationResult result = await fixture.File.ConfirmUploadedContentAsync(
            Proof(binding, content, CloudContentPreparation.ConvertRegularFile));
        Assert.Equal(CloudContentConfirmationOutcome.Confirmed, result.Outcome);
        Assert.True(result.NativeApplied);
        Assert.True(result.DurableProjectionCommitted);
        Assert.Equal(CloudSynchronizationState.NotInSync, result.ObservedSynchronizationState);
        Assert.Equal(CloudSynchronizationState.NotInSync, (await fixture.File.InspectAsync()).SynchronizationState);
        Assert.Equal("change"u8.ToArray(), await File.ReadAllBytesAsync(fixture.File.FullPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeIdentityReplacementDuringProjectionReportsAConflict(bool duringCommit)
    {
        FaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new FaultFactory(path));
        byte[] content = "upload"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudContentConfirmationRequest proof = Proof(binding, content, CloudContentPreparation.ConvertRegularFile);
        CloudPlaceholderIdentity subsequent = new(proof.AcceptedIdentity.ItemId, proof.AcceptedIdentity.RemoteId, "revision-3");
        Func<ValueTask> replace = () =>
        {
            Assert.Equal(0, ReplaceNativeIdentity(fixture.File.FullPath, subsequent));
            return ValueTask.CompletedTask;
        };
        if (duringCommit)
        {
            factory!.BeforeCommit = replace;
        }
        else
        {
            factory!.BeforeTransaction = replace;
        }

        CloudContentConfirmationResult result = await fixture.File.ConfirmUploadedContentAsync(proof);
        Assert.Equal(CloudContentConfirmationOutcome.ProjectionConflict, result.Outcome);
        Assert.True(result.NativeIdentityPrepared);
        Assert.True(result.NativeApplied);
        Assert.True(result.NativeConfirmationVerified);
        Assert.Equal(duringCommit, result.DurableProjectionCommitted);
        Assert.Equal(CloudContentConfirmationStage.Projection, result.Stage);
        Assert.IsType<IOException>(result.Error);
        Assert.Same(result.Error, result.ProjectionError);
        Assert.Null(result.NativeError);
        Assert.Equal(CloudContentConfirmationStage.Mark, result.NativeStage);
        Assert.Equal(CloudSynchronizationState.NotInSync, result.ObservedSynchronizationState);
        Assert.Equal(subsequent.Encode(), result.ObservedPlaceholderIdentity!.Value.ToArray());
        CloudItemSnapshot snapshot = await fixture.File.InspectAsync();
        Assert.Equal(subsequent.Encode(), snapshot.PlaceholderIdentity.ToArray());
        Assert.Equal(duringCommit ? proof.AcceptedIdentity.RemoteRevision : null, snapshot.RemoteRevision);
        Assert.Equal(binding, snapshot.LocalBinding);
    }

    private static unsafe int ReplaceNativeIdentity(string path, CloudPlaceholderIdentity identity)
    {
        // Use an ordinary writable Win32 handle on the same file, without another store owner
        // or path replacement. A no-delete metadata guard does not prevent this CFAPI update.
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        byte[] bytes = identity.Encode();
        fixed (byte* pointer = bytes)
        {
            return CfApi.CfUpdatePlaceholder(handle.DangerousGetHandle(), null, pointer, (uint)bytes.Length,
                null, 0, CfUpdateFlags.ClearInSync, null, null);
        }
    }

    [Fact]
    public async Task DisposalFailureAfterCommitRetainsTheActualDatabaseCommit()
    {
        FaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new FaultFactory(path));
        byte[] content = "upload"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudContentConfirmationRequest proof = Proof(binding, content, CloudContentPreparation.ConvertRegularFile);
        ProjectionFault fault = new();
        factory!.BeforeCommit = () =>
        {
            factory.NextDisposeFault = fault;
            return ValueTask.CompletedTask;
        };
        CloudContentConfirmationResult result = await fixture.File.ConfirmUploadedContentAsync(proof);
        Assert.Equal(CloudContentConfirmationOutcome.NativeAppliedProjectionPending, result.Outcome);
        Assert.Equal(CloudContentConfirmationStage.Projection, result.Stage);
        Assert.True(result.NativeApplied);
        Assert.True(result.NativeConfirmationVerified);
        Assert.True(result.DurableProjectionCommitted);
        Assert.Same(fault, result.ProjectionError);
        Assert.Same(fault, result.Error);
        Assert.Null(result.NativeError);
        Assert.Equal(proof.AcceptedIdentity.RemoteRevision, (await fixture.File.InspectAsync()).RemoteRevision);
    }

    [Fact]
    public async Task OtherFilesProgressWhileSameFileOperationsAndShutdownDrain()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        await using (FileStream stream = new(fixture.File.FullPath, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength(128L * 1024 * 1024);
        }

        CloudPlaceholderIdentity accepted = new(Guid.NewGuid(), "large-object", "revision-1");
        await fixture.File.ConvertToPlaceholderAsync(accepted);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudContentConfirmationRequest request = new(binding, accepted, 128L * 1024 * 1024, new byte[32], segmentSize: 128);
        Task<CloudContentConfirmationResult> hashing = fixture.File.ConfirmUploadedContentAsync(request).AsTask();
        await Task.Delay(100);
        Assert.False(hashing.IsCompleted);
        Task<CloudStateChangeResult> sameFile = fixture.File.SetInSyncAsync(false).AsTask();
        await Task.Delay(50);
        Assert.False(sameFile.IsCompleted);
        CloudFile other = fixture.System.GetFile("other.bin");
        byte[] content = "upload"u8.ToArray();
        await File.WriteAllBytesAsync(other.FullPath, content);
        CloudLocalFileBinding otherBinding = Assert.IsType<CloudLocalFileBinding>((await other.InspectAsync()).LocalBinding);
        CloudContentConfirmationResult independent = await other.ConfirmUploadedContentAsync(
            Proof(otherBinding, content, CloudContentPreparation.ConvertRegularFile)).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CloudContentConfirmationOutcome.Confirmed, independent.Outcome);
        await fixture.System.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        CloudContentConfirmationResult canceled = await hashing;
        Assert.Equal(CloudContentConfirmationOutcome.Canceled, canceled.Outcome);
        Assert.False(canceled.NativeApplied);
        Assert.True(canceled.BytesVerified > 0);
        await sameFile;
        Assert.Equal(CloudFileSystemLifecycleState.Disposed, fixture.System.LifecycleState);
    }

    private sealed class ProjectionFault : Exception;

    // Decorate the official store rather than replace it with an in-memory imitation. Every
    // failed transaction is disposed by production code and rolls back its real SQLite writes.
    private sealed class FaultFactory(string path) : ICloudStateStoreFactory
    {
        private readonly SqliteCloudStateStoreFactory _inner = new(path);
        internal Exception? NextCommitFault { get; set; }
        internal Exception? NextDisposeFault { get; set; }
        internal Func<ValueTask>? BeforeTransaction { get; set; }
        internal Func<ValueTask>? BeforeCommit { get; set; }

        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context,
            CancellationToken cancellationToken = default) =>
            new FaultStore(await _inner.OpenAsync(context, cancellationToken), this);

        private sealed class FaultStore(ICloudStateStore inner, FaultFactory faults) : ICloudStateStore
        {
            public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
            {
                if (faults.BeforeTransaction is { } before)
                {
                    faults.BeforeTransaction = null;
                    await before();
                }

                return new FaultTransaction(await inner.BeginTransactionAsync(cancellationToken), faults);
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }

        private sealed class FaultTransaction(ICloudStateTransaction inner, FaultFactory faults) : ICloudStateTransaction
        {
            public ICloudItemStateRepository Items => inner.Items;
            public ICloudCheckpointRepository Checkpoints => inner.Checkpoints;
            public ICloudOperationJournal Operations => inner.Operations;
            public ICloudConflictRepository Conflicts => inner.Conflicts;
            public ICloudRemoteBatchRepository RemoteBatches => inner.RemoteBatches;
            public ICloudEchoSuppressionRepository EchoSuppressions => inner.EchoSuppressions;

            public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
            {
                if (faults.BeforeCommit is { } before)
                {
                    faults.BeforeCommit = null;
                    await before();
                }

                if (faults.NextCommitFault is { } exception)
                {
                    faults.NextCommitFault = null;
                    throw exception;
                }

                await inner.CommitAsync(cancellationToken);
            }

            public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
            public async ValueTask DisposeAsync()
            {
                await inner.DisposeAsync();
                if (faults.NextDisposeFault is { } exception)
                {
                    faults.NextDisposeFault = null;
                    throw exception;
                }
            }
        }
    }
}
