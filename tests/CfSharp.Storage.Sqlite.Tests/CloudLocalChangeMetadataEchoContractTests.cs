using System.Collections.Concurrent;

namespace CfSharp.Storage.Sqlite.Tests;

public sealed partial class CloudLocalChangeFeedTests
{
    [Theory]
    [InlineData("different owner")]
    [InlineData("moved owner")]
    [InlineData("same owner, new native generation")]
    [InlineData("lone passthrough without the original native directory")]
    public async Task DirectoryNotificationReadsOnlyItsPathAndItemMetadataHistory(string scenario)
    {
        const string relative = "directory";
        Directory.CreateDirectory(Path.Combine(_syncRootPath, relative));
        await using ICloudStateStore store = await OpenStoreAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid itemId = Guid.NewGuid();
        bool movedOwner = scenario == "moved owner";
        bool lonePassthrough = scenario == "lone passthrough without the original native directory";
        RemoteMetadataIntent historical = new(Guid.NewGuid(), Guid.NewGuid(), scenario == "different owner" ? Guid.NewGuid() : itemId,
            movedOwner ? "old-directory" : "DIRECTORY", new(1, Guid.NewGuid(), Guid.NewGuid()), Coordinated: false, Completed: true)
        { Reconciled = true };
        if (lonePassthrough)
        { historical = historical with { Passthrough = true, OutcomeConfirmed = true, NativeWriteApplied = true }; }
        RemoteMetadataIntent successor = new(Guid.NewGuid(), Guid.NewGuid(), itemId, relative,
            new(1, Guid.NewGuid(), Guid.NewGuid()), Coordinated: false, Completed: true)
        { Passthrough = true, OutcomeConfirmed = true, NativeWriteApplied = true };
        await using (ICloudStateTransaction seed = await store.BeginTransactionAsync())
        {
            await seed.Items.UpsertAsync(new(itemId, "directory-owner", relative, CloudItemKind.Directory,
                "r1", null, false, now));
            // Simulate a database from before the path index existed.
            await seed.Checkpoints.UpsertAsync(new(historical.Name, historical.Encode(), now));
            if (!movedOwner && !lonePassthrough)
            { await seed.Checkpoints.UpsertAsync(new(successor.Name, successor.Encode(), now)); }
            for (int index = 0; index < 64; index++)
            {
                RemoteMetadataIntent unrelated = historical with
                {
                    Id = Guid.NewGuid(),
                    ItemId = Guid.NewGuid(),
                    RelativePath = $"unrelated-{index}",
                };
                await seed.Checkpoints.UpsertAsync(new(unrelated.Name, unrelated.Encode(), now));
            }
            await seed.CommitAsync();
        }

        RaceStore observed = new(store);
        FakeSource source = new();
        await using CloudLocalChangeFeed feed = CreateFeed(observed, source);
        await feed.StartAsync();
        Assert.False((await feed.BeginScanAsync()).RequiresFullRescan);
        ConcurrentQueue<string> prefixes = new();
        observed.CheckpointPrefixRead = prefixes.Enqueue;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        await source.EmitAsync(new(LocalChangeSourceAction.Modified, relative));
        CloudLocalChangeBatch fenced = await feed.ReadBatchAsync(deadline.Token);
        Assert.True(fenced.RequiresFullRescan);
        Assert.Empty(fenced.Changes);
        Assert.Contains(RemoteMetadataIntent.PathPrefix(relative), prefixes);
        Assert.Contains(RemoteMetadataIntent.ItemPrefix(itemId), prefixes);
        Assert.DoesNotContain(RemoteMetadataIntent.Prefix, prefixes);
        Assert.DoesNotContain(RemoteMetadataIntent.PathIndexPrefix, prefixes);

        CloudOperationJournalEntry original;
        await using (ICloudStateTransaction state = await store.BeginTransactionAsync(deadline.Token))
        {
            CloudStateCheckpoint indexed = (await state.Checkpoints.GetAsync(historical.PathIndexName, deadline.Token))!;
            Assert.True(RemoteMetadataIntent.DecodePathIndex(indexed).RequiresReconciliation);
            original = Assert.Single(await state.Operations.ListAsync(10, cancellationToken: deadline.Token));
            if (!movedOwner)
            {
                Assert.NotNull(await state.Checkpoints.GetAsync(RemoteMetadataIntent.PendingObservationItemName(itemId), deadline.Token));
                Assert.NotNull(await state.Checkpoints.GetAsync(RemoteMetadataIntent.PendingObservationPathName(relative), deadline.Token));
            }
        }
        await feed.AcknowledgeFullRescanAsync(deadline.Token);
        CloudLocalChange delivered = Assert.Single((await feed.ReadBatchAsync(deadline.Token)).Changes);
        Assert.Equal((original.OperationId, original.Sequence), (delivered.OperationId, delivered.Sequence));
        Assert.Null(delivered.ItemId);
        await feed.AcknowledgeAsync([delivered.OperationId], deadline.Token);
    }

