namespace CfSharp;

/// <summary>Selects an explicitly guarded identity preparation before content confirmation.</summary>
public enum CloudContentPreparation
{
    /// <summary>Requires the placeholder to already carry the accepted identity.</summary>
    None,
    /// <summary>Converts an ordinary file after checking its object binding and complete content.</summary>
    ConvertRegularFile,
    /// <summary>Replaces an explicitly expected previous placeholder identity after complete verification.</summary>
    ReplacePlaceholderIdentity,
}

/// <summary>Describes an immutable proof of content already accepted by a remote service.</summary>
/// <remarks>
/// Owns all supplied byte sequences. Concurrent reads are safe; no native handles or remote work
/// are retained. The caller supplies the upload-time binding and authenticates remote acceptance.
/// Neither a newly captured replacement binding nor a digest computed only after uploading proves
/// which object the remote service accepted. This contract requires Windows 10 version 1709 or later.
/// </remarks>
public sealed class CloudContentConfirmationRequest
{
    private readonly byte[] _hash;
    private readonly byte[] _previousIdentity;
    private readonly byte[] _acceptedIdentity;

    /// <summary>Initializes a copied proof with finite protection and total-work budgets.</summary>
    /// <param name="expectedBinding">Upload-time volume, root, and file object binding.</param>
    /// <param name="acceptedIdentity">Exact identity of the remotely accepted revision.</param>
    /// <param name="expectedLength">Accepted full content length, including zero.</param>
    /// <param name="expectedSha256">Exactly 32 bytes of accepted full-content SHA-256.</param>
    /// <param name="preparation">Explicit identity preparation, or none.</param>
    /// <param name="expectedPlaceholderIdentity">Complete previous opaque identity, required only for replacement.</param>
    /// <param name="segmentSize">Bytes per reference, from 1 through 16 MiB; defaults to 1 MiB.</param>
    /// <param name="referenceBudget">Positive read/reference budget up to one minute; defaults to 250 ms.</param>
    /// <param name="deadline">Positive total budget including item-lease waiting, up to one day; defaults to ten minutes.</param>
    /// <exception cref="ArgumentNullException">A required object is null.</exception>
    /// <exception cref="ArgumentException">A hash, preparation, identity, or budget is invalid.</exception>
    /// <remarks>
    /// Kernel cancellation must drain before memory or protection can be released, so cancellation
    /// drain and synchronous native calls can exceed these budgets. Budgets prevent further work
    /// and marking after expiration; they are not promises that a stalled driver returns on time.
    /// </remarks>
    public CloudContentConfirmationRequest(CloudLocalFileBinding expectedBinding,
        CloudPlaceholderIdentity acceptedIdentity, long expectedLength, ReadOnlySpan<byte> expectedSha256,
        CloudContentPreparation preparation = CloudContentPreparation.None,
        ReadOnlySpan<byte> expectedPlaceholderIdentity = default, int segmentSize = 1024 * 1024,
        TimeSpan? referenceBudget = null, TimeSpan? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(expectedBinding);
        ArgumentNullException.ThrowIfNull(acceptedIdentity);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedLength);
        if (expectedSha256.Length != 32)
        {
            throw new ArgumentException("A full SHA-256 digest must contain exactly 32 bytes.", nameof(expectedSha256));
        }

        if (!Enum.IsDefined(preparation) ||
            preparation == CloudContentPreparation.ReplacePlaceholderIdentity && expectedPlaceholderIdentity.IsEmpty ||
            preparation != CloudContentPreparation.ReplacePlaceholderIdentity && !expectedPlaceholderIdentity.IsEmpty ||
            expectedPlaceholderIdentity.Length > SyncRootRegistrationOptions.MaxFileIdentityLength)
        {
            throw new ArgumentException("Identity preparation requires a valid mode and an explicit previous identity for replacement.", nameof(preparation));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(segmentSize, 16 * 1024 * 1024);
        TimeSpan reference = referenceBudget ?? TimeSpan.FromMilliseconds(250);
        TimeSpan total = deadline ?? TimeSpan.FromMinutes(10);
        if (reference <= TimeSpan.Zero || reference > TimeSpan.FromMinutes(1) ||
            total <= TimeSpan.Zero || total > TimeSpan.FromDays(1) || reference > total)
        {
            throw new ArgumentOutOfRangeException(nameof(referenceBudget), "Finite positive reference and total budgets are required.");
        }

        ExpectedBinding = expectedBinding;
        AcceptedIdentity = acceptedIdentity;
        ExpectedLength = expectedLength;
        Preparation = preparation;
        SegmentSize = segmentSize;
        ReferenceBudget = reference;
        Deadline = total;
        _hash = expectedSha256.ToArray();
        _previousIdentity = expectedPlaceholderIdentity.ToArray();
        _acceptedIdentity = acceptedIdentity.Encode();
    }

