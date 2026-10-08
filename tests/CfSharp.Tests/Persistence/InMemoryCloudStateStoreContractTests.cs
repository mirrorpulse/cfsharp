namespace CfSharp.Tests.Persistence;

public sealed class InMemoryCloudStateStoreContractTests : CloudStateStoreContractTests
{
    protected override ICloudStateStoreFactory CreateFactory() => new InMemoryStateStoreFactory();

    internal static ICloudStateStoreFactory CreateFactoryForTesting() => new InMemoryStateStoreFactory();

    private sealed class InMemoryStateStoreFactory : ICloudStateStoreFactory
    {
        private readonly MemoryDatabase _database = new();

        public ValueTask<ICloudStateStore> OpenAsync(
            CloudStateStoreContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ICloudStateStore>(new InMemoryStateStore(_database));
        }
    }

    private sealed class InMemoryStateStore : ICloudStateStore
    {
        private readonly MemoryDatabase _database;
        private int _disposed;

        internal InMemoryStateStore(MemoryDatabase database)
        {
            _database = database;
        }

        public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _database.TransactionGate.WaitAsync(cancellationToken);
            if (Volatile.Read(ref _disposed) != 0)
            {
                _database.TransactionGate.Release();
                throw new ObjectDisposedException(GetType().FullName);
            }

