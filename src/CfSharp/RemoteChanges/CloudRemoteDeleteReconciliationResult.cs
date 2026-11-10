namespace CfSharp;

/// <summary>Describes a metadata-only decision for one original remote Delete entry.</summary>
/// <remarks>No value certifies native deletion or application/backend acceptance.</remarks>
public enum CloudRemoteDeleteReconciliationStatus
{
    /// <summary>A retained conflict, its replay receipt, and one entry of progress committed atomically.</summary>
    RecordedConflict = 0,
    /// <summary>The same original entry already has a durable reconciliation result.</summary>
    AlreadyRecorded = 1,
    /// <summary>A lookup found no reconciliation receipt for this original entry.</summary>
    NotFound = 2,
    /// <summary>The requested ordinal is not the next durable entry and has no reconciliation receipt.</summary>
    EntryNotCurrent = 3,
    /// <summary>The original batch fingerprint, count, or identity differs from retained progress.</summary>
    BatchMismatch = 4,
    /// <summary>The policy is destructive, supplies a resolver, or differs from this entry's retained policy.</summary>
    PolicyMismatch = 5,
    /// <summary>The receipt belongs to a different durable store scope or Windows root path.</summary>
    ScopeMismatch = 6,
    /// <summary>No matching official tombstone exists, including exact identity/kind/revision and Windows-equivalent path.</summary>
    NotApplicable = 7,
    /// <summary>Original native recovery evidence exists; ordinary remote replay must handle it.</summary>
    PendingNativeRecovery = 8,
    /// <summary>Another remote apply, resolution, or reconciliation owns this instance's remote gate.</summary>
    Busy = 9,
}

/// <summary>Owns the immutable result of rejecting an original Delete without native work.</summary>
/// <remarks>
/// Safe for concurrent reads and independent of the owner/store lifetime. Cursor bytes are copied.
/// A recorded result reports the prefix and cursor at its own commit, not later batch progress.
/// The conflict remains queryable here after dismissal. Its LocalState is the original tombstone
/// snapshot, unlike the query-time state returned by general remote-conflict queries. Neither
/// a recorded conflict nor an Applied batch status claims a successful native Delete.
/// </remarks>
public sealed class CloudRemoteDeleteReconciliationResult
{
    private readonly byte[] _safeCursor;

    internal CloudRemoteDeleteReconciliationResult(CloudRemoteDeleteReconciliationStatus status, int entryIndex,
        Guid? conflictId = null, CloudRemoteApplyEntryResult? entry = null, int? appliedEntryCount = null,
        CloudRemoteBatchStatus? batchStatus = null, ReadOnlySpan<byte> safeCursor = default)
    {
        Status = status;
        EntryIndex = entryIndex;
        ConflictId = conflictId;
        Entry = entry;
        AppliedEntryCount = appliedEntryCount;
        BatchStatus = batchStatus;
        _safeCursor = safeCursor.ToArray();
    }

    /// <summary>Gets the typed result or non-mutating refusal.</summary>
    public CloudRemoteDeleteReconciliationStatus Status { get; }
    /// <summary>Gets the requested zero-based ordinal in the unchanged original batch.</summary>
    public int EntryIndex { get; }
    /// <summary>Gets the stable conflict identifier for a recorded result, otherwise null.</summary>
    public Guid? ConflictId { get; }
    /// <summary>Gets the original Conflict entry result, otherwise null.</summary>
    public CloudRemoteApplyEntryResult? Entry { get; }
    /// <summary>Gets the prefix at this result's commit, otherwise null.</summary>
    public int? AppliedEntryCount { get; }
    /// <summary>Gets the batch status at this result's commit, otherwise null.</summary>
    public CloudRemoteBatchStatus? BatchStatus { get; }
    /// <summary>Gets the owned safe cursor at this result's commit; empty for an unrecorded result.</summary>
    public ReadOnlyMemory<byte> SafeCursor => _safeCursor;
    /// <summary>Gets false: this operation never queries or mutates a native namespace object.</summary>
    public bool NativeMutationAttempted { get; }
}
