using System.ComponentModel;
using System.Diagnostics;

namespace CfSharp.Storage.Sqlite.Tests;

public sealed partial class CloudLocalChangeFeedTests
{
    [Fact]
    public async Task FeedStartupWaitsForSourceReadiness()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        StartupSource source = new(Path.Combine(_temporaryDirectory, "startup.lock"));
        await using CloudLocalChangeFeed feed = CloudLocalChangeFeed.CreateForTesting(_syncRootPath, store, new(), source);
        Task starting = feed.StartAsync().AsTask();
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(starting.IsCompleted);
        }
        finally { source.Ready.TrySetResult(); }

        await starting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(feed.IsStarted);
        await feed.DisposeAsync();
        Assert.Equal(1, source.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceStartupFailurePropagatesAndReleasesPartialResources(bool delayed)
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        string leasePath = Path.Combine(_temporaryDirectory, "startup.lock");
        StartupSource source = new(leasePath);
        Win32Exception failure = new(5);
        if (!delayed) { source.Ready.TrySetException(failure); }
        await using CloudLocalChangeFeed feed = CloudLocalChangeFeed.CreateForTesting(_syncRootPath, store, new(), source);
        Task starting = feed.StartAsync().AsTask();
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        source.Ready.TrySetException(failure);

        Win32Exception observed = await Assert.ThrowsAsync<Win32Exception>(() => starting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Same(failure, observed);
        Assert.Equal(5, observed.NativeErrorCode);
        Assert.False(feed.IsStarted);
        Assert.Equal(1, source.DisposeCount);
        using FileStream reopened = new(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => feed.ReadBatchAsync().AsTask());
        await feed.DisposeAsync();
        Assert.Equal(1, source.DisposeCount);
    }

    [Fact]
    public async Task MissingNativeWatchDirectoryFailsFeedStartup()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        Directory.Delete(_syncRootPath);
        WindowsLocalChangeSource source = new(_syncRootPath, 4096);
        await using CloudLocalChangeFeed feed = CloudLocalChangeFeed.CreateForTesting(_syncRootPath, store, new(), source);

        await Assert.ThrowsAsync<ArgumentException>(() => feed.StartAsync().AsTask());
        Assert.False(feed.IsStarted);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.StartAsync(_ => ValueTask.CompletedTask, default));
        await feed.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupErrorSurvivesCleanupAndDiagnosticFailures(bool throwingListener)
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        StartupCleanupStore failingStore = new(store);
        string leasePath = Path.Combine(_temporaryDirectory, "startup.lock");
        StartupSource source = new(leasePath);
        Win32Exception original = new(5);
        Activity? reported = null;
        using ActivityListener listener = new()
        {
            ShouldListenTo = candidate => candidate.Name == CloudDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName != "cfsharp.local-change-feed.startup-cleanup") { return; }
                reported = activity;
                if (throwingListener) { throw new IOException("injected diagnostic failure"); }
            },
        };
        ActivitySource.AddActivityListener(listener);
        await using CloudLocalChangeFeed feed = CloudLocalChangeFeed.CreateForTesting(_syncRootPath, failingStore, new(), source);
        Task starting = feed.StartAsync().AsTask();
        await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Exception? observed;
        try
        {
            failingStore.RejectTransactions = true;
            source.Ready.TrySetException(original);
            observed = await Record.ExceptionAsync(() => starting.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            failingStore.RejectTransactions = false;
            await feed.DisposeAsync();
        }

        Assert.Same(original, observed);
        Assert.Equal(5, original.NativeErrorCode);
        Exception cleanup = Assert.IsAssignableFrom<Exception>(original.Data["CfSharp.LocalChangeFeed.StartupCleanupFailure"]);
        Assert.Contains("injected cleanup marker failure", cleanup.ToString());
        Assert.NotNull(reported);
        Assert.Equal(cleanup.GetType().Name, reported.GetTagItem("cfsharp.error.type"));
        Assert.False(feed.IsStarted);
        Assert.Equal(1, source.DisposeCount);
        using FileStream reopened = new(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using ICloudStateTransaction verify = await store.BeginTransactionAsync();
        Assert.True(LocalChangeCheckpoint.Decode((await verify.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName))!.Value).RequiresFullRescan);
    }

    private sealed class StartupCleanupStore(ICloudStateStore inner) : ICloudStateStore
    {
        internal bool RejectTransactions { get; set; }
        public ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
            RejectTransactions ? throw new IOException("injected cleanup marker failure") : inner.BeginTransactionAsync(cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StartupSource(string leasePath) : ILocalChangeSource
    {
        private FileStream? _lease;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeCount { get; private set; }

        public async Task StartAsync(Func<LocalChangeSourceEvent, ValueTask> eventHandler, CancellationToken cancellationToken)
        {
            _lease = new(leasePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            Entered.TrySetResult();
            await Ready.Task.ConfigureAwait(false);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _lease?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
