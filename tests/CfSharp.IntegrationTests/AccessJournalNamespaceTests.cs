using System.Collections.Concurrent;
using System.Security.AccessControl;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    public async Task DelayedNativeSecurityAfterRenameRetainsUnboundObservation(bool directory, int replacement)
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        string before = Path.Combine(fixture.Root, "before");
        string after = Path.Combine(fixture.Root, "after");
        if (directory) { Directory.CreateDirectory(before); }
        else { await File.WriteAllTextAsync(before, "original-bytes"); }
        CloudItem original = directory ? fixture.System.GetDirectory("before") : fixture.System.GetFile("before");
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "security-original", "v1");
        await original.ConvertToPlaceholderAsync(identity);
        CloudItemSnapshot snapshot = await original.InspectAsync();
        Guid retainedId = Guid.NewGuid();
        await using (ICloudStateTransaction seed = await factory!.Store!.BeginTransactionAsync())
        {
            await seed.Operations.EnqueueAsync(new(retainedId, CloudStateOperationKind.MetadataUpdate, identity.ItemId,
                new LocalChangePayload("before", null, directory, DateTimeOffset.UtcNow).Encode(), DateTimeOffset.UtcNow));
            await seed.CommitAsync();
        }
        IReadOnlyList<CloudOperationJournalEntry> originals = await AccessRowsAsync(factory!);
        DelayedSecuritySource source = new(fixture.Root, line => output.WriteLine(line));
        Guid delayedId;
        Guid? replacementId = null;
        await using (CloudLocalChangeFeed feed = CloudLocalChangeFeed.CreateForTesting(fixture.Root, factory!.Store!,
            new CloudLocalChangeFeedOptions { BufferCapacity = 64 }, source))
        {
            await feed.StartAsync();
            CloudProtectedLocalOperationResult applied = await original.RunProtectedLocalOperationAsync(new(snapshot.LocalBinding!,
                directory ? CloudProtectedLocalOperationMode.DirectoryMetadata : CloudProtectedLocalOperationMode.ExclusiveFile), async (scope, stop) =>
            {
                FileSystemSecurity access = await scope.ReadAccessDescriptorAsync(stop);
                access.AddAccessRule(AccessTestRule());
                await scope.ApplyAccessDescriptorAsync(access, stop);
            });
            Assert.True(applied.AccessDescriptorApplied && applied.AccessDescriptorReadBack);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
            LocalChangeSourceEvent security = await source.Security.Task.WaitAsync(deadline.Token);
            if (directory) { Directory.Move(before, after); }
            else { File.Move(before, after); }
            await source.Rename.Task.WaitAsync(deadline.Token);
            await source.DeliverAsync(source.Events.Single(change => change.Action == LocalChangeSourceAction.RenamedOldName && change.RelativePath == "before"));
            await source.DeliverAsync(source.Events.Single(change => change.Action == LocalChangeSourceAction.RenamedNewName && change.RelativePath == "after"));
            while (true)
            {
                await using ICloudStateTransaction read = await factory.Store!.BeginTransactionAsync(deadline.Token);
                if ((await read.Items.GetByItemIdAsync(identity.ItemId, deadline.Token))?.RelativePath == "after") { break; }
                await Task.Delay(20, deadline.Token);
            }
            if (replacement != 0)
            {
                if (directory) { Directory.CreateDirectory(before); }
                else { await File.WriteAllTextAsync(before, "replacement-bytes"); }
                if (replacement == 3)
                {
                    CloudPlaceholderIdentity successor = new(Guid.NewGuid(), "security-successor", "v2");
                    await original.ConvertToPlaceholderAsync(successor);
                    replacementId = successor.ItemId;
                }
                else if (replacement == 2)
                {
                    replacementId = Guid.NewGuid();
                    await using ICloudStateTransaction track = await factory.Store!.BeginTransactionAsync();
                    await track.Items.UpsertAsync(new(replacementId.Value, "local:successor", "before", original.Kind,
                        null, null, false, DateTimeOffset.UtcNow));
                    await track.CommitAsync();
                }
            }
            // Only delivery is controlled. DACL, rename, kernel notification and (when
            // requested) successor conversion are real native operations on this root.
            await source.DeliverAsync(security);
            await feed.DisposeAsync();
            await using ICloudStateTransaction verify = await factory.Store!.BeginTransactionAsync();
            Assert.Equal("after", (await verify.Items.GetByItemIdAsync(identity.ItemId))!.RelativePath);
            Assert.Equal(replacementId, (await verify.Items.GetByRelativePathAsync("before"))?.ItemId);
            IReadOnlyList<CloudOperationJournalEntry> rows = await verify.Operations.ListAsync(100);
            AssertOriginalAccessRows(originals, rows);
            CloudOperationJournalEntry delayed = Assert.Single(rows.Where(row => row.Kind == CloudStateOperationKind.MetadataUpdate && row.OperationId != retainedId));
            Assert.Null(delayed.ItemId);
            delayedId = delayed.OperationId;
            Assert.Equal(directory, LocalChangePayload.Decode(delayed.Payload).IsDirectory);
            Assert.Single(await verify.Checkpoints.ListAsync(CloudLocalChangeFeed.NamespaceObservationsPrefix));
            Assert.True(LocalChangeCheckpoint.Decode((await verify.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName))!.Value).RequiresFullRescan);
            output.WriteLine($"Delayed native security: directory={directory}; replacement={replacement}; original={identity.ItemId}; successor={replacementId}; unbound={delayedId}; nativeFile={snapshot.LocalFileId}.");
        }
        await fixture.RestartAsync();
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed();
        await reopened.StartAsync();
        CloudLocalChangeScan scan = await reopened.BeginScanAsync();
        Assert.True(scan.RequiresFullRescan);
        Assert.Empty((await reopened.ReadPageAsync(scan, 0, 100)).Changes);
        await reopened.AcknowledgeFullRescanAsync();
        CloudLocalChangePage page = await reopened.ReadPageAsync(await reopened.BeginScanAsync(), 0, 100);
        CloudLocalChange observation = Assert.Single(page.Changes.Where(row => row.OperationId == delayedId));
        Assert.Null(observation.ItemId);
        Assert.Equal(CloudLocalChangeKind.MetadataUpdate, observation.Kind);
        await reopened.AcknowledgeAsync([delayedId]);
        AssertOriginalAccessRows(originals, await AccessRowsAsync(factory!));
        Assert.Equal(0, fixture.Provider.Fetches);
    }

    private sealed class DelayedSecuritySource(string root, Action<string> trace, string securityPath = "before") : ILocalChangeSource
    {
        private readonly WindowsLocalChangeSource _native = new(root, 64 * 1024);
        private Func<LocalChangeSourceEvent, ValueTask>? _handler;
        internal ConcurrentQueue<LocalChangeSourceEvent> Events { get; } = new();
        internal TaskCompletionSource<LocalChangeSourceEvent> Security { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Rename { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task StartAsync(Func<LocalChangeSourceEvent, ValueTask> handler, CancellationToken stop)
        {
            _handler = handler;
            return _native.StartAsync(change =>
            {
                Events.Enqueue(change);
                trace($"Captured native event: {change.Action}; path={change.RelativePath}; error={change.ErrorCode}; identity={change.SecurityIdentity}.");
                if (change.Action is LocalChangeSourceAction.Error or LocalChangeSourceAction.Overflow)
                {
                    Security.TrySetException(new IOException($"Native security source error: {change.ErrorCode}."));
                }
                if (change.Action == LocalChangeSourceAction.SecurityModified && change.RelativePath == securityPath) { Security.TrySetResult(change); }
                if (change.Action == LocalChangeSourceAction.RenamedNewName && change.RelativePath == "after") { Rename.TrySetResult(); }
                return ValueTask.CompletedTask;
            }, stop);
        }
        internal ValueTask DeliverAsync(LocalChangeSourceEvent change) => _handler!(change);
        public ValueTask DisposeAsync() => _native.DisposeAsync();
    }
}
