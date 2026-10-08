namespace CfSharp;

/// <summary>Describes a completed placeholder conversion, patch, or reversion.</summary>
/// <remarks>
/// The result owns no native resources. Its snapshot was captured after native and durable-state
/// work completed and is safe for concurrent reads. The path-bound item reference itself remains
/// immutable.
/// </remarks>
public sealed class CloudPlaceholderMutationResult
{
    internal CloudPlaceholderMutationResult(
        string path,
        long? operationUsn,
        CloudItemSnapshot snapshot,
        bool durableStateUpdated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        Path = path;
        OperationUsn = operationUsn;
        Snapshot = snapshot;
        DurableStateUpdated = durableStateUpdated;
    }

    /// <summary>Gets the normalized absolute path that was mutated.</summary>
    public string Path { get; }

    /// <summary>Gets the final USN returned by Windows, when the native operation supplies one.</summary>
    /// <remarks>
    /// The native value is preserved, including zero after a successful mutation. Zero cannot be
    /// used as a conditional token. Use <see cref="CloudItem.ReadUsnAsync"/> before independently
    /// verifying content when a fresh positive USN is required.
    /// </remarks>
    public long? OperationUsn { get; }

    /// <summary>Gets a fresh handle-free snapshot captured after the operation.</summary>
    public CloudItemSnapshot Snapshot { get; }

    /// <summary>Gets whether the operation changed and committed durable identity state.</summary>
    public bool DurableStateUpdated { get; }
}

/// <summary>Describes a completed pin, in-sync, or population-independent state change.</summary>
public sealed class CloudStateChangeResult
{
    internal CloudStateChangeResult(
        string path,
        long? operationUsn,
        CloudItemSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        Path = path;
        OperationUsn = operationUsn;
        Snapshot = snapshot;
    }

    /// <summary>Gets the normalized absolute path that was changed.</summary>
    public string Path { get; }

    /// <summary>Gets the final USN returned by Windows, when available.</summary>
    /// <remarks>
    /// Windows may return zero after success. This is not a conditional token; use
    /// <see cref="CloudItem.ReadUsnAsync"/> before verifying content and conditionally marking in sync.
    /// </remarks>
    public long? OperationUsn { get; }

    /// <summary>Gets a fresh handle-free snapshot captured after the change.</summary>
    public CloudItemSnapshot Snapshot { get; }
}

/// <summary>Describes completed steps of a requested file availability transition.</summary>
public sealed class CloudAvailabilityChangeResult
{
    internal CloudAvailabilityChangeResult(
        string path,
        CloudAvailabilityTarget target,
        bool pinStateApplied,
        bool contentStateApplied,
        CloudItemSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        Target = CloudPlaceholderConversionOptions.RequireDefined(target, nameof(target));
        Path = path;
        PinStateApplied = pinStateApplied;
        ContentStateApplied = contentStateApplied;
        Snapshot = snapshot;
    }

    /// <summary>Gets the normalized absolute path that was changed.</summary>
    public string Path { get; }

    /// <summary>Gets the requested availability target.</summary>
    public CloudAvailabilityTarget Target { get; }

    /// <summary>Gets whether the target pin-state step completed.</summary>
    public bool PinStateApplied { get; }

    /// <summary>Gets whether the target hydration or dehydration step completed.</summary>
    public bool ContentStateApplied { get; }

    /// <summary>Gets a fresh snapshot captured after all completed steps.</summary>
    public CloudItemSnapshot Snapshot { get; }
}

/// <summary>Reports a failed availability transition while preserving completed steps.</summary>
public sealed class CloudAvailabilityTransitionException : Exception
{
    internal CloudAvailabilityTransitionException(
        CloudFilesException failure,
        CloudAvailabilityChangeResult partialResult)
        : base(failure.Message, failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(partialResult);
        Failure = failure;
        PartialResult = partialResult;
        HResult = failure.HResult;
    }

    /// <summary>Gets the original native Cloud Files failure with operation and path details.</summary>
    public CloudFilesException Failure { get; }

    /// <summary>Gets completed transition steps and a fresh post-failure snapshot.</summary>
    public CloudAvailabilityChangeResult PartialResult { get; }
}

/// <summary>Describes a completed same-root move or rename.</summary>
/// <remarks>
/// The result owns no native resources. Its item reference and snapshot are immutable and safe for
/// concurrent reads; subsequent file-system changes require a new inspection.
/// </remarks>
public sealed class CloudItemMoveResult
{
    internal CloudItemMoveResult(
        string sourcePath,
        string destinationPath,
        CloudItem item,
        CloudItemSnapshot snapshot,
        int durableStateEntriesUpdated,
        CloudDirectoryMoveReconciliationResult? directoryReconciliation = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegative(durableStateEntriesUpdated);
        SourcePath = sourcePath;
        DestinationPath = destinationPath;
        Item = item;
        Snapshot = snapshot;
        DurableStateEntriesUpdated = durableStateEntriesUpdated;
        DirectoryReconciliation = directoryReconciliation;
    }

