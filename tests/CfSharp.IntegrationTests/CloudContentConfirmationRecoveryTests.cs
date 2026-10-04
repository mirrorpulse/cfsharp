using System.Security.Cryptography;

using CfSharp.Storage.Sqlite;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
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
        internal Func<ValueTask>? BeforeCommit { get; set; }

        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context,
            CancellationToken cancellationToken = default) =>
            new FaultStore(await _inner.OpenAsync(context, cancellationToken), this);

        private sealed class FaultStore(ICloudStateStore inner, FaultFactory faults) : ICloudStateStore
        {
            public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
                new FaultTransaction(await inner.BeginTransactionAsync(cancellationToken), faults);
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
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
