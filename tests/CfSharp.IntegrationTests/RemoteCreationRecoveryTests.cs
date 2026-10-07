using System.Runtime.Versioning;

using CfSharp.Storage.Sqlite;

namespace CfSharp.IntegrationTests;

public sealed class RemoteCreationRecoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [SupportedOSPlatform("windows10.0.16299")]
    public async Task NewDirectoryBatchAndFailedCreationCommitReplayWithStableIdentities(bool failCommit, bool files)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return;
        }

        string area = Path.Combine(Path.GetTempPath(), "CfSharp-create-recovery", Guid.NewGuid().ToString("N"));
        string root = Path.Combine(area, "root");
        Directory.CreateDirectory(root);
        FailingFactory factory = new(new SqliteCloudStateStoreFactory(Path.Combine(area, "state.db")));
        bool registered = false;
        try
        {
            await using CloudFileSystem fileSystem = CloudFileSystem.CreateBuilder(root).WithStateStore(factory)
                .WithRegistration(SyncRootRegistrationOptions.CreateBuilder("CfSharp Creation Test", "1.0")
                    .WithProviderId(Guid.NewGuid()).WithRootMarkedInSync().Build())
                .WithContentProvider(new EmptyProvider()).Build();
            await fileSystem.StartAsync();
            registered = true;
            CloudRemoteChangeKind kind = files ? CloudRemoteChangeKind.FileUpsert : CloudRemoteChangeKind.DirectoryUpsert;
            CloudItemKind itemKind = files ? CloudItemKind.File : CloudItemKind.Directory;
            CloudPlaceholderMetadata metadata = files ? CloudPlaceholderMetadata.CreateFileBuilder().Build()
                : CloudPlaceholderMetadata.CreateDirectoryBuilder().Build();
            CloudRemoteChange first = new("one", kind, "one", "r1",
                itemKind, "one", length: files ? 0 : null, metadata: metadata,
                cursorAfter: new byte[] { 1 });
            CloudRemoteChange second = new("two", kind, "two", "r1",
                itemKind, "two", length: files ? 0 : null, metadata: metadata);
            CloudRemoteChangeBatch batch = new("batch", Array.Empty<byte>(), [first, second], new byte[] { 2 });
            factory.FailCreationCommit = failCommit;
            CloudRemoteApplyResult initial = await fileSystem.ApplyRemoteChangesAsync(batch,
                new CloudRemoteApplyOptions { MaximumEntries = 1 });
            CloudItem firstItem = files ? fileSystem.GetFile("one") : fileSystem.GetDirectory("one");
            Guid nativeId = CloudPlaceholderIdentity.Decode(
                (await firstItem.InspectAsync()).PlaceholderIdentity.Span).ItemId;
            if (failCommit)
            {
                Assert.Equal(CloudRemoteApplyEntryStatus.Failed, initial.Entries[0].Status);
                Assert.Equal(0, initial.AppliedEntryCount);
                Assert.Empty(initial.SafeCursor.ToArray());
                await using ICloudStateTransaction transaction = await factory.Store!.BeginTransactionAsync();
                Assert.Null(await transaction.Items.GetByRemoteIdAsync("one"));
                Assert.Single(await transaction.Checkpoints.ListAsync(RemoteCreationIntent.Prefix));
            }
            else
            {
                Assert.Equal(CloudRemoteApplyEntryStatus.Applied, initial.Entries[0].Status);
                Assert.Equal(1, initial.AppliedEntryCount);
                Assert.Equal(new byte[] { 1 }, initial.SafeCursor.ToArray());
            }

            FakeSource source = new();
            await using CloudLocalChangeFeed feed = CloudLocalChangeFeed.CreateForTesting(root, factory.Store!,
                new CloudLocalChangeFeedOptions(), source);
            await feed.StartAsync();
            if (failCommit)
            {
                await source.EmitAsync(new(LocalChangeSourceAction.Created, "one"));
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
                while (true)
                {
                    await using ICloudStateTransaction transaction = await factory.Store!.BeginTransactionAsync(timeout.Token);
                    if ((await transaction.Checkpoints.ListAsync(RemoteCreationIntent.ObservationsPrefix, timeout.Token)).Count == 1)
                    {
                        break;
                    }

                    await transaction.RollbackAsync(timeout.Token);
                    await Task.Delay(10, timeout.Token);
                }

                Assert.True((await feed.ReadBatchAsync()).RequiresFullRescan);
                CloudLocalChangeScan scan = await feed.BeginScanAsync();
                Assert.True(scan.RequiresFullRescan);
                CloudLocalChangePage page = await feed.ReadPageAsync(scan, 0, 4);
                Assert.True(page.RequiresFullRescan);
                Assert.Empty(page.Changes);
                Assert.Equal(0, page.LastScannedSequence);
                await Assert.ThrowsAsync<InvalidOperationException>(() => feed.AcknowledgeFullRescanAsync().AsTask());
            }

            CloudRemoteApplyResult resumed = await fileSystem.ApplyRemoteChangesAsync(batch);
            Assert.False(resumed.RequiresRetry);
            Assert.Equal(2, resumed.AppliedEntryCount);
            Assert.Equal(new byte[] { 2 }, resumed.SafeCursor.ToArray());
            Assert.Equal(nativeId, (await firstItem.InspectAsync()).ItemId);
            Assert.True(files ? File.Exists(Path.Combine(root, "two")) : Directory.Exists(Path.Combine(root, "two")));
            if (failCommit)
            {
                await feed.AcknowledgeFullRescanAsync();
            }

            await source.EmitAsync(new(LocalChangeSourceAction.Modified, "one"));
            using (CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10)))
            {
                while (true)
                {
                    await using ICloudStateTransaction observed = await factory.Store!.BeginTransactionAsync(timeout.Token);
                    CloudStateCheckpoint? checkpoint = await observed.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName, timeout.Token);
                    if (checkpoint is not null && LocalChangeCheckpoint.Decode(checkpoint.Value).Observation >= (failCommit ? 2 : 1))
                    {
                        break;
                    }

                    await observed.RollbackAsync(timeout.Token);
                    await Task.Delay(10, timeout.Token);
                }
            }

            await using ICloudStateTransaction complete = await factory.Store!.BeginTransactionAsync();
            Assert.Empty(await complete.Checkpoints.ListAsync(RemoteCreationIntent.Prefix));
            Assert.Empty(await complete.Operations.ListAsync(10));
            Assert.Equal(2, (await complete.Items.ListSubtreeAsync(string.Empty)).Count);
            Assert.All(await complete.EchoSuppressions.ListActiveAsync(DateTimeOffset.UtcNow),
                suppression => Assert.NotNull(suppression.ItemId));
            if (!files)
            {
                CloudDirectoryProvenance provenance = CloudDirectoryProvenance.Decode((await complete.Checkpoints.GetAsync(
                    CloudDirectoryProvenance.BindingName(nativeId)))!.Value);
                Assert.Equal(nativeId, provenance.RootItemId);
                Assert.Equal("one", provenance.RelativePath);
                Assert.Equal(CloudPlaceholderIdentity.Decode(provenance.Identity).ItemId, nativeId);
            }
        }
        finally
        {
            if (registered)
            {
                CloudSyncRoot.Open(root).Unregister();
            }

            Directory.Delete(area, recursive: true);
        }
    }

    private sealed class EmptyProvider : ICloudFileContentProvider
    {
        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream>(new MemoryStream());
    }

    private sealed class FakeSource : ILocalChangeSource
    {
        private Func<LocalChangeSourceEvent, ValueTask>? _handler;
        public Task StartAsync(Func<LocalChangeSourceEvent, ValueTask> eventHandler, CancellationToken cancellationToken)
        {
            _handler = eventHandler;
            return Task.CompletedTask;
        }

        internal ValueTask EmitAsync(LocalChangeSourceEvent value) => _handler!(value);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FailingFactory(ICloudStateStoreFactory inner) : ICloudStateStoreFactory
    {
        internal bool FailCreationCommit { get; set; }
        internal ICloudStateStore? Store { get; private set; }
        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context,
            CancellationToken cancellationToken = default) =>
            Store = new WrappedStore(await inner.OpenAsync(context, cancellationToken), this);

        private sealed class WrappedStore(ICloudStateStore inner, FailingFactory owner) : ICloudStateStore
        {
            public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
                new Transaction(await inner.BeginTransactionAsync(cancellationToken), owner);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }

        private sealed class Transaction(ICloudStateTransaction inner, FailingFactory owner) : ICloudStateTransaction
        {
            public ICloudItemStateRepository Items => inner.Items;
            public ICloudCheckpointRepository Checkpoints => inner.Checkpoints;
            public ICloudOperationJournal Operations => inner.Operations;
            public ICloudConflictRepository Conflicts => inner.Conflicts;
            public ICloudRemoteBatchRepository RemoteBatches => inner.RemoteBatches;
            public ICloudEchoSuppressionRepository EchoSuppressions => inner.EchoSuppressions;
            public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
            {
                if (owner.FailCreationCommit && (await Checkpoints.ListAsync(RemoteCreationIntent.Prefix, cancellationToken))
                    .Any(value => RemoteCreationIntent.Decode(value.Value).Committed))
                {
                    owner.FailCreationCommit = false;
                    throw new IOException("Injected native-success/state-commit failure.");
                }

                await inner.CommitAsync(cancellationToken);
            }

            public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
