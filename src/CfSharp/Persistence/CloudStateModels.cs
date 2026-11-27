namespace CfSharp;

/// <summary>Identifies the file-system kind represented by a cloud item.</summary>
public enum CloudItemKind
{
    /// <summary>The item is a file.</summary>
    File = 0,

    /// <summary>The item is a directory.</summary>
    Directory = 1,
}

/// <summary>Identifies the durable local operation awaiting application acknowledgement.</summary>
public enum CloudStateOperationKind
{
    /// <summary>A local item was created.</summary>
    Create = 0,

    /// <summary>Local file content changed.</summary>
    ContentUpdate = 1,

    /// <summary>Local item metadata changed.</summary>
    MetadataUpdate = 2,

    /// <summary>A local item moved or was renamed.</summary>
    Move = 3,

    /// <summary>A local item was deleted.</summary>
    Delete = 4,
}

/// <summary>Classifies a durable synchronization conflict without choosing its resolution.</summary>
public enum CloudStateConflictKind
{
    /// <summary>Local and remote file content changed incompatibly.</summary>
    Content = 0,

    /// <summary>Local and remote metadata changed incompatibly.</summary>
    Metadata = 1,

    /// <summary>Concurrent moves or renames cannot be reconciled automatically.</summary>
    Move = 2,

    /// <summary>A deletion conflicts with another local or remote change.</summary>
    Delete = 3,
}

/// <summary>Describes the durable lifecycle state of a remote change batch.</summary>
public enum CloudRemoteBatchStatus
{
    /// <summary>The batch has durable progress but is not fully applied.</summary>
    Applying = 0,

    /// <summary>The batch completed and its resulting cursor is safe to publish.</summary>
    Applied = 1,

    /// <summary>The batch stopped with a recoverable or terminal failure.</summary>
    Failed = 2,
}

/// <summary>Provides immutable sync-root information to a state-store factory.</summary>
/// <remarks>
/// The context owns only managed strings and is safe for concurrent reads. The path is normalized
/// to an absolute path but need not exist when a caller constructs a context for conformance tests.
/// Production contexts are created only after <c>CloudFileSystem</c> validates the root.
/// </remarks>
public sealed class CloudStateStoreContext
{
    /// <summary>Initializes a context for a normalized sync-root path.</summary>
    /// <param name="syncRootPath">Absolute path of the sync root associated with the store.</param>
    /// <exception cref="ArgumentException">The path is empty or not fully qualified.</exception>
    public CloudStateStoreContext(string syncRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(syncRootPath);
        if (!Path.IsPathFullyQualified(syncRootPath))
        {
            throw new ArgumentException("The sync-root path must be fully qualified.", nameof(syncRootPath));
        }

        SyncRootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(syncRootPath));
    }

    /// <summary>Gets the normalized absolute sync-root path.</summary>
    public string SyncRootPath { get; }
}

/// <summary>Represents immutable durable identity and revision state for one cloud item.</summary>
/// <remarks>
/// This value owns only managed immutable data and is safe for concurrent reads. Its relative
/// path is validated against CfSharp's Windows relative-path rules. Its directory-separator
/// spelling is retained because state-store keys must remain compatible with existing stores.
/// </remarks>
public sealed class CloudItemState
{
    /// <summary>Initializes an item-state value.</summary>
    public CloudItemState(
        Guid itemId,
        string remoteId,
        string relativePath,
        CloudItemKind kind,
        string? remoteRevision,
        long? localFileId,
        bool isTombstone,
        DateTimeOffset updatedAt)
    {
        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("The item identifier cannot be empty.", nameof(itemId));
        }

        ItemId = itemId;
        RemoteId = CloudStateModelValidation.RequireText(remoteId, nameof(remoteId));
        RelativePath = CloudStateModelValidation.CanonicalizeRelativePath(relativePath, nameof(relativePath));
        Kind = CloudStateModelValidation.RequireDefined(kind, nameof(kind));
        RemoteRevision = remoteRevision;
        LocalFileId = localFileId;
        IsTombstone = isTombstone;
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Gets the stable CfSharp identity encoded into placeholder identity data.</summary>
    public Guid ItemId { get; }

    /// <summary>Gets the provider-defined stable remote object identifier.</summary>
    public string RemoteId { get; }

