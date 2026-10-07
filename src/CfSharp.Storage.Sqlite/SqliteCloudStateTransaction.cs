using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CfSharp.Storage.Sqlite;

internal sealed class SqliteCloudStateTransaction : ICloudStateTransaction
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly string _databasePath;
    private readonly Action _completed;
    private int _terminal;

    internal SqliteCloudStateTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string databasePath,
        Action completed)
    {
        _connection = connection;
        _transaction = transaction;
        _databasePath = databasePath;
        _completed = completed;
        Items = new ItemRepository(this);
        Checkpoints = new CheckpointRepository(this);
        Operations = new OperationRepository(this);
        Conflicts = new ConflictRepository(this);
        RemoteBatches = new RemoteBatchRepository(this);
        EchoSuppressions = new EchoSuppressionRepository(this);
    }

    public ICloudItemStateRepository Items { get; }

    public ICloudCheckpointRepository Checkpoints { get; }

    public ICloudOperationJournal Operations { get; }

    public ICloudConflictRepository Conflicts { get; }

    public ICloudRemoteBatchRepository RemoteBatches { get; }

    public ICloudEchoSuppressionRepository EchoSuppressions { get; }

    public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
    {
        CheckActive(cancellationToken);
        try
        {
            // Once the pre-commit cancellation check passed, COMMIT must run to an
            // unambiguous terminal point. A caller token racing this statement could otherwise
            // report cancellation after SQLite had already committed.
            await _transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await RollBackAfterFailureAsync().ConfigureAwait(false);
            _ = await CompleteAsync().ConfigureAwait(false);
            if (exception is OperationCanceledException)
            {
                throw;
            }

            throw SqliteSchema.TranslateFailure(
                _databasePath,
                "The SQLite state transaction could not be committed.",
                exception);
        }

        Exception? cleanupFailure = await CompleteAsync().ConfigureAwait(false);
        if (cleanupFailure is not null)
        {
            Trace.TraceError(
                "CfSharp SQLite transaction committed, but cleanup failed: {0}",
                cleanupFailure);
        }
    }

    public async ValueTask RollbackAsync(CancellationToken cancellationToken = default)
    {
        CheckActive(cancellationToken);

        Exception? rollbackFailure = null;
        try
        {
            await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            rollbackFailure = exception;
        }

        Exception? cleanupFailure = await CompleteAsync().ConfigureAwait(false);
        if (cleanupFailure is not null)
        {
            rollbackFailure = rollbackFailure is null
                ? cleanupFailure
                : new AggregateException(rollbackFailure, cleanupFailure);
        }

        if (rollbackFailure is OperationCanceledException)
        {
            throw rollbackFailure;
        }

        if (rollbackFailure is not null)
        {
            throw SqliteSchema.TranslateFailure(
                _databasePath,
                "The SQLite state transaction could not be rolled back.",
                rollbackFailure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _terminal) != 0)
        {
            return;
        }

        Exception? rollbackFailure = null;
        try
        {
            await _transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            rollbackFailure = exception;
        }

        Exception? cleanupFailure = await CompleteAsync().ConfigureAwait(false);
        if (cleanupFailure is not null)
        {
            rollbackFailure = rollbackFailure is null
                ? cleanupFailure
                : new AggregateException(rollbackFailure, cleanupFailure);
        }

        if (rollbackFailure is not null)
        {
            // DisposeAsync is commonly running while another exception is already unwinding
            // an await-using scope. Do not mask that business failure with best-effort rollback
            // or connection cleanup diagnostics; explicit RollbackAsync remains available when
            // callers need a surfaced rollback result.
            Trace.TraceError(
                "CfSharp SQLite transaction disposal cleanup failed for '{0}': {1}",
                _databasePath,
                rollbackFailure);
        }
    }

    internal SqliteCommand CreateCommand(string commandText, CancellationToken cancellationToken)
    {
        CheckActive(cancellationToken);
        SqliteCommand command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = commandText;
        return command;
    }

    internal async ValueTask<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        CheckActive(cancellationToken);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw SqliteSchema.TranslateFailure(_databasePath, failureMessage, exception);
        }
        catch (Exception exception) when (
            exception is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            throw new SqliteCloudStateStoreException(
                SqliteCloudStateStoreError.InvalidSchema,
                _databasePath,
                "The SQLite state database contains an invalid persisted value.",
                exception);
        }
    }

    private void CheckActive(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _terminal) != 0)
        {
            throw new InvalidOperationException("The transaction has already terminated.");
        }
    }

    private async ValueTask RollBackAfterFailureAsync()
    {
        try
        {
            await _transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The original commit failure is more actionable and retains the SQLite result code.
        }
    }

    private async ValueTask<Exception?> CompleteAsync()
    {
        if (Interlocked.Exchange(ref _terminal, 1) != 0)
        {
            return null;
        }

        List<Exception>? failures = null;
        try
        {
            try
            {
                await _transaction.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            try
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        finally
        {
            try
            {
                _completed();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        return failures is null ? null : new AggregateException(failures);
    }

    private sealed class ItemRepository : ICloudItemStateRepository
    {
        private const string SelectColumns = """
            SELECT item_id, remote_id, relative_path, kind, remote_revision,
                   local_file_id, is_tombstone, updated_at_ticks
            FROM items
            """;

        private readonly SqliteCloudStateTransaction _owner;

        internal ItemRepository(SqliteCloudStateTransaction owner)
        {
            _owner = owner;
        }

        public ValueTask<CloudItemState?> GetByItemIdAsync(
            Guid itemId,
            CancellationToken cancellationToken = default) =>
            GetSingleAsync(
                SelectColumns + " WHERE item_id = $value;",
                itemId.ToString("D", CultureInfo.InvariantCulture),
                cancellationToken);

        public ValueTask<CloudItemState?> GetByRemoteIdAsync(
            string remoteId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);
            return GetSingleAsync(SelectColumns + " WHERE remote_id = $value;", remoteId, cancellationToken);
        }

        public ValueTask<CloudItemState?> GetByRelativePathAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(relativePath);
            return GetSingleAsync(
                SelectColumns + " WHERE relative_path = $value COLLATE CFSHARP_UNICODE_NOCASE;",
                relativePath,
                cancellationToken);
        }

        public async ValueTask<IReadOnlyList<CloudItemState>> ListSubtreeAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(relativePath);
            return await _owner.ExecuteAsync(
                async token =>
                {
                    string condition = relativePath.Length == 0
                        ? string.Empty
                        : """
                            WHERE relative_path = $path COLLATE CFSHARP_UNICODE_NOCASE
                              OR substr(relative_path, 1, length($primary_prefix)) = $primary_prefix COLLATE CFSHARP_UNICODE_NOCASE
                              OR substr(relative_path, 1, length($alternate_prefix)) = $alternate_prefix COLLATE CFSHARP_UNICODE_NOCASE
                           """;
                    await using SqliteCommand command = _owner.CreateCommand(
                        SelectColumns + condition +
                        """
                         ORDER BY
                           (length(relative_path) - length(replace(relative_path, '\', '')) +
                            length(relative_path) - length(replace(relative_path, '/', ''))),
                           length(relative_path),
                           relative_path COLLATE CFSHARP_UNICODE_NOCASE,
                           item_id;
                        """,
                        token);
                    if (relativePath.Length != 0)
                    {
                        command.Parameters.AddWithValue("$path", relativePath);
                        command.Parameters.AddWithValue(
                            "$primary_prefix",
                            relativePath + Path.DirectorySeparatorChar);
                        command.Parameters.AddWithValue(
                            "$alternate_prefix",
                            relativePath + Path.AltDirectorySeparatorChar);
                    }

                    await using SqliteDataReader reader = await command
                        .ExecuteReaderAsync(token)
                        .ConfigureAwait(false);
                    List<CloudItemState> items = [];
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        items.Add(ReadItem(reader));
                    }

                    return (IReadOnlyList<CloudItemState>)items;
                },
                "The SQLite item subtree could not be listed.",
                cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask UpsertAsync(
            CloudItemState item,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(item);
            await _owner.ExecuteAsync(
                async token =>
                {
                    await using SqliteCommand command = _owner.CreateCommand(
                        """
                        INSERT INTO items(
                            item_id, remote_id, relative_path, kind, remote_revision,
                            local_file_id, is_tombstone, updated_at_ticks)
                        VALUES(
                            $item_id, $remote_id, $relative_path, $kind, $remote_revision,
                            $local_file_id, $is_tombstone, $updated_at_ticks)
                        ON CONFLICT(item_id) DO UPDATE SET
                            remote_id = excluded.remote_id,
                            relative_path = excluded.relative_path,
                            kind = excluded.kind,
                            remote_revision = excluded.remote_revision,
                            local_file_id = excluded.local_file_id,
                            is_tombstone = excluded.is_tombstone,
                            updated_at_ticks = excluded.updated_at_ticks;
                        """,
                        token);
                    command.Parameters.AddWithValue("$item_id", item.ItemId.ToString("D"));
                    command.Parameters.AddWithValue("$remote_id", item.RemoteId);
                    command.Parameters.AddWithValue("$relative_path", item.RelativePath);
                    command.Parameters.AddWithValue("$kind", (int)item.Kind);
                    command.Parameters.AddWithValue("$remote_revision", (object?)item.RemoteRevision ?? DBNull.Value);
                    command.Parameters.AddWithValue("$local_file_id", (object?)item.LocalFileId ?? DBNull.Value);
                    command.Parameters.AddWithValue("$is_tombstone", item.IsTombstone ? 1 : 0);
                    command.Parameters.AddWithValue("$updated_at_ticks", ToUtcTicks(item.UpdatedAt));
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                },
                "The SQLite item state could not be written.",
                cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask RemoveAsync(
            Guid itemId,
            CancellationToken cancellationToken = default)
        {
            await ExecuteDeleteAsync(
                _owner,
                "DELETE FROM items WHERE item_id = $value;",
                itemId.ToString("D"),
                "The SQLite item state could not be removed.",
                cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask<CloudItemState?> GetSingleAsync(
            string sql,
            object value,
            CancellationToken cancellationToken) =>
            await _owner.ExecuteAsync(
                async token =>
                {
                    await using SqliteCommand command = _owner.CreateCommand(sql, token);
                    command.Parameters.AddWithValue("$value", value);
                    await using SqliteDataReader reader = await command
                        .ExecuteReaderAsync(token)
                        .ConfigureAwait(false);
                    return await reader.ReadAsync(token).ConfigureAwait(false)
                        ? ReadItem(reader)
                        : null;
                },
                "The SQLite item state could not be read.",
                cancellationToken).ConfigureAwait(false);

        private static CloudItemState ReadItem(SqliteDataReader reader) =>
            new(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                ReadDefinedEnum<CloudItemKind>(reader.GetInt32(3), "items.kind"),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.GetInt64(6) != 0,
                FromUtcTicks(reader.GetInt64(7)));
    }

    private sealed class CheckpointRepository : ICloudCheckpointRepository
    {
        private readonly SqliteCloudStateTransaction _owner;

        internal CheckpointRepository(SqliteCloudStateTransaction owner)
        {
            _owner = owner;
        }

        public async ValueTask<CloudStateCheckpoint?> GetAsync(
            string name,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            return await _owner.ExecuteAsync(
                async token =>
                {
                    await using SqliteCommand command = _owner.CreateCommand(
                        "SELECT value, updated_at_ticks FROM checkpoints WHERE name = $name;",
                        token);
                    command.Parameters.AddWithValue("$name", name);
                    await using SqliteDataReader reader = await command
                        .ExecuteReaderAsync(token)
                        .ConfigureAwait(false);
                    return await reader.ReadAsync(token).ConfigureAwait(false)
                        ? new CloudStateCheckpoint(name, ReadBytes(reader, 0), FromUtcTicks(reader.GetInt64(1)))
                        : null;
                },
                "The SQLite checkpoint could not be read.",
                cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<IReadOnlyList<CloudStateCheckpoint>> ListAsync(
            string namePrefix,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(namePrefix);
            return await _owner.ExecuteAsync(
                async token =>
                {
                    await using SqliteCommand command = _owner.CreateCommand(
                        """
                        SELECT name, value, updated_at_ticks
                        FROM checkpoints
                        WHERE substr(name, 1, length($prefix)) = $prefix
                        ORDER BY name COLLATE BINARY;
                        """,
                        token);
                    command.Parameters.AddWithValue("$prefix", namePrefix);
                    await using SqliteDataReader reader = await command
                        .ExecuteReaderAsync(token)
                        .ConfigureAwait(false);
                    List<CloudStateCheckpoint> checkpoints = [];
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        checkpoints.Add(new CloudStateCheckpoint(
                            reader.GetString(0),
                            ReadBytes(reader, 1),
                            FromUtcTicks(reader.GetInt64(2))));
                    }

                    return (IReadOnlyList<CloudStateCheckpoint>)checkpoints;
                },
                "The SQLite checkpoints could not be listed.",
                cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask UpsertAsync(
            CloudStateCheckpoint checkpoint,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(checkpoint);
            await _owner.ExecuteAsync(
                async token =>
                {
                    await using SqliteCommand command = _owner.CreateCommand(
                        """
                        INSERT INTO checkpoints(name, value, updated_at_ticks)
                        VALUES($name, $value, $updated_at_ticks)
                        ON CONFLICT(name) DO UPDATE SET
                            value = excluded.value,
                            updated_at_ticks = excluded.updated_at_ticks;
                        """,
                        token);
                    command.Parameters.AddWithValue("$name", checkpoint.Name);
                    command.Parameters.AddWithValue("$value", checkpoint.Value.ToArray());
                    command.Parameters.AddWithValue("$updated_at_ticks", ToUtcTicks(checkpoint.UpdatedAt));
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                },
                "The SQLite checkpoint could not be written.",
                cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask RemoveAsync(
            string name,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            await ExecuteDeleteAsync(
                _owner,
                "DELETE FROM checkpoints WHERE name = $value;",
                name,
                "The SQLite checkpoint could not be removed.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class OperationRepository : ICloudOperationJournal, ICloudOperationJournalPaging
    {
        private const string SelectColumns = """
            SELECT operation_id, kind, item_id, payload, created_at_ticks,
                   attempt_count, retry_after_ticks, sequence
            FROM operations
            """;

        private readonly SqliteCloudStateTransaction _owner;

        internal OperationRepository(SqliteCloudStateTransaction owner)
        {
            _owner = owner;
        }

        public async ValueTask<CloudOperationJournalEntry> EnqueueAsync(
            CloudOperationJournalEntry operation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(operation);
            if (operation.Sequence != 0)
            {
                throw new ArgumentException("A new operation must not have a sequence.", nameof(operation));
            }

            long sequence = await _owner.ExecuteAsync(
                async token =>
                {
                    await using SqliteCommand command = _owner.CreateCommand(
                        """
                        INSERT INTO operations(
                            operation_id, kind, item_id, payload, created_at_ticks,
                            attempt_count, retry_after_ticks)
                        VALUES(
                            $operation_id, $kind, $item_id, $payload, $created_at_ticks,
                            $attempt_count, $retry_after_ticks);
                        SELECT last_insert_rowid();
                        """,
                        token);
                    BindOperation(command, operation, includeSequence: false);
                    object? value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                    return Convert.ToInt64(value, CultureInfo.InvariantCulture);
                },
                "The SQLite operation could not be enqueued.",
                cancellationToken).ConfigureAwait(false);

            return new CloudOperationJournalEntry(
                operation.OperationId,
                operation.Kind,
                operation.ItemId,
                operation.Payload.Span,
                operation.CreatedAt,
                operation.AttemptCount,
                operation.RetryAfter,
                sequence);
        }

        public async ValueTask<CloudOperationJournalEntry?> GetAsync(
            Guid operationId,
            CancellationToken cancellationToken = default) =>
            await _owner.ExecuteAsync(
                async token =>
                {
                    await using SqliteCommand command = _owner.CreateCommand(
                        SelectColumns + " WHERE operation_id = $operation_id;",
                        token);
                    command.Parameters.AddWithValue("$operation_id", operationId.ToString("D"));
                    await using SqliteDataReader reader = await command
                        .ExecuteReaderAsync(token)
                        .ConfigureAwait(false);
                    return await reader.ReadAsync(token).ConfigureAwait(false)
                        ? ReadOperation(reader)
                        : null;
                },
                "The SQLite operation could not be read.",
                cancellationToken).ConfigureAwait(false);

        public async ValueTask<IReadOnlyList<CloudOperationJournalEntry>> ListAsync(
            int maximumCount,
            CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
            return await _owner.ExecuteAsync(
                async token =>
                {
                    await using SqliteCommand command = _owner.CreateCommand(
                        SelectColumns + " ORDER BY sequence LIMIT $maximum_count;",
                        token);
                    command.Parameters.AddWithValue("$maximum_count", maximumCount);
                    await using SqliteDataReader reader = await command
                        .ExecuteReaderAsync(token)
                        .ConfigureAwait(false);
                    List<CloudOperationJournalEntry> operations = [];
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        operations.Add(ReadOperation(reader));
                    }

                    return (IReadOnlyList<CloudOperationJournalEntry>)operations;
                },
                "The SQLite operation journal could not be listed.",
                cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<long> GetHighWaterSequenceAsync(CancellationToken cancellationToken = default) =>
            await _owner.ExecuteAsync(
                async token =>
                {
                    await using SqliteCommand command = _owner.CreateCommand(
                        "SELECT COALESCE(MAX(sequence), 0) FROM operations;", token);
                    return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);
                },
                "The SQLite journal boundary could not be read.",
                cancellationToken).ConfigureAwait(false);

        public async ValueTask<CloudOperationJournalPage> ReadPageAsync(
            long afterSequence,
            long throughSequence,
            int limit,
            CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
            ArgumentOutOfRangeException.ThrowIfLessThan(throughSequence, afterSequence);
            ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 4096);
            return await _owner.ExecuteAsync(
                async token =>
                {
                    // The extra row is only a continuation probe. Do not deliver it or advance
                    // the cursor past it: callers must be able to reach it after an ACK hole.
                    await using SqliteCommand command = _owner.CreateCommand(
                        SelectColumns + " WHERE sequence > $after AND sequence <= $through ORDER BY sequence LIMIT $count;", token);
                    command.Parameters.AddWithValue("$after", afterSequence);
                    command.Parameters.AddWithValue("$through", throughSequence);
                    command.Parameters.AddWithValue("$count", limit + 1);
                    await using SqliteDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                    List<CloudOperationJournalEntry> rows = [];
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        rows.Add(ReadOperation(reader));
                    }

                    bool hasMore = rows.Count > limit;
                    if (hasMore)
                    {
                        rows.RemoveAt(rows.Count - 1);
                    }

                    return new CloudOperationJournalPage(rows, rows.Count == 0 ? throughSequence : rows[^1].Sequence, hasMore);
                },
                "The SQLite journal page could not be read.",
                cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<IReadOnlyList<CloudOperationJournalEntry>> ListByItemIdAsync(
            Guid itemId,
            int maximumCount,
            CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
            return await _owner.ExecuteAsync(
                async token =>
                {
                    await using SqliteCommand command = _owner.CreateCommand(
                        SelectColumns +
                        " WHERE item_id = $item_id ORDER BY sequence LIMIT $maximum_count;",
                        token);
                    command.Parameters.AddWithValue("$item_id", itemId.ToString("D"));
                    command.Parameters.AddWithValue("$maximum_count", maximumCount);
                    await using SqliteDataReader reader = await command
                        .ExecuteReaderAsync(token)
                        .ConfigureAwait(false);
                    List<CloudOperationJournalEntry> operations = [];
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        operations.Add(ReadOperation(reader));
                    }

                    return (IReadOnlyList<CloudOperationJournalEntry>)operations;
                },
                "The SQLite item operation journal could not be listed.",
                cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask UpdateAsync(
            CloudOperationJournalEntry operation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(operation);
            int affected = await _owner.ExecuteAsync(
                async token =>
                {
                    await using SqliteCommand command = _owner.CreateCommand(
                        """
                        UPDATE operations SET
                            kind = $kind,
                            item_id = $item_id,
                            payload = $payload,
                            created_at_ticks = $created_at_ticks,
                            attempt_count = $attempt_count,
                            retry_after_ticks = $retry_after_ticks
                        WHERE operation_id = $operation_id AND sequence = $sequence;
                        """,
                        token);
                    BindOperation(command, operation, includeSequence: true);
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                },
                "The SQLite operation could not be updated.",
                cancellationToken).ConfigureAwait(false);
            if (affected == 0)
            {
                throw new InvalidOperationException(
                    "The operation does not have its assigned durable sequence.");
            }
        }

        public async ValueTask RemoveAsync(
            Guid operationId,
            CancellationToken cancellationToken = default)
        {
            await ExecuteDeleteAsync(
                _owner,
                "DELETE FROM operations WHERE operation_id = $value;",
                operationId.ToString("D"),
                "The SQLite operation could not be removed.",
                cancellationToken).ConfigureAwait(false);
        }

        private static void BindOperation(
            SqliteCommand command,
            CloudOperationJournalEntry operation,
            bool includeSequence)
        {
            command.Parameters.AddWithValue("$operation_id", operation.OperationId.ToString("D"));
            command.Parameters.AddWithValue("$kind", (int)operation.Kind);
            command.Parameters.AddWithValue(
                "$item_id",
                operation.ItemId is Guid itemId ? itemId.ToString("D") : DBNull.Value);
            command.Parameters.AddWithValue("$payload", operation.Payload.ToArray());
            command.Parameters.AddWithValue("$created_at_ticks", ToUtcTicks(operation.CreatedAt));
            command.Parameters.AddWithValue("$attempt_count", operation.AttemptCount);
            command.Parameters.AddWithValue(
                "$retry_after_ticks",
                operation.RetryAfter is DateTimeOffset retryAfter
                    ? ToUtcTicks(retryAfter)
                    : DBNull.Value);
            if (includeSequence)
            {
                command.Parameters.AddWithValue("$sequence", operation.Sequence);
            }
        }

        private static CloudOperationJournalEntry ReadOperation(SqliteDataReader reader) =>
            new(
                Guid.Parse(reader.GetString(0)),
                ReadDefinedEnum<CloudStateOperationKind>(reader.GetInt32(1), "operations.kind"),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                ReadBytes(reader, 3),
                FromUtcTicks(reader.GetInt64(4)),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : FromUtcTicks(reader.GetInt64(6)),
                reader.GetInt64(7));
    }

    private sealed class ConflictRepository : ICloudConflictRepository
    {
        private const string SelectColumns = """
            SELECT conflict_id, item_id, kind, payload, created_at_ticks
            FROM conflicts
            """;

        private readonly SqliteCloudStateTransaction _owner;

        internal ConflictRepository(SqliteCloudStateTransaction owner)
        {
            _owner = owner;
        }

        public async ValueTask<CloudConflictState?> GetAsync(
            Guid conflictId,
            CancellationToken cancellationToken = default) =>
            await _owner.ExecuteAsync(
                async token =>
                {
                    await using SqliteCommand command = _owner.CreateCommand(
                        SelectColumns + " WHERE conflict_id = $conflict_id;",
                        token);
                    command.Parameters.AddWithValue("$conflict_id", conflictId.ToString("D"));
                    await using SqliteDataReader reader = await command
                        .ExecuteReaderAsync(token)
                        .ConfigureAwait(false);
                    return await reader.ReadAsync(token).ConfigureAwait(false)
                        ? ReadConflict(reader)
                        : null;
                },
                "The SQLite conflict could not be read.",
                cancellationToken).ConfigureAwait(false);

        public async ValueTask<IReadOnlyList<CloudConflictState>> ListAsync(
            CancellationToken cancellationToken = default) =>
            await _owner.ExecuteAsync(
                async token =>
                {
                    await using SqliteCommand command = _owner.CreateCommand(
                        SelectColumns + " ORDER BY created_at_ticks, conflict_id;",
                        token);
                    await using SqliteDataReader reader = await command
                        .ExecuteReaderAsync(token)
                        .ConfigureAwait(false);
                    List<CloudConflictState> conflicts = [];
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        conflicts.Add(ReadConflict(reader));
                    }

                    return (IReadOnlyList<CloudConflictState>)conflicts;
                },
                "The SQLite conflicts could not be listed.",
                cancellationToken).ConfigureAwait(false);

        public async ValueTask UpsertAsync(
            CloudConflictState conflict,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(conflict);
            await _owner.ExecuteAsync(
                async token =>
                {
                    await using SqliteCommand command = _owner.CreateCommand(
                        """
                        INSERT INTO conflicts(conflict_id, item_id, kind, payload, created_at_ticks)
                        VALUES($conflict_id, $item_id, $kind, $payload, $created_at_ticks)
                        ON CONFLICT(conflict_id) DO UPDATE SET
                            item_id = excluded.item_id,
                            kind = excluded.kind,
                            payload = excluded.payload,
                            created_at_ticks = excluded.created_at_ticks;
                        """,
                        token);
                    command.Parameters.AddWithValue("$conflict_id", conflict.ConflictId.ToString("D"));
                    command.Parameters.AddWithValue(
                        "$item_id",
                        conflict.ItemId is Guid itemId ? itemId.ToString("D") : DBNull.Value);
                    command.Parameters.AddWithValue("$kind", (int)conflict.Kind);
                    command.Parameters.AddWithValue("$payload", conflict.Payload.ToArray());
                    command.Parameters.AddWithValue("$created_at_ticks", ToUtcTicks(conflict.CreatedAt));
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                },
                "The SQLite conflict could not be written.",
                cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask RemoveAsync(
            Guid conflictId,
            CancellationToken cancellationToken = default)
        {
            await ExecuteDeleteAsync(
                _owner,
                "DELETE FROM conflicts WHERE conflict_id = $value;",
                conflictId.ToString("D"),
                "The SQLite conflict could not be removed.",
                cancellationToken).ConfigureAwait(false);
        }

        private static CloudConflictState ReadConflict(SqliteDataReader reader) =>
            new(
                Guid.Parse(reader.GetString(0)),
                reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                ReadDefinedEnum<CloudStateConflictKind>(reader.GetInt32(2), "conflicts.kind"),
                ReadBytes(reader, 3),
                FromUtcTicks(reader.GetInt64(4)));
    }

    private sealed class RemoteBatchRepository : ICloudRemoteBatchRepository
    {
        private readonly SqliteCloudStateTransaction _owner;

        internal RemoteBatchRepository(SqliteCloudStateTransaction owner)
        {
            _owner = owner;
        }

        public async ValueTask<CloudRemoteBatchState?> GetAsync(
            string batchId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
            return await _owner.ExecuteAsync(
                async token =>
                {
                    await using SqliteCommand command = _owner.CreateCommand(
                        """
                        SELECT batch_id, cursor, applied_entry_count, total_entry_count,
                               status, payload, fingerprint, last_change_id, updated_at_ticks
                        FROM remote_batches WHERE batch_id = $batch_id;
                        """,
                        token);
                    command.Parameters.AddWithValue("$batch_id", batchId);
                    await using SqliteDataReader reader = await command
                        .ExecuteReaderAsync(token)
                        .ConfigureAwait(false);
                    return await reader.ReadAsync(token).ConfigureAwait(false)
                        ? ReadBatch(reader)
                        : null;
                },
                "The SQLite remote batch could not be read.",
                cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask UpsertAsync(
            CloudRemoteBatchState batch,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(batch);
            await _owner.ExecuteAsync(
                async token =>
                {
                    await using SqliteCommand command = _owner.CreateCommand(
                        """
                        INSERT INTO remote_batches(
                            batch_id, cursor, applied_entry_count, total_entry_count,
                            status, payload, fingerprint, last_change_id, updated_at_ticks)
                        VALUES(
                            $batch_id, $cursor, $applied_entry_count, $total_entry_count,
                            $status, $payload, $fingerprint, $last_change_id, $updated_at_ticks)
                        ON CONFLICT(batch_id) DO UPDATE SET
                            cursor = excluded.cursor,
                            applied_entry_count = excluded.applied_entry_count,
                            total_entry_count = excluded.total_entry_count,
                            status = excluded.status,
                            payload = excluded.payload,
                            fingerprint = excluded.fingerprint,
                            last_change_id = excluded.last_change_id,
                            updated_at_ticks = excluded.updated_at_ticks;
                        """,
                        token);
                    command.Parameters.AddWithValue("$batch_id", batch.BatchId);
                    command.Parameters.AddWithValue("$cursor", batch.Cursor.ToArray());
                    command.Parameters.AddWithValue("$applied_entry_count", batch.AppliedEntryCount);
                    command.Parameters.AddWithValue("$total_entry_count", batch.TotalEntryCount);
                    command.Parameters.AddWithValue("$status", (int)batch.Status);
                    command.Parameters.AddWithValue("$payload", batch.Payload.ToArray());
                    command.Parameters.AddWithValue("$fingerprint", batch.Fingerprint.ToArray());
                    command.Parameters.AddWithValue(
                        "$last_change_id",
                        (object?)batch.LastAppliedChangeId ?? DBNull.Value);
                    command.Parameters.AddWithValue("$updated_at_ticks", ToUtcTicks(batch.UpdatedAt));
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                },
                "The SQLite remote batch could not be written.",
                cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask RemoveAsync(
            string batchId,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
            await ExecuteDeleteAsync(
                _owner,
                "DELETE FROM remote_batches WHERE batch_id = $value;",
                batchId,
                "The SQLite remote batch could not be removed.",
                cancellationToken).ConfigureAwait(false);
        }

        private static CloudRemoteBatchState ReadBatch(SqliteDataReader reader) =>
            new(
                reader.GetString(0),
                ReadBytes(reader, 1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                ReadDefinedEnum<CloudRemoteBatchStatus>(reader.GetInt32(4), "remote_batches.status"),
                ReadBytes(reader, 5),
                FromUtcTicks(reader.GetInt64(8)),
                ReadBytes(reader, 6),
                reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    private sealed class EchoSuppressionRepository : ICloudEchoSuppressionRepository
    {
        private const string SelectColumns = """
            SELECT suppression_id, item_id, kind, relative_path, previous_relative_path,
                   payload, expires_at_ticks, remaining_observations
            FROM echo_suppressions
            """;

        private readonly SqliteCloudStateTransaction _owner;

        internal EchoSuppressionRepository(SqliteCloudStateTransaction owner)
        {
            _owner = owner;
        }

        public async ValueTask<CloudEchoSuppressionState?> GetAsync(
            Guid suppressionId,
            CancellationToken cancellationToken = default) =>
            await _owner.ExecuteAsync(
                async token =>
                {
                    await using SqliteCommand command = _owner.CreateCommand(
                        SelectColumns + " WHERE suppression_id = $suppression_id;",
                        token);
                    command.Parameters.AddWithValue("$suppression_id", suppressionId.ToString("D"));
                    await using SqliteDataReader reader = await command
                        .ExecuteReaderAsync(token)
                        .ConfigureAwait(false);
                    return await reader.ReadAsync(token).ConfigureAwait(false)
                        ? ReadSuppression(reader)
                        : null;
                },
                "The SQLite echo suppression could not be read.",
                cancellationToken).ConfigureAwait(false);

        public async ValueTask<IReadOnlyList<CloudEchoSuppressionState>> ListActiveAsync(
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default) =>
            await _owner.ExecuteAsync(
                async token =>
                {
                    await using SqliteCommand command = _owner.CreateCommand(
                        SelectColumns +
                        " WHERE expires_at_ticks > $now ORDER BY expires_at_ticks, suppression_id;",
                        token);
                    command.Parameters.AddWithValue("$now", ToUtcTicks(utcNow));
                    await using SqliteDataReader reader = await command
                        .ExecuteReaderAsync(token)
                        .ConfigureAwait(false);
                    List<CloudEchoSuppressionState> suppressions = [];
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        suppressions.Add(ReadSuppression(reader));
                    }

                    return (IReadOnlyList<CloudEchoSuppressionState>)suppressions;
                },
                "The SQLite echo suppressions could not be listed.",
                cancellationToken).ConfigureAwait(false);

        public async ValueTask UpsertAsync(
            CloudEchoSuppressionState suppression,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(suppression);
            await _owner.ExecuteAsync(
                async token =>
                {
                    await using SqliteCommand command = _owner.CreateCommand(
                        """
                        INSERT INTO echo_suppressions(
                            suppression_id, item_id, kind, relative_path, previous_relative_path,
                            payload, expires_at_ticks, remaining_observations)
                        VALUES(
                            $suppression_id, $item_id, $kind, $relative_path, $previous_relative_path,
                            $payload, $expires_at_ticks, $remaining_observations)
                        ON CONFLICT(suppression_id) DO UPDATE SET
                            item_id = excluded.item_id,
                            kind = excluded.kind,
                            relative_path = excluded.relative_path,
                            previous_relative_path = excluded.previous_relative_path,
                            payload = excluded.payload,
                            expires_at_ticks = excluded.expires_at_ticks,
                            remaining_observations = excluded.remaining_observations;
                        """,
                        token);
                    command.Parameters.AddWithValue(
                        "$suppression_id",
                        suppression.SuppressionId.ToString("D"));
                    command.Parameters.AddWithValue(
                        "$item_id",
                        suppression.ItemId is Guid itemId ? itemId.ToString("D") : DBNull.Value);
                    command.Parameters.AddWithValue("$kind", (int)suppression.Kind);
                    command.Parameters.AddWithValue("$relative_path", suppression.RelativePath);
                    command.Parameters.AddWithValue(
                        "$previous_relative_path",
                        suppression.PreviousRelativePath ?? (object)DBNull.Value);
                    command.Parameters.AddWithValue("$payload", suppression.Payload.ToArray());
                    command.Parameters.AddWithValue("$expires_at_ticks", ToUtcTicks(suppression.ExpiresAt));
                    command.Parameters.AddWithValue(
                        "$remaining_observations",
                        suppression.RemainingObservations);
                    return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                },
                "The SQLite echo suppression could not be written.",
                cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask RemoveAsync(
            Guid suppressionId,
            CancellationToken cancellationToken = default)
        {
            await ExecuteDeleteAsync(
                _owner,
                "DELETE FROM echo_suppressions WHERE suppression_id = $value;",
                suppressionId.ToString("D"),
                "The SQLite echo suppression could not be removed.",
                cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask RemoveExpiredAsync(
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default)
        {
            await ExecuteDeleteAsync(
                _owner,
                "DELETE FROM echo_suppressions WHERE expires_at_ticks <= $value;",
                ToUtcTicks(utcNow),
                "Expired SQLite echo suppressions could not be removed.",
                cancellationToken).ConfigureAwait(false);
        }

        private static CloudEchoSuppressionState ReadSuppression(SqliteDataReader reader) =>
            new(
                Guid.Parse(reader.GetString(0)),
                reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                ReadDefinedEnum<CloudStateOperationKind>(reader.GetInt32(2), "echo_suppressions.kind"),
                 reader.GetString(3),
                 ReadBytes(reader, 5),
                 FromUtcTicks(reader.GetInt64(6)),
                 reader.IsDBNull(4) ? null : reader.GetString(4),
                 reader.GetInt32(7));
    }

    private static async ValueTask ExecuteDeleteAsync(
        SqliteCloudStateTransaction owner,
        string sql,
        object value,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        await owner.ExecuteAsync(
            async token =>
            {
                await using SqliteCommand command = owner.CreateCommand(sql, token);
                command.Parameters.AddWithValue("$value", value);
                return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            },
            failureMessage,
            cancellationToken).ConfigureAwait(false);
    }

    private static byte[] ReadBytes(SqliteDataReader reader, int ordinal) =>
        (byte[])reader.GetValue(ordinal);

    private static TEnum ReadDefinedEnum<TEnum>(int value, string columnName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(typeof(TEnum), value))
        {
            throw new ArgumentException(
                $"The persisted value '{value}' is not defined for {columnName}.",
                nameof(value));
        }

        return (TEnum)Enum.ToObject(typeof(TEnum), value);
    }

    private static long ToUtcTicks(DateTimeOffset value) => value.UtcDateTime.Ticks;

    private static DateTimeOffset FromUtcTicks(long ticks) =>
        new(new DateTime(ticks, DateTimeKind.Utc));
}