    /// <summary>Gets the immutable upload-time native object binding.</summary>
    public CloudLocalFileBinding ExpectedBinding { get; }
    /// <summary>Gets the immutable identity of the remotely accepted revision.</summary>
    public CloudPlaceholderIdentity AcceptedIdentity { get; }
    /// <summary>Gets the expected complete file length.</summary>
    public long ExpectedLength { get; }
    /// <summary>Gets a defensive copy of the accepted SHA-256 digest.</summary>
    public ReadOnlyMemory<byte> ExpectedSha256 => _hash.ToArray();
    /// <summary>Gets the explicitly requested identity preparation.</summary>
    public CloudContentPreparation Preparation { get; }
    /// <summary>Gets a defensive copy of the expected complete previous opaque identity.</summary>
    public ReadOnlyMemory<byte> ExpectedPlaceholderIdentity => _previousIdentity.ToArray();
    /// <summary>Gets the bounded read-buffer size in bytes.</summary>
    public int SegmentSize { get; }
    /// <summary>Gets the maximum budget for each referenced read segment.</summary>
    public TimeSpan ReferenceBudget { get; }
    /// <summary>Gets the total budget including lease waiting before native confirmation.</summary>
    public TimeSpan Deadline { get; }

    internal ReadOnlySpan<byte> Hash => _hash;
    internal ReadOnlySpan<byte> PreviousIdentity => _previousIdentity;
    internal ReadOnlySpan<byte> EncodedIdentity => _acceptedIdentity;
}

/// <summary>Classifies confirmation without requiring exception-message parsing.</summary>
public enum CloudContentConfirmationOutcome
{
    /// <summary>The native mark and durable identity projection completed.</summary>
    Confirmed,
    /// <summary>The existing native mark was verified against the complete proof and projected.</summary>
    AlreadyConfirmed,
    /// <summary>The expected length or full digest did not match; no mark occurred.</summary>
    ContentMismatch,
    /// <summary>The complete opaque identity did not match; no mark occurred.</summary>
    IdentityMismatch,
    /// <summary>The file, actual root, or volume binding differed; no mark occurred.</summary>
    LocalObjectMismatch,
    /// <summary>Content was not fully local; no hydration or mark occurred.</summary>
    NotFullyLocal,
    /// <summary>A directory, ordinary file without conversion permission, hard link, or reparse target is unsupported.</summary>
    NotApplicable,
    /// <summary>A competing handle or mapping prevented protection; retry the entire proof later.</summary>
    Busy,
    /// <summary>The same protected handle could no longer be referenced; the accumulated digest was discarded.</summary>
    ProtectionLost,
    /// <summary>The finite work or reference budget expired before marking.</summary>
    DeadlineExceeded,
    /// <summary>Cancellation was observed before marking, after draining any pending read.</summary>
    Canceled,
    /// <summary>A native or I/O failure occurred; inspect the error, stage, and preparation receipt.</summary>
    Failed,
    /// <summary>A verified native confirmation exists but its durable identity projection needs recovery.</summary>
    NativeAppliedProjectionPending,
    /// <summary>The object or complete native identity changed during projection; reconcile native and durable state.</summary>
    /// <remarks>Historical confirmation and any successful database commit remain recorded in the receipt.</remarks>
    ProjectionConflict,
}

/// <summary>Identifies the stage at which the reported observation or failure occurred.</summary>
public enum CloudContentConfirmationStage
{
    /// <summary>Opening the exclusive opaque handle.</summary>
    Open,
    /// <summary>Referencing the same opaque handle.</summary>
    Reference,
    /// <summary>Reading and draining an individual segment.</summary>
    Read,
    /// <summary>Checking object, identity, availability, length, and digest.</summary>
    Verify,
    /// <summary>Preparing identity on the verified file object.</summary>
    Prepare,
    /// <summary>Marking the verified object through the referenced protected owner.</summary>
    Mark,
    /// <summary>Repairing the official durable identity projection after releasing protection.</summary>
    Projection,
    /// <summary>Completing native verification and durable projection.</summary>
    Complete,
}

/// <summary>Retains confirmation facts, native errors, and recoverable preparation or projection state.</summary>
/// <remarks>
/// Immutable and safe for concurrent reads. No handle ownership escapes the operation.
/// A receipt describes one past verification, not the current file state. Subsequent local writes
/// can clear in-sync. Native and database commits are not an atomic transaction. Retry the same
/// request after a pending projection or preparation, verifying the entire proof again. A projection
/// conflict requires reconciliation of the observed identity before deciding which proof to retry.
/// </remarks>
public sealed class CloudContentConfirmationResult
{
    private readonly byte[]? _observedIdentity;

