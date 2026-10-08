using System.Reflection;

namespace CfSharp.Storage.Sqlite.Tests;

public sealed partial class CloudLocalChangeFeedTests
{
    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1031:Do not use blocking task operations in test method",
        Justification = "A dedicated background caller installs a paused context; the test awaits startup and rescues queued callbacks.")]
    public async Task SourceWorkerPersistsLossWithoutPumpingTheStartingContext()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        FakeSource source = new();
        await using CloudLocalChangeFeed feed = CreateFeed(store, source);
        PausedSourceContext context = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread caller = new(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                feed.StartAsync().AsTask().GetAwaiter().GetResult();
                started.TrySetResult();
            }
            catch (Exception exception) { started.TrySetException(exception); }
            finally { SynchronizationContext.SetSynchronizationContext(null); }
        })
        { IsBackground = true };
        caller.Start();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(caller.Join(TimeSpan.FromSeconds(5)));
            await source.EmitAsync(new(LocalChangeSourceAction.Overflow, string.Empty));
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            bool persistedWithoutPumping = true;
            try { await WaitForLossPersistenceAsync(feed, timeout.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { persistedWithoutPumping = false; }
            Assert.True(persistedWithoutPumping, $"Loss persistence required pumping the startup context; queued callbacks={context.Posts}.");
            Assert.Equal(0, context.Posts);
            await using ICloudStateTransaction verify = await store.BeginTransactionAsync(timeout.Token);
            Assert.True(LocalChangeCheckpoint.Decode((await verify.Checkpoints
                .GetAsync(CloudLocalChangeFeed.CheckpointName, timeout.Token))!.Value).RequiresFullRescan);
        }
        finally
        {
            // Rescue the baseline worker before disposal, rather than leak a blocked processor.
            context.Release();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LossAfterCaptureBlocksBacklogAndInvalidatesScanAfterAcknowledgement(bool watcherError)
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        CloudOperationJournalEntry[] rows = await SeedChangesAsync(store, 9);
        FakeSource source = new();
        await using CloudLocalChangeFeed feed = CreateFeed(store, source);
        await feed.StartAsync();
        CloudLocalChangeScan scan = await feed.BeginScanAsync();
        CloudLocalChangePage first = await feed.ReadPageAsync(scan, 0, 4);
        await source.EmitAsync(new(watcherError ? LocalChangeSourceAction.Error : LocalChangeSourceAction.Overflow,
            string.Empty, watcherError ? 5 : 0));
        CloudLocalChangePage blocked = await feed.ReadPageAsync(scan, first.LastScannedSequence, 4);
        Assert.True(blocked.RequiresFullRescan);
        Assert.Empty(blocked.Changes);
        Assert.True(blocked.HasMore);
        Assert.Equal(first.LastScannedSequence, blocked.LastScannedSequence);
        CloudLocalChangeBatch legacy = await feed.ReadBatchAsync();
        Assert.True(legacy.RequiresFullRescan);
        Assert.Empty(legacy.Changes);

        await AcknowledgePersistedRescanAsync(feed);
        Assert.True((await feed.ReadPageAsync(scan, first.LastScannedSequence, 4)).RequiresFullRescan);
        Assert.False((await feed.BeginScanAsync()).RequiresFullRescan);
        await using ICloudStateTransaction verify = await store.BeginTransactionAsync();
        Assert.Equal(rows.Select(row => row.OperationId),
            (await verify.Operations.ListAsync(20)).Select(row => row.OperationId));
        Assert.False(LocalChangeCheckpoint.Decode((await verify.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName))!.Value).RequiresFullRescan);
    }

    [Fact]
    public async Task LossAtPageTransactionReleaseWithholdsAlreadyReadChanges()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        await SeedChangesAsync(store, 2);
        RaceStore raceStore = new(store);
        FakeSource source = new();
        await using CloudLocalChangeFeed feed = CreateFeed(raceStore, source);
        await feed.StartAsync();
        CloudLocalChangeScan scan = await feed.BeginScanAsync();
        raceStore.AfterRollback = () => source.EmitAsync(new(LocalChangeSourceAction.Overflow, string.Empty));
        CloudLocalChangePage page = await feed.ReadPageAsync(scan, 0, 1);
        Assert.True(page.RequiresFullRescan);
        Assert.Empty(page.Changes);
        Assert.Equal(0, page.LastScannedSequence);
        Assert.True(page.HasMore);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1030:Do not use ConfigureAwait(false) in test method",
        Justification = "Only store callbacks detach from the deliberately paused context; test orchestration retains the xUnit context.")]
    public async Task PersistedLossAtBatchTransactionReleaseWithholdsBacklog(bool watcherError, int backlogSize)
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        CloudOperationJournalEntry[] rows = await SeedChangesAsync(store, backlogSize);
        RaceStore raceStore = new(store);
        FakeSource source = new();
        await using CloudLocalChangeFeed feed = CreateFeed(raceStore, source);
        await feed.StartAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        TaskCompletionSource releaseMarker = new(TaskCreationOptions.RunContinuationsAsynchronously);
        raceStore.AfterRollback = async () =>
        {
            raceStore.BeforeBegin = () => new ValueTask(releaseMarker.Task.WaitAsync(timeout.Token));
            await source.EmitAsync(new(watcherError ? LocalChangeSourceAction.Error : LocalChangeSourceAction.Overflow,
                string.Empty, watcherError ? 5 : 0)).ConfigureAwait(false);
            // Hold the older snapshot until the worker has completed the persistence handoff.
            // Availability is a consumable notification, not an exclusive completion barrier.
            Task persistence = WaitForLossPersistenceAsync(feed, timeout.Token);
            releaseMarker.TrySetResult();
            await persistence.ConfigureAwait(false);
            await using ICloudStateTransaction persisted = await store.BeginTransactionAsync(timeout.Token).ConfigureAwait(false);
            Assert.True(LocalChangeCheckpoint.Decode((await persisted.Checkpoints
                .GetAsync(CloudLocalChangeFeed.CheckpointName, timeout.Token).ConfigureAwait(false))!.Value).RequiresFullRescan);
            await persisted.RollbackAsync(timeout.Token).ConfigureAwait(false);
        };

        // Force the test barrier to begin waiting before the worker commits, under a context
        // that will never be pumped. Test-store wrappers must not capture the caller either.
        PausedSourceContext context = new();
        SynchronizationContext? previous = SynchronizationContext.Current;
        Task<CloudLocalChangeBatch> read;
        SynchronizationContext.SetSynchronizationContext(context);
        try { read = feed.ReadBatchAsync(timeout.Token).AsTask(); }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        CloudLocalChangeBatch batch;
        try
        {
            batch = await read.WaitAsync(timeout.Token);
            Assert.Equal(0, context.Posts);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            long loss = (long)typeof(CloudLocalChangeFeed).GetField("_lossGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(feed)!;
            long persisted = (long)typeof(CloudLocalChangeFeed).GetField("_persistedLossGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(feed)!;
            throw new TimeoutException($"Test persistence barrier did not complete; context posts={context.Posts}; loss={loss}; persisted={persisted}.");
        }
        finally
        {
            releaseMarker.TrySetResult();
            context.Release();
            try { await read; }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }

        Assert.True(batch.RequiresFullRescan);
        Assert.Empty(batch.Changes);
        // The read budget ends with its completed handoff. A queued test-runner continuation
        // may resume later; durable verification must not reuse that expired operation token.
        timeout.Cancel();
        using CancellationTokenSource verificationTimeout = new(TimeSpan.FromSeconds(10));
        CancellationToken verificationToken = verificationTimeout.Token;
        await using (ICloudStateTransaction verify = await store.BeginTransactionAsync(verificationToken))
        {
            IReadOnlyList<CloudOperationJournalEntry> retained = await verify.Operations.ListAsync(10, verificationToken);
            Assert.Equal(rows.Length, retained.Count);
            for (int index = 0; index < rows.Length; index++)
            {
                Assert.Equal(rows[index].OperationId, retained[index].OperationId);
                Assert.Equal(rows[index].Sequence, retained[index].Sequence);
                Assert.Equal(rows[index].Kind, retained[index].Kind);
                Assert.Equal(rows[index].ItemId, retained[index].ItemId);
                Assert.Equal(rows[index].Payload.ToArray(), retained[index].Payload.ToArray());
                Assert.Equal(rows[index].CreatedAt, retained[index].CreatedAt);
                Assert.Equal(rows[index].AttemptCount, retained[index].AttemptCount);
                Assert.Equal(rows[index].RetryAfter, retained[index].RetryAfter);
            }

            await verify.RollbackAsync(verificationToken);
        }

        await feed.AcknowledgeFullRescanAsync(verificationToken);
        Assert.False((await feed.BeginScanAsync(verificationToken)).RequiresFullRescan);
        if (backlogSize != 0)
        {
            CloudLocalChangeBatch recovered = await feed.ReadBatchAsync(verificationToken);
            Assert.False(recovered.RequiresFullRescan);
            Assert.Equal(rows.Select(row => row.OperationId), recovered.Changes.Select(change => change.OperationId));
            Assert.Equal(rows.Select(row => row.Sequence), recovered.Changes.Select(change => change.Sequence));
            Assert.Equal(rows.Select(row => LocalChangePayload.Decode(row.Payload).RelativePath),
                recovered.Changes.Select(change => change.RelativePath));
            Assert.Equal(rows.Select(row => LocalChangePayload.Decode(row.Payload).ObservedAt),
                recovered.Changes.Select(change => change.ObservedAt));
        }
    }

    [Fact]
    public async Task LossDuringRescanCommitCannotBeAcknowledgedByThatCommit()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        await SeedChangesAsync(store, 2);
        RaceStore raceStore = new(store);
        FakeSource source = new();
        await using CloudLocalChangeFeed feed = CreateFeed(raceStore, source);
        await feed.StartAsync();
        CloudLocalChangeScan original = await feed.BeginScanAsync();
        raceStore.BeforeCommit = () => source.EmitAsync(new(LocalChangeSourceAction.Error, string.Empty, 5));
        await feed.AcknowledgeFullRescanAsync();
        Assert.True((await feed.ReadPageAsync(original, 0, 1)).RequiresFullRescan);
        Assert.True((await feed.BeginScanAsync()).RequiresFullRescan);
        Assert.True((await feed.ReadBatchAsync()).RequiresFullRescan);
        await AcknowledgePersistedRescanAsync(feed);
        Assert.False((await feed.BeginScanAsync()).RequiresFullRescan);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "xUnit1030:Do not use ConfigureAwait(false) in test method",
        Justification = "Only worker/store barrier callbacks detach, matching the real asynchronous store; test orchestration retains the xUnit context.")]
    public async Task PendingLossBeforeReadCannotDisappearDuringSnapshotRelease(bool watcherError, bool scanRead)
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        CloudOperationJournalEntry[] rows = await SeedChangesAsync(store, 2);
        RaceStore raceStore = new(store);
        FakeSource source = new();
        await using CloudLocalChangeFeed feed = CreateFeed(raceStore, source);
        await feed.StartAsync();
        TaskCompletionSource lossPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releasePersistence = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        raceStore.BeforeBegin = async () =>
        {
            // MarkRescanRequired has recorded this loss generation, but cannot yet open its
            // transaction. Let the reader acquire an older checkpoint before persistence.
            lossPending.TrySetResult();
            await releasePersistence.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        };
        await source.EmitAsync(new(watcherError ? LocalChangeSourceAction.Error : LocalChangeSourceAction.Overflow,
            string.Empty, watcherError ? 5 : 0));
        try
        {
            await lossPending.Task.WaitAsync(timeout.Token);
            raceStore.AfterRollback = async () =>
            {
                releasePersistence.TrySetResult();
                await WaitForLossPersistenceAsync(feed, timeout.Token).ConfigureAwait(false);
            };
            if (scanRead)
            {
                CloudLocalChangeScan scan = await feed.BeginScanAsync(timeout.Token);
                Assert.True(scan.RequiresFullRescan);
                Assert.Empty((await feed.ReadPageAsync(scan, 0, 2, timeout.Token)).Changes);
            }
            else
            {
                CloudLocalChangeBatch batch = await feed.ReadBatchAsync(timeout.Token);
                Assert.True(batch.RequiresFullRescan);
                Assert.Empty(batch.Changes);
            }

            await feed.AcknowledgeFullRescanAsync(timeout.Token);
            CloudLocalChangePage recovered = await feed.ReadPageAsync(await feed.BeginScanAsync(timeout.Token), 0, 2, timeout.Token);
            Assert.False(recovered.RequiresFullRescan);
            Assert.Equal(rows.Select(row => row.OperationId), recovered.Changes.Select(change => change.OperationId));
            Assert.Equal(rows.Select(row => row.Sequence), recovered.Changes.Select(change => change.Sequence));
        }
        finally
        {
            releasePersistence.TrySetResult();
        }
    }

    [Fact]
    public async Task CreationIntentAndRetainedObservationFenceScansUntilExplicitRecovery()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        await SeedChangesAsync(store, 2);
        await using CloudLocalChangeFeed feed = CreateFeed(store, new FakeSource());
        await feed.StartAsync();
        CloudLocalChangeScan original = await feed.BeginScanAsync();
        string intentName = RemoteCreationIntent.Prefix + "/test";
        string observationName = RemoteCreationIntent.ObservationsPrefix + "/test";
        await using (ICloudStateTransaction write = await store.BeginTransactionAsync())
        {
            RemoteCreationIntent intent = new("pending", CloudItemKind.Directory,
                new CloudPlaceholderIdentity(Guid.NewGuid(), "pending-remote"), new byte[32], false);
            await write.Checkpoints.UpsertAsync(new CloudStateCheckpoint(intentName, intent.Encode(), DateTimeOffset.UtcNow));
            await write.Checkpoints.UpsertAsync(new CloudStateCheckpoint(observationName, [1], DateTimeOffset.UtcNow));
            await write.CommitAsync();
        }

        Assert.True((await feed.BeginScanAsync()).RequiresFullRescan);
        Assert.True((await feed.ReadPageAsync(original, 0, 1)).RequiresFullRescan);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await feed.AcknowledgeFullRescanAsync());
        await using (ICloudStateTransaction recover = await store.BeginTransactionAsync())
        {
            await recover.Checkpoints.RemoveAsync(intentName);
            await recover.CommitAsync();
        }

        // Removing a completed creation intent does not implicitly accept its uncertain observations.
        Assert.True((await feed.ReadPageAsync(original, 0, 1)).RequiresFullRescan);
        await feed.AcknowledgeFullRescanAsync();
        Assert.True((await feed.ReadPageAsync(original, 0, 1)).RequiresFullRescan);
        Assert.False((await feed.BeginScanAsync()).RequiresFullRescan);
        await using ICloudStateTransaction verify = await store.BeginTransactionAsync();
        Assert.Null(await verify.Checkpoints.GetAsync(observationName));
        Assert.Equal(2, (await verify.Operations.ListAsync(10)).Count);
    }

    [Fact]
    public async Task DurableRescanFenceSurvivesFullStoreAndFeedRestart()
    {
        await using (ICloudStateStore store = await OpenStoreAsync())
        await using (CloudLocalChangeFeed feed = CreateFeed(store, new FakeSource()))
        {
            await SeedChangesAsync(store, 2);
            await feed.StartAsync();
            await using ICloudStateTransaction write = await store.BeginTransactionAsync();
            await write.Checkpoints.UpsertAsync(new CloudStateCheckpoint(CloudLocalChangeFeed.CheckpointName,
                new LocalChangeCheckpoint(42, true).Encode(), DateTimeOffset.UtcNow));
            await write.CommitAsync();
        }

        await using ICloudStateStore reopened = await OpenStoreAsync();
        await using CloudLocalChangeFeed restarted = CreateFeed(reopened, new FakeSource());
        await restarted.StartAsync();
        CloudLocalChangeScan scan = await restarted.BeginScanAsync();
        Assert.True(scan.RequiresFullRescan);
        CloudLocalChangePage page = await restarted.ReadPageAsync(scan, 0, 1);
        Assert.Empty(page.Changes);
        Assert.True(page.HasMore);
        Assert.True(page.RequiresFullRescan);
    }

    private static async Task WaitForLossPersistenceAsync(CloudLocalChangeFeed feed, CancellationToken cancellationToken)
    {
        // These private counters select the race boundary only; assertions exercise the public
        // feed and real durable journal. No timing delay substitutes for the worker's commit.
        FieldInfo loss = typeof(CloudLocalChangeFeed).GetField("_lossGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!;
        FieldInfo persisted = typeof(CloudLocalChangeFeed).GetField("_persistedLossGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long generation = (long)loss.GetValue(feed)!;
            if (generation != 0 && generation == (long)persisted.GetValue(feed)!)
            {
                return;
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task AcknowledgePersistedRescanAsync(CloudLocalChangeFeed feed)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (true)
        {
            try
            {
                await feed.AcknowledgeFullRescanAsync(timeout.Token);
                return;
            }
            catch (InvalidOperationException exception) when (exception.Message.StartsWith("A new loss signal", StringComparison.Ordinal))
            {
                await Task.Delay(10, timeout.Token);
            }
        }
    }

    private sealed class PausedSourceContext : SynchronizationContext
    {
        private readonly object _gate = new();
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _pending = new();
        private bool _released;
        private int _posts;
        internal int Posts => Volatile.Read(ref _posts);
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _posts);
            lock (_gate)
            {
                if (!_released)
                {
                    _pending.Enqueue((callback, state));
                    return;
                }
            }

            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        internal void Release()
        {
            lock (_gate)
            {
                _released = true;
                while (_pending.TryDequeue(out var continuation))
                {
                    ThreadPool.QueueUserWorkItem(_ => continuation.Callback(continuation.State));
                }
            }
        }
    }

    private sealed class RaceStore(ICloudStateStore inner) : ICloudStateStore
    {
        internal Func<ValueTask>? BeforeBegin { get; set; }
        internal Func<ValueTask>? AfterRollback { get; set; }
        internal Func<ValueTask>? BeforeCommit { get; set; }
        public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        {
            Func<ValueTask>? hook = BeforeBegin;
            BeforeBegin = null;
            if (hook is not null)
            {
                await hook().ConfigureAwait(false);
            }

            return new RaceTransaction(await inner.BeginTransactionAsync(cancellationToken).ConfigureAwait(false), this);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RaceTransaction(ICloudStateTransaction inner, RaceStore store) : ICloudStateTransaction
    {
        public ICloudItemStateRepository Items => inner.Items;
        public ICloudCheckpointRepository Checkpoints => inner.Checkpoints;
        public ICloudOperationJournal Operations => inner.Operations;
        public ICloudConflictRepository Conflicts => inner.Conflicts;
        public ICloudRemoteBatchRepository RemoteBatches => inner.RemoteBatches;
        public ICloudEchoSuppressionRepository EchoSuppressions => inner.EchoSuppressions;
        public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
        {
            Func<ValueTask>? hook = store.BeforeCommit;
            store.BeforeCommit = null;
            if (hook is not null)
            {
                await hook().ConfigureAwait(false);
            }

            await inner.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask RollbackAsync(CancellationToken cancellationToken = default)
        {
            await inner.RollbackAsync(cancellationToken).ConfigureAwait(false);
            Func<ValueTask>? hook = store.AfterRollback;
            store.AfterRollback = null;
            if (hook is not null)
            {
                await hook().ConfigureAwait(false);
            }
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
