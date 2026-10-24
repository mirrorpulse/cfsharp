using Microsoft.Data.Sqlite;

namespace CfSharp.Storage.Sqlite.Tests;

public sealed partial class SqliteCloudStateStoreTests
{
    [Fact]
    public async Task VersionEightInlinePathConstraintUpgradePreservesOriginalReferencesAndHistory()
    {
        Guid originalId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        Guid conflictId = Guid.NewGuid();
        Guid suppressionId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long sequence;
        await using (ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext()))
        await using (ICloudStateTransaction write = await store.BeginTransactionAsync())
        {
            await write.Items.UpsertAsync(new(originalId, "old-remote", "Docs\\Ä.txt", CloudItemKind.File, "v2", 123, true, now));
            sequence = (await write.Operations.EnqueueAsync(new(operationId, CloudStateOperationKind.Delete, originalId, [1], now))).Sequence;
            await write.Conflicts.UpsertAsync(new(conflictId, originalId, CloudStateConflictKind.Delete, [2], now));
            await write.EchoSuppressions.UpsertAsync(new(suppressionId, originalId, CloudStateOperationKind.Delete,
                "Docs\\Ä.txt", [3], now.AddHours(1)));
            await write.Checkpoints.UpsertAsync(new("retained/proof", [4], now));
            await write.RemoteBatches.UpsertAsync(new("old-delete", [2], 1, 1, CloudRemoteBatchStatus.Applied, [5], now, new byte[] { 6 }, "delete"));
            await write.CommitAsync();
        }

        // Build the real old inline UNIQUE constraint, rather than only editing its version.
        // Keep FK actions disabled while replacing the table, just as SQLite's rebuild requires.
        await ExecuteSqlAsync(_databasePath, """
            PRAGMA foreign_keys = OFF;
            BEGIN IMMEDIATE;
            CREATE TABLE items_preview_seven (
                item_id TEXT NOT NULL PRIMARY KEY,
                remote_id TEXT NOT NULL UNIQUE,
                relative_path TEXT NOT NULL COLLATE CFSHARP_UNICODE_NOCASE UNIQUE,
                kind INTEGER NOT NULL,
                remote_revision TEXT NULL,
                local_file_id INTEGER NULL,
                is_tombstone INTEGER NOT NULL,
                updated_at_ticks INTEGER NOT NULL
            );
            INSERT INTO items_preview_seven SELECT * FROM items;
            DROP TABLE items;
            ALTER TABLE items_preview_seven RENAME TO items;
            CREATE UNIQUE INDEX ux_items_relative_path_unicode_nocase ON items(relative_path COLLATE CFSHARP_UNICODE_NOCASE);
            UPDATE cfsharp_schema SET version = 8 WHERE singleton = 1;
            COMMIT;
            PRAGMA foreign_keys = ON;
            """);

        Guid successorId = Guid.NewGuid();
        await using (ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext()))
        await using (ICloudStateTransaction write = await store.BeginTransactionAsync())
        {
            CloudItemState original = (await write.Items.GetByItemIdAsync(originalId))!;
            Assert.True(original.IsTombstone);
            Assert.Equal("Docs\\Ä.txt", original.RelativePath);
            Assert.Equal("v2", original.RemoteRevision);
            Assert.Equal(123, original.LocalFileId);
            Assert.Equal(now, original.UpdatedAt);
            CloudOperationJournalEntry operation = (await write.Operations.GetAsync(operationId))!;
            Assert.Equal(originalId, operation.ItemId);
            Assert.Equal(sequence, operation.Sequence);
            Assert.Equal(new byte[] { 1 }, operation.Payload.ToArray());
            Assert.Equal(originalId, (await write.Conflicts.GetAsync(conflictId))!.ItemId);
            Assert.Equal(originalId, (await write.EchoSuppressions.GetAsync(suppressionId))!.ItemId);
            Assert.Equal(new byte[] { 4 }, (await write.Checkpoints.GetAsync("retained/proof"))!.Value.ToArray());
            Assert.Equal(new byte[] { 6 }, (await write.RemoteBatches.GetAsync("old-delete"))!.Fingerprint.ToArray());
            await write.Items.UpsertAsync(new(successorId, "new-remote", "docs\\ä.TXT", CloudItemKind.File, "v3", 456, false, now));
            Assert.True((await write.Operations.EnqueueAsync(new(Guid.NewGuid(), CloudStateOperationKind.MetadataUpdate,
                successorId, [7], now))).Sequence > sequence);
            await write.CommitAsync();
        }

        await using ICloudStateStore reopened = await CreateFactory().OpenAsync(CreateContext());
        await using ICloudStateTransaction verify = await reopened.BeginTransactionAsync();
        Assert.Equal(successorId, (await verify.Items.GetByRelativePathAsync("Docs\\Ä.txt"))!.ItemId);
        Assert.True((await verify.Items.GetByItemIdAsync(originalId))!.IsTombstone);
        Assert.Equal(9L, await ExecuteScalarInt64Async(_databasePath, "SELECT version FROM cfsharp_schema;"));
        Assert.Equal(0L, await ExecuteScalarInt64Async(_databasePath, "SELECT count(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task GenerationPathLookupUsesItsIndexWithoutSortingHistory()
    {
        await using (ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext()))
        await using (ICloudStateTransaction write = await store.BeginTransactionAsync())
        {
            for (int index = 0; index < 128; index++)
            {
                await write.Items.UpsertAsync(new(Guid.NewGuid(), "generation-" + index, "same.txt",
                    CloudItemKind.File, "v" + index, null, true, DateTimeOffset.UtcNow));
            }
            await write.CommitAsync();
        }
        await using SqliteConnection connection = new(CreateConnectionString(_databasePath));
        await connection.OpenAsync();
        await SqliteSchema.ConfigureConnectionAsync(connection, 5000, CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT item_id FROM items WHERE relative_path = 'same.txt' COLLATE CFSHARP_UNICODE_NOCASE " +
            "ORDER BY is_tombstone, updated_at_ticks DESC, item_id LIMIT 1;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        List<string> plan = [];
        while (await reader.ReadAsync()) { plan.Add(reader.GetString(3)); }
        Assert.Contains(plan, detail => detail.Contains("SEARCH items USING COVERING INDEX ix_items_path_generation", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, detail => detail.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenerationSchemaRejectsMissingOrUnconditionalPathIndex(bool unconditional)
    {
        await using (ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext())) { }
        await ExecuteSqlAsync(_databasePath, "DROP INDEX ux_items_relative_path_unicode_nocase;" +
            (unconditional ? "CREATE UNIQUE INDEX ux_items_relative_path_unicode_nocase ON items(relative_path COLLATE CFSHARP_UNICODE_NOCASE);" : string.Empty));
        SqliteCloudStateStoreException error = await Assert.ThrowsAsync<SqliteCloudStateStoreException>(async () =>
            await CreateFactory().OpenAsync(CreateContext()));
        Assert.Equal(SqliteCloudStateStoreError.InvalidSchema, error.Error);
    }
}
