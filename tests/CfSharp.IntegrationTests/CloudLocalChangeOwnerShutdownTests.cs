using System.Reflection;
using System.Runtime.Versioning;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed class CloudLocalChangeOwnerShutdownTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerDrainsTailRenameWhileStoppingAndRejectsNewPublicWork(bool directory)
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        string before = directory ? "Docs" : "Docs\\child.txt";
        string after = directory ? "Moved" : "Docs\\renamed.txt";
        TailRenameSource source = new(before, after);
        CloudLocalChangeFeed feed = InstallSource(root, source);
        await feed.StartAsync();
        if (directory)
        {
            Directory.Move(Path.Combine(root.RootPath, before), Path.Combine(root.RootPath, after));
        }
        else
        {
            File.Move(Path.Combine(root.RootPath, before), Path.Combine(root.RootPath, after));
        }

        Task shutdown = root.FileSystem.DisposeAsync().AsTask();
        await source.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task concurrentShutdown = root.FileSystem.DisposeAsync().AsTask();
        Assert.Equal(CloudFileSystemLifecycleState.Stopping, root.FileSystem.LifecycleState);
        Assert.Throws<ObjectDisposedException>(() => root.FileSystem.GetDirectory("Docs"));
        source.Release.TrySetResult();
        try
        {
            await Task.WhenAll(shutdown, concurrentShutdown).WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await feed.DisposeCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        }

        await root.RestartAsync();
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        CloudOperationJournalEntry move = Assert.Single(await verify.Operations.ListAsync(100));
        Assert.Equal(CloudStateOperationKind.Move, move.Kind);
        LocalChangePayload payload = LocalChangePayload.Decode(move.Payload);
        Assert.Equal(before, payload.PreviousRelativePath);
        Assert.Equal(after, payload.RelativePath);
        Assert.Equal(directory, payload.IsDirectory);
        Assert.False(LocalChangeCheckpoint.Decode((await verify.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName))!.Value).RequiresFullRescan);
        if (directory)
        {
            Assert.Equal("Moved", (await verify.Items.GetByItemIdAsync(root.DirectoryIdentity.ItemId))!.RelativePath);
            Assert.NotNull(await verify.Items.GetByRelativePathAsync("Moved\\child.txt"));
        }
    }

    private static CloudLocalChangeFeed InstallSource(DirectoryMoveTestRoot root, TailRenameSource source)
    {
        CloudLocalChangeFeed feed = root.FileSystem.CreateLocalChangeFeed(new() { ShutdownTimeout = TimeSpan.FromSeconds(1) });
        // Replace only the native producer. The owned feed retains the production operation
        // acquisition delegate and disposal callback, so the test exercises the owner lifecycle.
        typeof(CloudLocalChangeFeed).GetField("_source", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(feed, source);
        return feed;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedDrainPersistsLossBeforeCancellationAndRetainsStoreForRetry(bool markerCommitFails)
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        TailRenameSource source = new("Docs", "Moved");
        CloudLocalChangeFeed feed = InstallSource(root, source);
        await feed.StartAsync();
        using CloudFileSystem.CloudFileSystemOperationLease blocker = await root.FileSystem.AcquireOperationAsync(
            [CloudItemOperationScope.Subtree(Path.Combine(root.RootPath, "Docs"))]);
        Directory.Move(Path.Combine(root.RootPath, "Docs"), Path.Combine(root.RootPath, "Moved"));
        bool fenceCommittedBeforeCancellation = false;
        root.Faults.BeforeCommit = () =>
        {
            if (markerCommitFails)
            {
                throw new IOException("Injected shutdown fence commit failure.");
            }
        };
        root.Faults.AfterCommit = () => fenceCommittedBeforeCancellation = !source.Cancellation.IsCancellationRequested;
        try
        {
            Task shutdown = root.FileSystem.DisposeAsync().AsTask();
            await source.Stopping.Task.WaitAsync(TimeSpan.FromSeconds(10));
            source.Release.TrySetResult();
            if (markerCommitFails)
            {
                AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => shutdown.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.IsType<TimeoutException>(failure.InnerExceptions[0]);
                Assert.IsType<IOException>(failure.InnerExceptions[1]);
                Assert.False(source.Cancellation.IsCancellationRequested);
                Assert.False(feed.DisposeCompletion.IsCompleted);
            }
            else
            {
                await Assert.ThrowsAsync<TimeoutException>(() => shutdown.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.True(fenceCommittedBeforeCancellation);
                Assert.True(source.Cancellation.IsCancellationRequested);
                await feed.DisposeCompletion.WaitAsync(TimeSpan.FromSeconds(10));
                await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
                Assert.Empty(await verify.Operations.ListAsync(100));
                Assert.True(LocalChangeCheckpoint.Decode((await verify.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName))!.Value).RequiresFullRescan);
            }

            Assert.Equal(CloudFileSystemLifecycleState.Stopping, root.FileSystem.LifecycleState);
        }
        finally
        {
            root.Faults.BeforeCommit = null;
            root.Faults.AfterCommit = null;
            source.Release.TrySetResult();
            blocker.Dispose();
        }

        await feed.DisposeCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        await root.RestartAsync();
        await using ICloudStateTransaction reopened = await root.Store.BeginTransactionAsync();
        Assert.True(LocalChangeCheckpoint.Decode((await reopened.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName))!.Value).RequiresFullRescan);
        Assert.Equal(markerCommitFails ? 1 : 0, (await reopened.Operations.ListAsync(100)).Count);
    }

    private sealed class TailRenameSource(string before, string after) : ILocalChangeSource
    {
        private Func<LocalChangeSourceEvent, ValueTask> _handler = null!;
        internal TaskCompletionSource Stopping { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationToken Cancellation { get; private set; }

        public Task StartAsync(Func<LocalChangeSourceEvent, ValueTask> eventHandler, CancellationToken cancellationToken)
        {
            _handler = eventHandler;
            Cancellation = cancellationToken;
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            await _handler(new(LocalChangeSourceAction.RenamedOldName, before));
            await _handler(new(LocalChangeSourceAction.RenamedNewName, after));
            Stopping.TrySetResult();
            await Release.Task;
        }
    }
}
