namespace CfSharp;

/// <summary>
/// Creates a durable state store for one validated Cloud Files sync root.
/// </summary>
/// <remarks>
/// <para>
/// <c>CloudFileSystem</c> invokes a configured factory only after validating its sync-root
/// path and configuration. A successful call transfers disposal ownership of the returned store
/// to the file system. If opening fails, the factory remains responsible for releasing any
/// partially allocated resources.
/// </para>
/// <para>
/// Implementations must be safe for concurrent calls unless they document a stricter external
/// synchronization requirement. They must not retain the supplied context after the returned
/// operation completes unless they copy all required values.
/// </para>
/// </remarks>
public interface ICloudStateStoreFactory
{
    /// <summary>Opens or creates the state store for a sync root.</summary>
    /// <param name="context">Validated immutable information about the owning sync root.</param>
    /// <param name="cancellationToken">Token that cancels opening before ownership transfers.</param>
    /// <returns>The opened store. The caller owns it after successful completion.</returns>
    /// <exception cref="OperationCanceledException">
    /// Opening was canceled before a store was returned.
    /// </exception>
    ValueTask<ICloudStateStore> OpenAsync(
        CloudStateStoreContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a durable transactional store used by one <c>CloudFileSystem</c> instance.
/// </summary>
/// <remarks>
/// <para>
/// A store contains synchronization coordination metadata only. Implementations must not place
/// file content, authentication credentials, access tokens, or provider-specific business data
/// in the store.
/// </para>
/// <para>
/// Implementations may serialize transactions internally. Calls are safe from concurrent
/// threads, but each returned transaction and its repositories are single-consumer objects.
/// Dispose the store only after all transactions have completed or been disposed.
/// </para>
/// </remarks>
public interface ICloudStateStore : IAsyncDisposable
{
    /// <summary>Begins an atomic state transaction.</summary>
    /// <param name="cancellationToken">Token that cancels waiting for transaction ownership.</param>
    /// <returns>A transaction whose writes remain invisible until committed.</returns>
    /// <exception cref="OperationCanceledException">
    /// Waiting for the transaction was canceled.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The store has been disposed.</exception>
    ValueTask<ICloudStateTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Provides repository access within one atomic durable-state transaction.
/// </summary>
/// <remarks>
/// A transaction is not thread-safe. Exactly one of <see cref="CommitAsync"/>,
/// <see cref="RollbackAsync"/>, or <see cref="IAsyncDisposable.DisposeAsync"/> terminates it.
/// Disposal without a successful commit rolls back all writes. Repositories become invalid when
/// their transaction terminates and must then throw <see cref="InvalidOperationException"/>.
/// </remarks>
public interface ICloudStateTransaction : IAsyncDisposable
{
    /// <summary>Gets item identity, revision, path, and tombstone state.</summary>
    ICloudItemStateRepository Items { get; }

    /// <summary>Gets named local and remote synchronization checkpoints.</summary>
    ICloudCheckpointRepository Checkpoints { get; }

    /// <summary>Gets the ordered, replayable local-operation journal.</summary>
    ICloudOperationJournal Operations { get; }

    /// <summary>Gets durable unresolved-conflict records.</summary>
    ICloudConflictRepository Conflicts { get; }

    /// <summary>Gets resumable remote-batch progress.</summary>
    ICloudRemoteBatchRepository RemoteBatches { get; }

    /// <summary>Gets provider-originated local-change suppression records.</summary>
    ICloudEchoSuppressionRepository EchoSuppressions { get; }

    /// <summary>Atomically makes all transaction writes durable and visible.</summary>
    /// <param name="cancellationToken">
    /// Token that cancels commit before durability is guaranteed. If cancellation races with the
    /// storage engine's commit point, an implementation must report a deterministic committed or
    /// rolled-back outcome rather than leave it ambiguous.
    /// </param>
    /// <returns>An operation that completes after durability is established.</returns>
    ValueTask CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>Discards every uncommitted write in the transaction.</summary>
    /// <param name="cancellationToken">Token that cancels rollback work.</param>
    /// <returns>An operation that completes after the transaction is no longer active.</returns>
    ValueTask RollbackAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reads and writes durable item identity and revision records.</summary>
public interface ICloudItemStateRepository
{
    /// <summary>Finds an item by its stable CfSharp identifier.</summary>
    ValueTask<CloudItemState?> GetByItemIdAsync(
        Guid itemId,
        CancellationToken cancellationToken = default);

    /// <summary>Finds an item by its provider-defined stable remote identifier.</summary>
    ValueTask<CloudItemState?> GetByRemoteIdAsync(
        string remoteId,
        CancellationToken cancellationToken = default);

    /// <summary>Finds an item by its normalized path relative to the sync root.</summary>
    /// <remarks>
    /// Returns the live owner when present. Otherwise returns the most recently updated tombstone,
    /// breaking timestamp ties by ordinal ascending item ID in its canonical D format, or null
    /// when no generation is known. Path comparison is ordinal case-insensitive. Implementations
    /// must support indexed, bounded lookup and at most one live owner per normalized path while
    /// retaining historical generations at their original paths. Identity lookups include history.
    /// A returned tombstone is historical evidence, not authority over a later native occupant.
    /// </remarks>
    ValueTask<CloudItemState?> GetByRelativePathAsync(
        string relativePath,
        CancellationToken cancellationToken = default);

    /// <summary>Lists an item and its separator-delimited descendants in deterministic order.</summary>
    /// <param name="relativePath">
    /// Canonical relative subtree path, or an empty string to list the complete sync-root state.
    /// </param>
    /// <param name="cancellationToken">Token that cancels the transactional read.</param>
    /// <returns>
    /// A caller-owned snapshot ordered by path depth and then ordinal case-insensitive path.
    /// Matching is ordinal case-insensitive and observes directory-segment boundaries. The snapshot
    /// includes retired generations; paths need not be unique. Identity queries retain each row,
    /// while namespace views must distinguish the live owner from historical tombstones.
    /// </returns>
    ValueTask<IReadOnlyList<CloudItemState>> ListSubtreeAsync(
        string relativePath,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts a new item or replaces the record with the same item identifier.</summary>
    /// <remarks>
    /// Item and remote identifiers remain unique across live and retired records. Only live items
    /// require unique normalized paths; tombstones retain their paths and may share them with
    /// other generations. Incompatible live ownership must fail within the caller's transaction.
    /// </remarks>
    ValueTask UpsertAsync(
        CloudItemState item,
        CancellationToken cancellationToken = default);

    /// <summary>Removes an item record without modifying the file system.</summary>
    ValueTask RemoveAsync(Guid itemId, CancellationToken cancellationToken = default);
}

/// <summary>Reads and writes opaque named synchronization checkpoints.</summary>
public interface ICloudCheckpointRepository
{
    /// <summary>Gets a checkpoint by its ordinal, case-sensitive name.</summary>
    ValueTask<CloudStateCheckpoint?> GetAsync(
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>Lists checkpoints whose names equal or are descendants of a prefix.</summary>
    /// <param name="namePrefix">Case-sensitive checkpoint-name prefix.</param>
    /// <param name="cancellationToken">Token used to cancel the read.</param>
    /// <returns>Matching checkpoints in deterministic ordinal-name order.</returns>
    ValueTask<IReadOnlyList<CloudStateCheckpoint>> ListAsync(
        string namePrefix,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces a checkpoint.</summary>
    ValueTask UpsertAsync(
        CloudStateCheckpoint checkpoint,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a checkpoint if it exists.</summary>
    ValueTask RemoveAsync(string name, CancellationToken cancellationToken = default);
}

/// <summary>Maintains ordered local operations until an application acknowledges them.</summary>
public interface ICloudOperationJournal
{
    /// <summary>Adds an operation and assigns its durable monotonically increasing sequence.</summary>
    ValueTask<CloudOperationJournalEntry> EnqueueAsync(
        CloudOperationJournalEntry operation,
        CancellationToken cancellationToken = default);

    /// <summary>Gets an operation by its idempotency identifier.</summary>
    ValueTask<CloudOperationJournalEntry?> GetAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the earliest operations in ascending sequence order.</summary>
    ValueTask<IReadOnlyList<CloudOperationJournalEntry>> ListAsync(
        int maximumCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the earliest operations belonging to one item in ascending sequence order.
    /// </summary>
    /// <param name="itemId">Item identity whose pending operations are returned.</param>
    /// <param name="maximumCount">Positive upper bound for the returned page.</param>
    /// <param name="cancellationToken">Token that cancels the query.</param>
    ValueTask<IReadOnlyList<CloudOperationJournalEntry>> ListByItemIdAsync(
        Guid itemId,
        int maximumCount,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces retry metadata and payload for an existing operation.</summary>
    ValueTask UpdateAsync(
        CloudOperationJournalEntry operation,
        CancellationToken cancellationToken = default);

    /// <summary>Removes an acknowledged operation.</summary>
    ValueTask RemoveAsync(Guid operationId, CancellationToken cancellationToken = default);
}

/// <summary>Maintains unresolved synchronization conflicts.</summary>
public interface ICloudConflictRepository
{
    /// <summary>Gets a conflict by its stable identifier.</summary>
    ValueTask<CloudConflictState?> GetAsync(
        Guid conflictId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists conflicts in creation order.</summary>
    ValueTask<IReadOnlyList<CloudConflictState>> ListAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces a conflict.</summary>
    ValueTask UpsertAsync(
        CloudConflictState conflict,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a resolved conflict.</summary>
    ValueTask RemoveAsync(Guid conflictId, CancellationToken cancellationToken = default);
}

/// <summary>Maintains resumable application of remote change batches.</summary>
public interface ICloudRemoteBatchRepository
{
    /// <summary>Gets batch progress by the provider-defined batch identifier.</summary>
    ValueTask<CloudRemoteBatchState?> GetAsync(
        string batchId,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces remote-batch progress.</summary>
    ValueTask UpsertAsync(
        CloudRemoteBatchState batch,
        CancellationToken cancellationToken = default);

    /// <summary>Removes completed remote-batch state.</summary>
    ValueTask RemoveAsync(string batchId, CancellationToken cancellationToken = default);
}

/// <summary>Maintains durable records used to suppress provider-generated local echoes.</summary>
public interface ICloudEchoSuppressionRepository
{
    /// <summary>Gets a suppression record by its operation identifier.</summary>
    ValueTask<CloudEchoSuppressionState?> GetAsync(
        Guid suppressionId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists records that have not expired at the supplied UTC instant.</summary>
    ValueTask<IReadOnlyList<CloudEchoSuppressionState>> ListActiveAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces a suppression record.</summary>
    ValueTask UpsertAsync(
        CloudEchoSuppressionState suppression,
        CancellationToken cancellationToken = default);

    /// <summary>Removes a suppression record.</summary>
    ValueTask RemoveAsync(Guid suppressionId, CancellationToken cancellationToken = default);

    /// <summary>Removes records whose expiration time is not later than the supplied instant.</summary>
    ValueTask RemoveExpiredAsync(
        DateTimeOffset utcNow,
        CancellationToken cancellationToken = default);
}