    internal CloudContentConfirmationResult(CloudContentConfirmationRequest request,
        CloudContentConfirmationOutcome outcome, CloudContentConfirmationStage stage,
        bool prepared, bool applied, bool verified, bool projected, long bytes, int segments,
        TimeSpan elapsed, TimeSpan longestReference, long? preparationUsn, Exception? error,
        CloudSynchronizationState? observedSynchronizationState = null,
        int? preparationHResult = null, int? nativeMarkHResult = null,
        ReadOnlyMemory<byte>? observedPlaceholderIdentity = null,
        CloudContentConfirmationResult? nativeResult = null, Exception? projectionError = null)
    {
        Request = request;
        Outcome = outcome;
        Stage = stage;
        NativeIdentityPrepared = prepared;
        NativeApplied = applied;
        NativeConfirmationVerified = verified;
        DurableProjectionCommitted = projected;
        BytesVerified = bytes;
        SegmentsRead = segments;
        Elapsed = elapsed;
        LongestReference = longestReference;
        PreparationUsn = preparationUsn;
        Error = error;
        ObservedSynchronizationState = observedSynchronizationState;
        PreparationHResult = preparationHResult;
        NativeMarkHResult = nativeMarkHResult;
        _observedIdentity = observedPlaceholderIdentity?.ToArray();
        NativeStage = nativeResult?.NativeStage ?? stage;
        NativeError = nativeResult is null ? error : nativeResult.NativeError;
        ProjectionError = projectionError;
    }

    internal CloudContentConfirmationResult WithProjection(CloudContentConfirmationOutcome outcome,
        bool committed, TimeSpan projectionElapsed, CloudSynchronizationState? observedState,
        ReadOnlyMemory<byte>? observedIdentity, Exception? projectionError = null)
    {
        Exception? error = projectionError is null ? Error : Error is null ? projectionError :
            new AggregateException("Native confirmation and durable projection both failed.", Error, projectionError);
        CloudContentConfirmationStage stage = projectionError is not null ? CloudContentConfirmationStage.Projection :
            NativeConfirmationVerified && committed ? CloudContentConfirmationStage.Complete : Stage;
        return new(Request, outcome, stage, NativeIdentityPrepared, NativeApplied, NativeConfirmationVerified,
            committed, BytesVerified, SegmentsRead, Elapsed + projectionElapsed, LongestReference,
            PreparationUsn, error, observedState, PreparationHResult, NativeMarkHResult, observedIdentity,
            nativeResult: this, projectionError);
    }

    /// <summary>Gets the copied proof to retain for recovery.</summary>
    public CloudContentConfirmationRequest Request { get; }
    /// <summary>Gets the typed operation outcome.</summary>
    public CloudContentConfirmationOutcome Outcome { get; }
    /// <summary>Gets the completion or failure stage.</summary>
    public CloudContentConfirmationStage Stage { get; }
    /// <summary>Gets the native phase's final stage, preserved even if projection subsequently fails.</summary>
    public CloudContentConfirmationStage NativeStage { get; }
    /// <summary>Gets whether this invocation successfully converted or changed the native identity.</summary>
    public bool NativeIdentityPrepared { get; }
    /// <summary>Gets whether this invocation successfully issued the native in-sync mark.</summary>
    public bool NativeApplied { get; }
    /// <summary>Gets whether a native in-sync state was established or fully reverified against the proof.</summary>
    public bool NativeConfirmationVerified { get; }
    /// <summary>Gets whether the accepted identity was committed to the official store.</summary>
    /// <remarks>A true value is a commit fact; a projection conflict can still leave that row stale.</remarks>
    public bool DurableProjectionCommitted { get; }
    /// <summary>Gets full-proof bytes read, including a separate verification after identity preparation.</summary>
    public long BytesVerified { get; }
    /// <summary>Gets the count of drained native read segments.</summary>
    public int SegmentsRead { get; }
    /// <summary>Gets total elapsed operation time.</summary>
    public TimeSpan Elapsed { get; }
    /// <summary>Gets the longest observed protected reference lifetime, including cancellation drain.</summary>
    public TimeSpan LongestReference { get; }
    /// <summary>Gets the identity mutation's native USN observation, which may be zero.</summary>
    /// <remarks>The final mark uses a null USN pointer; this value is never a CAS token.</remarks>
    public long? PreparationUsn { get; }
    /// <summary>Gets the actual successful preparation HRESULT, or null when no preparation succeeded.</summary>
    public int? PreparationHResult { get; }
    /// <summary>Gets the actual successful mark HRESULT, or null when this invocation did not mark.</summary>
    public int? NativeMarkHResult { get; }
    /// <summary>Gets the preserved failure, including a structured native exception where applicable.</summary>
    /// <remarks>Contains an AggregateException when native confirmation and projection both fail.</remarks>
    public Exception? Error { get; }
    /// <summary>Gets the native phase's failure with its original stage and HRESULT, or null.</summary>
    /// <remarks>Inspect NativeStage independently of any later projection failure.</remarks>
    public Exception? NativeError { get; }
    /// <summary>Gets the projection failure independently of the native phase, or null.</summary>
    public Exception? ProjectionError { get; }
    /// <summary>Gets the later native state observed during projection, when available.</summary>
    /// <remarks>A subsequent local write can make this NotInSync despite a successful past mark.</remarks>
    public CloudSynchronizationState? ObservedSynchronizationState { get; }
    /// <summary>Gets a defensive copy of the complete native identity last observed during projection, or null.</summary>
    /// <remarks>An empty identity means the observed object was no longer a placeholder. This is a past observation, not a lock.</remarks>
    public ReadOnlyMemory<byte>? ObservedPlaceholderIdentity => _observedIdentity is null
        ? (ReadOnlyMemory<byte>?)null : new ReadOnlyMemory<byte>(_observedIdentity.ToArray());
}
