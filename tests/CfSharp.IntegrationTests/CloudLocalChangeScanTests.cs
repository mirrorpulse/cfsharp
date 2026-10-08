using System.Runtime.Versioning;

using CfSharp.Storage.Sqlite;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed class CloudLocalChangeScanTests
{
    [Fact]
    public async Task NativeScansReachActiveSuccessorAndKeepDeferredOperationsAcrossRoots()
    {
        string area = Path.Combine(Path.GetTempPath(), "CfSharp-native-scans", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(area);
        try
        {
            await using NativeScanRoot first = await NativeScanRoot.OpenAsync(area, "first");
            await using NativeScanRoot second = await NativeScanRoot.OpenAsync(area, "second");
            HashSet<string> deferredPaths = Enumerable.Range(0, 16)
                .Select(index => $"Offline\\deferred-{index:D2}.txt")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string path in deferredPaths)
            {
                await File.WriteAllTextAsync(Path.Combine(first.RootPath, path), "deferred");
            }

            await WaitForChangesAsync(first.Feed, changes => deferredPaths.All(path =>
                changes.Any(change => change.RelativePath.Equals(path, StringComparison.OrdinalIgnoreCase))));
            CloudOperationJournalEntry[] deferred;
            await using (ICloudStateTransaction capture = await first.Store.BeginTransactionAsync())
            {
                deferred = (await capture.Operations.ListAsync(4096)).ToArray();
            }

            Assert.True(deferred.Length >= 16);
            await File.WriteAllTextAsync(Path.Combine(first.RootPath, "Active", "live.txt"), "active");
            await File.WriteAllTextAsync(Path.Combine(second.RootPath, "Active", "isolated.txt"), "other root");
            IReadOnlyList<CloudLocalChange> all = await WaitForChangesAsync(first.Feed,
                changes => changes.Any(change => change.RelativePath == "Active\\live.txt"));
            CloudLocalChange accepted = all.First(change => change.RelativePath == "Active\\live.txt");
            Assert.True(accepted.Sequence > deferred[^1].Sequence);
            Assert.DoesNotContain(all, change => change.RelativePath == "Active\\isolated.txt");
            // Native notifications can include parent-directory metadata as well as file writes.
            // Treat every captured head operation as deferred and retain its original identity.
            Assert.Equal(deferred.Take(16).Select(row => row.OperationId),
                all.Take(16).Select(change => change.OperationId));
            await first.Feed.AcknowledgeAsync([new CloudLocalChangeAcknowledgement(accepted.OperationId, "accepted-live")]);

            await using (ICloudStateTransaction verify = await first.Store.BeginTransactionAsync())
            {
                foreach (CloudOperationJournalEntry original in deferred)
                {
                    CloudOperationJournalEntry? pending = await verify.Operations.GetAsync(original.OperationId);
                    Assert.NotNull(pending);
                    Assert.Equal(original.Sequence, pending.Sequence);
                    Assert.Equal(original.ItemId, pending.ItemId);
                    Assert.Equal(original.Payload.ToArray(), pending.Payload.ToArray());
                    Assert.Equal(original.CreatedAt, pending.CreatedAt);
                }

                Assert.Null(await verify.Operations.GetAsync(accepted.OperationId));
                Assert.Equal("accepted-live", (await verify.Items.GetByItemIdAsync(accepted.ItemId!.Value))!.RemoteRevision);
            }

            IReadOnlyList<CloudLocalChange> isolated = await WaitForChangesAsync(second.Feed,
                changes => changes.Any(change => change.RelativePath == "Active\\isolated.txt"));
            Assert.DoesNotContain(isolated, change => deferredPaths.Contains(change.RelativePath));
            CloudLocalChangeScan foreign = await first.Feed.BeginScanAsync();
            await Assert.ThrowsAsync<ArgumentException>(async () => await second.Feed.ReadPageAsync(foreign, 0, 4));
        }
        finally
        {
            Directory.Delete(area, recursive: true);
        }
    }

    [Fact]
    public async Task NativeCreateMoveDeleteDependenciesRemainOrderedAcrossSingleRowPages()
    {
        string area = Path.Combine(Path.GetTempPath(), "CfSharp-native-scan-order", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(area);
        try
        {
            await using NativeScanRoot root = await NativeScanRoot.OpenAsync(area, "root");
            string before = Path.Combine(root.RootPath, "Active", "before.txt");
            string after = Path.Combine(root.RootPath, "Active", "after.txt");
            await File.WriteAllTextAsync(before, "local");
            IReadOnlyList<CloudLocalChange> created = await WaitForChangesAsync(root.Feed,
                changes => changes.Any(change => change.Kind == CloudLocalChangeKind.Create));
            Guid itemId = created.First(change => change.Kind == CloudLocalChangeKind.Create).ItemId!.Value;
            File.Move(before, after);
            await WaitForChangesAsync(root.Feed, changes => changes.Any(change => change.Kind == CloudLocalChangeKind.Move));
            File.Delete(after);
            IReadOnlyList<CloudLocalChange> changes = await WaitForChangesAsync(root.Feed,
                values => values.Any(change => change.Kind == CloudLocalChangeKind.Delete), limit: 1);
            CloudLocalChange move = Assert.Single(changes.Where(change => change.Kind == CloudLocalChangeKind.Move));
            CloudLocalChange delete = Assert.Single(changes.Where(change => change.Kind == CloudLocalChangeKind.Delete));
            Assert.Equal("Active\\before.txt", move.PreviousRelativePath);
            Assert.Equal("Active\\after.txt", move.RelativePath);
            Assert.Equal(move.RelativePath, delete.RelativePath);
            Assert.Equal(itemId, move.ItemId);
            Assert.Equal(itemId, delete.ItemId);
            Assert.True(move.Sequence < delete.Sequence);
            Assert.Equal(changes.Select(change => change.Sequence).Order(), changes.Select(change => change.Sequence));
            await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
            Assert.True((await verify.Items.GetByItemIdAsync(itemId))!.IsTombstone);
            Assert.NotNull(await verify.Operations.GetAsync(move.OperationId));
            Assert.NotNull(await verify.Operations.GetAsync(delete.OperationId));
        }
        finally
        {
            Directory.Delete(area, recursive: true);
        }
    }

    private static async Task<IReadOnlyList<CloudLocalChange>> WaitForChangesAsync(CloudLocalChangeFeed feed,
        Func<IReadOnlyList<CloudLocalChange>, bool> ready, int limit = 4)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        while (true)
        {
            CloudLocalChangeScan scan = await feed.BeginScanAsync(timeout.Token);
            List<CloudLocalChange> changes = [];
            long cursor = 0;
            CloudLocalChangePage page;
            do
            {
                page = await feed.ReadPageAsync(scan, cursor, limit, timeout.Token);
                Assert.False(page.RequiresFullRescan);
                Assert.InRange(page.Changes.Count, 0, limit);
                changes.AddRange(page.Changes);
                cursor = page.LastScannedSequence;
            }
            while (page.HasMore);

            if (ready(changes))
            {
                return changes;
            }

            await Task.Delay(20, timeout.Token);
        }
    }

    private sealed class NativeScanRoot(CloudFileSystem fileSystem, CapturingFactory factory) : IAsyncDisposable
    {
        internal string RootPath => fileSystem.SyncRootPath;
        internal ICloudStateStore Store => factory.Store!;
        internal CloudLocalChangeFeed Feed { get; } = fileSystem.CreateLocalChangeFeed(
            new CloudLocalChangeFeedOptions { BatchSize = 4, BufferCapacity = 1024 });

        internal static async Task<NativeScanRoot> OpenAsync(string area, string name)
        {
            string rootPath = Path.Combine(area, name);
            Directory.CreateDirectory(Path.Combine(rootPath, "Offline"));
            Directory.CreateDirectory(Path.Combine(rootPath, "Active"));
            CapturingFactory factory = new(new SqliteCloudStateStoreFactory(Path.Combine(area, name + ".db")));
            CloudFileSystem fileSystem = CloudFileSystem.CreateBuilder(rootPath).WithStateStore(factory)
                .WithRegistration(SyncRootRegistrationOptions.CreateBuilder("CfSharp Native Scan", "1.0-test")
                    .WithProviderId(Guid.NewGuid()).WithRootMarkedInSync().Build()).Build();
            bool registered = false;
            try
            {
                await fileSystem.StartAsync();
                registered = true;
                NativeScanRoot root = new(fileSystem, factory);
                await root.Feed.StartAsync();
                return root;
            }
            catch
            {
                await fileSystem.DisposeAsync();
                if (registered)
                {
                    CloudSyncRoot.Open(rootPath).Unregister();
                }

                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await fileSystem.DisposeAsync();
            CloudSyncRoot.Open(RootPath).Unregister();
        }
    }

    private sealed class CapturingFactory(ICloudStateStoreFactory inner) : ICloudStateStoreFactory
    {
        internal ICloudStateStore? Store { get; private set; }
        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context,
            CancellationToken cancellationToken = default) => Store = await inner.OpenAsync(context, cancellationToken);
    }
}