    /// <summary>Gets the normalized absolute source path used by the operation.</summary>
    public string SourcePath { get; }

    /// <summary>Gets the normalized absolute destination path.</summary>
    public string DestinationPath { get; }

    /// <summary>Gets the new immutable path-bound item reference.</summary>
    public CloudItem Item { get; }

    /// <summary>Gets a fresh handle-free snapshot captured at the destination.</summary>
    public CloudItemSnapshot Snapshot { get; }

    /// <summary>Gets the number of durable item paths updated in the committed transaction.</summary>
    public int DurableStateEntriesUpdated { get; }

    /// <summary>
    /// Gets verified directory coordination facts, including any required rescan, or null for a
    /// file, ordinary directory, unchanged path, or storage without supported recovery evidence.
    /// The value owns no protection lifetime and does not acknowledge pending operations.
    /// </summary>
    public CloudDirectoryMoveReconciliationResult? DirectoryReconciliation { get; }
}

/// <summary>Describes a completed file or empty-directory deletion.</summary>
/// <remarks>
/// The result owns no native resources. Its snapshot is immutable and safe for concurrent reads;
/// a durable tombstone contains coordination metadata only, never deleted file content.
/// </remarks>
public sealed class CloudItemDeleteResult
{
    internal CloudItemDeleteResult(
        string path,
        CloudItemKind kind,
        bool durableStateUpdated,
        CloudItemSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Kind = CloudPlaceholderConversionOptions.RequireDefined(kind, nameof(kind));
        ArgumentNullException.ThrowIfNull(snapshot);
        Path = path;
        DurableStateUpdated = durableStateUpdated;
        Snapshot = snapshot;
    }

    /// <summary>Gets the normalized absolute path that was deleted.</summary>
    public string Path { get; }

    /// <summary>Gets whether the deleted item was a file or directory.</summary>
    public CloudItemKind Kind { get; }

    /// <summary>Gets whether an existing durable identity was committed as a tombstone.</summary>
    public bool DurableStateUpdated { get; }

    /// <summary>Gets a fresh missing-item snapshot, including its tombstone when present.</summary>
    public CloudItemSnapshot Snapshot { get; }
}

/// <summary>
/// Reports that a file-system mutation completed but durable coordination or native completion
/// validation failed.
/// </summary>
/// <remarks>
/// Windows and a configured state store cannot share one physical transaction. The exception
/// preserves the completed operation and USN so callers can diagnose and reconcile the split
/// outcome. Retrying identity operations is safe when the native identity already matches.
/// </remarks>
public sealed class CloudItemCoordinationException : Exception
{
    internal CloudItemCoordinationException(
        string operation,
        string path,
        long? operationUsn,
        Exception innerException)
        : this(operation, path, destinationPath: null, operationUsn, innerException)
    {
    }

    internal CloudItemCoordinationException(
        string operation,
        string path,
        string? destinationPath,
        long? operationUsn,
        Exception innerException,
        CloudDirectoryMoveReconciliationResult? directoryReconciliation = null)
        : base(
            directoryReconciliation?.DurableProjectionCommitted == true
                ? $"File-system operation '{operation}' completed for '{path}' and committed its paths, but native completion requires reconciliation."
                : destinationPath is null
                ? $"File-system operation '{operation}' completed for '{path}', but its durable state could not be committed."
                : $"File-system operation '{operation}' moved '{path}' to '{destinationPath}', but its durable state could not be committed.",
            innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Operation = operation;
        Path = path;
        DestinationPath = destinationPath;
        OperationUsn = operationUsn;
        DirectoryReconciliation = directoryReconciliation;
    }

    internal CloudItemCoordinationException(
        string operation,
        string path,
        long? operationUsn,
        Exception innerException,
        CloudRecursiveOperationResult partialResult)
        : this(operation, path, destinationPath: null, operationUsn, innerException)
    {
        ArgumentNullException.ThrowIfNull(partialResult);
        PartialResult = partialResult;
    }

    /// <summary>Gets the stable CfSharp operation name.</summary>
    public string Operation { get; }

    /// <summary>Gets the normalized absolute path changed by Windows.</summary>
    public string Path { get; }

    /// <summary>Gets the move destination path, or null for a non-move operation.</summary>
    public string? DestinationPath { get; }

    /// <summary>Gets the final USN returned by Windows, when available.</summary>
    public long? OperationUsn { get; }

    /// <summary>
    /// Gets the ordered recursive result accumulated before durable-state coordination failed, or
    /// <see langword="null"/> for a non-recursive operation.
    /// </summary>
    public CloudRecursiveOperationResult? PartialResult { get; }

    /// <summary>
    /// Gets directory recovery facts, including a real commit followed by failed native validation,
    /// or null for other coordination failures. The original underlying failure remains the inner exception.
    /// </summary>
    public CloudDirectoryMoveReconciliationResult? DirectoryReconciliation { get; }
}
