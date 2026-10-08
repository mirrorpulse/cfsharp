namespace CfSharp;

public abstract partial class CloudItem
{
    /// <summary>Moves or renames this item within the owning sync root.</summary>
    /// <param name="destination">Existing destination directory owned by the same file system.</param>
    /// <param name="name">One valid destination child name.</param>
    /// <param name="options">Collision behavior or an original directory proof, or null for no replacement.</param>
    /// <param name="cancellationToken">
    /// Token observed before the synchronous file-system move and during durable-state work.
    /// </param>
    /// <returns>
    /// A new immutable reference and snapshot at the destination. This reference remains bound to
    /// the old path and is not mutated.
    /// Managed directory results also expose <see cref="CloudItemMoveResult.DirectoryReconciliation"/>
    /// and its rescan requirement. Supported managed directories prepare durable evidence before
    /// moving; a retry uses that retained evidence or the explicitly supplied proof and never moves
    /// an absent source again. An absent directory without historical evidence fails closed.
    /// </returns>
    /// <remarks>
    /// An admitted move retains its operation lease through preparation, native movement, and
    /// durable recovery. Concurrent owner disposal waits for that work; new public operations
    /// are rejected after stopping begins. Cancellation cannot interrupt durable recovery after
    /// the synchronous native move succeeds.
    /// Known file paths and retained live membership of source and destination ancestors commit
    /// together, with each ancestor refreshed once. Their native bindings, immutable directory
    /// preparations and receipts, and existing pending journal operations remain unchanged.
    /// A failed file projection can be retried on the original source reference and destination,
    /// including after restart, without issuing another move when the source is already absent.
    /// An ordinary directory move also relocates existing managed descendants' live provenance
    /// and rebuilds their membership in that path transaction, retaining their native identities
    /// and immutable recovery history. It does not create evidence for an ordinary directory.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The destination belongs to another file system, the name is invalid, or replacement was
    /// requested for a directory.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The sync root was selected, the destination lies below the source directory, or durable
    /// state already identifies an unrelated destination item.
    /// </exception>
    /// <exception cref="CloudFilesException">Windows rejects the namespace move.</exception>
    /// <exception cref="CloudItemCoordinationException">
    /// Windows moved the item but durable subtree paths or native completion validation failed.
    /// Directory recovery facts preserve whether projection actually committed.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Cancellation was observed before the synchronous move or during pre-move state access.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The owner began stopping before this move was admitted.</exception>
    public ValueTask<CloudItemMoveResult> MoveToAsync(
        CloudDirectory destination,
        string name,
        CloudMoveOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return Owner.MoveAsync(
            this,
            destination,
            name,
            options ?? CloudMoveOptions.Default,
            cancellationToken);
    }

    /// <summary>Deletes this file or empty directory without following a file-system link.</summary>
    /// <param name="cancellationToken">
    /// Token observed before the synchronous deletion and during durable-state work.
    /// </param>
    /// <returns>
    /// A missing-item snapshot and whether durable identity became a tombstone. When deleting an
    /// empty directory, all durable descendants are tombstoned as one transaction so orphaned
    /// placeholder state cannot survive an earlier external child deletion.
    /// </returns>
    /// <exception cref="InvalidOperationException">The sync root was selected.</exception>
    /// <exception cref="FileNotFoundException">The item no longer exists.</exception>
    /// <exception cref="CloudFilesException">Windows rejects the deletion.</exception>
    /// <exception cref="CloudItemCoordinationException">
    /// Windows deleted the item but its tombstone could not be committed.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Cancellation was observed before the synchronous deletion.
    /// </exception>
    public ValueTask<CloudItemDeleteResult> DeleteAsync(
        CancellationToken cancellationToken = default) =>
        Owner.DeleteAsync(this, cancellationToken);
}
