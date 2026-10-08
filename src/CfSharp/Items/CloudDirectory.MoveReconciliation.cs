namespace CfSharp;

public sealed partial class CloudDirectory
{
    /// <summary>Reconciles the known durable subtree after a move using its original prepared proof.</summary>
    /// <param name="proof">The proof returned before movement, or its exact decoded serialization.</param>
    /// <param name="cancellationToken">Cancels acquisition, validation, or storage before commit.</param>
    /// <returns>A result reporting native observation, actual durable completion, failures, and rescan requirements.</returns>
    /// <remarks>
    /// Call on the original source reference, including after restart. Requires Windows 10 version
    /// 1709 and complete native IDs. This method never moves a directory, rewrites native identity,
    /// hydrates content, clears dirty state, or acknowledges journal entries. It authenticates the
    /// proof in the same durable store, protects parent paths and the target with metadata guards,
    /// and validates native observations around one atomic path/receipt transaction. The transaction
    /// also refreshes retained live membership of source and destination ancestors once each;
    /// their bindings and historical preparations and receipts remain unchanged. Such guards
    /// do not freeze content, identity, or descendant changes across processes. Retry a pending
    /// projection with the same proof; conflicts require reconciliation, not a guessed replacement.
    /// The result owns no protection lifetime. Concurrent calls are coordinated within this owner.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The proof is null.</exception>
    /// <exception cref="ArgumentException">This reference is not the proof's original source path.</exception>
    public ValueTask<CloudDirectoryMoveReconciliationResult> ReconcileMoveAsync(CloudDirectoryMoveProof proof,
        CancellationToken cancellationToken = default) =>
        Owner.ReconcileDirectoryMoveAsync(this, proof, cancellationToken);
}
