using System.Runtime.Versioning;

using CfSharp;
using CfSharp.Storage.Sqlite;

[SupportedOSPlatform("windows10.0.16299")]
internal static class DirectoryMoveCrash
{
    internal static async Task<int> RunAsync(string database, string root, string mode)
    {
        if (mode is not ("directory-prepared" or "directory-native" or "directory-before-projection" or "directory-after-projection"))
        {
            return 2;
        }

        CrashFactory factory = new(database, mode);
        await using CloudFileSystem system = CloudFileSystem.CreateBuilder(root).WithStateStore(factory)
            .WithContentProvider(new NoFetchProvider()).Build();
        await system.StartAsync();
        CloudDirectory source = system.GetDirectory("Docs");
        CloudDirectoryMoveProof proof = await source.PrepareMoveAsync(system.Root, "Moved");
        // The application's retained original proof is outside both the root and state database.
        File.WriteAllBytes(database + ".directory-proof", proof.Encode());
        File.WriteAllText(database + "." + mode + ".ready", proof.ProofId.ToString("N"));
        if (mode == "directory-prepared")
        {
            Environment.Exit(91);
        }

        Directory.Move(source.FullPath, Path.Combine(root, "Moved"));
        if (mode == "directory-native")
        {
            Environment.Exit(92);
        }

        factory.Armed = true;
        await source.ReconcileMoveAsync(proof);
        return 3; // Missing a selected abrupt-exit point is a failing harness result.
    }

    private sealed class NoFetchProvider : ICloudFileContentProvider
    {
        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<Stream>(new InvalidOperationException("Directory recovery must not hydrate content."));
    }

    private sealed class CrashFactory(string path, string mode) : ICloudStateStoreFactory
    {
        internal bool Armed { get; set; }
        private string Mode => mode;
        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context, CancellationToken cancellationToken = default) =>
            new Store(await new SqliteCloudStateStoreFactory(path).OpenAsync(context, cancellationToken), this);

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
                if (owner.Armed && owner.Mode == "directory-before-projection")
                {
                    // Exit without finally/disposal while the real SQLite transaction contains
                    // all projected paths and its receipt; WAL recovery must discard them together.
                    Environment.Exit(93);
                }

                await inner.CommitAsync(cancellationToken);
                if (owner.Armed && owner.Mode == "directory-after-projection")
                {
                    Environment.Exit(94);
                }
            }

            public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
