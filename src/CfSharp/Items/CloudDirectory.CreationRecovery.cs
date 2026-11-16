namespace CfSharp;

public sealed partial class CloudDirectory
{
    /// <summary>Creates new children and durably records their original native bindings by operation ID.</summary>
    /// <param name="requests">Immutable original requests. IDs, names, item IDs and remote IDs must be distinct within this batch.</param>
    /// <param name="options">Whether to stop after the first result other than Created or Recovered.</param>
    /// <param name="cancellationToken">Cancels before an entry is prepared. After preparation that entry's attempt and commit are allowed to finish.</param>
    /// <returns>Owned, immutable results in input order. Each successful entry commits independently.</returns>
    /// <remarks>
    /// Requires a started Windows Cloud Files owner with a transactional store, complete native IDs,
    /// and same-handle USN support. Supports directories and online-only files with Fail collision
    /// behavior. Existing occupants cannot supply a first binding. Replaying an existing ID uses
    /// the same read-only checks as <see cref="RecoverPlaceholderCreationAsync"/>. A committed receipt
    /// and official projection are atomic. If execution stops after preparation but before that
    /// commit, the entry remains Pending and replay does not repeat native creation. Cancellation or
    /// store failure may leave earlier entries committed. Query those entries independently after
    /// restart. Handles are scoped to each call, and overlapping SDK operations are serialized.
    /// </remarks>
    /// <exception cref="ArgumentException">Requests are empty, contain null or repeat an identity or name.</exception>
    /// <exception cref="InvalidOperationException">The owner is not started.</exception>
    /// <exception cref="InvalidDataException">Retained evidence is malformed.</exception>
    /// <exception cref="NotSupportedException">The store or evidence format cannot support the protocol.</exception>
    /// <exception cref="OperationCanceledException">Cancellation occurred at an entry boundary.</exception>
    public ValueTask<IReadOnlyList<CloudPlaceholderCreationRecoveryResult>> CreateRecoverablePlaceholdersAsync(
        IEnumerable<CloudPlaceholderCreationRequest> requests, CloudPlaceholderBatchOptions? options = null,
        CancellationToken cancellationToken = default) => Owner.CreateRecoverablePlaceholdersAsync(
            this, requests, options ?? CloudPlaceholderBatchOptions.Default, cancellationToken);

    /// <summary>Authenticates the original child binding of an individually committed creation.</summary>
    /// <param name="request">The immutable original request for this parent and store.</param>
    /// <param name="cancellationToken">Cancels admission, store lookups or the read-only verification.</param>
    /// <returns>A typed outcome and an owned historical receipt when the original is authenticated.</returns>
    /// <remarks>
    /// Checks the original physical root and parent plus the current guarded object's native ID,
    /// kind and exact identity. It reads metadata only and does not hydrate, mutate the native
    /// object, repair projections or acknowledge operations. It never infers evidence from older
    /// item rows or current path state. A missing record in this store returns NotFound even if
    /// the original request was created in an independent store. The receipt uses the original
    /// creation USN and binding rather than mutable projection or availability metadata.
    /// This method closes its guards before returning. Use the owning application's protection
    /// and compare the binding again when taking later action. Requires a started Windows owner.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="InvalidOperationException">The owner is not started.</exception>
    /// <exception cref="InvalidDataException">Retained evidence is malformed.</exception>
    /// <exception cref="NotSupportedException">The store or evidence format cannot support the protocol.</exception>
    /// <exception cref="OperationCanceledException">The lookup or metadata check was canceled.</exception>
    public ValueTask<CloudPlaceholderCreationRecoveryResult> RecoverPlaceholderCreationAsync(
        CloudPlaceholderCreationRequest request, CancellationToken cancellationToken = default) =>
        Owner.RunRecoverablePlaceholderAsync(this, request, create: false, cancellationToken);
}