    [Fact]
    public async Task DelayedDirectoryTimestampCannotBindToAReplacementFile()
    {
        const string relative = "replacement";
        string path = Path.Combine(_syncRootPath, relative);
        const string contents = "replacement bytes";
        await File.WriteAllTextAsync(path, contents);
        await using ICloudStateStore store = await OpenStoreAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid itemId = Guid.NewGuid();
        await using (ICloudStateTransaction seed = await store.BeginTransactionAsync())
        {
            await seed.Items.UpsertAsync(new(itemId, "replacement-file", relative, CloudItemKind.File, "r1", null, false, now));
            await seed.Checkpoints.UpsertAsync(new(RemoteMetadataIntent.DirectoryTimeObservationName(relative),
                Guid.NewGuid().ToByteArray(), now));
            await seed.CommitAsync();
        }
        FakeSource source = new();
        await using CloudLocalChangeFeed feed = CreateFeed(store, source);
        await feed.StartAsync();
        await source.EmitAsync(new(LocalChangeSourceAction.Modified, relative));
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        await WaitForJournalStateAsync(store, expectedOperations: 1, expectedSuppressions: 0);
        Assert.True((await feed.ReadBatchAsync(deadline.Token)).RequiresFullRescan);
        await feed.AcknowledgeFullRescanAsync(deadline.Token);
        CloudLocalChange observation = Assert.Single((await feed.ReadBatchAsync(deadline.Token)).Changes);
        Assert.Equal(CloudLocalChangeKind.ContentUpdate, observation.Kind);
        Assert.Null(observation.ItemId);
        Assert.Equal(contents, await File.ReadAllTextAsync(path, deadline.Token));
    }

