using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CfSharp.Storage.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class RemoteMoveRecoveryTests
{
    [Theory]
    [InlineData(false, false, "outcome")]
    [InlineData(true, false, "outcome")]
    [InlineData(false, true, "outcome")]
    [InlineData(true, true, "outcome")]
    [InlineData(false, false, "projection")]
    [InlineData(true, false, "projection")]
    [InlineData(false, false, "prepare-after")]
    [InlineData(true, false, "prepare-after")]
    [InlineData(false, false, "projection-after")]
    [InlineData(true, false, "projection-after")]
    [InlineData(false, false, "outcome-after")]
    [InlineData(true, false, "outcome-after")]
    [InlineData(false, false, "outcome-default")]
    [InlineData(true, false, "outcome-default")]
    public async Task OriginalBatchRecoversAcrossSqliteReopenWithoutRenamingOrHydratingAgain(
        bool caseOnly, bool equalRevisions, string boundary)
    {
        await using MoveRoot root = await MoveRoot.OpenAsync(caseOnly, equalRevisions, preserveInSync: boundary != "outcome-default");
        CloudItemSnapshot original = await root.FileSystem.GetFile("Docs/remote.bin").InspectAsync();
        root.Factory.Boundary = boundary == "outcome-default" ? "outcome" : boundary;
        root.Factory.FailuresRemaining = 1;
        if (boundary.StartsWith("outcome", StringComparison.Ordinal))
        {
            Assert.Same(root.Factory.Error, await Assert.ThrowsAsync<IOException>(() => root.ApplyAsync()));
        }
        else
        {
            CloudRemoteApplyResult interrupted = await root.ApplyAsync();
            Assert.True(interrupted.RequiresRetry);
            Assert.Equal(0, interrupted.AppliedEntryCount);
            Assert.Empty(interrupted.SafeCursor.ToArray());
            Assert.Same(root.Factory.Error, interrupted.Entries[0].Error);
        }

        Assert.Equal(1, root.Factory.FailuresObserved);
        await root.RestartAsync();
        bool moved = boundary != "prepare-after";
        if (moved)
        {
            CloudItemSnapshot pending = await root.FileSystem.GetFile(root.Target).InspectAsync();
            Assert.Equal(original.LocalBinding, pending.LocalBinding);
            Assert.Equal(original.PlaceholderIdentity.ToArray(), pending.PlaceholderIdentity.ToArray());
            Assert.Equal(CloudContentAvailability.OnlineOnly, pending.ContentAvailability);
            await AssertExactTargetAsync(root);
        }

        await using (ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync())
        {
            CloudRemoteBatchState progress = (await verify.RemoteBatches.GetAsync(root.Batch.BatchId))!;
            Assert.Equal(boundary == "outcome-after" ? 1 : 0, progress.AppliedEntryCount);
            Assert.Equal(root.Batch.Fingerprint.ToArray(), progress.Fingerprint.ToArray());
            Assert.Empty(await verify.Conflicts.ListAsync());
            Assert.Equal(boundary == "outcome-after" ? 0 : 1,
                (await verify.Checkpoints.ListAsync(RemoteMoveIntent.Prefix)).Count);
        }

        // A no-delete target handle makes a second native rename fail, including a case-only
        // redispatch through the source alias. Recovery only observes and commits evidence.
        using (var noRename = moved ? OpenNoRename(Path.Combine(root.Path, root.Target)) : null)
        {
            Assert.False(noRename?.IsInvalid ?? false);
            CloudRemoteApplyResult replay = await root.ApplyAsync();
            Assert.True(!replay.RequiresRetry, replay.Entries[0].Error?.ToString());
            Assert.Equal(1, replay.AppliedEntryCount);
            Assert.Empty(replay.ConflictIds);
            Assert.Equal(new byte[] { 9 }, replay.SafeCursor.ToArray());
            Assert.Equal(boundary == "outcome-after" ? CloudRemoteApplyEntryStatus.AlreadyApplied : CloudRemoteApplyEntryStatus.Applied,
                replay.Entries[0].Status);
        }

        await root.RestartAsync();
        Assert.Equal(CloudRemoteApplyEntryStatus.AlreadyApplied, (await root.ApplyAsync()).Entries[0].Status);
        Assert.Equal(original.LocalBinding, (await root.FileSystem.GetFile(root.Target).InspectAsync()).LocalBinding);
        await AssertExactTargetAsync(root);
        await using ICloudStateTransaction complete = await root.Factory.Store!.BeginTransactionAsync();
        Assert.Empty(await complete.Checkpoints.ListAsync(RemoteMoveIntent.Prefix));
        Assert.Empty(await complete.Operations.ListAsync(100));
        CloudItemState item = (await complete.Items.GetByItemIdAsync(root.ItemId))!;
        Assert.Equal(root.Target, item.RelativePath);
        Assert.Equal(equalRevisions ? "v1" : "v2", item.RemoteRevision);
        Assert.Equal(0, root.Provider.Reads);
    }

    [Theory]
    [InlineData("outcome")]
    [InlineData("projection")]
    public async Task SecondRecoveryCommitFailureKeepsOriginalEvidenceAndCursor(string boundary)
    {
        await using MoveRoot root = await MoveRoot.OpenAsync(false, false);
        root.Factory.Boundary = boundary;
        root.Factory.FailuresRemaining = 2;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (boundary == "outcome")
            {
                Assert.Same(root.Factory.Error, await Assert.ThrowsAsync<IOException>(() => root.ApplyAsync()));
            }
            else
            {
                CloudRemoteApplyResult failed = await root.ApplyAsync();
                Assert.True(failed.RequiresRetry);
                Assert.Equal(0, failed.AppliedEntryCount);
                Assert.Same(root.Factory.Error, failed.Entries[0].Error);
            }

            await root.RestartAsync();
            await using ICloudStateTransaction transaction = await root.Factory.Store!.BeginTransactionAsync();
            Assert.Single(await transaction.Checkpoints.ListAsync(RemoteMoveIntent.Prefix));
            Assert.Equal(0, (await transaction.RemoteBatches.GetAsync(root.Batch.BatchId))!.AppliedEntryCount);
            Assert.Empty(await transaction.Conflicts.ListAsync());
        }

        Assert.False((await root.ApplyAsync()).RequiresRetry);
        Assert.Equal(2, root.Factory.FailuresObserved);
        Assert.Equal(0, root.Provider.Reads);
    }

    [Fact]
    public async Task MultiEntryRecoveryRetainsCommittedPrefixAndDoesNotProcessLaterEntriesEarly()
    {
        await using MoveRoot root = await MoveRoot.OpenAsync(false, false);
        CloudRemoteChange first = new("first", CloudRemoteChangeKind.FileUpsert, "first", "v1", CloudItemKind.File,
            "first.bin", length: 512, metadata: CloudPlaceholderMetadata.CreateFileBuilder().Build(), cursorAfter: new byte[] { 1 });
        CloudRemoteChange last = new("last", CloudRemoteChangeKind.FileUpsert, "last", "v1", CloudItemKind.File,
            "last.bin", length: 512, metadata: CloudPlaceholderMetadata.CreateFileBuilder().Build());
        root.UseBatch(new(root.Batch.BatchId, ReadOnlyMemory<byte>.Empty, [first, root.Batch.Changes[0], last], new byte[] { 9 }));
        root.Factory.Boundary = "outcome";
        root.Factory.OutcomeCount = 2;
        root.Factory.FailuresRemaining = 1;
        await Assert.ThrowsAsync<IOException>(() => root.ApplyAsync());
        await root.RestartAsync();
        Assert.False(File.Exists(Path.Combine(root.Path, "last.bin")));
        await using (ICloudStateTransaction prefix = await root.Factory.Store!.BeginTransactionAsync())
        {
            CloudRemoteBatchState batch = (await prefix.RemoteBatches.GetAsync(root.Batch.BatchId))!;
            Assert.Equal(1, batch.AppliedEntryCount);
            Assert.Equal(new byte[] { 1 }, batch.Cursor.ToArray());
        }

        CloudRemoteApplyResult recovered = await root.FileSystem.ApplyRemoteChangesAsync(root.Batch, new() { MaximumEntries = 1 });
        Assert.True(recovered.RequiresRetry);
        Assert.Equal(2, recovered.AppliedEntryCount);
        Assert.Equal(CloudRemoteApplyEntryStatus.AlreadyApplied, recovered.Entries[0].Status);
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, recovered.Entries[1].Status);
        Assert.Equal(CloudRemoteApplyEntryStatus.NotProcessed, recovered.Entries[2].Status);
        Assert.False(File.Exists(Path.Combine(root.Path, "last.bin")));
        await root.RestartAsync();
        CloudRemoteApplyResult complete = await root.ApplyAsync();
        Assert.False(complete.RequiresRetry);
        Assert.Equal(3, complete.AppliedEntryCount);
        Assert.Equal(new byte[] { 9 }, complete.SafeCursor.ToArray());
        Assert.True(File.Exists(Path.Combine(root.Path, "last.bin")));
        Assert.Equal(0, root.Provider.Reads);
    }

    [Theory]
    [InlineData("prepare-after")]
    [InlineData("projection-after")]
    public async Task CancellationAfterDurableBoundaryRetainsRecoverableOriginalEntry(string boundary)
    {
        await using MoveRoot root = await MoveRoot.OpenAsync(false, false);
        using CancellationTokenSource cancellation = new();
        root.Factory.Boundary = boundary;
        root.Factory.FailuresRemaining = 1;
        root.Factory.Cancel = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => root.FileSystem.ApplyRemoteChangesAsync(
            root.Batch, cancellationToken: cancellation.Token).AsTask());
        await root.RestartAsync();
        Assert.False((await root.ApplyAsync()).RequiresRetry);
        Assert.Equal(0, root.Provider.Reads);
    }

    [Fact]
    public async Task ExplicitForceMovePreservesItsOriginalDirtyStateAndPendingJournalOnRecovery()
    {
        await using MoveRoot root = await MoveRoot.OpenAsync(false, false);
        await root.FileSystem.GetFile("Docs/remote.bin").UpdatePlaceholderAsync(
            CloudPlaceholderPatch.CreateBuilder().WithInSyncState(false).Build());
        Guid operationId = Guid.NewGuid();
        await using (ICloudStateTransaction seed = await root.Factory.Store!.BeginTransactionAsync())
        {
            await seed.Operations.EnqueueAsync(new(operationId, CloudStateOperationKind.ContentUpdate,
                root.ItemId, new byte[] { 6, 7 }, DateTimeOffset.UtcNow));
            await seed.CommitAsync();
        }

        CloudRemoteApplyOptions policy = new() { PreserveUnsynchronizedLocalContent = false };
        root.Factory.Boundary = "outcome";
        root.Factory.FailuresRemaining = 1;
        await Assert.ThrowsAsync<IOException>(() => root.FileSystem.ApplyRemoteChangesAsync(root.Batch, policy).AsTask());
        await root.RestartAsync();
        CloudRemoteApplyResult recovered = await root.FileSystem.ApplyRemoteChangesAsync(root.Batch, policy);
        Assert.False(recovered.RequiresRetry);
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, recovered.Entries[0].Status);
        Assert.Equal(CloudSynchronizationState.NotInSync, (await root.FileSystem.GetFile(root.Target).InspectAsync()).SynchronizationState);
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        Assert.Equal(new byte[] { 6, 7 }, (await verify.Operations.GetAsync(operationId))!.Payload.ToArray());
        Assert.Equal(0, root.Provider.Reads);
    }

    [Theory]
    [InlineData("replacement")]
    [InlineData("source-occupant")]
    [InlineData("parent-replaced")]
    [InlineData("pending-edit")]
    [InlineData("dirty")]
    [InlineData("policy")]
    public async Task ChangedEvidenceRemainsPendingWithoutInventingAConflictOrOverwritingLaterWork(string change)
    {
        await using MoveRoot root = await MoveRoot.OpenAsync(false, false);
        root.Factory.Boundary = "outcome";
        root.Factory.FailuresRemaining = 1;
        await Assert.ThrowsAsync<IOException>(() => root.ApplyAsync());
        await root.RestartAsync();
        Guid? journalId = null;
        switch (change)
        {
            case "replacement":
                File.Move(Path.Combine(root.Path, root.Target), Path.Combine(root.Path, "original.bin"));
                // Copying the exact identity still cannot adopt a different native object.
                await root.FileSystem.GetDirectory("Docs/right").CreatePlaceholderAsync(
                    CloudFilePlaceholderSpec.CreateBuilder("remote.bin", root.Identity, 512).WithInSyncState(true).Build());
                break;
            case "source-occupant":
                await File.WriteAllTextAsync(Path.Combine(root.Path, "Docs/remote.bin"), "retain me");
                break;
            case "parent-replaced":
                Directory.Move(Path.Combine(root.Path, "Docs/right"), Path.Combine(root.Path, "Docs/retained"));
                Directory.CreateDirectory(Path.Combine(root.Path, "Docs/right"));
                break;
            case "pending-edit":
                await using (ICloudStateTransaction edit = await root.Factory.Store!.BeginTransactionAsync())
                {
                    journalId = Guid.NewGuid();
                    await edit.Operations.EnqueueAsync(new(journalId.Value, CloudStateOperationKind.ContentUpdate,
                        root.ItemId, new byte[] { 4, 2 }, DateTimeOffset.UtcNow));
                    await edit.CommitAsync();
                }

                break;
            case "dirty":
                await root.FileSystem.GetFile(root.Target).UpdatePlaceholderAsync(
                    CloudPlaceholderPatch.CreateBuilder().WithInSyncState(false).Build());
                break;
        }

        if (change == "policy")
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => root.FileSystem.ApplyRemoteChangesAsync(root.Batch,
                new CloudRemoteApplyOptions { SuppressLocalEcho = false }).AsTask());
        }
        else
        {
            CloudRemoteApplyResult unresolved = await root.ApplyAsync();
            Assert.True(unresolved.RequiresRetry);
            Assert.Equal(0, unresolved.AppliedEntryCount);
            Assert.Equal(CloudRemoteApplyEntryStatus.Failed, unresolved.Entries[0].Status);
            Assert.NotNull(unresolved.Entries[0].Error);
            Assert.Empty(unresolved.ConflictIds);
            Assert.Empty(unresolved.SafeCursor.ToArray());
        }
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        Assert.Single(await verify.Checkpoints.ListAsync(RemoteMoveIntent.Prefix));
        Assert.Empty(await verify.Conflicts.ListAsync());
        if (journalId is Guid id)
        {
            Assert.Equal(new byte[] { 4, 2 }, (await verify.Operations.GetAsync(id))!.Payload.ToArray());
        }

        if (change == "source-occupant")
        {
            Assert.Equal("retain me", await File.ReadAllTextAsync(Path.Combine(root.Path, "Docs/remote.bin")));
        }

        Assert.Equal(0, root.Provider.Reads);
    }

    private static async Task AssertExactTargetAsync(MoveRoot root)
    {
        List<string> children = [];
        await foreach (CloudItem item in root.FileSystem.GetDirectory(Path.GetDirectoryName(root.Target)!).EnumerateLocalChildrenAsync())
        {
            children.Add(item.RelativePath);
        }

        Assert.Contains(root.Target, children, StringComparer.Ordinal);
    }

    private static SafeFileHandle OpenNoRename(string path) => CreateFile(path, 0x80, 3, 0, 3, 0x02200000, 0);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    private sealed class MoveRoot(string area, CloudFileSystem fileSystem, MoveFactory factory,
        RejectingProvider provider, CloudPlaceholderIdentity identity, CloudRemoteChangeBatch batch, string target) : IAsyncDisposable
    {
        internal CloudFileSystem FileSystem => fileSystem;
        internal string Path => fileSystem.SyncRootPath;
        internal MoveFactory Factory => factory;
        internal RejectingProvider Provider => provider;
        internal CloudPlaceholderIdentity Identity => identity;
        internal Guid ItemId => identity.ItemId;
        internal CloudRemoteChangeBatch Batch => batch;
        internal void UseBatch(CloudRemoteChangeBatch value) => batch = value;
        internal string Target => target;
        internal Task<CloudRemoteApplyResult> ApplyAsync() => fileSystem.ApplyRemoteChangesAsync(batch).AsTask();

        internal async Task RestartAsync()
        {
            string root = Path;
            await fileSystem.DisposeAsync();
            fileSystem = CloudFileSystem.CreateBuilder(root).WithStateStore(factory).WithContentProvider(provider).Build();
            await fileSystem.StartAsync();
        }

        internal static async Task<MoveRoot> OpenAsync(bool caseOnly, bool equalRevisions, bool preserveInSync = true)
        {
            string area = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CfSharp-remote-move", Guid.NewGuid().ToString("N"));
            string root = System.IO.Path.Combine(area, "root");
            Directory.CreateDirectory(root);
            MoveFactory factory = new(new SqliteCloudStateStoreFactory(System.IO.Path.Combine(area, "state.db")));
            RejectingProvider provider = new();
            CloudFileSystem fileSystem = CloudFileSystem.CreateBuilder(root).WithStateStore(factory)
                .WithRegistration(SyncRootRegistrationOptions.CreateBuilder("CfSharp Remote Move Test", "1.0")
                    .WithProviderId(Guid.NewGuid()).WithRootMarkedInSync()
                    .WithInSyncPolicy(preserveInSync ? CloudInSyncPolicy.TrackAll | CloudInSyncPolicy.PreserveForSyncEngine : CloudInSyncPolicy.TrackAll)
                    .Build()).WithContentProvider(provider).Build();
            bool registered = false;
            try
            {
                await fileSystem.StartAsync();
                registered = true;
                Directory.CreateDirectory(System.IO.Path.Combine(root, "Docs/right"));
                CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "remote-file", "v1");
                await fileSystem.GetDirectory("Docs").CreatePlaceholderAsync(
                    CloudFilePlaceholderSpec.CreateBuilder("remote.bin", identity, 512).WithInSyncState(true).Build());
                string target = caseOnly ? "Docs\\REMOTE.bin" : "Docs\\right\\remote.bin";
                CloudRemoteChange move = new("move", CloudRemoteChangeKind.Move, identity.RemoteId,
                    equalRevisions ? "v1" : "v2", CloudItemKind.File, target, identity.ItemId,
                    previousRemoteRevision: "v1", previousRelativePath: "Docs/remote.bin");
                CloudRemoteChangeBatch batch = new("stable-original-batch", ReadOnlyMemory<byte>.Empty, [move], new byte[] { 9 });
                return new(area, fileSystem, factory, provider, identity, batch, target);
            }
            catch
            {
                await fileSystem.DisposeAsync();
                if (registered)
                {
                    CloudSyncRoot.Open(root).Unregister();
                }

                Directory.Delete(area, recursive: true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await fileSystem.DisposeAsync();
            CloudSyncRoot.Open(Path).Unregister();
            Directory.Delete(area, recursive: true);
        }
    }

    private sealed class RejectingProvider : ICloudFileContentProvider
    {
        internal int Reads { get; private set; }
        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken cancellationToken)
        {
            Reads++;
            throw new InvalidOperationException("Remote move recovery must never hydrate content.");
        }
    }

    private sealed class MoveFactory(ICloudStateStoreFactory inner) : ICloudStateStoreFactory
    {
        internal ICloudStateStore? Store { get; private set; }
        internal string Boundary { get; set; } = string.Empty;
        internal int FailuresRemaining { get; set; }
        internal int OutcomeCount { get; set; } = 1;
        internal Action? Cancel { get; set; }
        internal int FailuresObserved { get; private set; }
        internal IOException Error { get; } = new("Synthetic remote move commit interruption.");

        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context, CancellationToken cancellationToken = default) =>
            Store = new WrappedStore(await inner.OpenAsync(context, cancellationToken), this);

        private sealed class WrappedStore(ICloudStateStore store, MoveFactory owner) : ICloudStateStore
        {
            public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
                new Transaction(await store.BeginTransactionAsync(cancellationToken), owner);
            public ValueTask DisposeAsync() => store.DisposeAsync();
        }

        private sealed class Transaction(ICloudStateTransaction transaction, MoveFactory owner) : ICloudStateTransaction
        {
            public ICloudItemStateRepository Items => transaction.Items;
            public ICloudCheckpointRepository Checkpoints => transaction.Checkpoints;
            public ICloudOperationJournal Operations => transaction.Operations;
            public ICloudConflictRepository Conflicts => transaction.Conflicts;
            public ICloudRemoteBatchRepository RemoteBatches => transaction.RemoteBatches;
            public ICloudEchoSuppressionRepository EchoSuppressions => transaction.EchoSuppressions;

            public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
            {
                bool inject = false;
                if (owner.FailuresRemaining > 0)
                {
                    CloudRemoteBatchState? batch = await RemoteBatches.GetAsync("stable-original-batch", cancellationToken);
                    IReadOnlyList<CloudStateCheckpoint> preparations = await Checkpoints.ListAsync(RemoteMoveIntent.Prefix, cancellationToken);
                    bool prepared = preparations.Count != 0;
                    bool projected = prepared && RemoteMoveIntent.Decode(preparations[0].Value).ProjectedAtTicks.HasValue;
                    inject = owner.Boundary.StartsWith("outcome", StringComparison.Ordinal) ? batch?.AppliedEntryCount == owner.OutcomeCount :
                        owner.Boundary.StartsWith("projection", StringComparison.Ordinal) ? projected : prepared;
                    if (inject)
                    {
                        owner.FailuresRemaining--;
                        owner.FailuresObserved++;
                    }
                }

                if (inject && owner.Cancel is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    owner.Cancel();
                    return;
                }

                if (inject && !owner.Boundary.EndsWith("-after", StringComparison.Ordinal))
                {
                    throw owner.Error;
                }

                await transaction.CommitAsync(cancellationToken);
                if (inject)
                {
                    throw owner.Error;
                }
            }

            public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => transaction.DisposeAsync();
        }
    }
}
