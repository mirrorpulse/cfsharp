using System.Runtime.Versioning;

using CfSharp.Storage.Sqlite;

namespace CfSharp.IntegrationTests;

public sealed class RemoteDirectoryDeletionTests
{
    [Theory]
    [InlineData("ordinary", true)]
    [InlineData("dirty", true)]
    [InlineData("pending", true)]
    [InlineData("clean", true)]
    [InlineData("ordinary", false)]
    [SupportedOSPlatform("windows10.0.16299")]
    public async Task PreservationDefersEveryTreeUntilDeletionIsAuthorized(string childState, bool preserve)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return;
        }

        string area = Path.Combine(Path.GetTempPath(), "CfSharp-remote-delete", Guid.NewGuid().ToString("N"));
        string root = Path.Combine(area, "root");
        Directory.CreateDirectory(root);
        CapturingFactory factory = new(new SqliteCloudStateStoreFactory(Path.Combine(area, "state.db")));
        bool registered = false;
        try
        {
            await using CloudFileSystem fileSystem = CloudFileSystem.CreateBuilder(root)
                .WithStateStore(factory)
                .WithContentProvider(new EmptyProvider())
                .WithRegistration(SyncRootRegistrationOptions.CreateBuilder("CfSharp Delete Test", "1.0")
                    .WithProviderId(Guid.NewGuid()).WithRootMarkedInSync().Build()).Build();
            await fileSystem.StartAsync();
            registered = true;
            await fileSystem.Root.CreatePlaceholderAsync(CloudDirectoryPlaceholderSpec.CreateBuilder(
                "parent", new CloudPlaceholderIdentity(Guid.NewGuid(), "parent", "r1")).WithInSyncState(true).WithPopulationState(CloudDirectoryPopulationState.Complete).Build());
            string childPath = Path.Combine(root, "parent", "child.txt");
            await File.WriteAllTextAsync(childPath, "local content");
            Guid childId = Guid.NewGuid();
            if (childState != "ordinary")
            {
                CloudFile child = fileSystem.GetFile("parent/child.txt");
                await child.ConvertToPlaceholderAsync(new CloudPlaceholderIdentity(childId, "child", "r1"));
                await child.SetInSyncAsync(childState != "dirty");
            }

            if (childState == "pending")
            {
                await using ICloudStateTransaction transaction = await factory.Store!.BeginTransactionAsync();
                await transaction.Operations.EnqueueAsync(new CloudOperationJournalEntry(Guid.NewGuid(),
                    CloudStateOperationKind.ContentUpdate, childId, Array.Empty<byte>(), DateTimeOffset.UtcNow));
                await transaction.CommitAsync();
            }

            await fileSystem.GetDirectory("parent").SetInSyncAsync(true);
            CloudRemoteChange change = new("delete", CloudRemoteChangeKind.Delete, "parent", "r2",
                CloudItemKind.Directory, "parent", previousRemoteRevision: "r1");
            CloudRemoteApplyResult result = await fileSystem.ApplyRemoteChangesAsync(
                new CloudRemoteChangeBatch("batch", Array.Empty<byte>(), [change], new byte[] { 1 }),
                new CloudRemoteApplyOptions { PreserveUnsynchronizedLocalContent = preserve });
            bool protectedChild = preserve;
            Assert.Equal(protectedChild ? CloudRemoteApplyEntryStatus.Conflict : CloudRemoteApplyEntryStatus.Applied,
                Assert.Single(result.Entries).Status);
            Assert.Equal(protectedChild, File.Exists(childPath));
            if (protectedChild)
            {
                Assert.Equal("local content", await File.ReadAllTextAsync(childPath));
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

    private sealed class CapturingFactory(ICloudStateStoreFactory inner) : ICloudStateStoreFactory
    {
        internal ICloudStateStore? Store { get; private set; }

        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context,
            CancellationToken cancellationToken = default) =>
            Store = await inner.OpenAsync(context, cancellationToken);
    }
}
