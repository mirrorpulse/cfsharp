namespace CfSharp;

public sealed partial class CloudFileSystem
{
    private const string ConflictDismissalPrefix = "cfsharp/remote-conflict-dismissals/v1";

    /// <summary>Preserves current local content and durably dismisses one unresolved remote conflict.</summary>
    /// <param name="conflictId">The stable identifier returned by remote batch application.</param>
    /// <param name="cancellationToken">Cancels admission or the transaction before its commit point.</param>
    /// <returns>A durable dismissal, a previous dismissal, or an identifier not known to this API.</returns>
    /// <exception cref="ArgumentException">The identifier is empty.</exception>
    /// <exception cref="InvalidOperationException">The file system has not started.</exception>
    /// <exception cref="InvalidDataException">The conflict envelope or dismissal receipt is unsupported or corrupt.</exception>
    /// <remarks>
    /// Requires a started Windows file system and a transactional state store. Safe for concurrent
    /// callers; serialized with remote apply and resolution, and admitted against disposal. The
    /// conflict removal and receipt commit atomically. Receipts persist across restart without
    /// automatic expiry. Storage errors propagate and permit retry. No native mutation, upload,
    /// batch-cursor advance, or local-operation acknowledgement occurs. KeepLocal retains its
    /// existing unresolved semantics. A later remote change may create a new conflict. NotFound
    /// does not claim that an unknown identifier was successfully resolved by some other action.
    /// </remarks>
    public async ValueTask<CloudRemoteConflictDismissalStatus> DismissRemoteConflictAsync(
        Guid conflictId,
        CancellationToken cancellationToken = default)
    {
        if (conflictId == Guid.Empty)
        {
            throw new ArgumentException("The conflict identifier cannot be empty.", nameof(conflictId));
        }

        EnsureStarted();
        await _remoteApplyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using CloudFileSystemOperationLease operation = await AcquireStateOperationAsync(cancellationToken).ConfigureAwait(false);
            await using ICloudStateTransaction transaction = await operation.StateStore
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            string receiptName = ConflictDismissalPrefix + "/" + conflictId.ToString("N");
            CloudStateCheckpoint? receipt = await transaction.Checkpoints.GetAsync(receiptName, cancellationToken)
                .ConfigureAwait(false);
            if (receipt is not null)
            {
                if (!receipt.Value.Span.SequenceEqual(new byte[] { 1 }))
                {
                    throw new InvalidDataException($"The dismissal receipt for '{conflictId}' is unsupported.");
                }

                return CloudRemoteConflictDismissalStatus.AlreadyDismissed;
            }

            CloudConflictState? conflict = await transaction.Conflicts.GetAsync(conflictId, cancellationToken)
                .ConfigureAwait(false);
            if (conflict is null)
            {
                return CloudRemoteConflictDismissalStatus.NotFound;
            }

            // Only dismiss a valid remote envelope. Arbitrary custom/local conflict payloads
            // must not be silently removed by a remote-only API.
            _ = await DecodeRemoteConflictAsync(conflict, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.Checkpoints.UpsertAsync(
                new CloudStateCheckpoint(receiptName, new byte[] { 1 }, DateTimeOffset.UtcNow), cancellationToken)
                .ConfigureAwait(false);
            await transaction.Conflicts.RemoveAsync(conflictId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return CloudRemoteConflictDismissalStatus.Dismissed;
        }
        finally
        {
            _remoteApplyGate.Release();
        }
    }
}
