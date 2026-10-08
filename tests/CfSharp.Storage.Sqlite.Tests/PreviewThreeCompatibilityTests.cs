using CfSharp.Tests.Persistence;

using Microsoft.Data.Sqlite;

namespace CfSharp.Storage.Sqlite.Tests;

public sealed class PreviewThreeCompatibilityTests
{
    [Fact]
    public async Task PublishedPreviewThreeDatabaseSupportsBoundedPagingAndCompleteRestart()
    {
        string area = Path.Combine(Path.GetTempPath(), "CfSharp-preview3-compatibility", Guid.NewGuid().ToString("N"));
        string root = Path.Combine(area, "root");
        string database = Path.Combine(area, "state.db");
        Directory.CreateDirectory(root);
        PreviewThreeStateFixture.Extract(database, root);
        try
        {
            await using (SqliteConnection legacy = new($"Data Source={database};Mode=ReadOnly;Pooling=False"))
            {
                await legacy.OpenAsync();
                using SqliteCommand command = legacy.CreateCommand();
                command.CommandText = "SELECT version FROM cfsharp_schema WHERE singleton=1;";
                Assert.Equal(5L, await command.ExecuteScalarAsync());
            }

            Guid nextId = Guid.NewGuid();
            await using (ICloudStateStore store = await new SqliteCloudStateStoreFactory(database).OpenAsync(new(root)))
            await using (ICloudStateTransaction transaction = await store.BeginTransactionAsync())
            {
                await PreviewThreeStateFixture.AssertRetainedAsync(transaction);
                ICloudOperationJournalPaging paging = Assert.IsAssignableFrom<ICloudOperationJournalPaging>(transaction.Operations);
                long boundary = await paging.GetHighWaterSequenceAsync();
                Assert.Equal(1, boundary);
                CloudOperationJournalPage page = await paging.ReadPageAsync(0, boundary, 1);
                Assert.Equal(PreviewThreeStateFixture.OperationId, Assert.Single(page.Operations).OperationId);
                await transaction.Operations.EnqueueAsync(new(nextId, CloudStateOperationKind.MetadataUpdate, null, new byte[] { 11 }, DateTimeOffset.UtcNow));
                Assert.Empty((await paging.ReadPageAsync(boundary, boundary, 1)).Operations);
                await transaction.Checkpoints.UpsertAsync(new("compatibility/new-runtime", new byte[] { 12 }, DateTimeOffset.UtcNow));
                await transaction.CommitAsync();
            }

            await using ICloudStateStore reopened = await new SqliteCloudStateStoreFactory(database).OpenAsync(new(root));
            await using ICloudStateTransaction verify = await reopened.BeginTransactionAsync();
            await PreviewThreeStateFixture.AssertRetainedAsync(verify);
            Assert.Equal(2, (await verify.Operations.GetAsync(nextId))!.Sequence);
            Assert.Equal(new byte[] { 12 }, (await verify.Checkpoints.GetAsync("compatibility/new-runtime"))!.Value.ToArray());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(area, recursive: true);
        }
    }
}
