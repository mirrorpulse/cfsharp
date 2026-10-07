using System.Diagnostics;
using System.Runtime.Versioning;
using CfSharp.Storage.Sqlite;

namespace CfSharp.IntegrationTests;

public sealed class ProviderDirectoryPopulationTests
{
    [Fact]
    [SupportedOSPlatform("windows10.0.16299")]
    public async Task PartialDirectoryRequestsAnOrderedProviderPage()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return;
        }

        string testPath = Path.Combine(Path.GetTempPath(), $"CfSharp-{Guid.NewGuid():N}");
        string rootPath = Path.Combine(testPath, "root");
        string databasePath = Path.Combine(testPath, "state", "cfsharp.db");
        Directory.CreateDirectory(rootPath);
        Guid providerId = Guid.NewGuid();
        CloudFileSystem? fileSystem = null;
        CloudSyncRoot? root = null;
        bool registered = false;
        DemandProvider provider = new();
        DirectoryMoveTestRoot.CapturingFactory factory = new(new SqliteCloudStateStoreFactory(databasePath));

        try
        {
            SyncRootRegistrationOptions registration = SyncRootRegistrationOptions
                .CreateBuilder($"CfSharp Population {providerId:N}", "1.0.0-test")
                .WithProviderId(providerId)
                .WithSyncRootIdentity(providerId.ToByteArray())
                .WithHydrationPolicy(CloudHydrationPolicy.Progressive)
                .WithPopulationPolicy(CloudPopulationPolicy.Full)
                .WithRootMarkedInSync()
                .Build();

            fileSystem = CloudFileSystem.CreateBuilder(rootPath)
                .WithStateStore(factory)
                .WithRegistration(registration)
                .WithContentProvider(provider)
                .Build();
            await fileSystem.StartAsync();
            root = CloudSyncRoot.Open(rootPath);
            registered = true;

            CloudDirectoryPlaceholderSpec directory = CloudDirectoryPlaceholderSpec
                .CreateBuilder("remote", "remote-directory")
                .WithPopulationState(CloudDirectoryPopulationState.Partial)
                .WithInSyncState(true)
                .Build();
            await fileSystem.Root.CreatePlaceholderAsync(directory);
            await fileSystem.GetDirectory("remote")
                .SetPopulationStateAsync(CloudDirectoryPopulationState.Partial);
            CloudItemSnapshot directorySnapshot = await fileSystem
                .GetDirectory("remote")
                .InspectAsync();
            Assert.True(
                directorySnapshot.PlaceholderState.HasFlag(CloudPlaceholderState.Partial),
                $"Expected a partial directory, got {directorySnapshot.PlaceholderState}.");

            _ = Directory.GetFileSystemEntries(Path.Combine(rootPath, "remote"));
            using (Process enumerationProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c dir /b \"{Path.Combine(rootPath, "remote")}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            })!)
            {
                await enumerationProcess.WaitForExitAsync();
            }
            CloudProviderFetchPlaceholdersRequest request =
                await provider.Request.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(
                request.NormalizedPath.EndsWith(
                    Path.Combine("remote"),
                    StringComparison.OrdinalIgnoreCase),
                $"Unexpected normalized callback path: {request.NormalizedPath}");
            Assert.Equal("*", request.SearchPattern);
            Assert.True(File.Exists(Path.Combine(rootPath, "remote", "child.txt")));
            CloudItemSnapshot childSnapshot = await WaitForDurableChildAsync(fileSystem);
            Assert.Equal("remote-child", childSnapshot.RemoteId);
            Assert.NotNull(childSnapshot.DurableStateUpdatedAt);
            Assert.Equal(1, provider.RequestCount);
            CloudItemSnapshot childDirectory = await fileSystem.GetDirectory("remote/folder").InspectAsync();
            await using (ICloudStateTransaction transaction = await factory.Store!.BeginTransactionAsync())
            {
                CloudDirectoryProvenance provenance = CloudDirectoryProvenance.Decode((await transaction.Checkpoints.GetAsync(
                    CloudDirectoryProvenance.BindingName(childDirectory.ItemId!.Value)))!.Value);
                Assert.Equal(childDirectory.LocalBinding, provenance.Binding);
                Assert.Equal(childDirectory.PlaceholderIdentity.ToArray(), provenance.Identity);
            }

            await fileSystem.DisposeAsync();
            fileSystem = null;
            root.Unregister();
            registered = false;
        }
        finally
        {
            if (fileSystem is not null)
            {
                await fileSystem.DisposeAsync();
            }

            if (registered && root is not null)
            {
                try
                {
                    root.Unregister();
                }
                catch (CloudFilesException)
                {
                    // Preserve the original failure while still attempting cleanup.
                }
            }

            if (Directory.Exists(testPath))
            {
                Directory.Delete(testPath, recursive: true);
            }
        }
    }

    [SupportedOSPlatform("windows10.0.16299")]
    private static async Task<CloudItemSnapshot> WaitForDurableChildAsync(
        CloudFileSystem fileSystem)
    {
        CloudFile child = fileSystem.GetFile("remote/child.txt");
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            CloudItemSnapshot snapshot = await child.InspectAsync();
            if (snapshot.RemoteId is not null)
            {
                return snapshot;
            }

            await Task.Delay(25);
        }

        return await child.InspectAsync();
    }

    private sealed class DemandProvider : ICloudDemandProvider
    {
        public TaskCompletionSource<CloudProviderFetchPlaceholdersRequest> Request { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RequestCount { get; private set; }

        public ValueTask<Stream> OpenReadAsync(
            CloudFileFetchRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        public ValueTask<CloudProviderDirectoryPage> FetchChildrenAsync(
            CloudProviderFetchPlaceholdersRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestCount++;
            Request.TrySetResult(request);
            CloudFilePlaceholderSpec child = CloudFilePlaceholderSpec
                .CreateBuilder("child.txt", "remote-child", 3)
                .WithInSyncState(false)
                .Build();
            CloudDirectoryPlaceholderSpec directory = CloudDirectoryPlaceholderSpec.CreateBuilder("folder", "remote-folder")
                .WithPopulationState(CloudDirectoryPopulationState.Complete).Build();
            return ValueTask.FromResult(new CloudProviderDirectoryPage([child, directory], totalCount: 2));
        }
    }
}
