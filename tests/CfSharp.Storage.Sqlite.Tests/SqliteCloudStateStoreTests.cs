using System.Diagnostics;
using System.Runtime.InteropServices;

using CfSharp.Tests.Persistence;
using Microsoft.Data.Sqlite;
using Xunit.Sdk;

namespace CfSharp.Storage.Sqlite.Tests;

public sealed class SqliteCloudStateStoreTests : CloudStateStoreContractTests, IAsyncLifetime
{
    private string _temporaryDirectory = null!;
    private string _syncRootPath = null!;
    private string _databasePath = null!;
    private string? _directoryLinkPath;
    private string? _sidecarLinkPath;

    public Task InitializeAsync()
    {
        string testTemporaryRoot = Environment.GetEnvironmentVariable("CFSHARP_TEST_TEMP_ROOT")
            ?? Path.GetTempPath();
        _temporaryDirectory = Path.Combine(
            testTemporaryRoot,
            "CfSharp-sqlite-tests",
            Guid.NewGuid().ToString("N"));
        _syncRootPath = Path.Combine(_temporaryDirectory, "sync-root");
        _databasePath = Path.Combine(_temporaryDirectory, "state", "cfsharp.db");
        Directory.CreateDirectory(_syncRootPath);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (_directoryLinkPath is not null && Directory.Exists(_directoryLinkPath))
        {
            Directory.Delete(_directoryLinkPath);
        }

        if (_sidecarLinkPath is not null && File.Exists(_sidecarLinkPath))
        {
            File.Delete(_sidecarLinkPath);
        }

        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    protected override ICloudStateStoreFactory CreateFactory() =>
        new SqliteCloudStateStoreFactory(_databasePath);

    protected override CloudStateStoreContext CreateContext() => new(_syncRootPath);

    [Fact]
    public async Task JournalPagingUsesSequenceIndexWithoutTemporarySort()
    {
        await using (ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext()))
        {
        }

        await using SqliteConnection connection = new($"Data Source={_databasePath};Pooling=False");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN SELECT sequence, operation_id FROM operations " +
            "WHERE sequence > 1 AND sequence <= 100000 ORDER BY sequence LIMIT 8;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync();
        List<string> plan = [];
        while (await reader.ReadAsync())
        {
            plan.Add(reader.GetString(3));
        }

        Assert.Contains(plan, detail => detail.Contains("SEARCH operations USING INTEGER PRIMARY KEY", StringComparison.Ordinal));
        Assert.DoesNotContain(plan, detail => detail.Contains("TEMP B-TREE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CommitDoesNotReportCleanupCallbackFailureAsCommitFailure()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SqliteTransaction nativeTransaction =
            (SqliteTransaction)await connection.BeginTransactionAsync();
        int completionCalls = 0;
        await using SqliteCloudStateTransaction transaction = new(
            connection,
            nativeTransaction,
            "in-memory",
            () =>
            {
                completionCalls++;
                throw new InvalidOperationException("test cleanup failure");
            });

        await transaction.CommitAsync();

        Assert.Equal(1, completionCalls);
        await transaction.DisposeAsync();
    }

    [Fact]
    public async Task DisposeReportsCleanupFailureThroughTraceWithoutThrowing()
    {
        await using SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync();
        await using SqliteTransaction nativeTransaction =
            (SqliteTransaction)await connection.BeginTransactionAsync();
        using StringWriter traceOutput = new();
        using TextWriterTraceListener traceListener = new(traceOutput);
        Trace.Listeners.Add(traceListener);

        try
        {
            await using SqliteCloudStateTransaction transaction = new(
                connection,
                nativeTransaction,
                "in-memory",
                () => throw new InvalidOperationException("test cleanup failure"));

            await transaction.DisposeAsync();
            Trace.Flush();

            string diagnostics = traceOutput.ToString();
            Assert.Contains("CfSharp SQLite transaction disposal cleanup failed", diagnostics);
            Assert.Contains("in-memory", diagnostics);
            Assert.Contains("test cleanup failure", diagnostics);
        }
        finally
        {
            Trace.Listeners.Remove(traceListener);
        }
    }

    [Fact]
    public async Task AbruptProcessExitPreservesOnlyDurableWritesAcrossWalRecovery()
    {
        int iterations = ReadCrashRecoveryIterations();
        for (int iteration = 0; iteration < iterations; iteration++)
        {
            string databasePath = iteration == 0
                ? _databasePath
                : Path.Combine(
                    _temporaryDirectory,
                    $"round-{iteration:D3}",
                    "state",
                    "cfsharp.db");
            string syncRootPath = iteration == 0
                ? _syncRootPath
                : Path.Combine(_temporaryDirectory, $"round-{iteration:D3}", "sync-root");
            await VerifyCrashRecoveryRoundAsync(databasePath, syncRootPath);
        }
    }

    private async Task VerifyCrashRecoveryRoundAsync(string databasePath, string syncRootPath)
    {
        Directory.CreateDirectory(syncRootPath);
        (int beforeExitCode, _) = await RunCrashHarnessAsync(
            "before-commit",
            databasePath,
            syncRootPath);
        Assert.NotEqual(0, beforeExitCode);
        Assert.True(File.Exists(databasePath + ".before-commit.started"));

        await using (ICloudStateStore afterBeforeCommit = await new SqliteCloudStateStoreFactory(
            databasePath).OpenAsync(new CloudStateStoreContext(syncRootPath)))
        await using (ICloudStateTransaction readBeforeCommit =
            await afterBeforeCommit.BeginTransactionAsync())
        {
            Assert.Null(await readBeforeCommit.Items.GetByRelativePathAsync("crash-recovery.txt"));
            await readBeforeCommit.RollbackAsync();
        }

        (int afterExitCode, _) = await RunCrashHarnessAsync(
            "after-commit",
            databasePath,
            syncRootPath);
        Assert.NotEqual(0, afterExitCode);
        Assert.True(File.Exists(databasePath + ".after-commit.started"));

        await using ICloudStateStore afterCommit = await new SqliteCloudStateStoreFactory(
            databasePath).OpenAsync(new CloudStateStoreContext(syncRootPath));
        await using ICloudStateTransaction readAfterCommit = await afterCommit.BeginTransactionAsync();
        CloudItemState? recovered = await readAfterCommit.Items.GetByRelativePathAsync("crash-recovery.txt");
        Assert.NotNull(recovered);
        Assert.Equal("revision-1", recovered.RemoteRevision);
        await readAfterCommit.RollbackAsync();

        Assert.Equal("ok", await ExecuteScalarStringAsync(databasePath, "PRAGMA integrity_check;"));
    }

    private static int ReadCrashRecoveryIterations()
    {
        string? value = Environment.GetEnvironmentVariable("CFSHARP_CRASH_RECOVERY_ITERATIONS");
        return int.TryParse(value, out int iterations) && iterations > 0
            ? Math.Min(iterations, 100)
            : 1;
    }

    [Fact]
    public async Task CheckpointPrefixQueryListsOnlyTheRequestedDirectorySubtree()
    {
        await using ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext());
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await using (ICloudStateTransaction write = await store.BeginTransactionAsync())
        {
            await write.Checkpoints.UpsertAsync(
                new CloudStateCheckpoint("cfsharp.directory.\\root", [1], now));
            await write.Checkpoints.UpsertAsync(
                new CloudStateCheckpoint("cfsharp.directory.\\root\\child", [2], now));
            await write.Checkpoints.UpsertAsync(
                new CloudStateCheckpoint("cfsharp.directory.\\other", [3], now));
            await write.CommitAsync();
        }

        await using ICloudStateTransaction read = await store.BeginTransactionAsync();
        IReadOnlyList<CloudStateCheckpoint> checkpoints = await read.Checkpoints
            .ListAsync("cfsharp.directory.\\root");
        Assert.Equal(
            ["cfsharp.directory.\\root", "cfsharp.directory.\\root\\child"],
            checkpoints.Select(checkpoint => checkpoint.Name).ToArray());
        await read.RollbackAsync();
    }

    [Fact]
    public async Task DatabaseInsideSyncRootIsRejected()
    {
        SqliteCloudStateStoreFactory factory = new(
            Path.Combine(_syncRootPath, "state", "cfsharp.db"));

        SqliteCloudStateStoreException exception = await Assert.ThrowsAsync<
            SqliteCloudStateStoreException>(async () =>
                await factory.OpenAsync(CreateContext()));

        Assert.Equal(SqliteCloudStateStoreError.PathInsideSyncRoot, exception.Error);
    }

    [Fact]
    public async Task DatabaseInsideSyncRootViaShortNameIsRejected()
    {
        string shortRoot = GetShortPath(_syncRootPath);
        if (string.Equals(shortRoot, _syncRootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw SkipException.ForSkip("The test volume does not expose an 8.3 short name.");
        }

        SqliteCloudStateStoreFactory factory = new(
            Path.Combine(shortRoot, "short-state", "cfsharp.db"));

        SqliteCloudStateStoreException exception = await Assert.ThrowsAsync<
            SqliteCloudStateStoreException>(async () =>
                await factory.OpenAsync(CreateContext()));

        Assert.Equal(SqliteCloudStateStoreError.PathInsideSyncRoot, exception.Error);
    }

    [Fact]
    public async Task DirectoryLinkIntoSyncRootIsRejected()
    {
        string target = Path.Combine(_syncRootPath, "state");
        Directory.CreateDirectory(target);
        _directoryLinkPath = Path.Combine(_temporaryDirectory, "linked-state");
        Directory.CreateSymbolicLink(_directoryLinkPath, target);
        SqliteCloudStateStoreFactory factory = new(
            Path.Combine(_directoryLinkPath, "cfsharp.db"));

        SqliteCloudStateStoreException exception = await Assert.ThrowsAsync<
            SqliteCloudStateStoreException>(async () =>
                await factory.OpenAsync(CreateContext()));

        Assert.Equal(SqliteCloudStateStoreError.PathInsideSyncRoot, exception.Error);
    }

    [Fact]
    public async Task ReparsePointWalSidecarIsRejected()
    {
        await using (ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext()))
        {
        }

        // The handle lease intentionally creates the WAL/SHM sidecars before SQLite opens the
        // database, closing the reparse-point planting window. Remove the ordinary sidecars so
        // this test can replace the WAL path with the malicious link it is meant to reject.
        File.Delete(_databasePath + "-wal");
        File.Delete(_databasePath + "-shm");
        string target = Path.Combine(_temporaryDirectory, "wal-target");
        await File.WriteAllTextAsync(target, "sidecar target");
        _sidecarLinkPath = _databasePath + "-wal";
        File.CreateSymbolicLink(_sidecarLinkPath, target);

        SqliteCloudStateStoreException exception = await Assert.ThrowsAsync<
            SqliteCloudStateStoreException>(async () =>
                await CreateFactory().OpenAsync(CreateContext()));

        Assert.Equal(SqliteCloudStateStoreError.InvalidPath, exception.Error);
    }

    [Fact]
    public async Task OwnershipIsExclusiveAndReleasedByDisposal()
    {
        SqliteCloudStateStoreFactory factory = new(_databasePath);
        ICloudStateStore first = await factory.OpenAsync(CreateContext());
        try
        {
            SqliteCloudStateStoreException exception = await Assert.ThrowsAsync<
                SqliteCloudStateStoreException>(async () =>
                    await factory.OpenAsync(CreateContext()));
            Assert.Equal(SqliteCloudStateStoreError.AlreadyInUse, exception.Error);
        }
        finally
        {
            await first.DisposeAsync();
        }

        await using ICloudStateStore reopened = await factory.OpenAsync(CreateContext());
    }

    [Fact]
    public async Task DatabaseCannotBeReusedForAnotherSyncRoot()
    {
        await using (ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext()))
        {
        }

        string otherRoot = Path.Combine(_temporaryDirectory, "other-sync-root");
        Directory.CreateDirectory(otherRoot);
        SqliteCloudStateStoreException exception = await Assert.ThrowsAsync<
            SqliteCloudStateStoreException>(async () =>
                await CreateFactory().OpenAsync(new CloudStateStoreContext(otherRoot)));

        Assert.Equal(SqliteCloudStateStoreError.SyncRootMismatch, exception.Error);
    }