            return new InMemoryTransaction(_database, _database.State.Clone());
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _disposed, 1);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryTransaction :
        ICloudStateTransaction,
        ICloudItemStateRepository,
        ICloudCheckpointRepository,
        ICloudOperationJournal,
        ICloudOperationJournalPaging,
        ICloudConflictRepository,
        ICloudRemoteBatchRepository,
        ICloudEchoSuppressionRepository
    {
        private readonly MemoryDatabase _database;
        private readonly MemoryState _state;
        private int _terminal;

        internal InMemoryTransaction(MemoryDatabase database, MemoryState state)
        {
            _database = database;
            _state = state;
        }

        public ICloudItemStateRepository Items => this;

        public ICloudCheckpointRepository Checkpoints => this;

        public ICloudOperationJournal Operations => this;

        public ICloudConflictRepository Conflicts => this;

        public ICloudRemoteBatchRepository RemoteBatches => this;

        public ICloudEchoSuppressionRepository EchoSuppressions => this;

        public ValueTask<CloudItemState?> GetByItemIdAsync(
            Guid itemId,
            CancellationToken cancellationToken = default)
        {
            CheckActive(cancellationToken);
            _state.Items.TryGetValue(itemId, out CloudItemState? item);
            return ValueTask.FromResult(item);
        }

        public ValueTask<CloudItemState?> GetByRemoteIdAsync(
            string remoteId,
            CancellationToken cancellationToken = default)
        {
            CheckActive(cancellationToken);
            CloudItemState? item = _state.Items.Values.SingleOrDefault(candidate =>
                string.Equals(candidate.RemoteId, remoteId, StringComparison.Ordinal));
            return ValueTask.FromResult(item);
        }

        public ValueTask<CloudItemState?> GetByRelativePathAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            CheckActive(cancellationToken);
            CloudItemState? item = _state.Items.Values.SingleOrDefault(candidate =>
                string.Equals(candidate.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
            return ValueTask.FromResult(item);
        }

        public ValueTask<IReadOnlyList<CloudItemState>> ListSubtreeAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            CheckActive(cancellationToken);
            ArgumentNullException.ThrowIfNull(relativePath);
            string primaryPrefix = relativePath.Length == 0
                ? string.Empty
                : relativePath + Path.DirectorySeparatorChar;
            string alternatePrefix = relativePath.Length == 0
                ? string.Empty
                : relativePath + Path.AltDirectorySeparatorChar;
            IReadOnlyList<CloudItemState> items = _state.Items.Values
                .Where(item =>
                    relativePath.Length == 0 ||
                    string.Equals(
                        item.RelativePath,
                        relativePath,
                        StringComparison.OrdinalIgnoreCase) ||
                    item.RelativePath.StartsWith(primaryPrefix, StringComparison.OrdinalIgnoreCase) ||
                    item.RelativePath.StartsWith(alternatePrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.RelativePath.Count(character =>
                    character == Path.DirectorySeparatorChar ||
                    character == Path.AltDirectorySeparatorChar))
                .ThenBy(item => item.RelativePath.Length)
                .ThenBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ItemId)
                .ToArray();
            return ValueTask.FromResult(items);
        }

        public ValueTask UpsertAsync(
            CloudItemState item,
            CancellationToken cancellationToken = default)
        {
            CheckActive(cancellationToken);
            ArgumentNullException.ThrowIfNull(item);
            _state.Items[item.ItemId] = item;
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveAsync(Guid itemId, CancellationToken cancellationToken = default)
        {
            CheckActive(cancellationToken);
            _state.Items.Remove(itemId);
            return ValueTask.CompletedTask;
        }

        ValueTask<CloudStateCheckpoint?> ICloudCheckpointRepository.GetAsync(
            string name,
            CancellationToken cancellationToken)
        {
            CheckActive(cancellationToken);
            _state.Checkpoints.TryGetValue(name, out CloudStateCheckpoint? checkpoint);
            return ValueTask.FromResult(checkpoint);
        }

        ValueTask<IReadOnlyList<CloudStateCheckpoint>> ICloudCheckpointRepository.ListAsync(
            string namePrefix,
            CancellationToken cancellationToken)
        {
            CheckActive(cancellationToken);
            ArgumentNullException.ThrowIfNull(namePrefix);
            IReadOnlyList<CloudStateCheckpoint> checkpoints = _state.Checkpoints
                .Where(pair => pair.Key.StartsWith(namePrefix, StringComparison.Ordinal))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Value)
                .ToArray();
            return ValueTask.FromResult(checkpoints);
        }

        ValueTask ICloudCheckpointRepository.UpsertAsync(
            CloudStateCheckpoint checkpoint,
            CancellationToken cancellationToken)
        {
            CheckActive(cancellationToken);
            ArgumentNullException.ThrowIfNull(checkpoint);
            _state.Checkpoints[checkpoint.Name] = checkpoint;
            return ValueTask.CompletedTask;
        }

        ValueTask ICloudCheckpointRepository.RemoveAsync(
            string name,
            CancellationToken cancellationToken)
        {
            CheckActive(cancellationToken);
            _state.Checkpoints.Remove(name);
            return ValueTask.CompletedTask;
        }

        public ValueTask<CloudOperationJournalEntry> EnqueueAsync(
            CloudOperationJournalEntry operation,
            CancellationToken cancellationToken = default)
        {
            CheckActive(cancellationToken);
            ArgumentNullException.ThrowIfNull(operation);
            if (operation.Sequence != 0)
            {
                throw new ArgumentException("A new operation must not have a sequence.", nameof(operation));
            }

            long sequence = _state.NextOperationSequence++;
            CloudOperationJournalEntry stored = new(
                operation.OperationId,
                operation.Kind,
                operation.ItemId,
                operation.Payload.Span,
                operation.CreatedAt,
                operation.AttemptCount,
                operation.RetryAfter,
                sequence);
            _state.Operations.Add(operation.OperationId, stored);
            return ValueTask.FromResult(stored);
        }

        ValueTask<CloudOperationJournalEntry?> ICloudOperationJournal.GetAsync(
            Guid operationId,
            CancellationToken cancellationToken)
        {
            CheckActive(cancellationToken);
            _state.Operations.TryGetValue(operationId, out CloudOperationJournalEntry? operation);
            return ValueTask.FromResult(operation);
        }

        public ValueTask<IReadOnlyList<CloudOperationJournalEntry>> ListAsync(
            int maximumCount,
            CancellationToken cancellationToken = default)
        {
            CheckActive(cancellationToken);
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
            IReadOnlyList<CloudOperationJournalEntry> operations = _state.Operations.Values
                .OrderBy(operation => operation.Sequence)
                .Take(maximumCount)
                .ToArray();
            return ValueTask.FromResult(operations);
        }

        public ValueTask<long> GetHighWaterSequenceAsync(CancellationToken cancellationToken = default)
        {
            CheckActive(cancellationToken);
            return ValueTask.FromResult(_state.Operations.Values.Select(row => row.Sequence).DefaultIfEmpty().Max());
        }

        public ValueTask<CloudOperationJournalPage> ReadPageAsync(
            long afterSequence, long throughSequence, int limit, CancellationToken cancellationToken = default)
        {
            CheckActive(cancellationToken);
            ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
            ArgumentOutOfRangeException.ThrowIfLessThan(throughSequence, afterSequence);
            ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 4096);
            CloudOperationJournalEntry[] rows = _state.Operations.Values
                .Where(row => row.Sequence > afterSequence && row.Sequence <= throughSequence)
                .OrderBy(row => row.Sequence).Take(limit + 1).ToArray();
            bool more = rows.Length > limit;
            if (more)
            {
                rows = rows[..limit];
            }

            return ValueTask.FromResult(new CloudOperationJournalPage(rows,
                rows.Length == 0 ? throughSequence : rows[^1].Sequence, more));
        }