    /// <summary>
    /// Gets the validated path relative to the owning sync root. Existing separator spelling is
    /// retained for state-store key compatibility.
    /// </summary>
    public string RelativePath { get; }

    /// <summary>Gets whether the item is a file or directory.</summary>
    public CloudItemKind Kind { get; }

    /// <summary>Gets the last remote revision acknowledged by both sides, when available.</summary>
    public string? RemoteRevision { get; }

    /// <summary>Gets the volume-specific local file identifier, when it has been observed.</summary>
    public long? LocalFileId { get; }

    /// <summary>Gets whether this record preserves a deletion tombstone.</summary>
    public bool IsTombstone { get; }

    /// <summary>Gets the UTC instant at which this state was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; }
}

/// <summary>Represents an opaque named synchronization checkpoint.</summary>
public sealed class CloudStateCheckpoint
{
    private readonly byte[] _value;

    /// <summary>Initializes a checkpoint and copies its opaque value.</summary>
    public CloudStateCheckpoint(string name, ReadOnlySpan<byte> value, DateTimeOffset updatedAt)
    {
        Name = CloudStateModelValidation.RequireText(name, nameof(name));
        _value = value.ToArray();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Gets the ordinal, case-sensitive checkpoint name.</summary>
    public string Name { get; }

    /// <summary>Gets a read-only view of the provider or coordinator-defined checkpoint bytes.</summary>
    public ReadOnlyMemory<byte> Value => _value;

    /// <summary>Gets the UTC instant at which this checkpoint was written.</summary>
    public DateTimeOffset UpdatedAt { get; }
}

/// <summary>Represents one ordered, replayable local operation.</summary>
public sealed class CloudOperationJournalEntry
{
    private readonly byte[] _payload;

    /// <summary>Initializes an operation journal entry and copies its CfSharp-defined payload.</summary>
    public CloudOperationJournalEntry(
        Guid operationId,
        CloudStateOperationKind kind,
        Guid? itemId,
        ReadOnlySpan<byte> payload,
        DateTimeOffset createdAt,
        int attemptCount = 0,
        DateTimeOffset? retryAfter = null,
        long sequence = 0)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("The operation identifier cannot be empty.", nameof(operationId));
        }

        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("The item identifier cannot be empty when supplied.", nameof(itemId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(attemptCount);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        OperationId = operationId;
        Kind = CloudStateModelValidation.RequireDefined(kind, nameof(kind));
        ItemId = itemId;
        _payload = payload.ToArray();
        CreatedAt = createdAt.ToUniversalTime();
        AttemptCount = attemptCount;
        RetryAfter = retryAfter?.ToUniversalTime();
        Sequence = sequence;
    }

    /// <summary>Gets the idempotency identifier of the operation.</summary>
    public Guid OperationId { get; }

    /// <summary>Gets the local operation kind.</summary>
    public CloudStateOperationKind Kind { get; }

    /// <summary>Gets the related item identifier, when one is known.</summary>
    public Guid? ItemId { get; }

    /// <summary>Gets a read-only view of the versioned CfSharp-defined operation payload.</summary>
    public ReadOnlyMemory<byte> Payload => _payload;

    /// <summary>Gets the UTC instant at which the operation was first observed.</summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>Gets the number of failed delivery attempts.</summary>
    public int AttemptCount { get; }

    /// <summary>Gets the earliest UTC instant for another attempt, when delayed.</summary>
    public DateTimeOffset? RetryAfter { get; }

    /// <summary>Gets the durable ordering sequence, or zero before enqueueing.</summary>
    public long Sequence { get; }
}

/// <summary>Represents a durable unresolved synchronization conflict.</summary>
public sealed class CloudConflictState
{
    private readonly byte[] _payload;

    /// <summary>Initializes a conflict record and copies its CfSharp-defined payload.</summary>
    public CloudConflictState(
        Guid conflictId,
        Guid? itemId,
        CloudStateConflictKind kind,
        ReadOnlySpan<byte> payload,
        DateTimeOffset createdAt)
    {
        if (conflictId == Guid.Empty)
        {
            throw new ArgumentException("The conflict identifier cannot be empty.", nameof(conflictId));
        }

        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("The item identifier cannot be empty when supplied.", nameof(itemId));
        }