    [Fact]
    public async Task StoreDisposalRejectsAnActiveTransactionWithoutReleasingOwnership()
    {
        SqliteCloudStateStoreFactory factory = new(_databasePath);
        ICloudStateStore store = await factory.OpenAsync(CreateContext());
        ICloudStateTransaction transaction = await store.BeginTransactionAsync();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await store.DisposeAsync());

            SqliteCloudStateStoreException exception = await Assert.ThrowsAsync<
                SqliteCloudStateStoreException>(async () =>
                    await factory.OpenAsync(CreateContext()));
            Assert.Equal(SqliteCloudStateStoreError.AlreadyInUse, exception.Error);
        }
        finally
        {
            await transaction.DisposeAsync();
            await store.DisposeAsync();
        }
    }

    [Fact]
    public async Task VersionZeroSchemaMigratesToCurrentVersion()
    {
        await ExecuteSqlAsync(
            _databasePath,
            """
            CREATE TABLE cfsharp_schema (
                singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 1),
                version INTEGER NOT NULL
            );
            INSERT INTO cfsharp_schema(singleton, version) VALUES(1, 0);
            """);

        SqliteCloudStateStoreFactory factory = new(_databasePath);
        await using (ICloudStateStore store = await factory.OpenAsync(CreateContext()))
        {
        }

        Assert.Equal(
            6L,
            await ExecuteScalarInt64Async(
                _databasePath,
                "SELECT version FROM cfsharp_schema WHERE singleton = 1;"));
        Assert.Equal(
            1L,
            await ExecuteScalarInt64Async(
                _databasePath,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'items';"));
    }

    [Fact]
    public async Task VersionOneRemoteBatchSchemaMigratesToVersionSix()
    {
        await using (ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext()))
        {
        }

        await ExecuteSqlAsync(
            _databasePath,
            "UPDATE cfsharp_schema SET version = 1 WHERE singleton = 1;");

        await using (ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext()))
        {
        }

        Assert.Equal(
            6L,
            await ExecuteScalarInt64Async(
                _databasePath,
                "SELECT version FROM cfsharp_schema WHERE singleton = 1;"));
        Assert.Equal(
            2L,
            await ExecuteScalarInt64Async(
                _databasePath,
                "SELECT COUNT(*) FROM pragma_table_info('remote_batches') " +
                "WHERE name IN ('fingerprint', 'last_change_id');"));
    }

    [Fact]
    public async Task VersionTwoEchoSuppressionSchemaMigratesToVersionSix()
    {
        await using (ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext()))
        {
        }

        await ExecuteSqlAsync(
            _databasePath,
            "UPDATE cfsharp_schema SET version = 2 WHERE singleton = 1;");

        await using (ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext()))
        {
        }

        Assert.Equal(
            6L,
            await ExecuteScalarInt64Async(
                _databasePath,
                "SELECT version FROM cfsharp_schema WHERE singleton = 1;"));
        Assert.Equal(
            2L,
            await ExecuteScalarInt64Async(
                _databasePath,
                "SELECT COUNT(*) FROM pragma_table_info('echo_suppressions') " +
                "WHERE name IN ('previous_relative_path', 'remaining_observations');"));
    }

    [Fact]
    public async Task VersionFourUpgradePreservesRecoveryMetadata()
    {
        await using (ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext()))
        {
            await using ICloudStateTransaction transaction = await store.BeginTransactionAsync();
            await transaction.Checkpoints.UpsertAsync(new CloudStateCheckpoint("existing/checkpoint", new byte[] { 7 }, DateTimeOffset.UtcNow));
            await transaction.CommitAsync();
        }

        await ExecuteSqlAsync(_databasePath, "UPDATE cfsharp_schema SET version = 4 WHERE singleton = 1;");
        await using (ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext()))
        {
            await using ICloudStateTransaction transaction = await store.BeginTransactionAsync();
            Assert.Equal(new byte[] { 7 }, (await transaction.Checkpoints.GetAsync("existing/checkpoint"))!.Value.ToArray());
        }

        Assert.Equal(6L, await ExecuteScalarInt64Async(_databasePath,
            "SELECT version FROM cfsharp_schema WHERE singleton = 1;"));
    }

    [Fact]
    public async Task VersionFiveUpgradePreservesCoordinationRowsAndSequenceAllocation()
    {
        Guid itemId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        Guid conflictId = Guid.NewGuid();
        Guid suppressionId = Guid.NewGuid();
        DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        CloudOperationJournalEntry original;
        await using (ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext()))
        await using (ICloudStateTransaction write = await store.BeginTransactionAsync())
        {
            await write.Items.UpsertAsync(new CloudItemState(itemId, "retained-remote", "Docs\\gone.txt",
                CloudItemKind.File, "retained-revision", 321, true, timestamp));
            await write.Checkpoints.UpsertAsync(new CloudStateCheckpoint("retained/checkpoint", [1, 2], timestamp));
            original = await write.Operations.EnqueueAsync(new CloudOperationJournalEntry(operationId,
                CloudStateOperationKind.Move, itemId, [3, 4], timestamp, 3, timestamp.AddMinutes(1)));
            await write.Conflicts.UpsertAsync(new CloudConflictState(conflictId, itemId, CloudStateConflictKind.Move, [5, 6], timestamp));
            await write.RemoteBatches.UpsertAsync(new CloudRemoteBatchState("retained-batch", [7], 1, 2,
                CloudRemoteBatchStatus.Applying, [8], timestamp, new byte[] { 9 }, "retained-change"));
            await write.EchoSuppressions.UpsertAsync(new CloudEchoSuppressionState(suppressionId,
                itemId, CloudStateOperationKind.Move, "Docs\\gone.txt", [10], timestamp.AddMinutes(5), "old.txt", 2));
            await write.CommitAsync();
        }

        await ExecuteSqlAsync(_databasePath, "UPDATE cfsharp_schema SET version = 5 WHERE singleton = 1;");
        await using (ICloudStateStore reopened = await CreateFactory().OpenAsync(CreateContext()))
        await using (ICloudStateTransaction verify = await reopened.BeginTransactionAsync())
        {
            CloudItemState item = (await verify.Items.GetByItemIdAsync(itemId))!;
            Assert.Equal("Docs\\gone.txt", item.RelativePath);
            Assert.Equal("retained-remote", item.RemoteId);
            Assert.Equal("retained-revision", item.RemoteRevision);
            Assert.Equal(321, item.LocalFileId);
            Assert.True(item.IsTombstone);
            Assert.Equal(timestamp, item.UpdatedAt);
            Assert.Equal(new byte[] { 1, 2 }, (await verify.Checkpoints.GetAsync("retained/checkpoint"))!.Value.ToArray());
            CloudOperationJournalEntry operation = (await verify.Operations.GetAsync(operationId))!;
            Assert.Equal(original.Sequence, operation.Sequence);
            Assert.Equal(itemId, operation.ItemId);
            Assert.Equal(new byte[] { 3, 4 }, operation.Payload.ToArray());
            Assert.Equal(timestamp, operation.CreatedAt);
            Assert.Equal(3, operation.AttemptCount);
            Assert.Equal(original.RetryAfter, operation.RetryAfter);
            Assert.Equal(new byte[] { 5, 6 }, (await verify.Conflicts.GetAsync(conflictId))!.Payload.ToArray());
            CloudRemoteBatchState batch = (await verify.RemoteBatches.GetAsync("retained-batch"))!;
            Assert.Equal(new byte[] { 7 }, batch.Cursor.ToArray());
            Assert.Equal(new byte[] { 9 }, batch.Fingerprint.ToArray());
            Assert.Equal("retained-change", batch.LastAppliedChangeId);
            CloudEchoSuppressionState suppression = (await verify.EchoSuppressions.GetAsync(suppressionId))!;
            Assert.Equal("old.txt", suppression.PreviousRelativePath);
            Assert.Equal(2, suppression.RemainingObservations);
            Assert.Equal(new byte[] { 10 }, suppression.Payload.ToArray());
            CloudOperationJournalEntry next = await verify.Operations.EnqueueAsync(new CloudOperationJournalEntry(
                Guid.NewGuid(), CloudStateOperationKind.MetadataUpdate, itemId, [11], timestamp));
            Assert.True(next.Sequence > original.Sequence);
            await verify.CommitAsync();
        }

        Assert.Equal(6L, await ExecuteScalarInt64Async(_databasePath,
            "SELECT version FROM cfsharp_schema WHERE singleton = 1;"));
    }

    [Fact]
    public async Task VersionSixFenceSurvivesProcessExitBeforeApplicationCommit()
    {
        await using (ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext()))
        await using (ICloudStateTransaction seed = await store.BeginTransactionAsync())
        {
            await seed.Checkpoints.UpsertAsync(new CloudStateCheckpoint("retained/before-crash", [12], DateTimeOffset.UtcNow));
            await seed.CommitAsync();
        }

        await ExecuteSqlAsync(_databasePath, "UPDATE cfsharp_schema SET version = 5 WHERE singleton = 1;");
        (int exitCode, _) = await RunCrashHarnessAsync("before-commit", _databasePath, _syncRootPath);
        Assert.NotEqual(0, exitCode);
        Assert.True(File.Exists(_databasePath + ".before-commit.started"));
        Assert.Equal(6L, await ExecuteScalarInt64Async(_databasePath,
            "SELECT version FROM cfsharp_schema WHERE singleton = 1;"));
        await using ICloudStateStore recovered = await CreateFactory().OpenAsync(CreateContext());
        await using ICloudStateTransaction verify = await recovered.BeginTransactionAsync();
        Assert.Equal(new byte[] { 12 }, (await verify.Checkpoints.GetAsync("retained/before-crash"))!.Value.ToArray());
        Assert.Null(await verify.Items.GetByRelativePathAsync("crash-recovery.txt"));
    }

    [Fact]
    public async Task NewerSchemaVersionIsRejected()
    {
        await ExecuteSqlAsync(
            _databasePath,
            """
            CREATE TABLE cfsharp_schema (
                singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 1),
                version INTEGER NOT NULL
            );
            INSERT INTO cfsharp_schema(singleton, version) VALUES(1, 7);
            """);

        SqliteCloudStateStoreException exception = await Assert.ThrowsAsync<
            SqliteCloudStateStoreException>(async () =>
                await CreateFactory().OpenAsync(CreateContext()));

        Assert.Equal(SqliteCloudStateStoreError.UnsupportedSchema, exception.Error);
    }

    [Fact]
    public async Task UnrecognizedSqliteDatabaseIsRejected()
    {
        await ExecuteSqlAsync(_databasePath, "CREATE TABLE application_data(value TEXT NOT NULL);");

        SqliteCloudStateStoreException exception = await Assert.ThrowsAsync<
            SqliteCloudStateStoreException>(async () =>
                await CreateFactory().OpenAsync(CreateContext()));

        Assert.Equal(SqliteCloudStateStoreError.InvalidSchema, exception.Error);
    }

    [Fact]
    public async Task VersionOneDatabaseMissingRequiredTableIsRejected()
    {
        await ExecuteSqlAsync(
            _databasePath,
            """
            CREATE TABLE cfsharp_schema (
                singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 1),
                version INTEGER NOT NULL
            );
            INSERT INTO cfsharp_schema(singleton, version) VALUES(1, 1);
            """);

        SqliteCloudStateStoreException exception = await Assert.ThrowsAsync<
            SqliteCloudStateStoreException>(async () =>
                await CreateFactory().OpenAsync(CreateContext()));

        Assert.Equal(SqliteCloudStateStoreError.InvalidSchema, exception.Error);
    }

    [Fact]
    public async Task InitializationEnablesWalAndConnectionSafetyPragmas()
    {
        await using (ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext()))
        {
        }

        Assert.Equal("wal", await ExecuteScalarStringAsync(_databasePath, "PRAGMA journal_mode;"));

        await using SqliteConnection connection = new(CreateConnectionString(_databasePath));
        await connection.OpenAsync();
        await SqliteSchema.ConfigureConnectionAsync(connection, 1234, CancellationToken.None);
        Assert.Equal(1L, await ExecuteScalarInt64Async(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(1234L, await ExecuteScalarInt64Async(connection, "PRAGMA busy_timeout;"));
    }

    [Fact]
    public async Task RelativePathCollationMatchesUnicodeCaseInsensitiveFileNames()
    {
        await using ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext());
        CloudItemState upper = new(
            Guid.NewGuid(),
            "remote-upper",
            "Ä.txt",
            CloudItemKind.File,
            null,
            null,
            false,
            DateTimeOffset.UtcNow);
        await using (ICloudStateTransaction write = await store.BeginTransactionAsync())
        {
            await write.Items.UpsertAsync(upper);
            await write.CommitAsync();
        }

        await using (ICloudStateTransaction read = await store.BeginTransactionAsync())
        {
            CloudItemState? equivalent = await read.Items.GetByRelativePathAsync("ä.txt");
            Assert.Equal(upper.ItemId, equivalent?.ItemId);
            Assert.Single(await read.Items.ListSubtreeAsync(""));
            await read.RollbackAsync();
        }

        await using ICloudStateTransaction duplicate = await store.BeginTransactionAsync();
        SqliteCloudStateStoreException exception = await Assert.ThrowsAsync<
            SqliteCloudStateStoreException>(async () =>
                await duplicate.Items.UpsertAsync(
                    new CloudItemState(
                        Guid.NewGuid(),
                        "remote-lower",
                        "ä.txt",
                        CloudItemKind.File,
                        null,
                        null,
                        false,
                        DateTimeOffset.UtcNow)));
        Assert.Equal(19, exception.SqliteErrorCode);
        await duplicate.RollbackAsync();
    }

    [Fact]
    public async Task StoreConnectionsEnforceForeignKeysAndPreserveSqliteCodes()
    {
        await using ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext());
        await using ICloudStateTransaction transaction = await store.BeginTransactionAsync();

        SqliteCloudStateStoreException exception = await Assert.ThrowsAsync<
            SqliteCloudStateStoreException>(async () =>
                await transaction.Operations.EnqueueAsync(
                    new CloudOperationJournalEntry(
                        Guid.NewGuid(),
                        CloudStateOperationKind.Create,
                        Guid.NewGuid(),
                        [],
                        DateTimeOffset.UtcNow)));

        Assert.Equal(SqliteCloudStateStoreError.DatabaseFailure, exception.Error);
        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.NotEqual(0, exception.SqliteExtendedErrorCode);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task InvalidPersistedValueIsReportedAsInvalidSchema()
    {
        await using (ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext()))
        {
        }

        await ExecuteSqlAsync(
            _databasePath,
            """
            INSERT INTO items(
                item_id, remote_id, relative_path, kind, remote_revision,
                local_file_id, is_tombstone, updated_at_ticks)
            VALUES('not-a-guid', 'remote-invalid', 'invalid.txt', 0, NULL, NULL, 0, 0);
            """);

        await using ICloudStateStore reopened = await CreateFactory().OpenAsync(CreateContext());
        await using ICloudStateTransaction transaction = await reopened.BeginTransactionAsync();
        SqliteCloudStateStoreException exception = await Assert.ThrowsAsync<
            SqliteCloudStateStoreException>(async () =>
                await transaction.Items.GetByRemoteIdAsync("remote-invalid"));

        Assert.Equal(SqliteCloudStateStoreError.InvalidSchema, exception.Error);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task InvalidPersistedEnumValuesAreReportedAsInvalidSchema()
    {
        await using (ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext()))
        {
        }

        await ExecuteSqlAsync(
            _databasePath,
            """
            INSERT INTO items(
                item_id, remote_id, relative_path, kind, remote_revision,
                local_file_id, is_tombstone, updated_at_ticks)
            VALUES('00000000-0000-0000-0000-000000000101', 'remote-invalid-enum',
                   'invalid-enum-item.txt', 999, NULL, NULL, 0, 0);
            INSERT INTO operations(
                operation_id, kind, item_id, payload, created_at_ticks,
                attempt_count, retry_after_ticks)
            VALUES('00000000-0000-0000-0000-000000000102', 999, NULL, X'', 0, 0, NULL);
            INSERT INTO conflicts(
                conflict_id, item_id, kind, payload, created_at_ticks)
            VALUES('00000000-0000-0000-0000-000000000103', NULL, 999, X'', 0);
            INSERT INTO remote_batches(
                batch_id, cursor, applied_entry_count, total_entry_count,
                status, payload, fingerprint, last_change_id, updated_at_ticks)
            VALUES('invalid-enum-batch', X'', 0, 0, 999, X'', X'', NULL, 0);
            INSERT INTO echo_suppressions(
                suppression_id, item_id, kind, relative_path, previous_relative_path,
                payload, expires_at_ticks, remaining_observations)
            VALUES('00000000-0000-0000-0000-000000000104', NULL, 999,
                   'invalid-enum-echo.txt', NULL, X'', 0, 1);
            """);

        await using ICloudStateStore reopened = await CreateFactory().OpenAsync(CreateContext());
        await using ICloudStateTransaction transaction = await reopened.BeginTransactionAsync();

        SqliteCloudStateStoreException itemException = await Assert.ThrowsAsync<
            SqliteCloudStateStoreException>(async () =>
                await transaction.Items.GetByRemoteIdAsync("remote-invalid-enum"));
        Assert.Equal(SqliteCloudStateStoreError.InvalidSchema, itemException.Error);

        SqliteCloudStateStoreException operationException = await Assert.ThrowsAsync<
            SqliteCloudStateStoreException>(async () =>
                await transaction.Operations.GetAsync(
                    Guid.Parse("00000000-0000-0000-0000-000000000102")));
        Assert.Equal(SqliteCloudStateStoreError.InvalidSchema, operationException.Error);

        SqliteCloudStateStoreException conflictException = await Assert.ThrowsAsync<
            SqliteCloudStateStoreException>(async () =>
                await transaction.Conflicts.GetAsync(
                    Guid.Parse("00000000-0000-0000-0000-000000000103")));
        Assert.Equal(SqliteCloudStateStoreError.InvalidSchema, conflictException.Error);

        SqliteCloudStateStoreException batchException = await Assert.ThrowsAsync<
            SqliteCloudStateStoreException>(async () =>
                await transaction.RemoteBatches.GetAsync("invalid-enum-batch"));
        Assert.Equal(SqliteCloudStateStoreError.InvalidSchema, batchException.Error);

        SqliteCloudStateStoreException suppressionException = await Assert.ThrowsAsync<
            SqliteCloudStateStoreException>(async () =>
                await transaction.EchoSuppressions.GetAsync(
                    Guid.Parse("00000000-0000-0000-0000-000000000104")));
        Assert.Equal(SqliteCloudStateStoreError.InvalidSchema, suppressionException.Error);

        await transaction.RollbackAsync();
    }

    private static async Task ExecuteSqlAsync(string databasePath, string sql)
    {
        string? parent = Path.GetDirectoryName(databasePath);
        Assert.False(string.IsNullOrEmpty(parent));
        Directory.CreateDirectory(parent);
        await using SqliteConnection connection = new(CreateConnectionString(databasePath));
        await connection.OpenAsync();
        await SqliteSchema.ConfigureConnectionAsync(connection, 5000, CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<(int ExitCode, string Output)> RunCrashHarnessAsync(
        string mode,
        string? databasePath = null,
        string? syncRootPath = null)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CfSharp.sln")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        string harness = Path.Combine(
            root!.FullName,
            "tests",
            "CfSharp.Storage.Sqlite.CrashHarness",
            "bin",
            "Release",
            "net10.0-windows",
            "CfSharp.Storage.Sqlite.CrashHarness.dll");
        Assert.True(File.Exists(harness), $"Crash harness was not built: {harness}");

        ProcessStartInfo startInfo = new("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(harness);
        startInfo.ArgumentList.Add(databasePath ?? _databasePath);
        startInfo.ArgumentList.Add(syncRootPath ?? _syncRootPath);
        startInfo.ArgumentList.Add(mode);

        using Process process = new() { StartInfo = startInfo };
        Assert.True(process.Start());
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);
        string output = await process.StandardOutput.ReadToEndAsync() +
            await process.StandardError.ReadToEndAsync();
        return (process.ExitCode, output);
    }

    private static async Task<long> ExecuteScalarInt64Async(string databasePath, string sql)
    {
        await using SqliteConnection connection = new(CreateConnectionString(databasePath));
        await connection.OpenAsync();
        await SqliteSchema.ConfigureConnectionAsync(connection, 5000, CancellationToken.None);
        return await ExecuteScalarInt64Async(connection, sql);
    }

    private static async Task<long> ExecuteScalarInt64Async(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = await command.ExecuteScalarAsync();
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ExecuteScalarStringAsync(string databasePath, string sql)
    {
        await using SqliteConnection connection = new(CreateConnectionString(databasePath));
        await connection.OpenAsync();
        await SqliteSchema.ConfigureConnectionAsync(connection, 5000, CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = await command.ExecuteScalarAsync();
        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string GetShortPath(string path)
    {
        char[] buffer = new char[260];
        while (true)
        {
            uint length = GetShortPathName(path, buffer, checked((uint)buffer.Length));
            if (length == 0)
            {
                throw new IOException(
                    $"GetShortPathName failed for '{path}' with Win32 error " +
                    $"{Marshal.GetLastWin32Error()}.");
            }

            if (length < buffer.Length - 1)
            {
                return new string(buffer, 0, checked((int)length));
            }

            Array.Resize(ref buffer, checked((int)length + 1));
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(
        string longPath,
        [Out] char[] shortPath,
        uint bufferLength);

    private static string CreateConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
}
