namespace CfSharp.Tests.Persistence;

public abstract class CloudStateStoreContractTests
{
    protected abstract ICloudStateStoreFactory CreateFactory();

    [Fact]
    public async Task JournalPagingTraversesBacklogWithoutAcknowledgingOrIncludingLaterWrites()
    {
        await using ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext());
        List<CloudOperationJournalEntry> expected = [];
        CloudStateOperationKind[] kinds = Enum.GetValues<CloudStateOperationKind>();
        await using (ICloudStateTransaction write = await store.BeginTransactionAsync())
        {
            for (int index = 0; index < 4105; index++)
            {
                expected.Add(await write.Operations.EnqueueAsync(new CloudOperationJournalEntry(
                    Guid.NewGuid(), kinds[index % kinds.Length], null, BitConverter.GetBytes(index),
                    DateTimeOffset.UtcNow, attemptCount: index % 3)));
            }

            await write.CommitAsync();
        }

        long through;
        await using (ICloudStateTransaction capture = await store.BeginTransactionAsync())
        {
            through = await Assert.IsAssignableFrom<ICloudOperationJournalPaging>(capture.Operations)
                .GetHighWaterSequenceAsync();
            await capture.RollbackAsync();
        }

        // Leave holes both before and inside later pages; the cursor must use assigned sequences,
        // not an offset or the count of remaining rows. New rows must stay outside this scan.
        await using (ICloudStateTransaction update = await store.BeginTransactionAsync())
        {
            foreach (CloudOperationJournalEntry removed in expected.Where(row => row.Sequence % 17 == 0))
            {
                await update.Operations.RemoveAsync(removed.OperationId);
            }

            await update.Operations.EnqueueAsync(new CloudOperationJournalEntry(
                Guid.NewGuid(), kinds[0], null, [99], DateTimeOffset.UtcNow));
            await update.CommitAsync();
        }

        expected.RemoveAll(row => row.Sequence % 17 == 0);
        List<CloudOperationJournalEntry> delivered = [];
        long cursor = 0;
        bool more;
        do
        {
            await using ICloudStateTransaction read = await store.BeginTransactionAsync();
            CloudOperationJournalPage page = await Assert.IsAssignableFrom<ICloudOperationJournalPaging>(read.Operations)
                .ReadPageAsync(cursor, through, 7);
            Assert.InRange(page.Operations.Count, 1, 7);
            Assert.All(page.Operations, row => Assert.InRange(row.Sequence, cursor + 1, through));
            Assert.Equal(page.Operations[^1].Sequence, page.LastScannedSequence);
            delivered.AddRange(page.Operations);
            cursor = page.LastScannedSequence;
            more = page.HasMore;
            await read.RollbackAsync();
        }
        while (more);

