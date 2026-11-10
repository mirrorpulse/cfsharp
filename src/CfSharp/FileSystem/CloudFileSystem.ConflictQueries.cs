namespace CfSharp;

public sealed partial class CloudFileSystem
{
    /// <summary>Reads one decoded unresolved remote conflict without changing its state.</summary>
    /// <param name="conflictId">The stable identifier returned by a remote batch or conflict query.</param>
    /// <param name="cancellationToken">Cancels admission or the transactional read.</param>
    /// <returns>An owned, immutable record, or null if this instance has no such unresolved conflict.</returns>
    /// <exception cref="ArgumentException">The identifier is empty.</exception>
    /// <exception cref="InvalidOperationException">The file system has not started.</exception>
    /// <exception cref="InvalidDataException">The stored envelope is corrupt or unsupported.</exception>
    /// <remarks>
    /// Requires a started Windows file system. Concurrent calls are supported and participate in
    /// disposal admission. LocalState is read in the same transaction, not a historical snapshot.
    /// No native mutation, conflict resolution, or state-store ownership transfer occurs.
    /// </remarks>
    public async ValueTask<CloudRemoteConflictRecord?> GetRemoteConflictAsync(
        Guid conflictId,
        CancellationToken cancellationToken = default)
    {
        if (conflictId == Guid.Empty)
        {
            throw new ArgumentException("The conflict identifier cannot be empty.", nameof(conflictId));
        }

        using CloudFileSystemOperationLease operation = await AcquireStateOperationAsync(cancellationToken).ConfigureAwait(false);
        await using ICloudStateTransaction transaction = await operation.StateStore
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        CloudConflictState? state = await transaction.Conflicts.GetAsync(conflictId, cancellationToken)
            .ConfigureAwait(false);
        CloudRemoteConflictRecord? result = state is null ? null : new(
            state.ConflictId, SyncRootPath,
            await DecodeRemoteConflictAsync(state, transaction, cancellationToken).ConfigureAwait(false));
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>Reads a complete decoded snapshot of this instance's unresolved remote conflicts.</summary>
    /// <param name="cancellationToken">Cancels admission, enumeration, or decoding.</param>
    /// <returns>An owned read-only snapshot ordered by creation time, then conflict identifier.</returns>
    /// <exception cref="InvalidOperationException">The file system has not started.</exception>
    /// <exception cref="InvalidDataException">A stored envelope is corrupt or unsupported.</exception>
    /// <remarks>
    /// Requires a started Windows file system. All records and their current LocalState are read
    /// in one transaction. The result is safe for concurrent reads and survives disposal. This
    /// method reads the complete repository and uses memory proportional to the conflict count;
    /// it does not silently omit undecodable records or mutate conflicts, checkpoints, or receipts.
    /// </remarks>
    public async ValueTask<IReadOnlyList<CloudRemoteConflictRecord>> ListRemoteConflictsAsync(
        CancellationToken cancellationToken = default)
    {
        using CloudFileSystemOperationLease operation = await AcquireStateOperationAsync(cancellationToken).ConfigureAwait(false);
        await using ICloudStateTransaction transaction = await operation.StateStore
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CloudConflictState> states = await transaction.Conflicts.ListAsync(cancellationToken)
            .ConfigureAwait(false);
        List<CloudRemoteConflictRecord> result = new(states.Count);
        foreach (CloudConflictState state in states.OrderBy(state => state.CreatedAt).ThenBy(state => state.ConflictId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new CloudRemoteConflictRecord(state.ConflictId, SyncRootPath,
                await DecodeRemoteConflictAsync(state, transaction, cancellationToken).ConfigureAwait(false)));
        }

        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return result.AsReadOnly();
    }
}
