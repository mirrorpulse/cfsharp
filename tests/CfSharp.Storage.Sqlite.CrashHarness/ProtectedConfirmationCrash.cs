using System.Runtime.Versioning;
using System.Text.Json;

using CfSharp;
using CfSharp.Storage.Sqlite;

[SupportedOSPlatform("windows10.0.16299")]
internal static class ProtectedConfirmationCrash
{
    internal static async Task<int> RunAsync(string databasePath, string root)
    {
        using JsonDocument proof = JsonDocument.Parse(await File.ReadAllTextAsync(databasePath + ".confirmation.json"));
        JsonElement values = proof.RootElement;
        CloudContentConfirmationRequest request = new(
            new CloudLocalFileBinding(values.GetProperty("Volume").GetUInt64(),
                values.GetProperty("Root").GetGuid(), values.GetProperty("File").GetGuid()),
            new CloudPlaceholderIdentity(values.GetProperty("Item").GetGuid(),
                values.GetProperty("Remote").GetString()!, values.GetProperty("Revision").GetString()),
            values.GetProperty("Length").GetInt64(), values.GetProperty("Hash").GetBytesFromBase64(),
            CloudContentPreparation.ConvertRegularFile);
        CrashFactory factory = new(databasePath);
        await using CloudFileSystem system = CloudFileSystem.CreateBuilder(root)
            .WithStateStore(factory).WithContentProvider(new NoFetchProvider()).Build();
        await system.StartAsync();
        factory.Armed = true;
        await system.GetFile("content.bin").ConfirmUploadedContentAsync(request);
        return 3; // Reaching here means the intended native/projection crash point was missed.
    }

    private sealed class NoFetchProvider : ICloudFileContentProvider
    {
        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<Stream>(new InvalidOperationException("The crash fixture must not hydrate content."));
    }

    private sealed class CrashFactory(string path) : ICloudStateStoreFactory
    {
        private readonly SqliteCloudStateStoreFactory _inner = new(path);
        internal bool Armed { get; set; }
        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context,
            CancellationToken cancellationToken = default) =>
            new Store(await _inner.OpenAsync(context, cancellationToken), this);

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
                if (owner.Armed)
                {
                    // Exit terminates this isolated process without running finally blocks or
                    // disposing the uncommitted real transaction. Recovery must reopen its WAL.
                    Environment.Exit(91);
                }

                await inner.CommitAsync(cancellationToken);
            }

            public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
