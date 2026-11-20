using System.Buffers.Binary;

using CfSharp.Storage.Sqlite;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Theory]
    [InlineData(false, false, "leaf", false)]
    [InlineData(false, true, "leaf", false)]
    [InlineData(true, false, "leaf", false)]
    [InlineData(true, true, "leaf", false)]
    [InlineData(false, true, "parent", false)]
    [InlineData(true, false, "parent", false)]
    [InlineData(false, true, "replace", false)]
    [InlineData(true, true, "replace", false)]
    [InlineData(false, true, "case", false)]
    [InlineData(false, true, "leaf", true)]
    [InlineData(true, false, "leaf", true)]
    public async Task AttributeObservationAllowsRenameWhilePersistenceIsPaused(bool directory, bool placeholder,
        string move, bool pausePublication)
    {
        AttributeCommitFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path, pausePublication));
        string parent = Path.Combine(fixture.Root, "parent");
        Directory.CreateDirectory(parent);
        string relative = "parent\\original";
        string path = Path.Combine(fixture.Root, relative);
        byte[] bytes = "original resident bytes"u8.ToArray();
        if (directory) { Directory.CreateDirectory(path); }
        else { await File.WriteAllBytesAsync(path, bytes); }
        CloudItem item = directory ? fixture.System.GetDirectory(relative) : fixture.System.GetFile(relative);
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "concurrent-attribute", "accepted");
        if (placeholder) { await item.ConvertToPlaceholderAsync(identity); }
        CloudItemSnapshot before = await item.InspectAsync();
        if (!placeholder)
        {
            byte[] nativeId = before.LocalBinding!.LocalFileId.ToByteArray();
            await using ICloudStateTransaction seed = await factory!.Inner!.BeginTransactionAsync();
            await seed.Items.UpsertAsync(new(identity.ItemId, identity.RemoteId, relative,
                directory ? CloudItemKind.Directory : CloudItemKind.File, identity.RemoteRevision,
                BinaryPrimitives.ReadInt64LittleEndian(nativeId), false, DateTimeOffset.UtcNow));
            await seed.CommitAsync();
        }
        DateTime lastWrite = File.GetLastWriteTimeUtc(path);
        if (move == "parent")
        {
            string probe = Path.Combine(fixture.Root, "parent-probe");
            Directory.Move(parent, probe);
            Directory.Move(probe, parent);
        }
        factory!.RelativePath = relative;
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed();
        await feed.StartAsync();
        Guid observationId;
        string destination = move == "parent" ? Path.Combine(fixture.Root, "moved-parent", "original")
            : Path.Combine(parent, move == "case" ? "ORIGINAL" : "moved");
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            observationId = await factory.Paused.Task.WaitAsync(deadline.Token);
            // No retry: an advisory observation must not make an ordinary user rename fail,
            // even while the exact transaction/publication boundary is deliberately stalled.
            if (move == "parent") { Directory.Move(parent, Path.GetDirectoryName(destination)!); }
            else if (directory) { Directory.Move(path, destination); }
            else { File.Move(path, destination); }
            if (move == "replace")
            {
                if (directory) { Directory.CreateDirectory(path); }
                else { await File.WriteAllTextAsync(path, "replacement generation", deadline.Token); }
            }
        }
        finally { factory.Resume.TrySetResult(); }

        IReadOnlyList<CloudOperationJournalEntry> rows;
        do
        {
            await using ICloudStateTransaction read = await factory.Inner!.BeginTransactionAsync(deadline.Token);
            rows = await read.Operations.ListAsync(1000, deadline.Token);
            if (rows.Any(row => row.OperationId == observationId) && rows.Any(row => row.Kind == CloudStateOperationKind.Move)) { break; }
            await Task.Delay(20, deadline.Token);
        } while (true);
        await feed.DisposeAsync();
        CloudOperationJournalEntry observation = Assert.Single(rows.Where(row => row.OperationId == observationId));
        Assert.Equal(CloudStateOperationKind.MetadataUpdate, observation.Kind);
        Assert.Equal(relative, LocalChangePayload.Decode(observation.Payload).RelativePath);
        if (move == "case") { Assert.Equal(identity.ItemId, observation.ItemId); }
        else { Assert.Null(observation.ItemId); }
        Assert.DoesNotContain(rows, row => row.Kind == CloudStateOperationKind.ContentUpdate);
        CloudItem movedItem = directory
            ? fixture.System.GetDirectory(Path.GetRelativePath(fixture.Root, destination))
            : fixture.System.GetFile(Path.GetRelativePath(fixture.Root, destination));
        Assert.Equal(before.LocalBinding, (await movedItem.InspectAsync()).LocalBinding);
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(destination));
        if (!directory) { Assert.Equal(bytes, await File.ReadAllBytesAsync(destination, deadline.Token)); }

        await fixture.RestartAsync();
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed();
        await reopened.StartAsync();
        CloudLocalChangeScan scan = await reopened.BeginScanAsync();
        if (move != "case") { Assert.True(scan.RequiresFullRescan); }
        if (scan.RequiresFullRescan) { await reopened.AcknowledgeFullRescanAsync(); }
        CloudLocalChangePage page = await reopened.ReadPageAsync(await reopened.BeginScanAsync(), 0, 1000);
        CloudLocalChange retained = Assert.Single(page.Changes.Where(change => change.OperationId == observationId));
        Assert.Equal(observation.Sequence, retained.Sequence);
        Assert.Equal(observation.ItemId, retained.ItemId);
        await reopened.AcknowledgeAsync([observationId]);
        Assert.Equal(0, fixture.Provider.Fetches);
        output.WriteLine($"Native attribute observation {observationId}: directory={directory}; placeholder={placeholder}; move={move}; publication={pausePublication}; original={before.LocalBinding}; rename succeeded without retry.");
    }

    private sealed class AttributeCommitFactory(string path, bool pausePublication) : ICloudStateStoreFactory
    {
        private readonly bool _pausePublication = pausePublication;
        private int _paused;
        private Guid? _committed;
        internal string? RelativePath { get; set; }
        internal ICloudStateStore? Inner { get; private set; }
        internal TaskCompletionSource<Guid> Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context, CancellationToken cancellationToken = default)
        {
            Inner = await new SqliteCloudStateStoreFactory(path).OpenAsync(context, cancellationToken);
            return new AttributeCommitStore(Inner, this);
        }

        private async ValueTask PauseAsync(Guid operationId, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _paused, 1) != 0) { return; }
            Paused.TrySetResult(operationId);
            await Resume.Task.WaitAsync(cancellationToken);
        }

        private sealed class AttributeCommitStore(ICloudStateStore inner, AttributeCommitFactory owner) : ICloudStateStore
        {
            public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
            {
                if (owner._pausePublication && owner._committed is Guid operationId) { await owner.PauseAsync(operationId, cancellationToken); }
                return new AttributeCommitTransaction(await inner.BeginTransactionAsync(cancellationToken), owner);
            }
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }

        private sealed class AttributeCommitTransaction(ICloudStateTransaction inner, AttributeCommitFactory owner) : ICloudStateTransaction
        {
            public ICloudItemStateRepository Items => inner.Items;
            public ICloudCheckpointRepository Checkpoints => inner.Checkpoints;
            public ICloudOperationJournal Operations => inner.Operations;
            public ICloudConflictRepository Conflicts => inner.Conflicts;
            public ICloudRemoteBatchRepository RemoteBatches => inner.RemoteBatches;
            public ICloudEchoSuppressionRepository EchoSuppressions => inner.EchoSuppressions;
            public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
            {
                IReadOnlyList<CloudStateCheckpoint> receipts = await inner.Checkpoints.ListAsync(CloudLocalChangeFeed.SecurityValidationPrefix, cancellationToken);
                CloudStateCheckpoint? receipt = receipts.FirstOrDefault(value =>
                    LocalChangePayload.Decode(value.Value).RelativePath == owner.RelativePath);
                Guid? operationId = receipt is null ? null : Guid.ParseExact(receipt.Name[CloudLocalChangeFeed.SecurityValidationPrefix.Length..], "N");
                if (!owner._pausePublication && operationId is Guid staged) { await owner.PauseAsync(staged, cancellationToken); }
                await inner.CommitAsync(cancellationToken);
                if (operationId.HasValue) { owner._committed = operationId; }
            }
            public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