        public ValueTask<IReadOnlyList<CloudOperationJournalEntry>> ListByItemIdAsync(
            Guid itemId,
            int maximumCount,
            CancellationToken cancellationToken = default)
        {
            CheckActive(cancellationToken);
            ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
            IReadOnlyList<CloudOperationJournalEntry> operations = _state.Operations.Values
                .Where(operation => operation.ItemId == itemId)
                .OrderBy(operation => operation.Sequence)
                .Take(maximumCount)
                .ToArray();
            return ValueTask.FromResult(operations);
        }

        public ValueTask UpdateAsync(
            CloudOperationJournalEntry operation,
            CancellationToken cancellationToken = default)
        {
            CheckActive(cancellationToken);
            ArgumentNullException.ThrowIfNull(operation);
            if (!_state.Operations.TryGetValue(
                    operation.OperationId,
                    out CloudOperationJournalEntry? current) ||
                operation.Sequence != current.Sequence)
            {
                throw new InvalidOperationException(
                    "The operation does not have its assigned durable sequence.");
            }

            _state.Operations[operation.OperationId] = operation;
            return ValueTask.CompletedTask;
        }

        ValueTask ICloudOperationJournal.RemoveAsync(
            Guid operationId,
            CancellationToken cancellationToken)
        {
            CheckActive(cancellationToken);
            _state.Operations.Remove(operationId);
            return ValueTask.CompletedTask;
        }

        ValueTask<CloudConflictState?> ICloudConflictRepository.GetAsync(
            Guid conflictId,
            CancellationToken cancellationToken)
        {
            CheckActive(cancellationToken);
            _state.Conflicts.TryGetValue(conflictId, out CloudConflictState? conflict);
            return ValueTask.FromResult(conflict);
        }

        ValueTask<IReadOnlyList<CloudConflictState>> ICloudConflictRepository.ListAsync(
            CancellationToken cancellationToken)
        {
            CheckActive(cancellationToken);
            IReadOnlyList<CloudConflictState> conflicts = _state.Conflicts.Values
                .OrderBy(conflict => conflict.CreatedAt)
                .ThenBy(conflict => conflict.ConflictId)
                .ToArray();
            return ValueTask.FromResult(conflicts);
        }

        ValueTask ICloudConflictRepository.UpsertAsync(
            CloudConflictState conflict,
            CancellationToken cancellationToken)
        {
            CheckActive(cancellationToken);
            ArgumentNullException.ThrowIfNull(conflict);
            _state.Conflicts[conflict.ConflictId] = conflict;
            return ValueTask.CompletedTask;
        }

        ValueTask ICloudConflictRepository.RemoveAsync(
            Guid conflictId,
            CancellationToken cancellationToken)
        {
            CheckActive(cancellationToken);
            _state.Conflicts.Remove(conflictId);
            return ValueTask.CompletedTask;
        }

        ValueTask<CloudRemoteBatchState?> ICloudRemoteBatchRepository.GetAsync(
            string batchId,
            CancellationToken cancellationToken)
        {
            CheckActive(cancellationToken);
            _state.RemoteBatches.TryGetValue(batchId, out CloudRemoteBatchState? batch);
            return ValueTask.FromResult(batch);
        }

        ValueTask ICloudRemoteBatchRepository.UpsertAsync(
            CloudRemoteBatchState batch,
            CancellationToken cancellationToken)
        {
            CheckActive(cancellationToken);
            ArgumentNullException.ThrowIfNull(batch);
            _state.RemoteBatches[batch.BatchId] = batch;
            return ValueTask.CompletedTask;
        }

        ValueTask ICloudRemoteBatchRepository.RemoveAsync(
            string batchId,
            CancellationToken cancellationToken)
        {
            CheckActive(cancellationToken);
            _state.RemoteBatches.Remove(batchId);
            return ValueTask.CompletedTask;
        }

        ValueTask<CloudEchoSuppressionState?> ICloudEchoSuppressionRepository.GetAsync(
            Guid suppressionId,
            CancellationToken cancellationToken)
        {
            CheckActive(cancellationToken);
            _state.EchoSuppressions.TryGetValue(
                suppressionId,
                out CloudEchoSuppressionState? suppression);
            return ValueTask.FromResult(suppression);
        }