        Assert.Equal(expected.Select(row => row.OperationId), delivered.Select(row => row.OperationId));
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Sequence, delivered[index].Sequence);
            Assert.Equal(expected[index].Kind, delivered[index].Kind);
            Assert.Equal(expected[index].AttemptCount, delivered[index].AttemptCount);
            Assert.Equal(expected[index].Payload.ToArray(), delivered[index].Payload.ToArray());
        }

        await using ICloudStateTransaction verify = await store.BeginTransactionAsync();
        ICloudOperationJournalPaging paging = Assert.IsAssignableFrom<ICloudOperationJournalPaging>(verify.Operations);
        CloudOperationJournalPage terminal = await paging.ReadPageAsync(through, through, 4096);
        Assert.Empty(terminal.Operations);
        Assert.Equal(through, terminal.LastScannedSequence);
        Assert.False(terminal.HasMore);
        Assert.Equal(expected.Count + 1, (await verify.Operations.ListAsync(int.MaxValue)).Count);
        Assert.True(await paging.GetHighWaterSequenceAsync() > through);
        await verify.RollbackAsync();
    }

    [Fact]
    public async Task JournalPagingEnforcesBoundsCancellationAndTransactionLifetime()
    {
        await using ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext());
        await using ICloudStateTransaction transaction = await store.BeginTransactionAsync();
        ICloudOperationJournalPaging paging = Assert.IsAssignableFrom<ICloudOperationJournalPaging>(transaction.Operations);
        Assert.Equal(0, await paging.GetHighWaterSequenceAsync());
        Assert.Empty((await paging.ReadPageAsync(0, 0, 1)).Operations);
        foreach ((long after, long through, int limit) in new[] { (-1L, 0L, 1), (2L, 1L, 1), (0L, 0L, 0), (0L, 0L, 4097) })
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
                await paging.ReadPageAsync(after, through, limit));
        }

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await paging.ReadPageAsync(0, 0, 1, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await paging.GetHighWaterSequenceAsync(cancellation.Token));
        await transaction.RollbackAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await paging.ReadPageAsync(0, 0, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await paging.GetHighWaterSequenceAsync());
    }

    [Fact]
    public async Task CommittedItemSurvivesStoreReopen()
    {
        ICloudStateStoreFactory factory = CreateFactory();
        CloudStateStoreContext context = CreateContext();
        Guid itemId = Guid.NewGuid();
        DateTimeOffset timestamp = new(2026, 9, 20, 12, 30, 0, TimeSpan.FromHours(8));
        CloudItemState expected = new(
            itemId,
            "remote-42",
            "Documents/report.txt",
            CloudItemKind.File,
            "revision-7",
            1234,
            false,
            timestamp);

        await using (ICloudStateStore store = await factory.OpenAsync(context))
        await using (ICloudStateTransaction transaction = await store.BeginTransactionAsync())
        {
            await transaction.Items.UpsertAsync(expected);
            await transaction.CommitAsync();
        }

        await using ICloudStateStore reopened = await factory.OpenAsync(context);
        await using ICloudStateTransaction read = await reopened.BeginTransactionAsync();
        CloudItemState? byId = await read.Items.GetByItemIdAsync(itemId);
        CloudItemState? byRemote = await read.Items.GetByRemoteIdAsync("remote-42");
        CloudItemState? byPath = await read.Items.GetByRelativePathAsync("Documents/report.txt");

        AssertItem(expected, byId);
        AssertItem(expected, byRemote);
        AssertItem(expected, byPath);
        await read.RollbackAsync();
    }

    [Fact]
    public async Task ItemSubtreeQueryHonorsPathBoundariesAndOrdering()
    {
        ICloudStateStoreFactory factory = CreateFactory();
        await using ICloudStateStore store = await factory.OpenAsync(CreateContext());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CloudItemState[] items =
        [
            CreateItem("remote-report", "Documents/report.txt", CloudItemKind.File, now),
            CreateItem("remote-other", "Documents2/other.txt", CloudItemKind.File, now),
            CreateItem("remote-root", string.Empty, CloudItemKind.Directory, now),
            CreateItem("remote-subdirectory", "Documents/Sub", CloudItemKind.Directory, now),
            CreateItem("remote-subfile", "Documents/Sub/data.bin", CloudItemKind.File, now),
            CreateItem("remote-documents", "Documents", CloudItemKind.Directory, now),
        ];

        await using (ICloudStateTransaction write = await store.BeginTransactionAsync())
        {
            foreach (CloudItemState item in items)
            {
                await write.Items.UpsertAsync(item);
            }

            await write.CommitAsync();
        }

        await using ICloudStateTransaction read = await store.BeginTransactionAsync();
        IReadOnlyList<CloudItemState> subtree = await read.Items.ListSubtreeAsync("DOCUMENTS");
        IReadOnlyList<CloudItemState> all = await read.Items.ListSubtreeAsync(string.Empty);

        Assert.Equal(
            ["Documents", "Documents/Sub", "Documents/report.txt", "Documents/Sub/data.bin"],
            subtree.Select(static item => item.RelativePath));
        Assert.Equal(string.Empty, all[0].RelativePath);
        Assert.Equal(items.Length, all.Count);
        Assert.DoesNotContain(
            subtree,
            static item => string.Equals(
                item.RelativePath,
                "Documents2/other.txt",
                StringComparison.OrdinalIgnoreCase));
        await read.RollbackAsync();
    }

    [Fact]
    public async Task ListSubtreeOrdersByPathDepthBeforePathLength()
    {
        ICloudStateStoreFactory factory = CreateFactory();
        await using ICloudStateStore store = await factory.OpenAsync(CreateContext());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (ICloudStateTransaction write = await store.BeginTransactionAsync())
        {
            await write.Items.UpsertAsync(CreateItem(
                "depth-root",
                "root",
                CloudItemKind.Directory,
                now));
            await write.Items.UpsertAsync(CreateItem(
                "depth-long-child",
                "root\\very-long-name.txt",
                CloudItemKind.File,
                now));
            await write.Items.UpsertAsync(CreateItem(
                "depth-short-grandchild",
                "root\\x\\y",
                CloudItemKind.File,
                now));
            await write.CommitAsync();
        }

        await using ICloudStateTransaction read = await store.BeginTransactionAsync();
        IReadOnlyList<CloudItemState> items = await read.Items.ListSubtreeAsync("root");
        Assert.Equal(
            ["root", "root\\very-long-name.txt", "root\\x\\y"],
            items.Select(static item => item.RelativePath));
        await read.RollbackAsync();
    }

    [Fact]
    public async Task DisposalAndExplicitRollbackDiscardWrites()
    {
        ICloudStateStoreFactory factory = CreateFactory();
        await using ICloudStateStore store = await factory.OpenAsync(CreateContext());

        await using (ICloudStateTransaction disposed = await store.BeginTransactionAsync())
        {
            await disposed.Checkpoints.UpsertAsync(
                new CloudStateCheckpoint("disposed", [1], DateTimeOffset.UtcNow));
        }

        await using (ICloudStateTransaction rolledBack = await store.BeginTransactionAsync())
        {
            await rolledBack.Checkpoints.UpsertAsync(
                new CloudStateCheckpoint("rolled-back", [2], DateTimeOffset.UtcNow));
            await rolledBack.RollbackAsync();
        }

        await using ICloudStateTransaction read = await store.BeginTransactionAsync();
        Assert.Null(await read.Checkpoints.GetAsync("disposed"));
        Assert.Null(await read.Checkpoints.GetAsync("rolled-back"));
        await read.RollbackAsync();
    }

    [Fact]
    public async Task TransactionCommitsEveryRepositoryAtomically()
    {
        ICloudStateStoreFactory factory = CreateFactory();
        await using ICloudStateStore store = await factory.OpenAsync(CreateContext());
        Guid itemId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        Guid conflictId = Guid.NewGuid();
        Guid suppressionId = Guid.NewGuid();
        DateTimeOffset now = new(2026, 9, 20, 10, 0, 0, TimeSpan.Zero);

        await using (ICloudStateTransaction write = await store.BeginTransactionAsync())
        {
            await write.Items.UpsertAsync(
                new CloudItemState(
                    itemId,
                    "remote-atomic",
                    "file.txt",
                    CloudItemKind.File,
                    null,
                    null,
                    false,
                    now));
            await write.Checkpoints.UpsertAsync(new CloudStateCheckpoint("remote", [1, 2], now));
            CloudOperationJournalEntry queued = await write.Operations.EnqueueAsync(
                new CloudOperationJournalEntry(
                    operationId,
                    CloudStateOperationKind.ContentUpdate,
                    itemId,
                    [3, 4],
                    now));
            Assert.Equal(1, queued.Sequence);
            await write.Conflicts.UpsertAsync(
                new CloudConflictState(
                    conflictId,
                    itemId,
                    CloudStateConflictKind.Content,
                    [5, 6],
                    now));
            await write.RemoteBatches.UpsertAsync(
                new CloudRemoteBatchState(
                    "batch-1",
                    [7],
                    2,
                    5,
                    CloudRemoteBatchStatus.Applying,
                    [8],
                    now));
            await write.EchoSuppressions.UpsertAsync(
                new CloudEchoSuppressionState(
                    suppressionId,
                    itemId,
                    CloudStateOperationKind.ContentUpdate,
                    "file.txt",
                    [9],
                    now.AddMinutes(5)));
            await write.CommitAsync();
        }

        await using ICloudStateTransaction read = await store.BeginTransactionAsync();
        CloudStateCheckpoint? checkpoint = await read.Checkpoints.GetAsync("remote");
        CloudOperationJournalEntry? operation = await read.Operations.GetAsync(operationId);
        CloudConflictState? conflict = await read.Conflicts.GetAsync(conflictId);
        CloudRemoteBatchState? batch = await read.RemoteBatches.GetAsync("batch-1");
        CloudEchoSuppressionState? suppression = await read.EchoSuppressions.GetAsync(suppressionId);

        Assert.NotNull(checkpoint);
        Assert.True(checkpoint.Value.Span.SequenceEqual(new byte[] { 1, 2 }));
        Assert.Single(await read.Checkpoints.ListAsync("rem"));
        Assert.NotNull(operation);
        Assert.Equal(1, operation.Sequence);
        Assert.True(operation.Payload.Span.SequenceEqual(new byte[] { 3, 4 }));
        Assert.NotNull(conflict);
        Assert.True(conflict.Payload.Span.SequenceEqual(new byte[] { 5, 6 }));
        Assert.NotNull(batch);
        Assert.Equal(2, batch.AppliedEntryCount);
        Assert.NotNull(suppression);
        Assert.Single(await read.EchoSuppressions.ListActiveAsync(now));
        await read.RollbackAsync();
    }

    [Fact]
    public async Task JournalPreservesSequenceAndRetryUpdates()
    {
        ICloudStateStoreFactory factory = CreateFactory();
        await using ICloudStateStore store = await factory.OpenAsync(CreateContext());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();

        await using (ICloudStateTransaction write = await store.BeginTransactionAsync())
        {
            CloudOperationJournalEntry first = await write.Operations.EnqueueAsync(
                new CloudOperationJournalEntry(
                    firstId,
                    CloudStateOperationKind.Create,
                    null,
                    [1],
                    now));
            CloudOperationJournalEntry second = await write.Operations.EnqueueAsync(
                new CloudOperationJournalEntry(
                    secondId,
                    CloudStateOperationKind.Delete,
                    null,
                    [2],
                    now.AddSeconds(1)));
            await write.Operations.UpdateAsync(
                new CloudOperationJournalEntry(
                    first.OperationId,
                    first.Kind,
                    first.ItemId,
                    first.Payload.Span,
                    first.CreatedAt,
                    1,
                    now.AddMinutes(1),
                    first.Sequence));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await write.Operations.UpdateAsync(
                    new CloudOperationJournalEntry(
                        first.OperationId,
                        first.Kind,
                        first.ItemId,
                        first.Payload.Span,
                        first.CreatedAt,
                        first.AttemptCount,
                        first.RetryAfter,
                        first.Sequence + 10)));
            Assert.Equal(2, second.Sequence);
            await write.CommitAsync();
        }

        await using ICloudStateTransaction read = await store.BeginTransactionAsync();
        IReadOnlyList<CloudOperationJournalEntry> operations =
            await read.Operations.ListAsync(10);
        Assert.Equal(new[] { firstId, secondId }, operations.Select(item => item.OperationId));
        Assert.Equal(1, operations[0].AttemptCount);
        Assert.Equal(now.AddMinutes(1), operations[0].RetryAfter);
        await read.RollbackAsync();
    }

    [Fact]
    public async Task ItemScopedJournalQueryFindsEntriesBeyondGlobalPageLimit()
    {
        ICloudStateStoreFactory factory = CreateFactory();
        await using ICloudStateStore store = await factory.OpenAsync(CreateContext());
        Guid targetItemId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using (ICloudStateTransaction write = await store.BeginTransactionAsync())
        {
            await write.Items.UpsertAsync(
                new CloudItemState(
                    targetItemId,
                    "target",
                    "target.txt",
                    CloudItemKind.File,
                    null,
                    null,
                    false,
                    now));
            for (int index = 0; index < 4096; index++)
            {
                await write.Operations.EnqueueAsync(
                    new CloudOperationJournalEntry(
                        Guid.NewGuid(),
                        CloudStateOperationKind.ContentUpdate,
                        null,
                        [1],
                        now.AddTicks(index)));
            }

            Guid targetOperationId = Guid.NewGuid();
            await write.Operations.EnqueueAsync(
                new CloudOperationJournalEntry(
                    targetOperationId,
                    CloudStateOperationKind.ContentUpdate,
                    targetItemId,
                    [2],
                    now.AddTicks(4096)));
            await write.CommitAsync();
        }

        await using ICloudStateTransaction read = await store.BeginTransactionAsync();
        IReadOnlyList<CloudOperationJournalEntry> targetOperations =
            await read.Operations.ListByItemIdAsync(targetItemId, int.MaxValue);
        CloudOperationJournalEntry target = Assert.Single(targetOperations);
        Assert.Equal(targetItemId, target.ItemId);
        Assert.Equal(new byte[] { 2 }, target.Payload.ToArray());
        await read.RollbackAsync();
    }

    [Fact]
    public async Task ExpiredEchoSuppressionsCanBeRemoved()
    {
        ICloudStateStoreFactory factory = CreateFactory();
        await using ICloudStateStore store = await factory.OpenAsync(CreateContext());
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await using (ICloudStateTransaction write = await store.BeginTransactionAsync())
        {
            await write.EchoSuppressions.UpsertAsync(
                new CloudEchoSuppressionState(
                    Guid.NewGuid(),
                    null,
                    CloudStateOperationKind.Move,
                    "old.txt",
                    [],
                    now.AddSeconds(-1)));
            await write.EchoSuppressions.UpsertAsync(
                new CloudEchoSuppressionState(
                    Guid.NewGuid(),
                    null,
                    CloudStateOperationKind.Move,
                    "new.txt",
                    [],
                    now.AddMinutes(1)));
            await write.EchoSuppressions.RemoveExpiredAsync(now);
            await write.CommitAsync();
        }

        await using ICloudStateTransaction read = await store.BeginTransactionAsync();
        CloudEchoSuppressionState active = Assert.Single(
            await read.EchoSuppressions.ListActiveAsync(now));
        Assert.Equal("new.txt", active.RelativePath);
        await read.RollbackAsync();
    }

    [Fact]
    public async Task TerminalTransactionRejectsRepositoryAndCommitReuse()
    {
        ICloudStateStoreFactory factory = CreateFactory();
        await using ICloudStateStore store = await factory.OpenAsync(CreateContext());
        await using ICloudStateTransaction transaction = await store.BeginTransactionAsync();

        await transaction.CommitAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await transaction.Checkpoints.GetAsync("after-commit"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await transaction.CommitAsync());
    }

    [Fact]
    public async Task FactoryAndStoreHonorPreCanceledTokens()
    {
        ICloudStateStoreFactory factory = CreateFactory();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await factory.OpenAsync(CreateContext(), cancellation.Token));

        await using ICloudStateStore store = await factory.OpenAsync(CreateContext());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await store.BeginTransactionAsync(cancellation.Token));
    }

    protected virtual CloudStateStoreContext CreateContext() =>
        new(Path.Combine(Path.GetTempPath(), "CfSharp-contract", Guid.NewGuid().ToString("N")));

    private static CloudItemState CreateItem(
        string remoteId,
        string relativePath,
        CloudItemKind kind,
        DateTimeOffset updatedAt) =>
        new(
            Guid.NewGuid(),
            remoteId,
            relativePath,
            kind,
            remoteRevision: null,
            localFileId: null,
            isTombstone: false,
            updatedAt);

    private static void AssertItem(CloudItemState expected, CloudItemState? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.ItemId, actual.ItemId);
        Assert.Equal(expected.RemoteId, actual.RemoteId);
        Assert.Equal(expected.RelativePath, actual.RelativePath);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.RemoteRevision, actual.RemoteRevision);
        Assert.Equal(expected.LocalFileId, actual.LocalFileId);
        Assert.Equal(expected.IsTombstone, actual.IsTombstone);
        Assert.Equal(expected.UpdatedAt, actual.UpdatedAt);
    }
}
