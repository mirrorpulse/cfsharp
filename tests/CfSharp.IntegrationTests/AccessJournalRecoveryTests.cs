using System.Collections.Concurrent;
using System.ComponentModel;
using System.Security.AccessControl;

using CfSharp.Storage.Sqlite;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(false)]
    public async Task NativeSecurityRearmFailurePreservesCompletedWorkAndShutdownLoss(bool disposeBeforeTerminalCallback)
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        // Keep the watched directory outside the connected root and SQLite store. The test
        // removes it between decode and the next real ReadDirectoryChangesExW call.
        string watched = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "security-watch");
        Directory.CreateDirectory(watched);
        string path = Path.Combine(watched, "before.txt");
        await File.WriteAllTextAsync(path, "unchanged");
        TaskCompletionSource terminalDecided = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim resumeDelivery = new(false);
        WindowsLocalChangeSource native = new(watched, 64 * 1024, beforeSecurityRearm: () =>
        {
            File.Delete(path);
            Directory.Delete(watched);
        }, beforeTerminalSecurityDelivery: () =>
        {
            terminalDecided.TrySetResult();
            resumeDelivery.Wait(TimeSpan.FromSeconds(25));
        });
        ObservedSecuritySource source = new(native, forwardChanges: !disposeBeforeTerminalCallback);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        await using (CloudLocalChangeFeed feed = CloudLocalChangeFeed.CreateForTesting(watched, factory!.Store!, new(), source))
        {
            await feed.StartAsync(deadline.Token);
            Assert.False((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
            Task? disposing = null;
            try
            {
                FileInfo file = new(path);
                FileSecurity access = file.GetAccessControl();
                access.AddAccessRule(AccessTestRule());
                file.SetAccessControl(access);
                await terminalDecided.Task.WaitAsync(deadline.Token);
                if (disposeBeforeTerminalCallback)
                {
                    // DisposeAsync runs synchronously through source teardown until it awaits
                    // the paused native pump. Resume only after it reaches that boundary.
                    disposing = feed.DisposeAsync().AsTask();
                    Assert.False(disposing.IsCompleted);
                }
            }
            finally { resumeDelivery.Set(); }

            if (disposing is not null)
            {
                await disposing.WaitAsync(deadline.Token);
            }
            else
            {
                await source.TerminalDelivered.Task.WaitAsync(deadline.Token);
                await feed.DisposeAsync();
            }
        }

        Assert.False(Directory.Exists(watched));
        if (disposeBeforeTerminalCallback)
        {
            // The observer forwarded only terminal loss in this case. A journaled or
            // ambiguous ordinary notification cannot independently make the test pass.
            Assert.Empty(await AccessRowsAsync(factory!));
        }

        await fixture.RestartAsync();
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed();
        await reopened.StartAsync(deadline.Token);
        Assert.True((await reopened.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        await reopened.AcknowledgeFullRescanAsync(deadline.Token);
        CloudLocalChangePage page = await reopened.ReadPageAsync(await reopened.BeginScanAsync(deadline.Token), 0, 10, deadline.Token);
        LocalChangeSourceEvent[] events = source.Events.ToArray();
        if (disposeBeforeTerminalCallback)
        {
            Assert.Empty(page.Changes);
        }
        else
        {
            Assert.NotEmpty(page.Changes);
            Assert.Equal(events.Length - 1, page.Changes.Count);
            Assert.All(page.Changes, historical =>
            {
                Assert.Equal(CloudLocalChangeKind.MetadataUpdate, historical.Kind);
                Assert.Equal("before.txt", historical.RelativePath);
                Assert.Null(historical.ItemId);
            });
        }

        Assert.True(events.Length >= 2);
        Assert.All(events[..^1], security =>
        {
            Assert.Equal(LocalChangeSourceAction.SecurityModified, security.Action);
            Assert.Equal("before.txt", security.RelativePath);
            Assert.NotEqual(0ul, security.SecurityIdentity!.Value.FileId);
        });
        Assert.Equal(LocalChangeSourceAction.SecuritySubscriptionFailed, events[^1].Action);
        Assert.NotEqual(0, events[^1].ErrorCode);
        output.WriteLine($"Native security rearm failed with Win32 {events[^1].ErrorCode} after delivering the decoded batch. Disposal raced terminal delivery: {disposeBeforeTerminalCallback}. The restarted feed required reconciliation.");
    }

    private sealed class ObservedSecuritySource(WindowsLocalChangeSource native, bool forwardChanges) : ILocalChangeSource
    {
        internal ConcurrentQueue<LocalChangeSourceEvent> Events { get; } = new();
        internal TaskCompletionSource TerminalDelivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task StartAsync(Func<LocalChangeSourceEvent, ValueTask> handler, CancellationToken cancellationToken) =>
            native.StartAsync(async change =>
            {
                if (change.Action is LocalChangeSourceAction.SecurityModified or LocalChangeSourceAction.SecuritySubscriptionFailed)
                {
                    Events.Enqueue(change);
                    if (change.Action == LocalChangeSourceAction.SecuritySubscriptionFailed)
                    {
                        await handler(change);
                        TerminalDelivered.TrySetResult();
                    }
                    else if (forwardChanges)
                    {
                        await handler(change);
                    }
                }
            }, cancellationToken);

        public ValueTask DisposeAsync() => native.DisposeAsync();
    }

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecurityObservationSurvivesRemoteCreationReplay(bool directory)
    {
        RemoteCreationRecoveryTests.FailingFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(new SqliteCloudStateStoreFactory(path)));
        CloudItemKind kind = directory ? CloudItemKind.Directory : CloudItemKind.File;
        CloudPlaceholderMetadata metadata = directory ? CloudPlaceholderMetadata.CreateDirectoryBuilder().Build()
            : CloudPlaceholderMetadata.CreateFileBuilder().Build();
        CloudRemoteChange change = new("before", directory ? CloudRemoteChangeKind.DirectoryUpsert : CloudRemoteChangeKind.FileUpsert,
            "remote-before", "r1", kind, "before", length: directory ? null : 0, metadata: metadata);
        CloudRemoteChangeBatch batch = new("pending-create", Array.Empty<byte>(), [change], new byte[] { 1 });
        factory!.FailCreationCommit = true;
        CloudRemoteApplyResult first = await fixture.System.ApplyRemoteChangesAsync(batch);
        Assert.True(first.RequiresRetry);
        Assert.Equal(CloudRemoteApplyEntryStatus.Failed, Assert.Single(first.Entries).Status);
        CloudItem item = directory ? fixture.System.GetDirectory("before") : fixture.System.GetFile("before");
        Assert.NotEmpty((await item.InspectAsync()).PlaceholderIdentity.ToArray());
        DelayedSecuritySource source = new(fixture.Root, line => output.WriteLine(line));
        Guid operationId;
        long sequence;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(25));
        await using (CloudLocalChangeFeed feed = new(fixture.Root, factory.Store!, new(), source,
            acquireOperation: async (scopes, stop) => await fixture.System.AcquireOperationAsync(scopes, stop)))
        {
            await feed.StartAsync(deadline.Token);
            if (directory)
            {
                DirectoryInfo info = new(item.FullPath);
                DirectorySecurity access = info.GetAccessControl();
                access.AddAccessRule(AccessTestRule());
                info.SetAccessControl(access);
            }
            else
            {
                FileInfo info = new(item.FullPath);
                FileSecurity access = info.GetAccessControl();
                access.AddAccessRule(AccessTestRule());
                info.SetAccessControl(access);
            }

            await source.DeliverAsync(await source.Security.Task.WaitAsync(deadline.Token));
            await feed.DisposeAsync();
            await using ICloudStateTransaction read = await factory.Store!.BeginTransactionAsync(deadline.Token);
            Assert.Single(await read.Checkpoints.ListAsync(RemoteCreationIntent.Prefix, deadline.Token));
            Assert.Single(await read.Checkpoints.ListAsync(RemoteCreationIntent.ObservationsPrefix, deadline.Token));
            CloudOperationJournalEntry pending = Assert.Single(await read.Operations.ListAsync(10, deadline.Token));
            Assert.Equal(CloudStateOperationKind.MetadataUpdate, pending.Kind);
            Assert.Null(pending.ItemId);
            operationId = pending.OperationId;
            sequence = pending.Sequence;
        }

        await fixture.RestartAsync();
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed();
        await reopened.StartAsync(deadline.Token);
        Assert.Empty((await reopened.ReadPageAsync(await reopened.BeginScanAsync(deadline.Token), 0, 10, deadline.Token)).Changes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.AcknowledgeFullRescanAsync(deadline.Token).AsTask());
        CloudRemoteApplyResult replay = await fixture.System.ApplyRemoteChangesAsync(batch,
            new CloudRemoteApplyOptions { SuppressLocalEcho = true }, deadline.Token);
        Assert.False(replay.RequiresRetry);
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single(replay.Entries).Status);
        await reopened.AcknowledgeFullRescanAsync(deadline.Token);
        CloudLocalChangePage page = await reopened.ReadPageAsync(await reopened.BeginScanAsync(deadline.Token), 0, 10, deadline.Token);
        CloudLocalChange security = Assert.Single(page.Changes.Where(entry => entry.OperationId == operationId));
        Assert.Equal(sequence, security.Sequence);
        Assert.Equal(CloudLocalChangeKind.MetadataUpdate, security.Kind);
        Assert.Equal("before", security.RelativePath);
        Assert.Equal(directory, security.IsDirectory);
        Assert.Null(security.ItemId);
        await reopened.AcknowledgeAsync([operationId], deadline.Token);
        await using ICloudStateTransaction verify = await factory.Store!.BeginTransactionAsync(deadline.Token);
        Assert.Null(await verify.Operations.GetAsync(operationId, deadline.Token));
    }

    [Trait("Category", "AccessJournal")]
    [Fact]
    public async Task RestartRecoversStagedSecurityObservationWithoutBinding()
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        Guid operationId = Guid.NewGuid();
        DateTimeOffset time = DateTimeOffset.UtcNow;
        LocalChangePayload payload = new("historical.bin", null, false, time);
        await using (ICloudStateTransaction stage = await factory!.Store!.BeginTransactionAsync())
        {
            await stage.Checkpoints.UpsertAsync(new(CloudLocalChangeFeed.SecurityValidationPrefix + operationId.ToString("N"), payload.Encode(), time));
            await stage.CommitAsync();
        }
        await fixture.RestartAsync();
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed();
        await feed.StartAsync();
        CloudLocalChangeScan fenced = await feed.BeginScanAsync();
        Assert.True(fenced.RequiresFullRescan);
        Assert.Empty((await feed.ReadPageAsync(fenced, 0, 10)).Changes);
        await feed.AcknowledgeFullRescanAsync();
        CloudLocalChange entry = Assert.Single((await feed.ReadPageAsync(await feed.BeginScanAsync(), 0, 10)).Changes);
        Assert.Equal(operationId, entry.OperationId);
        Assert.Equal(CloudLocalChangeKind.MetadataUpdate, entry.Kind);
        Assert.Equal(payload.RelativePath, entry.RelativePath);
        Assert.Equal(time, entry.ObservedAt);
        Assert.Null(entry.ItemId);
        await feed.AcknowledgeAsync([operationId]);
        await using ICloudStateTransaction verify = await factory.Store!.BeginTransactionAsync();
        Assert.Empty(await verify.Checkpoints.ListAsync(CloudLocalChangeFeed.SecurityValidationPrefix));
        Assert.Null(await verify.Operations.GetAsync(operationId));
    }

    [Trait("Category", "AccessJournal")]
    [Fact]
    public async Task TerminalSecuritySubscriptionFailureRequiresNewFeed()
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        WindowsLocalChangeSource source = new(fixture.Root, 64 * 1024);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(25));
        await using (CloudLocalChangeFeed feed = CloudLocalChangeFeed.CreateForTesting(fixture.Root, factory!.Store!, new(), source))
        {
            await feed.StartAsync(deadline.Token);
            source.OnSecurityError(1022, terminal: false);
            Assert.True((await feed.ReadBatchAsync(deadline.Token)).RequiresFullRescan);
            while (true)
            {
                try { await feed.AcknowledgeFullRescanAsync(deadline.Token); break; }
                catch (InvalidOperationException) { await Task.Delay(10, deadline.Token); }
            }
            Assert.False((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
            Task<CloudLocalChangeBatch>[] waiting = Enumerable.Range(0, 3).Select(_ => feed.ReadBatchAsync(deadline.Token).AsTask()).ToArray();
            // Use the same error code with a failed rearm. The native pump's terminal
            // status, rather than the numeric overflow code, controls the feed lifetime.
            source.OnSecurityError(1022, terminal: true);
            foreach (Task<CloudLocalChangeBatch> reader in waiting)
            {
                InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(async () => { await reader; });
                Assert.Equal(1022, Assert.IsType<Win32Exception>(failure.InnerException).NativeErrorCode);
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => feed.AcknowledgeFullRescanAsync(deadline.Token).AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => feed.BeginScanAsync(deadline.Token).AsTask());
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, "later.bin"), "after failure", deadline.Token);
        }

        Assert.Empty(await AccessRowsAsync(factory!));
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed();
        await reopened.StartAsync(deadline.Token);
        Assert.True((await reopened.BeginScanAsync(deadline.Token)).RequiresFullRescan);
        await reopened.AcknowledgeFullRescanAsync(deadline.Token);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "reopened.bin"), "after restart", deadline.Token);
        CloudLocalChangeBatch resumed = await reopened.ReadBatchAsync(deadline.Token);
        Assert.Contains(resumed.Changes, entry => entry.Kind == CloudLocalChangeKind.Create && entry.RelativePath == "reopened.bin");
    }
}