    [Fact]
    public async Task LegacyMetadataTicketCannotConsumeAGenuineDirectoryObservation()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        string path = Path.Combine(_syncRootPath, "directory");
        Directory.CreateDirectory(path);
        Guid itemId = Guid.NewGuid();
        Guid ticketId = Guid.NewGuid();
        await using (ICloudStateTransaction seed = await store.BeginTransactionAsync())
        {
            await seed.Items.UpsertAsync(new(itemId, "legacy-metadata", "directory", CloudItemKind.Directory,
                "r1", null, false, DateTimeOffset.UtcNow));
            await seed.EchoSuppressions.UpsertAsync(new(ticketId, itemId, CloudStateOperationKind.MetadataUpdate,
                "directory", Array.Empty<byte>(), DateTimeOffset.UtcNow.AddMinutes(1), remainingObservations: 4));
            await seed.CommitAsync();
        }
        FakeSource source = new();
        await using CloudLocalChangeFeed feed = CreateFeed(store, source);
        await feed.StartAsync();
        await Assert.ThrowsAsync<NotSupportedException>(() => feed.SuppressProviderEchoAsync(
            CloudStateOperationKind.MetadataUpdate, "directory", DateTimeOffset.UtcNow.AddMinutes(1), itemId).AsTask());
        await source.EmitAsync(new(LocalChangeSourceAction.Modified, "directory"));
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        CloudLocalChangeBatch batch = await feed.ReadBatchAsync(deadline.Token);
        CloudLocalChange change = Assert.Single(batch.Changes);
        Assert.Equal(CloudLocalChangeKind.MetadataUpdate, change.Kind);
        Assert.Equal(itemId, change.ItemId);
        await using ICloudStateTransaction state = await store.BeginTransactionAsync();
        Assert.Equal(4, (await state.EchoSuppressions.GetAsync(ticketId))!.RemainingObservations);
        Assert.Equal(change.OperationId, Assert.Single(await state.Operations.ListAsync(10)).OperationId);
    }

    [Fact]
    public async Task CorruptMetadataEvidenceCannotStartTheFeedOrConsumeAnOriginal()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        RemoteMetadataIntent intent = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Docs\\Mixed.BIN",
            new(1, Guid.NewGuid(), Guid.NewGuid()), Coordinated: true, Completed: true);
        CloudStateCheckpoint evidence = new(intent.Name, intent.Encode(), DateTimeOffset.UtcNow);
        Assert.Equal(intent.RelativePath, RemoteMetadataIntent.Decode(evidence).RelativePath);
        Guid operationId = Guid.NewGuid();
        byte[] payload = new LocalChangePayload(intent.RelativePath, null, false, DateTimeOffset.UtcNow).Encode();
        await using (ICloudStateTransaction seed = await store.BeginTransactionAsync())
        {
            await seed.Checkpoints.UpsertAsync(new(intent.Name, evidence.Value.Span[..^3], evidence.UpdatedAt));
            await seed.Operations.EnqueueAsync(new(operationId, CloudStateOperationKind.MetadataUpdate, null, payload, DateTimeOffset.UtcNow));
            await seed.CommitAsync();
        }
        FakeSource source = new();
        await using CloudLocalChangeFeed feed = CreateFeed(store, source);
        await Assert.ThrowsAsync<InvalidDataException>(() => feed.StartAsync().AsTask());
        await using ICloudStateTransaction retained = await store.BeginTransactionAsync();
        Assert.NotNull(await retained.Checkpoints.GetAsync(intent.Name));
        Assert.NotNull(await retained.Operations.GetAsync(operationId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyCompletedPassthroughWithoutNativeWriteEvidenceRequiresReconciliation(bool outcomeConfirmed)
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        RemoteMetadataIntent intent = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "directory", null,
            Coordinated: false, Completed: true)
        { Passthrough = true, OutcomeConfirmed = outcomeConfirmed };
        await using (ICloudStateTransaction seed = await store.BeginTransactionAsync())
        {
            int missingFlags = outcomeConfirmed ? 1 : 2;
            await seed.Checkpoints.UpsertAsync(new(intent.Name, intent.Encode().AsSpan()[..^missingFlags], DateTimeOffset.UtcNow));
            await seed.CommitAsync();
        }
        await using CloudLocalChangeFeed feed = CreateFeed(store, new FakeSource());
        await feed.StartAsync();
        Assert.True((await feed.BeginScanAsync()).RequiresFullRescan);
        await feed.AcknowledgeFullRescanAsync();
        Assert.False((await feed.BeginScanAsync()).RequiresFullRescan);
        await using ICloudStateTransaction state = await store.BeginTransactionAsync();
        RemoteMetadataIntent retained = RemoteMetadataIntent.Decode((await state.Checkpoints.GetAsync(intent.Name))!);
        Assert.True(retained.Reconciled);
        Assert.False(retained.IsConfirmedPassthrough);
    }

    [Fact]
    public async Task JournalPagesReadPendingMetadataWithoutLoadingReconciledHistory()
    {
        await using ICloudStateStore store = await OpenStoreAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset originalCheckpointTime = now.AddDays(-1);
        Guid operationId = Guid.NewGuid();
        RemoteMetadataIntent pending = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "pending", null,
            Coordinated: false, Completed: false);
        const int historyCount = 64;
        await using (ICloudStateTransaction seed = await store.BeginTransactionAsync())
        {
            for (int index = 0; index < historyCount; index++)
            {
                RemoteMetadataIntent historical = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), $"history-{index}",
                    new(1, Guid.NewGuid(), Guid.NewGuid()), Coordinated: true, Completed: true)
                { Reconciled = true };
                await seed.Checkpoints.UpsertAsync(new(historical.Name, historical.Encode(), originalCheckpointTime));
            }
            await seed.Checkpoints.UpsertAsync(new(pending.Name, pending.Encode(), now));
            await seed.Operations.EnqueueAsync(new(operationId, CloudStateOperationKind.MetadataUpdate, null,
                new LocalChangePayload("journal.txt", null, false, now).Encode(), now));
            await seed.CommitAsync();
        }

        RaceStore observed = new(store);
        await using CloudLocalChangeFeed feed = CreateFeed(observed, new FakeSource());
        await feed.StartAsync();
        Assert.True((await feed.BeginScanAsync()).RequiresFullRescan);
        await using (ICloudStateTransaction state = await store.BeginTransactionAsync())
        {
            CloudStateCheckpoint indexed = Assert.Single(await state.Checkpoints.ListAsync(RemoteMetadataIntent.PendingPrefix));
            Assert.Equal(pending.Id, RemoteMetadataIntent.DecodePending(indexed).Id);
        }
        await feed.AcknowledgeFullRescanAsync();
        await feed.AcknowledgeFullRescanAsync();
        await using (ICloudStateTransaction state = await store.BeginTransactionAsync())
        {
            Assert.Empty(await state.Checkpoints.ListAsync(RemoteMetadataIntent.PendingPrefix));
            IReadOnlyList<CloudStateCheckpoint> receipts = await state.Checkpoints.ListAsync(RemoteMetadataIntent.Prefix);
            Assert.Equal(historyCount + 1, receipts.Count);
            Assert.All(receipts.Where(receipt => receipt.Name != pending.Name),
                receipt => Assert.Equal(originalCheckpointTime, receipt.UpdatedAt));
        }

        List<string> prefixes = [];
        observed.CheckpointPrefixRead = prefixes.Add;
        CloudLocalChangeScan scan = await feed.BeginScanAsync();
        CloudLocalChangePage first = await feed.ReadPageAsync(scan, 0, 1);
        Assert.False(first.RequiresFullRescan);
        Assert.Equal(operationId, Assert.Single(first.Changes).OperationId);
        Assert.Equal(operationId, Assert.Single((await feed.ReadBatchAsync()).Changes).OperationId);
        await using (ICloudStateTransaction write = await store.BeginTransactionAsync())
        {
            await (pending with { Id = Guid.NewGuid() }).PersistAsync(write.Checkpoints, now, CancellationToken.None);
            await write.CommitAsync();
        }
        Assert.True((await feed.ReadPageAsync(scan, 0, 1)).RequiresFullRescan);
        Assert.Contains(RemoteMetadataIntent.PendingPrefix, prefixes);
        Assert.DoesNotContain(RemoteMetadataIntent.Prefix, prefixes);
    }
}