        ConflictId = conflictId;
        ItemId = itemId;
        Kind = CloudStateModelValidation.RequireDefined(kind, nameof(kind));
        _payload = payload.ToArray();
        CreatedAt = createdAt.ToUniversalTime();
    }

    /// <summary>Gets the stable conflict identifier.</summary>
    public Guid ConflictId { get; }

    /// <summary>Gets the related item identifier, when one is known.</summary>
    public Guid? ItemId { get; }

    /// <summary>Gets the conflict classification.</summary>
    public CloudStateConflictKind Kind { get; }

    /// <summary>Gets a read-only view of the versioned CfSharp-defined conflict payload.</summary>
    public ReadOnlyMemory<byte> Payload => _payload;

    /// <summary>Gets the UTC instant at which the conflict was detected.</summary>
    public DateTimeOffset CreatedAt { get; }
}

/// <summary>Represents durable progress for one provider-defined remote batch.</summary>
public sealed class CloudRemoteBatchState
{
    private readonly byte[] _cursor;
    private readonly byte[] _fingerprint;
    private readonly byte[] _payload;

    /// <summary>Initializes remote-batch progress and copies all opaque bytes.</summary>
    public CloudRemoteBatchState(
        string batchId,
        ReadOnlySpan<byte> cursor,
        int appliedEntryCount,
        int totalEntryCount,
        CloudRemoteBatchStatus status,
        ReadOnlySpan<byte> payload,
        DateTimeOffset updatedAt,
        ReadOnlyMemory<byte> fingerprint = default,
        string? lastAppliedChangeId = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(appliedEntryCount);
        ArgumentOutOfRangeException.ThrowIfNegative(totalEntryCount);
        if (appliedEntryCount > totalEntryCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(appliedEntryCount),
                appliedEntryCount,
                "Applied entries cannot exceed total entries.");
        }

        BatchId = CloudStateModelValidation.RequireText(batchId, nameof(batchId));
        _cursor = cursor.ToArray();
        _fingerprint = fingerprint.ToArray();
        if (lastAppliedChangeId is not null)
        {
            lastAppliedChangeId = CloudStateModelValidation.RequireText(
                lastAppliedChangeId,
                nameof(lastAppliedChangeId));
        }

        LastAppliedChangeId = lastAppliedChangeId;
        AppliedEntryCount = appliedEntryCount;
        TotalEntryCount = totalEntryCount;
        Status = CloudStateModelValidation.RequireDefined(status, nameof(status));
        _payload = payload.ToArray();
        UpdatedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>Gets the provider-defined idempotent batch identifier.</summary>
    public string BatchId { get; }

    /// <summary>Gets the opaque remote cursor associated with durable progress.</summary>
    public ReadOnlyMemory<byte> Cursor => _cursor;

    /// <summary>
    /// Gets the deterministic fingerprint of the immutable remote batch, or empty for legacy
    /// state created before remote-batch fingerprints were introduced.
    /// </summary>
    public ReadOnlyMemory<byte> Fingerprint => _fingerprint;

    /// <summary>Gets the last durably completed entry identifier, when one has been applied.</summary>
    public string? LastAppliedChangeId { get; }

    /// <summary>Gets the number of entries durably applied.</summary>
    public int AppliedEntryCount { get; }

    /// <summary>Gets the total number of entries declared by the batch.</summary>
    public int TotalEntryCount { get; }

    /// <summary>Gets the durable batch lifecycle status.</summary>
    public CloudRemoteBatchStatus Status { get; }

    /// <summary>Gets a read-only view of versioned CfSharp recovery data.</summary>
    public ReadOnlyMemory<byte> Payload => _payload;

    /// <summary>Gets the UTC instant at which progress was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; }
}

/// <summary>Represents a durable provider-originated local-change suppression record.</summary>
public sealed class CloudEchoSuppressionState
{
    internal const string RemoteChangePayloadPrefix = "remote-change/v1|";
    private static readonly byte[] s_remoteChangePayloadPrefix = System.Text.Encoding.UTF8.GetBytes(RemoteChangePayloadPrefix);
    private readonly byte[] _payload;

