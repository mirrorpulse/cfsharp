using System.IO.Compression;

using Microsoft.Data.Sqlite;

namespace CfSharp.Tests.Persistence;

// Generated with the three public 0.1.0-preview.3 packages from NuGet, source commit
// b65b6b7a81c411e8af84f421e4259825e91536d6. SQLite package SHA-256:
// EF7E7FBA235125963365E5FBD031CD99660BECE60C760920957587B5CAF86189.
// Its real schema-5 database was checkpointed after closing the old store; no schema
// number was rewritten. Only the 2 KiB compressed database is a checked-in test asset.
internal static class PreviewThreeStateFixture
{
    internal static readonly Guid ItemId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    internal static readonly Guid OperationId = Guid.Parse("22222222-2222-3333-4444-555555555555");
    private static readonly Guid ConflictId = Guid.Parse("33333333-2222-3333-4444-555555555555");
    private static readonly Guid SuppressionId = Guid.Parse("44444444-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    internal static void Extract(string database, string syncRoot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        using Stream resource = typeof(PreviewThreeStateFixture).Assembly.GetManifestResourceStream("CfSharp.PreviewThreeStateFixture.zip")!;
        using ZipArchive archive = new(resource, ZipArchiveMode.Read);
        using Stream entry = archive.GetEntry("preview3.db")!.Open();
        using (FileStream destination = File.Create(database))
        {
            entry.CopyTo(destination);
        }

        // Rehome only this disposable fixture's machine-specific root path. Preserve the
        // genuine schema version and every legacy repository value; production databases
        // remain bound to their original root and must never be relocated this way.
        using SqliteConnection connection = new($"Data Source={database};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE cfsharp_schema SET sync_root_path=$root WHERE singleton=1;";
        command.Parameters.AddWithValue("$root", Path.GetFullPath(syncRoot));
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    internal static async Task AssertRetainedAsync(ICloudStateTransaction transaction, string directory = "Docs")
    {
        CloudItemState item = (await transaction.Items.GetByItemIdAsync(ItemId))!;
        Assert.Equal(directory + "\\deleted.txt", item.RelativePath);
        Assert.Equal("legacy-remote", item.RemoteId);
        Assert.Equal("legacy-revision", item.RemoteRevision);
        Assert.Equal(321, item.LocalFileId);
        Assert.True(item.IsTombstone);
        CloudStateCheckpoint checkpoint = (await transaction.Checkpoints.GetAsync("legacy/checkpoint"))!;
        Assert.Equal(new byte[] { 1, 2 }, checkpoint.Value.ToArray());
        Assert.Equal(Timestamp, checkpoint.UpdatedAt);
        CloudOperationJournalEntry operation = (await transaction.Operations.GetAsync(OperationId))!;
        Assert.Equal(1, operation.Sequence);
        Assert.Equal(ItemId, operation.ItemId);
        Assert.Equal(new byte[] { 3, 4 }, operation.Payload.ToArray());
        Assert.Equal(Timestamp, operation.CreatedAt);
        Assert.Equal(3, operation.AttemptCount);
        Assert.Equal(Timestamp.AddDays(90), operation.RetryAfter);
        Assert.Equal(new byte[] { 5, 6 }, (await transaction.Conflicts.GetAsync(ConflictId))!.Payload.ToArray());
        CloudRemoteBatchState batch = (await transaction.RemoteBatches.GetAsync("legacy-batch"))!;
        Assert.Equal(CloudRemoteBatchStatus.Applying, batch.Status);
        Assert.Equal(new byte[] { 7 }, batch.Cursor.ToArray());
        Assert.Equal(new byte[] { 8 }, batch.Payload.ToArray());
        Assert.Equal(new byte[] { 9 }, batch.Fingerprint.ToArray());
        Assert.Equal("legacy-change", batch.LastAppliedChangeId);
        CloudEchoSuppressionState suppression = (await transaction.EchoSuppressions.GetAsync(SuppressionId))!;
        Assert.Equal("Old\\deleted.txt", suppression.PreviousRelativePath);
        Assert.Equal("Docs\\deleted.txt", suppression.RelativePath);
        Assert.Equal(2, suppression.RemainingObservations);
        Assert.Equal(new byte[] { 10 }, suppression.Payload.ToArray());
    }
}
