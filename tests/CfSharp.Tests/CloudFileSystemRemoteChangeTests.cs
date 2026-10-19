using System.Runtime.Versioning;

using CfSharp.Tests.Persistence;

namespace CfSharp.Tests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed class CloudFileSystemRemoteChangeTests
{
    [Fact]
    public async Task FailureStateCommitErrorPreservesTheOriginalDeleteErrorAndSafePrefix()
    {
        string rootPath = CreateRoot();
        FailureStateFactory factory = new(InMemoryCloudStateStoreContractTests.CreateFactoryForTesting());
        try
        {
            await using CloudFileSystem system = CloudFileSystem.CreateBuilder(rootPath, new TestRuntime()).WithStateStore(factory).Build();
            await system.StartAsync();
            Guid item = Guid.NewGuid();
            await using (ICloudStateTransaction seed = await factory.Store!.BeginTransactionAsync())
            {
                await seed.Items.UpsertAsync(new(item, "remote", "gone.txt", CloudItemKind.File, "v1", null, true, DateTimeOffset.UtcNow));
                await seed.CommitAsync();
            }

            CloudRemoteChange change = new("delete", CloudRemoteChangeKind.Delete, "remote", "v2", CloudItemKind.File,
                "gone.txt", item, previousRemoteRevision: "v1");
            CloudRemoteChangeBatch batch = new("failure-batch", new byte[] { 1 }, [change], new byte[] { 9 });
            AggregateException failure = await Assert.ThrowsAsync<AggregateException>(() => system.ApplyRemoteChangesAsync(batch).AsTask());
            Assert.IsType<CloudRemoteDeleteRecoveryException>(failure.InnerExceptions[0]);
            Assert.Same(factory.Error, failure.InnerExceptions[1]);
            await using ICloudStateTransaction verify = await factory.Store!.BeginTransactionAsync();
            CloudRemoteBatchState progress = (await verify.RemoteBatches.GetAsync(batch.BatchId))!;
            Assert.Equal(0, progress.AppliedEntryCount);
            Assert.Equal(new byte[] { 1 }, progress.Cursor.ToArray());
            Assert.True((await verify.Items.GetByItemIdAsync(item))!.IsTombstone);
        }
        finally
        {
            DeleteRoot(rootPath);
        }
    }

