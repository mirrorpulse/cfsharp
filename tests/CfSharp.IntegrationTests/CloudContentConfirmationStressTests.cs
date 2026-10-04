using System.Security.Cryptography;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Fact]
    public async Task RepeatedIdentityRacesAndCancellationRetainConsistentReceipts()
    {
        FaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new FaultFactory(path));
        byte[] content = new byte[128 * 1024];
        Random.Shared.NextBytes(content);
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudPlaceholderIdentity accepted = new(Guid.NewGuid(), "stress-object", "accepted-revision");
        await fixture.File.ConvertToPlaceholderAsync(accepted);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudContentConfirmationRequest proof = new(binding, accepted, content.Length, SHA256.HashData(content),
            segmentSize: 16 * 1024, referenceBudget: TimeSpan.FromSeconds(2), deadline: TimeSpan.FromSeconds(10));
        int cancellations = 0;
        int conflicts = 0;
        int coordinatedUpdates = 0;
        for (int iteration = 0; iteration < 24; iteration++)
        {
            await fixture.File.SetInSyncAsync(false);
            using CancellationTokenSource cancellation = new();
            CloudPlaceholderIdentity subsequent = new(accepted.ItemId, accepted.RemoteId, $"concurrent-{iteration}");
            if (iteration % 3 == 0)
            {
                await using CloudItemLease lease = await fixture.File.AcquireLeaseAsync(CloudItemLeaseOptions.ExclusiveWrite);
                Task<CloudContentConfirmationResult> waiting = fixture.File.ConfirmUploadedContentAsync(proof, cancellation.Token).AsTask();
                cancellation.Cancel();
                CloudContentConfirmationResult canceled = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(CloudContentConfirmationOutcome.Canceled, canceled.Outcome);
                Assert.Equal(CloudContentConfirmationStage.Open, canceled.Stage);
                Assert.False(canceled.NativeApplied);
                Assert.False(canceled.DurableProjectionCommitted);
                cancellations++;
            }
            else if (iteration % 3 == 1)
            {
                factory!.BeforeCommit = () =>
                {
                    Assert.Equal(0, ReplaceNativeIdentity(fixture.File.FullPath, subsequent));
                    return ValueTask.CompletedTask;
                };
                CloudContentConfirmationResult conflict = await fixture.File.ConfirmUploadedContentAsync(proof)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(CloudContentConfirmationOutcome.ProjectionConflict, conflict.Outcome);
                Assert.True(conflict.NativeApplied);
                Assert.True(conflict.NativeConfirmationVerified);
                Assert.True(conflict.DurableProjectionCommitted);
                Assert.IsType<IOException>(conflict.ProjectionError);
                Assert.Equal(subsequent.Encode(), conflict.ObservedPlaceholderIdentity!.Value.ToArray());
                CloudItemSnapshot snapshot = await fixture.File.InspectAsync();
                Assert.Equal(subsequent.Encode(), snapshot.PlaceholderIdentity.ToArray());
                Assert.Equal(accepted.RemoteRevision, snapshot.RemoteRevision);
                Assert.Equal(CloudSynchronizationState.NotInSync, snapshot.SynchronizationState);
                conflicts++;
            }
            else
            {
                TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                factory!.BeforeCommit = () =>
                {
                    entered.SetResult();
                    return new ValueTask(release.Task);
                };
                Task<CloudContentConfirmationResult> confirming = fixture.File.ConfirmUploadedContentAsync(proof, cancellation.Token).AsTask();
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Task<CloudPlaceholderMutationResult> updating = fixture.File.UpdatePlaceholderAsync(
                        CloudPlaceholderPatch.CreateBuilder().WithIdentity(subsequent).WithInSyncState(false).Build()).AsTask();
                    Assert.False(updating.IsCompleted);
                    cancellation.Cancel();
                    release.SetResult();
                    CloudContentConfirmationResult confirmed = await confirming.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.Equal(CloudContentConfirmationOutcome.Confirmed, confirmed.Outcome);
                    Assert.True(confirmed.NativeApplied);
                    Assert.True(confirmed.DurableProjectionCommitted);
                    Assert.Null(confirmed.Error);
                    await updating.WaitAsync(TimeSpan.FromSeconds(5));
                    CloudItemSnapshot snapshot = await fixture.File.InspectAsync();
                    Assert.Equal(subsequent.Encode(), snapshot.PlaceholderIdentity.ToArray());
                    Assert.Equal(subsequent.RemoteRevision, snapshot.RemoteRevision);
                    coordinatedUpdates++;
                }
                finally
                {
                    release.TrySetResult();
                }
            }

            // Reset this owned fixture explicitly; production confirmation must never undo
            // the competing writer's identity or restore the historical in-sync bit itself.
            await fixture.File.UpdatePlaceholderAsync(CloudPlaceholderPatch.CreateBuilder()
                .WithIdentity(accepted).WithInSyncState(false).Build());
            Assert.Equal(binding, (await fixture.File.InspectAsync()).LocalBinding);
        }

        Assert.Equal(8, cancellations);
        Assert.Equal(8, conflicts);
        Assert.Equal(8, coordinatedUpdates);
        Assert.Equal(0, fixture.Provider.Fetches);
        output.WriteLine($"Iterations=24; AdmissionCancellations={cancellations}; NativeIdentityConflicts={conflicts}; CoordinatedUpdatesAfterMark={coordinatedUpdates}");
    }
}
