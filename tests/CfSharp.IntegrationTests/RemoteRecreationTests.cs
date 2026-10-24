using System.Runtime.Versioning;
using System.Security.Principal;

using CfSharp.Storage.Sqlite;
using Xunit.Abstractions;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
[Trait("Category", "RemoteRecreation")]
public sealed partial class RemoteRecreationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitSuccessorAtRetiredPathPreservesBothGenerationsAcrossReopen(bool directory)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        bool administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        string architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        output.WriteLine($"Native producer: architecture={architecture}; OS={Environment.OSVersion.VersionString}; SID={identity.User?.Value}; administrator={administrator}.");
        if (Environment.GetEnvironmentVariable("CFSHARP_EXPECTED_TEST_ARCHITECTURE") is string expectedArchitecture)
        {
            Assert.Equal(expectedArchitecture, architecture, ignoreCase: true);
        }
        if (Environment.GetEnvironmentVariable("CFSHARP_EXPECTED_TEST_SID") is string expectedSid)
        {
            Assert.Equal(expectedSid, identity.User?.Value);
            Assert.False(administrator);
        }
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        await ReopenPreviewSevenLayoutAsync(root);
        CloudRemoteApplyResult result = await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch);
        AssertApplied(result, root.SuccessorBatch);
        await root.AssertGenerationsAsync();
        await root.RestartAsync();
        foreach (CloudRemoteChangeBatch batch in new[] { root.OriginalBatch, root.DeleteBatch, root.SuccessorBatch })
        {
            CloudRemoteApplyResult replay = await root.FileSystem.ApplyRemoteChangesAsync(batch,
                batch == root.DeleteBatch ? AuthorizedDelete : null);
            Assert.False(replay.RequiresRetry);
            Assert.Equal(CloudRemoteApplyEntryStatus.AlreadyApplied, Assert.Single(replay.Entries).Status);
            Assert.Equal(batch.FinalCursor.ToArray(), replay.SafeCursor.ToArray());
            await root.AssertGenerationsAsync();
        }
    }

    private static readonly CloudRemoteApplyOptions AuthorizedDelete = new()
    {
        PreserveUnsynchronizedLocalContent = false,
    };

    private static void AssertApplied(CloudRemoteApplyResult result, CloudRemoteChangeBatch batch)
    {
        Assert.False(result.RequiresRetry);
        Assert.Empty(result.ConflictIds);
        Assert.Equal(1, result.AppliedEntryCount);
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single(result.Entries).Status);
        Assert.Equal(batch.FinalCursor.ToArray(), result.SafeCursor.ToArray());
    }

    private sealed class RecreationRoot : IAsyncDisposable
    {
        private readonly string _area;
        private readonly RejectingProvider _provider = new();
        internal RecordingFactory Factory { get; }
        internal CloudFileSystem FileSystem { get; private set; }
        internal CloudItemKind Kind { get; }
        internal string Target { get; } = "Docs\\recreated";
        internal Guid OriginalId { get; } = Guid.NewGuid();
        internal Guid SuccessorId { get; } = Guid.NewGuid();
        internal string Database => Path.Combine(_area, "state.db");
        internal int Reads => _provider.Reads;
        internal CloudRemoteChangeBatch OriginalBatch => Birth("original", OriginalId, "original-remote", "v1", 4, 0, 1);
        internal CloudRemoteChangeBatch DeleteBatch => new("delete", new byte[] { 1 },
            [new("delete", CloudRemoteChangeKind.Delete, "original-remote", "v2", Kind, Target,
                OriginalId, previousRemoteRevision: "v1")], new byte[] { 2 });
        internal CloudRemoteChangeBatch SuccessorBatch => Birth("successor", SuccessorId, "successor-remote", "v3", 7, 2, 3);

        private RecreationRoot(string area, bool directory, bool allowHardLinks)
        {
            _area = area;
            Kind = directory ? CloudItemKind.Directory : CloudItemKind.File;
            Factory = new(new SqliteCloudStateStoreFactory(Path.Combine(area, "state.db")));
            FileSystem = CloudFileSystem.CreateBuilder(Path.Combine(area, "root")).WithStateStore(Factory)
                .WithRegistration(SyncRootRegistrationOptions.CreateBuilder("CfSharp Recreation Test", "1.0")
                    .WithProviderId(Guid.NewGuid()).WithRootMarkedInSync().AllowHardLinks(allowHardLinks).Build())
                .WithContentProvider(_provider).Build();
        }

        internal CloudRemoteChangeBatch Birth(string batchId, Guid? itemId, string remoteId, string revision,
            long length, byte initial, byte final, string? path = null) =>
            new(batchId, new byte[] { initial }, [new(batchId,
                Kind == CloudItemKind.Directory ? CloudRemoteChangeKind.DirectoryUpsert : CloudRemoteChangeKind.FileUpsert,
                remoteId, revision, Kind, path ?? Target, itemId, length: Kind == CloudItemKind.File ? length : null,
                metadata: Kind == CloudItemKind.File ? CloudPlaceholderMetadata.CreateFileBuilder().Build() :
                    CloudPlaceholderMetadata.CreateDirectoryBuilder().Build())], new byte[] { final });

        internal CloudItem Item => Kind == CloudItemKind.File ? FileSystem.GetFile(Target) : FileSystem.GetDirectory(Target);
        internal async Task<CloudPlaceholderIdentity> InspectNativeIdentityAsync() =>
            CloudPlaceholderIdentity.Decode((await Item.InspectAsync()).PlaceholderIdentity.Span);

        internal static async Task<RecreationRoot> OpenAsync(bool directory, bool interruptDelete = false, bool allowHardLinks = false)
        {
            string area = Path.Combine(Path.GetTempPath(), "CfSharp-recreation", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(area, "root"));
            RecreationRoot root = new(area, directory, allowHardLinks);
            try
            {
                await root.FileSystem.StartAsync();
                await root.FileSystem.GetDirectory(string.Empty).CreatePlaceholderAsync(CloudDirectoryPlaceholderSpec.CreateBuilder(
                    "Docs", new CloudPlaceholderIdentity(Guid.NewGuid(), "parent", "p1")).WithInSyncState(true)
                    .WithPopulationState(CloudDirectoryPopulationState.Complete).Build());
                AssertApplied(await root.FileSystem.ApplyRemoteChangesAsync(root.OriginalBatch), root.OriginalBatch);
                Assert.Equal(root.OriginalId, (await root.InspectNativeIdentityAsync()).ItemId);
                if (interruptDelete)
                {
                    root.Factory.Boundary = "delete-outcome-before";
                    await Assert.ThrowsAsync<IOException>(() => root.FileSystem.ApplyRemoteChangesAsync(root.DeleteBatch, AuthorizedDelete).AsTask());
                }
                else { AssertApplied(await root.FileSystem.ApplyRemoteChangesAsync(root.DeleteBatch, AuthorizedDelete), root.DeleteBatch); }
                Assert.False(File.Exists(Path.Combine(root.FileSystem.SyncRootPath, root.Target)) ||
                    Directory.Exists(Path.Combine(root.FileSystem.SyncRootPath, root.Target)));
                await root.RestartAsync();
                await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
                Assert.True((await verify.Items.GetByItemIdAsync(root.OriginalId))!.IsTombstone);
                return root;
            }
            catch
            {
                await root.DisposeAsync();
                throw;
            }
        }

        internal async Task RestartAsync()
        {
            string path = FileSystem.SyncRootPath;
            await FileSystem.DisposeAsync();
            FileSystem = CloudFileSystem.CreateBuilder(path).WithStateStore(Factory).WithContentProvider(_provider).Build();
            await FileSystem.StartAsync();
        }

        internal async Task AssertGenerationsAsync()
        {
            CloudItemSnapshot native = await Item.InspectAsync();
            Assert.Equal(SuccessorId, native.ItemId);
            CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Decode(native.PlaceholderIdentity.Span);
            Assert.Equal(SuccessorId, identity.ItemId);
            Assert.Equal("successor-remote", identity.RemoteId);
            Assert.Equal("v3", identity.RemoteRevision);
            Assert.Equal(Kind, native.Kind);
            if (Kind == CloudItemKind.File) { Assert.Equal(7, native.Length); }
            await using ICloudStateTransaction verify = await Factory.Store!.BeginTransactionAsync();
            CloudItemState original = (await verify.Items.GetByItemIdAsync(OriginalId))!;
            Assert.True(original.IsTombstone);
            Assert.Equal("original-remote", original.RemoteId);
            Assert.Equal("v2", original.RemoteRevision);
            Assert.Equal(Target, original.RelativePath);
            CloudItemState successor = (await verify.Items.GetByItemIdAsync(SuccessorId))!;
            Assert.False(successor.IsTombstone);
            Assert.Equal("successor-remote", successor.RemoteId);
            Assert.Equal("v3", successor.RemoteRevision);
            Assert.Equal(Kind, successor.Kind);
            Assert.Equal(SuccessorId, (await verify.Items.GetByRelativePathAsync(SuccessorBatch.Changes[0].RelativePath))!.ItemId);
            Assert.Equal(DeleteBatch.Fingerprint.ToArray(), (await verify.RemoteBatches.GetAsync(DeleteBatch.BatchId))!.Fingerprint.ToArray());
            Assert.Equal(0, _provider.Reads);
        }

        public async ValueTask DisposeAsync()
        {
            Factory.Boundary = null;
            Factory.AfterProjection = null;
            string path = FileSystem.SyncRootPath;
            try
            {
                if (File.Exists(Path.Combine(path, Target)) || Directory.Exists(Path.Combine(path, Target)))
                {
                    CloudItem target = Directory.Exists(Path.Combine(path, Target)) ? FileSystem.GetDirectory(Target) : FileSystem.GetFile(Target);
                    await target.DeleteAsync();
                }
                if (Directory.Exists(Path.Combine(path, "Docs"))) { await FileSystem.GetDirectory("Docs").DeleteAsync(); }
            }
            finally
            {
                await FileSystem.DisposeAsync();
                CloudSyncRoot.Open(path).Unregister();
                Directory.Delete(_area, recursive: true);
            }
        }
    }

    private sealed class RejectingProvider : ICloudFileContentProvider
    {
        internal int Reads { get; private set; }
        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken cancellationToken)
        {
            Reads++;
            throw new InvalidOperationException("Remote recreation must not read content.");
        }
    }

    private sealed class RecordingFactory(ICloudStateStoreFactory inner) : ICloudStateStoreFactory
    {
        internal ICloudStateStore? Store { get; private set; }
        internal string? Boundary { get; set; }
        internal Action? AtBoundary { get; set; }
        internal Action? AfterProjection { get; set; }
        internal bool ThrowAtBoundary { get; set; } = true;
        internal int Faults { get; private set; }
        internal IOException Error { get; } = new("Injected successor creation interruption.");
        private Action? _nextTransaction;
        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context,
            CancellationToken cancellationToken = default) => Store = new StoreWrapper(await inner.OpenAsync(context, cancellationToken), this);

        private sealed class StoreWrapper(ICloudStateStore store, RecordingFactory owner) : ICloudStateStore
        {
            public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
            {
                ICloudStateTransaction transaction = await store.BeginTransactionAsync(cancellationToken);
                try
                {
                    Action? action = owner._nextTransaction;
                    owner._nextTransaction = null;
                    action?.Invoke();
                    return new Transaction(transaction, owner);
                }
                catch { await transaction.DisposeAsync(); throw; }
            }
            public ValueTask DisposeAsync() => store.DisposeAsync();
        }

        private sealed class Transaction(ICloudStateTransaction transaction, RecordingFactory owner) : ICloudStateTransaction
        {
            public ICloudItemStateRepository Items => transaction.Items;
            public ICloudCheckpointRepository Checkpoints => transaction.Checkpoints;
            public ICloudOperationJournal Operations => transaction.Operations;
            public ICloudConflictRepository Conflicts => transaction.Conflicts;
            public ICloudRemoteBatchRepository RemoteBatches => transaction.RemoteBatches;
            public ICloudEchoSuppressionRepository EchoSuppressions => transaction.EchoSuppressions;
            public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
            {
                RemoteCreationIntent? intent = (await Checkpoints.ListAsync(RemoteCreationIntent.Prefix, cancellationToken))
                    .Select(row => RemoteCreationIntent.Decode(row.Value)).SingleOrDefault();
                CloudRemoteBatchState? batch = await RemoteBatches.GetAsync("successor", cancellationToken);
                bool match = owner.Boundary is string boundary && (boundary == "delete-outcome-before"
                    ? (await RemoteBatches.GetAsync("delete", cancellationToken))?.AppliedEntryCount == 1
                    : boundary.StartsWith("outcome", StringComparison.Ordinal)
                    ? batch?.AppliedEntryCount == 1 : boundary.StartsWith("projection", StringComparison.Ordinal)
                        ? intent?.Committed == true : boundary.StartsWith("native", StringComparison.Ordinal)
                            ? intent?.CreatedObject is not null && !intent.Committed : intent is not null && intent.CreatedObject is null);
                string? selected = match ? owner.Boundary : null;
                if (match)
                {
                    owner.Boundary = null;
                    owner.Faults++;
                    if (!selected!.EndsWith("-after", StringComparison.Ordinal) && owner.ThrowAtBoundary) { throw owner.Error; }
                }
                await transaction.CommitAsync(cancellationToken);
                if (intent?.Committed == true && owner.AfterProjection is Action after)
                {
                    owner._nextTransaction = after;
                    owner.AfterProjection = null;
                }
                if (match)
                {
                    owner.AtBoundary?.Invoke();
                    if (owner.ThrowAtBoundary) { throw owner.Error; }
                }
            }
            public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => transaction.DisposeAsync();
        }
    }
}
