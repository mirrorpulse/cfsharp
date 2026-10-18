using System.Runtime.Versioning;

using CfSharp;
using CfSharp.Storage.Sqlite;

[SupportedOSPlatform("windows10.0.16299")]
internal static class RemoteDeleteCrash
{
    internal static async Task<int> RunAsync(string database, string root, string kind, string itemId, string mode)
    {
        bool directory = kind == "directory";
        string target = directory ? "Docs\\remote" : "Docs\\remote.bin";
        CrashFactory factory = new(database, root, target, mode);
        await using CloudFileSystem system = CloudFileSystem.CreateBuilder(root).WithStateStore(factory)
            .WithContentProvider(new NoFetch()).Build();
        await system.StartAsync();
        CloudRemoteChange change = new("delete", CloudRemoteChangeKind.Delete, "remote-file", "v2",
            directory ? CloudItemKind.Directory : CloudItemKind.File, target, Guid.Parse(itemId), previousRemoteRevision: "v1");
        await system.ApplyRemoteChangesAsync(new("stable-original-batch", new byte[] { 1 }, [change], new byte[] { 9 }));
        return 3; // Reaching this point means the selected kill boundary was not observed.
    }

    private sealed class NoFetch : ICloudFileContentProvider
    {
        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<Stream>(new InvalidOperationException("Delete recovery must not read content."));
    }

    private sealed class CrashFactory(string database, string root, string target, string mode) : ICloudStateStoreFactory
    {
        private void WaitForKill()
        {
            File.WriteAllText(database + ".delete-ready", mode);
            // The parent kills this real process while handles and possibly an uncommitted
            // SQLite transaction are live. No managed cleanup or rollback runs in this child.
            Thread.Sleep(Timeout.Infinite);
        }

        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context, CancellationToken cancellationToken = default) =>
            new Store(await new SqliteCloudStateStoreFactory(database).OpenAsync(context, cancellationToken), this);

        private sealed class Store(ICloudStateStore inner, CrashFactory owner) : ICloudStateStore
        {
            public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
                new Transaction(await inner.BeginTransactionAsync(cancellationToken), owner);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }

        private sealed class Transaction(ICloudStateTransaction inner, CrashFactory owner) : ICloudStateTransaction
        {
            public ICloudItemStateRepository Items => inner.Items;
            public ICloudCheckpointRepository Checkpoints => inner.Checkpoints;
            public ICloudOperationJournal Operations => inner.Operations;
            public ICloudConflictRepository Conflicts => inner.Conflicts;
            public ICloudRemoteBatchRepository RemoteBatches => inner.RemoteBatches;
            public ICloudEchoSuppressionRepository EchoSuppressions => inner.EchoSuppressions;

            public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
            {
                CloudRemoteBatchState? batch = await RemoteBatches.GetAsync("stable-original-batch", cancellationToken);
                CloudItemState? item = await Items.GetByRelativePathAsync(owner.Target, cancellationToken);
                bool prepared = (await Checkpoints.ListAsync("cfsharp/remote-delete/v1/", cancellationToken)).Count != 0;
                bool absent = !File.Exists(Path.Combine(owner.Root, owner.Target)) && !Directory.Exists(Path.Combine(owner.Root, owner.Target));
                string? stage = batch?.AppliedEntryCount == 1 ? "outcome" :
                    item?.IsTombstone == true ? "projection" : prepared ? (absent ? "native" : "prepare") : null;
                bool selected = stage is not null && owner.Mode.StartsWith("delete-" + stage + "-", StringComparison.Ordinal);
                if (selected && owner.Mode.EndsWith("-before", StringComparison.Ordinal))
                {
                    owner.WaitForKill();
                }

                await inner.CommitAsync(cancellationToken);
                if (selected && owner.Mode.EndsWith("-after", StringComparison.Ordinal))
                {
                    owner.WaitForKill();
                }
            }

            public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }

        private string Root => root;
        private string Target => target;
        private string Mode => mode;
    }
}
