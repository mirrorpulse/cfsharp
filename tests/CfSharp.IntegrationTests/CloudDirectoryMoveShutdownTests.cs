using System.Reflection;
using System.Runtime.Versioning;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class CloudDirectoryMoveTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AdmittedMoveCompletesDurableProjectionDuringOwnerShutdown(bool afterNativeMove, bool ownedFeed)
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudOperationJournalEntry original = await SeedPendingChildAsync(root);
        CloudDirectory source = root.FileSystem.GetDirectory("Docs");
        CloudDirectory destination = root.FileSystem.Root;
        ShutdownMoveSource? producer = ownedFeed ? new() : null;
        CloudLocalChangeFeed? feed = null;
        if (producer is not null)
        {
            feed = root.FileSystem.CreateLocalChangeFeed();
            // Preserve the owner's real acquisition and disposal callbacks while controlling
            // exactly when the final rename pair reaches the drain queue.
            typeof(CloudLocalChangeFeed).GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(feed, producer);
            await feed.StartAsync();
        }

        TaskCompletionSource reachedCut = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim releaseMove = new();
        void PauseMove()
        {
            root.Faults.BeforeCommit = null;
            root.Faults.AfterCommit = null;
            reachedCut.TrySetResult();
            Assert.True(releaseMove.Wait(TimeSpan.FromSeconds(20)), "The test did not release the admitted move.");
        }

        if (afterNativeMove)
        {
            // Preparation is still committed first. This second cut is at projection commit,
            // with the native rename already complete and its metadata guard held.
            root.Faults.BeforeCommit = () =>
            {
                if (Directory.Exists(Path.Combine(root.RootPath, "Moved")))
                {
                    PauseMove();
                }
            };
        }
        else
        {
            root.Faults.AfterCommit = PauseMove;
        }

        Task<CloudItemMoveResult> moveTask = Task.Run(async () => await source.MoveToAsync(destination, "Moved"));
        Task? shutdown = null;
        CloudItemMoveResult result;
        try
        {
            await reachedCut.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(afterNativeMove, Directory.Exists(Path.Combine(root.RootPath, "Moved")));
            Assert.Equal(!afterNativeMove, Directory.Exists(source.FullPath));
            shutdown = root.FileSystem.DisposeAsync().AsTask();
            Assert.True(SpinWait.SpinUntil(() => root.FileSystem.LifecycleState == CloudFileSystemLifecycleState.Stopping,
                TimeSpan.FromSeconds(10)));
            Assert.False(shutdown.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => root.FileSystem.GetDirectory("Moved"));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => source.InspectAsync().AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => source.PrepareMoveAsync(destination, "Other").AsTask());
            if (producer is not null)
            {
                await producer.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }

            releaseMove.Set();
            result = await moveTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            root.Faults.BeforeCommit = null;
            root.Faults.AfterCommit = null;
            releaseMove.Set();
            if (shutdown is not null)
            {
                await shutdown.WaitAsync(TimeSpan.FromSeconds(15));
            }

            if (feed is not null)
            {
                await feed.DisposeCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        Assert.Equal(CloudFileSystemLifecycleState.Disposed, root.FileSystem.LifecycleState);
        CloudDirectoryMoveReconciliationResult recovery = Assert.IsType<CloudDirectoryMoveReconciliationResult>(result.DirectoryReconciliation);
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.Projected, recovery.Outcome);
        Assert.Equal(CloudDirectoryMoveReconciliationStage.Completed, recovery.Stage);
        Assert.True(recovery.NativeMoveObserved && recovery.DurableProjectionCommitted);
        Assert.False(recovery.RequiresFullRescan);
        Assert.Equal(2, result.DurableStateEntriesUpdated);
        Assert.True(Directory.Exists(Path.Combine(root.RootPath, "Moved")));
        Assert.False(Directory.Exists(source.FullPath));

        await root.RestartAsync();
        await AssertProjectedChildAsync(root, "Moved", original);
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        CloudDirectoryMoveProof proof = CloudDirectoryMoveProof.Decode((await verify.Checkpoints.GetAsync(
            CloudDirectoryMoveEvidence.IntentName("Docs", "Moved")))!.Value.Span);
        Assert.NotNull(await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ReceiptName(proof.ProofId)));
        IReadOnlyList<CloudOperationJournalEntry> journal = await verify.Operations.ListAsync(100);
        if (ownedFeed)
        {
            CloudOperationJournalEntry move = Assert.Single(journal.Where(row => row.Kind == CloudStateOperationKind.Move));
            Assert.Equal(root.DirectoryIdentity.ItemId, move.ItemId);
            LocalChangePayload payload = LocalChangePayload.Decode(move.Payload);
            Assert.Equal("Docs", payload.PreviousRelativePath);
            Assert.Equal("Moved", payload.RelativePath);
            Assert.True(payload.IsDirectory);
            Assert.False(LocalChangeCheckpoint.Decode((await verify.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName))!.Value).RequiresFullRescan);
        }
        else
        {
            Assert.Equal(original.OperationId, Assert.Single(journal).OperationId);
            Assert.Null(await verify.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName));
        }
    }

    private sealed class ShutdownMoveSource : ILocalChangeSource
    {
        private Func<LocalChangeSourceEvent, ValueTask> _handler = null!;
        internal TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(Func<LocalChangeSourceEvent, ValueTask> eventHandler, CancellationToken cancellationToken)
        {
            _handler = eventHandler;
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await _handler(new(LocalChangeSourceAction.RenamedOldName, "Docs"));
            await _handler(new(LocalChangeSourceAction.RenamedNewName, "Moved"));
            Stopping.TrySetResult();
        }
    }
}