    [Fact]
    public async Task CompletedDeleteReceiptUsesTheSameProtocolWithAnIndependentTransactionalStore()
    {
        string rootPath = CreateRoot();
        ICloudStateStoreFactory factory = InMemoryCloudStateStoreContractTests.CreateFactoryForTesting();
        try
        {
            CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "remote", "v1");
            CloudRemoteChange delete = new("delete", CloudRemoteChangeKind.Delete, "remote", "v2", CloudItemKind.File,
                "gone.txt", identity.ItemId, previousRemoteRevision: "v1");
            CloudRemoteChangeBatch batch = new("original-delete", new byte[] { 1 }, [delete], new byte[] { 9 });
            DateTimeOffset originalTime = DateTimeOffset.UtcNow.AddSeconds(-5);
            DateTimeOffset completionTime = DateTimeOffset.UtcNow;
            CloudItemState original = new(identity.ItemId, "remote", "gone.txt", CloudItemKind.File, "v1", 123, false, originalTime);
            await using (ICloudStateStore store = await factory.OpenAsync(new(rootPath)))
            await using (ICloudStateTransaction seed = await store.BeginTransactionAsync())
            {
                Guid scope = await CloudDirectoryMoveEvidence.GetScopeAsync(seed, true, CancellationToken.None);
                LocalCloudItemInspection nativeRoot = CloudItemInspector.Inspect(rootPath, CloudItemKind.Directory, rootPath);
                CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>(nativeRoot.LocalBinding);
                RemoteNamespaceObject item = new("gone.txt", binding, identity.Encode(), 100, 200, 512);
                RemoteNamespaceObject root = new(string.Empty, binding, nativeRoot.PlaceholderIdentity.ToArray(),
                    nativeRoot.CreationTime!.Value.UtcDateTime.ToFileTimeUtc(), 60, 0);
                RemoteDeleteIntent intent = new(scope, rootPath, batch.BatchId, 0, batch.Fingerprint.ToArray(), true, true, true,
                    CloudRemoteApplyOptions.Default.EchoSuppressionLifetime.Ticks, CloudRemoteApplyOptions.Default.EchoSuppressionObservationCount,
                    [root], [new(CloudItemKind.File, item, original, RemoteNamespaceEvidence.FingerprintOperations([]),
                        completionTime.UtcTicks, completionTime.UtcTicks, null, (uint)FileAttributes.Archive)]);
                await seed.Items.UpsertAsync(new(original.ItemId, original.RemoteId, original.RelativePath, original.Kind,
                    "v2", original.LocalFileId, true, completionTime));
                await seed.Checkpoints.UpsertAsync(new(RemoteDeleteIntent.Name(batch.BatchId, 0), intent.Encode(), completionTime));
                await seed.RemoteBatches.UpsertAsync(new(batch.BatchId, batch.InitialCursor.Span, 0, 1, CloudRemoteBatchStatus.Applying,
                    [], completionTime, batch.Fingerprint));
                await seed.CommitAsync();
            }

            await using (CloudFileSystem fileSystem = CloudFileSystem.CreateBuilder(rootPath, new TestRuntime()).WithStateStore(factory).Build())
            {
                await fileSystem.StartAsync();
                CloudRemoteApplyResult replay = await fileSystem.ApplyRemoteChangesAsync(batch);
                Assert.False(replay.RequiresRetry);
                Assert.Empty(replay.ConflictIds);
                Assert.Equal(CloudRemoteApplyEntryStatus.Applied, replay.Entries[0].Status);
                Assert.Equal(new byte[] { 9 }, replay.SafeCursor.ToArray());
            }

            await using CloudFileSystem reopened = CloudFileSystem.CreateBuilder(rootPath, new TestRuntime()).WithStateStore(factory).Build();
            await reopened.StartAsync();
            Assert.Equal(CloudRemoteApplyEntryStatus.AlreadyApplied, (await reopened.ApplyRemoteChangesAsync(batch)).Entries[0].Status);
        }
        finally
        {
            DeleteRoot(rootPath);
        }
    }

    [Fact]
    public async Task EmptyRemoteBatchPublishesFinalCursorAndReplaysIdempotently()
    {
        string rootPath = CreateRoot();
        try
        {
            await using CloudFileSystem fileSystem = await StartAsync(rootPath);
            CloudRemoteChangeBatch batch = new(
                "empty-batch",
                new byte[] { 1, 2 },
                [],
                new byte[] { 3, 4 });

            CloudRemoteApplyResult first = await fileSystem.ApplyRemoteChangesAsync(batch);
            Assert.Equal(CloudRemoteBatchStatus.Applied, first.Status);
            Assert.False(first.RequiresRetry);
            Assert.True(first.SafeCursor.Span.SequenceEqual(new byte[] { 3, 4 }));

            CloudRemoteApplyResult replay = await fileSystem.ApplyRemoteChangesAsync(batch);
            Assert.Equal(CloudRemoteBatchStatus.Applied, replay.Status);
            Assert.Empty(replay.Entries);
            Assert.False(replay.RequiresRetry);
        }
        finally
        {
            DeleteRoot(rootPath);
        }
    }

    [Fact]
    public async Task MissingRemoteMoveBecomesDurableConflictAndRejectsChangedFingerprint()
    {
        string rootPath = CreateRoot();
        try
        {
            await using CloudFileSystem fileSystem = await StartAsync(rootPath);
            CloudRemoteChange move = new(
                "move-1",
                CloudRemoteChangeKind.Move,
                "remote-file",
                "revision-2",
                CloudItemKind.File,
                "new.txt",
                previousRelativePath: "old.txt");
            CloudRemoteChangeBatch batch = new("batch-1", Array.Empty<byte>(), [move], new byte[] { 9 });

            CloudRemoteApplyResult failed = await fileSystem.ApplyRemoteChangesAsync(batch);
            CloudRemoteApplyEntryResult entry = Assert.Single(failed.Entries);
            Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, entry.Status);
            Assert.False(failed.RequiresRetry);
            Assert.True(failed.SafeCursor.Span.SequenceEqual(new byte[] { 9 }));

            CloudRemoteChange changed = new(
                "move-1",
                CloudRemoteChangeKind.Move,
                "remote-file",
                "revision-3",
                CloudItemKind.File,
                "other.txt",
                previousRelativePath: "old.txt");
            CloudRemoteChangeBatch changedBatch = new(
                "batch-1",
                Array.Empty<byte>(),
                [changed],
                new byte[] { 9 });

            await Assert.ThrowsAsync<ArgumentException>(() => fileSystem
                .ApplyRemoteChangesAsync(changedBatch)
                .AsTask());
        }
        finally
        {
            DeleteRoot(rootPath);
        }
    }

    [Fact]
    public async Task MissingRemoteMetadataChangeDoesNotLeaveEchoSuppression()
    {
        string rootPath = CreateRoot();
        try
        {
            ICloudStateStoreFactory factory = InMemoryCloudStateStoreContractTests.CreateFactoryForTesting();
            CloudRemoteChange change = new(
                "missing-metadata",
                CloudRemoteChangeKind.MetadataUpdate,
                "remote-missing",
                "revision-1",
                CloudItemKind.File,
                "missing.txt",
                metadata: CloudPlaceholderMetadata.CreateFileBuilder().Build());

            await using (CloudFileSystem fileSystem = CloudFileSystem
                .CreateBuilder(rootPath, new TestRuntime())
                .WithStateStore(factory)
                .Build())
            {
                await fileSystem.StartAsync();
                CloudRemoteApplyResult result = await fileSystem.ApplyRemoteChangesAsync(
                    new CloudRemoteChangeBatch(
                        "missing-metadata-batch",
                        Array.Empty<byte>(),
                        [change],
                        new byte[] { 1 }));

                Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, Assert.Single(result.Entries).Status);
                Assert.Equal(CloudRemoteConflictReason.MissingItem, result.Entries[0].Conflict!.Reason);
            }

            await using ICloudStateStore store = await factory.OpenAsync(new CloudStateStoreContext(rootPath));
            await using ICloudStateTransaction transaction = await store.BeginTransactionAsync();
            Assert.Empty(await transaction.EchoSuppressions.ListActiveAsync(DateTimeOffset.UtcNow));
        }
        finally
        {
            DeleteRoot(rootPath);
        }
    }

    [Fact]
    public async Task DurableConflictEnvelopeCanBeReadAndDeferred()
    {
        string rootPath = CreateRoot();
        try
        {
            await using CloudFileSystem fileSystem = await StartAsync(rootPath);
            CloudRemoteChange move = new(
                "move-round-trip",
                CloudRemoteChangeKind.Move,
                "remote-file",
                "revision-2",
                CloudItemKind.File,
                "new.txt",
                previousRelativePath: "old.txt",
                metadata: CloudPlaceholderMetadata.CreateFileBuilder()
                    .WithAttributes(FileAttributes.ReadOnly)
                    .WithLastWriteTime(DateTimeOffset.UtcNow.AddMinutes(-1))
                    .Build());
            CloudRemoteChangeBatch batch = new(
                "batch-round-trip",
                Array.Empty<byte>(),
                [move],
                new byte[] { 7 });

            CloudRemoteApplyResult applied = await fileSystem.ApplyRemoteChangesAsync(batch);
            Guid conflictId = Assert.Single(applied.ConflictIds);

            CloudRemoteApplyEntryResult deferred = await fileSystem.ResolveRemoteConflictAsync(
                conflictId,
                new CloudRemoteConflictResolution(CloudRemoteConflictDecision.Defer));
            Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, deferred.Status);
            Assert.NotNull(deferred.Conflict);
            Assert.Equal(move.ChangeId, deferred.Conflict!.Change.ChangeId);
            Assert.Equal(move.RelativePath, deferred.Conflict.Change.RelativePath);
            Assert.Equal(move.Metadata!.Attributes, deferred.Conflict.Change.Metadata!.Attributes);
            Assert.Equal(CloudRemoteConflictReason.MissingItem, deferred.Conflict.Reason);
        }
        finally
        {
            DeleteRoot(rootPath);
        }
    }

    [Fact]
    public async Task RemoteDirectoryCatalogIsPagedAndCannotMutateTheNamespace()
    {
        string rootPath = CreateRoot();
        try
        {
            await using CloudFileSystem fileSystem = await StartAsync(rootPath);
            StaticRemoteCatalog catalog = new(
                new CloudRemoteDirectoryPage(
                [
                    new CloudRemoteDirectoryEntry(
                        "remote-alpha",
                        "revision-1",
                        CloudItemKind.File,
                        "alpha.txt",
                        length: 5,
                        metadata: CloudPlaceholderMetadata.CreateFileBuilder().Build()),
                    new CloudRemoteDirectoryEntry(
                        "remote-folder",
                        "revision-1",
                        CloudItemKind.Directory,
                        "folder",
                        metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().Build()),
                ]));

            CloudRemoteDirectoryPage page = await fileSystem.ReadRemoteDirectoryPageAsync(
                catalog,
                new CloudRemoteDirectoryQuery(pageSize: 2));

            Assert.True(catalog.WasCalled);
            Assert.Equal(["alpha.txt", "folder"], page.Entries.Select(entry => entry.Name));
            Assert.False(File.Exists(Path.Combine(rootPath, "alpha.txt")));
            Assert.False(Directory.Exists(Path.Combine(rootPath, "folder")));
        }
        finally
        {
            DeleteRoot(rootPath);
        }
    }

    [Fact]
    public async Task SynchronizedDirectoryViewIsDeterministicAndResumesByCursor()
    {
        string rootPath = CreateRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "b.txt"), "b");
            await File.WriteAllTextAsync(Path.Combine(rootPath, "a.txt"), "a");
            Directory.CreateDirectory(Path.Combine(rootPath, "folder"));
            await using CloudFileSystem fileSystem = await StartAsync(rootPath);

            CloudSynchronizedDirectoryPage first =
                await fileSystem.ReadSynchronizedDirectoryPageAsync(
                    new CloudSynchronizedDirectoryQuery(pageSize: 2));
            Assert.False(first.IsComplete);
            Assert.Equal(["a.txt", "b.txt"], first.Entries.Select(entry => entry.Name));
            Assert.All(first.Entries, entry => Assert.True(entry.IsMaterialized));

            CloudSynchronizedDirectoryPage second =
                await fileSystem.ReadSynchronizedDirectoryPageAsync(
                    new CloudSynchronizedDirectoryQuery(
                        pageSize: 2,
                        continuationCursor: first.ContinuationCursor));
            Assert.True(second.IsComplete);
            Assert.Equal(["folder"], second.Entries.Select(entry => entry.Name));
        }
        finally
        {
            DeleteRoot(rootPath);
        }
    }

    [Fact]
    public async Task SynchronizedDirectoryCursorRejectsChangedSnapshot()
    {
        string rootPath = CreateRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(rootPath, "a.txt"), "a");
            await File.WriteAllTextAsync(Path.Combine(rootPath, "b.txt"), "b");
            await using CloudFileSystem fileSystem = await StartAsync(rootPath);

            CloudSynchronizedDirectoryPage first =
                await fileSystem.ReadSynchronizedDirectoryPageAsync(
                    new CloudSynchronizedDirectoryQuery(pageSize: 1));
            Assert.False(first.IsComplete);

            await File.WriteAllTextAsync(Path.Combine(rootPath, "c.txt"), "c");

            await Assert.ThrowsAsync<ArgumentException>(() => fileSystem
                .ReadSynchronizedDirectoryPageAsync(
                    new CloudSynchronizedDirectoryQuery(
                        pageSize: 1,
                        continuationCursor: first.ContinuationCursor))
                .AsTask());
        }
        finally
        {
            DeleteRoot(rootPath);
        }
    }

    [Fact]
    public async Task RemoteBatchResumesPartialConflictResultsWithoutAdvancingPastSafeCursor()
    {
        string rootPath = CreateRoot();
        try
        {
            await using CloudFileSystem fileSystem = await StartAsync(rootPath);
            CloudRemoteChange firstChange = CreateMissingMove(
                "partial-1",
                "first.txt",
                "first-old.txt",
                new byte[] { 2 });
            CloudRemoteChange secondChange = CreateMissingMove("partial-2", "second.txt", "second-old.txt");
            CloudRemoteChangeBatch batch = new(
                "partial-batch",
                new byte[] { 1 },
                [firstChange, secondChange],
                new byte[] { 4 });

            CloudRemoteApplyResult first = await fileSystem.ApplyRemoteChangesAsync(
                batch,
                new CloudRemoteApplyOptions { MaximumEntries = 1 });
            Assert.Equal(CloudRemoteBatchStatus.Applying, first.Status);
            Assert.True(first.RequiresRetry);
            Assert.Equal(1, first.AppliedEntryCount);
            Assert.Equal(new byte[] { 2 }, first.SafeCursor.ToArray());
            Assert.Equal(
                [CloudRemoteApplyEntryStatus.Conflict, CloudRemoteApplyEntryStatus.NotProcessed],
                first.Entries.Select(entry => entry.Status));
            Assert.Single(first.ConflictIds);

            CloudRemoteApplyResult resumed = await fileSystem.ApplyRemoteChangesAsync(
                batch,
                new CloudRemoteApplyOptions { MaximumEntries = 1 });
            Assert.Equal(CloudRemoteBatchStatus.Applied, resumed.Status);
            Assert.False(resumed.RequiresRetry);
            Assert.Equal(2, resumed.AppliedEntryCount);
            Assert.Equal(new byte[] { 4 }, resumed.SafeCursor.ToArray());
            Assert.Equal(
                [CloudRemoteApplyEntryStatus.AlreadyApplied, CloudRemoteApplyEntryStatus.Conflict],
                resumed.Entries.Select(entry => entry.Status));
        }
        finally
        {
            DeleteRoot(rootPath);
        }
    }

    [Fact]
    public async Task CorruptAppliedRemoteBatchFailsClosedBeforePublishingCursor()
    {
        string rootPath = CreateRoot();
        try
        {
            ICloudStateStoreFactory factory = InMemoryCloudStateStoreContractTests.CreateFactoryForTesting();
            await using (ICloudStateStore store = await factory.OpenAsync(new CloudStateStoreContext(rootPath)))
            await using (ICloudStateTransaction transaction = await store.BeginTransactionAsync())
            {
                await transaction.RemoteBatches.UpsertAsync(
                    new CloudRemoteBatchState(
                        "corrupt-batch",
                        [1],
                        appliedEntryCount: 1,
                        totalEntryCount: 2,
                        CloudRemoteBatchStatus.Applied,
                        [],
                        DateTimeOffset.UtcNow,
                        ReadOnlyMemory<byte>.Empty,
                        lastAppliedChangeId: "first"));
                await transaction.CommitAsync();
            }

            await using CloudFileSystem fileSystem = CloudFileSystem
                .CreateBuilder(rootPath, new TestRuntime())
                .WithStateStore(factory)
                .Build();
            await fileSystem.StartAsync();

            CloudRemoteChangeBatch batch = new(
                "corrupt-batch",
                new byte[] { 1 },
                [
                    CreateMissingMove("first", "first.txt", "missing-first.txt"),
                    CreateMissingMove("second", "second.txt", "missing-second.txt"),
                ],
                new byte[] { 9 });

            await Assert.ThrowsAsync<InvalidOperationException>(() => fileSystem
                .ApplyRemoteChangesAsync(batch)
                .AsTask());
        }
        finally
        {
            DeleteRoot(rootPath);
        }
    }

    [Fact]
    public async Task ConflictResolverRunsAndDefaultDurableConflictRemainsActionable()
    {
        string rootPath = CreateRoot();
        try
        {
            await using CloudFileSystem fileSystem = await StartAsync(rootPath);
            RecordingResolver resolver = new(
                new CloudRemoteConflictResolution(CloudRemoteConflictDecision.KeepLocal));
            CloudRemoteChange change = CreateMissingMove("resolver-1", "resolved.txt", "missing.txt");
            CloudRemoteApplyResult result = await fileSystem.ApplyRemoteChangesAsync(
                new CloudRemoteChangeBatch(
                    "resolver-batch",
                    Array.Empty<byte>(),
                    [change],
                    new byte[] { 9 }),
                new CloudRemoteApplyOptions { ConflictResolver = resolver });

            Assert.True(resolver.WasCalled);
            Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, Assert.Single(result.Entries).Status);
            Assert.Single(result.ConflictIds);
        }
        finally
        {
            DeleteRoot(rootPath);
        }
    }

    [Fact]
    public async Task RemoteUpsertAgainstUntrackedLocalPathBecomesCollisionConflict()
    {
        string rootPath = CreateRoot();
        try
        {
            string occupiedPath = Path.Combine(rootPath, "occupied.txt");
            await File.WriteAllTextAsync(occupiedPath, "local");
            await using CloudFileSystem fileSystem = await StartAsync(rootPath);
            CloudRemoteChange upsert = new(
                "collision-1",
                CloudRemoteChangeKind.FileUpsert,
                "remote-collision",
                "revision-1",
                CloudItemKind.File,
                "occupied.txt",
                length: 6,
                metadata: CloudPlaceholderMetadata.CreateFileBuilder().Build());

            CloudRemoteApplyResult result = await fileSystem.ApplyRemoteChangesAsync(
                new CloudRemoteChangeBatch(
                    "collision-batch",
                    Array.Empty<byte>(),
                    [upsert],
                    new byte[] { 1 }));

            CloudRemoteApplyEntryResult entry = Assert.Single(result.Entries);
            Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, entry.Status);
            Assert.Equal(CloudRemoteConflictReason.PathCollision, entry.Conflict!.Reason);
        }
        finally
        {
            DeleteRoot(rootPath);
        }
    }

    private static async Task<CloudFileSystem> StartAsync(string rootPath)
    {
        CloudFileSystem fileSystem = CloudFileSystem
            .CreateBuilder(rootPath, new TestRuntime())
            .WithStateStore(InMemoryCloudStateStoreContractTests.CreateFactoryForTesting())
            .Build();
        await fileSystem.StartAsync();
        return fileSystem;
    }

    private static string CreateRoot()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "CfSharp-remote-change-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static CloudRemoteChange CreateMissingMove(
        string changeId,
        string destination,
        string source,
        ReadOnlyMemory<byte> cursorAfter = default) =>
        new(
            changeId,
            CloudRemoteChangeKind.Move,
            "remote-" + changeId,
            "revision-1",
            CloudItemKind.File,
            destination,
            previousRelativePath: source,
            cursorAfter: cursorAfter);

    private static void DeleteRoot(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class FailureStateFactory(ICloudStateStoreFactory inner) : ICloudStateStoreFactory
    {
        internal ICloudStateStore? Store { get; private set; }
        internal IOException Error { get; } = new("Synthetic failure-state commit interruption.");

        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context, CancellationToken cancellationToken = default) =>
            Store = new WrappedStore(await inner.OpenAsync(context, cancellationToken), this);

        private sealed class WrappedStore(ICloudStateStore inner, FailureStateFactory owner) : ICloudStateStore
        {
            public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
                new Transaction(await inner.BeginTransactionAsync(cancellationToken), owner);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }

        private sealed class Transaction(ICloudStateTransaction inner, FailureStateFactory owner) : ICloudStateTransaction
        {
            public ICloudItemStateRepository Items => inner.Items;
            public ICloudCheckpointRepository Checkpoints => inner.Checkpoints;
            public ICloudOperationJournal Operations => inner.Operations;
            public ICloudConflictRepository Conflicts => inner.Conflicts;
            public ICloudRemoteBatchRepository RemoteBatches => inner.RemoteBatches;
            public ICloudEchoSuppressionRepository EchoSuppressions => inner.EchoSuppressions;

            public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
            {
                if ((await RemoteBatches.GetAsync("failure-batch", cancellationToken))?.Status == CloudRemoteBatchStatus.Failed)
                {
                    throw owner.Error;
                }
                await inner.CommitAsync(cancellationToken);
            }
            public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private sealed class TestRuntime : ICloudFileSystemRuntime
    {
        public ICloudFileSystemRuntimeSession Start(
            string syncRootPath,
            SyncRootRegistrationOptions? registration,
            ICloudFileContentProvider? contentProvider,
            ICloudStateStore stateStore) => new TestRuntimeSession();
    }

    private sealed class TestRuntimeSession : ICloudFileSystemRuntimeSession
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StaticRemoteCatalog(CloudRemoteDirectoryPage page)
        : ICloudRemoteDirectoryCatalog
    {
        public bool WasCalled { get; private set; }

        public ValueTask<CloudRemoteDirectoryPage> ReadPageAsync(
            CloudRemoteDirectoryQuery query,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return ValueTask.FromResult(page);
        }
    }

    private sealed class RecordingResolver(CloudRemoteConflictResolution resolution)
        : ICloudRemoteConflictResolver
    {
        public bool WasCalled { get; private set; }

        public ValueTask<CloudRemoteConflictResolution> ResolveAsync(
            CloudRemoteConflict conflict,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return ValueTask.FromResult(resolution);
        }
    }
}