        public ValueTask<IReadOnlyList<CloudEchoSuppressionState>> ListActiveAsync(
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default)
        {
            CheckActive(cancellationToken);
            DateTimeOffset threshold = utcNow.ToUniversalTime();
            IReadOnlyList<CloudEchoSuppressionState> suppressions = _state.EchoSuppressions.Values
                .Where(suppression => suppression.ExpiresAt > threshold)
                .OrderBy(suppression => suppression.ExpiresAt)
                .ThenBy(suppression => suppression.SuppressionId)
                .ToArray();
            return ValueTask.FromResult(suppressions);
        }

        ValueTask ICloudEchoSuppressionRepository.UpsertAsync(
            CloudEchoSuppressionState suppression,
            CancellationToken cancellationToken)
        {
            CheckActive(cancellationToken);
            ArgumentNullException.ThrowIfNull(suppression);
            _state.EchoSuppressions[suppression.SuppressionId] = suppression;
            return ValueTask.CompletedTask;
        }

        ValueTask ICloudEchoSuppressionRepository.RemoveAsync(
            Guid suppressionId,
            CancellationToken cancellationToken)
        {
            CheckActive(cancellationToken);
            _state.EchoSuppressions.Remove(suppressionId);
            return ValueTask.CompletedTask;
        }

        public ValueTask RemoveExpiredAsync(
            DateTimeOffset utcNow,
            CancellationToken cancellationToken = default)
        {
            CheckActive(cancellationToken);
            DateTimeOffset threshold = utcNow.ToUniversalTime();
            Guid[] expired = _state.EchoSuppressions.Values
                .Where(suppression => suppression.ExpiresAt <= threshold)
                .Select(suppression => suppression.SuppressionId)
                .ToArray();
            foreach (Guid suppressionId in expired)
            {
                _state.EchoSuppressions.Remove(suppressionId);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask CommitAsync(CancellationToken cancellationToken = default)
        {
            CheckActive(cancellationToken);
            _database.State = _state;
            Complete();
            return ValueTask.CompletedTask;
        }

        public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
        {
            CheckActive(cancellationToken);
            Complete();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _terminal, 1) == 0)
            {
                _database.TransactionGate.Release();
            }

            return ValueTask.CompletedTask;
        }

        private void CheckActive(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _terminal) != 0)
            {
                throw new InvalidOperationException("The transaction has already terminated.");
            }
        }

        private void Complete()
        {
            if (Interlocked.Exchange(ref _terminal, 1) != 0)
            {
                throw new InvalidOperationException("The transaction has already terminated.");
            }

            _database.TransactionGate.Release();
        }
    }

    private sealed class MemoryDatabase
    {
        internal SemaphoreSlim TransactionGate { get; } = new(1, 1);

        internal MemoryState State { get; set; } = new();
    }

    private sealed class MemoryState
    {
        internal Dictionary<Guid, CloudItemState> Items { get; } = [];

        internal Dictionary<string, CloudStateCheckpoint> Checkpoints { get; } =
            new(StringComparer.Ordinal);

        internal Dictionary<Guid, CloudOperationJournalEntry> Operations { get; } = [];

        internal Dictionary<Guid, CloudConflictState> Conflicts { get; } = [];

        internal Dictionary<string, CloudRemoteBatchState> RemoteBatches { get; } =
            new(StringComparer.Ordinal);

        internal Dictionary<Guid, CloudEchoSuppressionState> EchoSuppressions { get; } = [];

        internal long NextOperationSequence { get; set; } = 1;

        internal MemoryState Clone()
        {
            MemoryState clone = new()
            {
                NextOperationSequence = NextOperationSequence,
            };
            Copy(Items, clone.Items);
            Copy(Checkpoints, clone.Checkpoints);
            Copy(Operations, clone.Operations);
            Copy(Conflicts, clone.Conflicts);
            Copy(RemoteBatches, clone.RemoteBatches);
            Copy(EchoSuppressions, clone.EchoSuppressions);
            return clone;
        }

        private static void Copy<TKey, TValue>(
            Dictionary<TKey, TValue> source,
            Dictionary<TKey, TValue> destination)
            where TKey : notnull
        {
            foreach ((TKey key, TValue value) in source)
            {
                destination.Add(key, value);
            }
        }
    }
}
