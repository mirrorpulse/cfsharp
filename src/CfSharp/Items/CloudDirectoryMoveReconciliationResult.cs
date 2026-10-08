namespace CfSharp;

/// <summary>Describes the observed outcome of directory move recovery without issuing a native move.</summary>
public enum CloudDirectoryMoveReconciliationOutcome
{
    /// <summary>The prepared object remains at its source; nothing was projected.</summary>
    NotMoved = 0,
    /// <summary>The verified destination and known durable subtree were coordinated in this call.</summary>
    Projected = 1,
    /// <summary>The verified destination, current rows, and immutable completion receipt already agree.</summary>
    AlreadyProjected = 2,
    /// <summary>The native move was verified, but durable projection has not committed; retry the same proof.</summary>
    NativeObservedProjectionPending = 3,
    /// <summary>Native identity or durable membership conflicts with the historical evidence.</summary>
    Conflict = 4,
    /// <summary>The proof does not belong to this store or supported recovery scope.</summary>
    NotApplicable = 5,
    /// <summary>Cancellation interrupted the reported stage.</summary>
    Canceled = 6,
    /// <summary>A native sharing or locking conflict prevented metadata protection.</summary>
    Busy = 7,
    /// <summary>Another failure prevented completion at the reported stage.</summary>
    Failed = 8,
}

/// <summary>Identifies the last phase reached by directory move recovery.</summary>
public enum CloudDirectoryMoveReconciliationStage
{
    /// <summary>Acquiring the lifecycle, path scopes, and parent guards.</summary>
    Acquisition = 0,
    /// <summary>Authenticating the immutable preparation in its durable store.</summary>
    PreparationValidation = 1,
    /// <summary>Observing and guarding the actual native namespace object.</summary>
    NativeObservation = 2,
    /// <summary>Preflighting and committing current item paths and the receipt.</summary>
    DurableProjection = 3,
    /// <summary>Validating native observations after the durable commit.</summary>
    CompletionValidation = 4,
    /// <summary>The call completed successfully or observed that no move occurred.</summary>
    Completed = 5,
}

/// <summary>Reports directory recovery facts, including partial completion and original failures.</summary>
/// <remarks>
/// Owns no handle or transaction. Immutable properties are safe for concurrent reads, although
/// the returned exception may contain mutable inherited data. Native movement and storage cannot
/// share an ACID transaction: inspect both fact flags and the rescan requirement before proceeding.
/// No outcome establishes content equality, clears dirty state, or acknowledges local operations.
/// </remarks>
public sealed class CloudDirectoryMoveReconciliationResult
{
    internal CloudDirectoryMoveReconciliationResult(CloudDirectoryMoveReconciliationOutcome outcome,
        CloudDirectoryMoveReconciliationStage stage, bool nativeMoveObserved, bool durableProjectionCommitted,
        bool requiresFullRescan = false, Exception? error = null, int? nativeHResult = null, int updatedCount = 0)
    {
        Outcome = outcome;
        Stage = stage;
        NativeMoveObserved = nativeMoveObserved;
        DurableProjectionCommitted = durableProjectionCommitted;
        RequiresFullRescan = requiresFullRescan;
        Error = error;
        NativeHResult = nativeHResult;
        DurableStateEntriesUpdated = updatedCount;
    }

    /// <summary>Gets the classified recovery outcome.</summary>
    public CloudDirectoryMoveReconciliationOutcome Outcome { get; }
    /// <summary>Gets the last phase reached, including the phase of a failure.</summary>
    public CloudDirectoryMoveReconciliationStage Stage { get; }
    /// <summary>Gets whether this call verified the prepared native object at its intended destination.</summary>
    public bool NativeMoveObserved { get; }
    /// <summary>Gets whether current item paths and their completion receipt are durably coordinated.</summary>
    public bool DurableProjectionCommitted { get; }
    /// <summary>Gets whether uncertainty requires a full namespace reconciliation before dispatch.</summary>
    public bool RequiresFullRescan { get; }
    /// <summary>Gets the original managed or native failure, without substituting a generic storage HRESULT.</summary>
    public Exception? Error { get; }
    /// <summary>Gets an actual native failure HRESULT, or null for logical conflicts, cancellation, and storage failures.</summary>
    public int? NativeHResult { get; }
    /// <summary>Gets the number of official item paths changed in this call's committed transaction.</summary>
    public int DurableStateEntriesUpdated { get; }
}