    /// <summary>Initializes a suppression record and copies its CfSharp-defined payload.</summary>
    public CloudEchoSuppressionState(
        Guid suppressionId,
        Guid? itemId,
        CloudStateOperationKind kind,
        string relativePath,
        ReadOnlySpan<byte> payload,
        DateTimeOffset expiresAt,
        string? previousRelativePath = null,
        int remainingObservations = 1)
    {
        if (suppressionId == Guid.Empty)
        {
            throw new ArgumentException(
                "The suppression identifier cannot be empty.",
                nameof(suppressionId));
        }

        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("The item identifier cannot be empty when supplied.", nameof(itemId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(remainingObservations);

        SuppressionId = suppressionId;
        ItemId = itemId;
        Kind = CloudStateModelValidation.RequireDefined(kind, nameof(kind));
        RelativePath = CloudStateModelValidation.CanonicalizeRelativePath(relativePath, nameof(relativePath));
        PreviousRelativePath = previousRelativePath is null
            ? null
            : CloudStateModelValidation.CanonicalizeRelativePath(previousRelativePath, nameof(previousRelativePath));
        _payload = payload.ToArray();
        ExpiresAt = expiresAt.ToUniversalTime();
        RemainingObservations = remainingObservations;
    }

    /// <summary>Gets the idempotent suppression identifier.</summary>
    public Guid SuppressionId { get; }

    /// <summary>Gets the related item identifier, when one is known.</summary>
    public Guid? ItemId { get; }

    /// <summary>Gets the provider-originated operation kind.</summary>
    public CloudStateOperationKind Kind { get; }

    /// <summary>
    /// Gets the validated affected path relative to the sync root. Existing separator spelling is
    /// retained for state-store key compatibility.
    /// </summary>
    public string RelativePath { get; }

    /// <summary>
    /// Gets the optional second path associated with the expected observation, such as the source
    /// path of a move.
    /// </summary>
    public string? PreviousRelativePath { get; }

    /// <summary>Gets a read-only view of versioned CfSharp matching data.</summary>
    public ReadOnlyMemory<byte> Payload => _payload;

    /// <summary>Gets the UTC instant after which the record is no longer active.</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>Gets the number of matching local observations that may still be consumed.</summary>
    public int RemainingObservations { get; }

    internal bool IsRemoteUpsertContentEcho =>
        Kind == CloudStateOperationKind.ContentUpdate && _payload.AsSpan().StartsWith(s_remoteChangePayloadPrefix);

    internal bool Matches(
        CloudStateOperationKind observedKind,
        string relativePath,
        string? previousRelativePath,
        Guid? observedItemId)
    {
        if (Kind != observedKind)
        {
            return false;
        }

        // A suppression tied to an item identity must fail closed when the
        // watcher cannot recover an identity for the observed path. Matching
        // by path alone could consume a remote suppression for an unrelated
        // local item that happens to reuse the same name.
        if (ItemId is Guid expectedItemId &&
            (observedItemId is not Guid actualItemId || expectedItemId != actualItemId))
        {
            return false;
        }

        return PathsOverlap(RelativePath, relativePath, previousRelativePath) ||
            (PreviousRelativePath is not null &&
             PathsOverlap(PreviousRelativePath, relativePath, previousRelativePath));
    }

    internal CloudEchoSuppressionState Consume() =>
        new(
            SuppressionId,
            ItemId,
            Kind,
            RelativePath,
            _payload,
            ExpiresAt,
            PreviousRelativePath,
            RemainingObservations - 1);

    private static bool PathsOverlap(
        string expectedPath,
        string observedPath,
        string? observedPreviousPath) =>
        string.Equals(expectedPath, observedPath, StringComparison.OrdinalIgnoreCase) ||
        (observedPreviousPath is not null && string.Equals(
            expectedPath,
            observedPreviousPath,
            StringComparison.OrdinalIgnoreCase));
}

internal static class CloudStateModelValidation
{
    internal static string CanonicalizeRelativePath(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length != 0)
        {
            // Validate every segment against the Windows relative-path contract, while retaining
            // the caller's separator spelling for repository keys and cursor compatibility.
            _ = CloudRemotePathValidation.Canonicalize(value, parameterName);
        }

        return value;
    }

    internal static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

    internal static string RequireNonNull(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        return value;
    }

    internal static T RequireDefined<T>(T value, string parameterName)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, null);
        }

        return value;
    }
}
