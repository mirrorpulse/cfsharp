using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace CfSharp;

/// <summary>Identifies the namespace mutation represented by one remote change.</summary>
public enum CloudRemoteChangeKind
{
    /// <summary>Creates or replaces a remote file placeholder.</summary>
    FileUpsert = 0,

    /// <summary>Creates or replaces a remote directory placeholder.</summary>
    DirectoryUpsert = 1,

    /// <summary>Updates remote metadata without changing the namespace path.</summary>
    MetadataUpdate = 2,

    /// <summary>Moves or renames an existing remote item.</summary>
    Move = 3,

    /// <summary>Deletes a remote item and records a durable tombstone when possible.</summary>
    Delete = 4,
}

/// <summary>Describes why a remote change cannot be applied without an explicit decision.</summary>
public enum CloudRemoteConflictReason
{
    /// <summary>Remote and local file content changed from the same acknowledged revision.</summary>
    Content = 0,

    /// <summary>Remote and local metadata changed incompatibly.</summary>
    Metadata = 1,

    /// <summary>Remote and local namespace moves cannot both be applied.</summary>
    Move = 2,

    /// <summary>A remote deletion requires destructive authorization or conflicts with local unsynchronized state.</summary>
    Delete = 3,

    /// <summary>The remote target path is occupied by an unrelated local item.</summary>
    PathCollision = 4,

    /// <summary>The remote entry does not follow the durable revision expected by the caller.</summary>
    StaleRemoteRevision = 5,

    /// <summary>The remote identity cannot be resolved to a required local item.</summary>
    MissingItem = 6,
}

/// <summary>Chooses how an application resolves a durable remote/local conflict.</summary>
public enum CloudRemoteConflictDecision
{
    /// <summary>Preserves local state and retains the remote change as unresolved history.</summary>
    KeepLocal = 0,

    /// <summary>Applies the remote change after a second safety check.</summary>
    KeepRemote = 1,

    /// <summary>Applies the remote item at an explicit conflict path.</summary>
    KeepBoth = 2,

    /// <summary>Leaves both states unchanged and defers the decision.</summary>
    Defer = 3,
}

/// <summary>Reports the durable outcome for one remote change entry.</summary>
public enum CloudRemoteApplyEntryStatus
{
    /// <summary>The remote mutation and its durable coordination state were committed.</summary>
    Applied = 0,

    /// <summary>The entry was already durably applied and no mutation was repeated.</summary>
    AlreadyApplied = 1,

    /// <summary>The entry became a durable conflict record rather than a namespace mutation.</summary>
    Conflict = 2,

    /// <summary>The entry failed and must be retried or reconciled.</summary>
    Failed = 3,

    /// <summary>The batch stopped before this entry was attempted.</summary>
    NotProcessed = 4,
}

/// <summary>Represents one immutable application-supplied remote namespace change.</summary>
/// <remarks>
/// <para>
/// The remote identifier and revision are opaque provider values. CfSharp copies them but never
/// compares revisions lexically or interprets their business meaning. The entry contains metadata
/// and length only; remote content bytes remain owned by the application's content provider.
/// </para>
/// <para>
/// Paths are canonicalized to Windows directory separators and validated as relative path
/// segments. The owning <see cref="CloudFileSystem"/> performs the final reparse-aware containment
/// check before a namespace mutation.
/// </para>
/// </remarks>
public sealed class CloudRemoteChange
{
    private readonly byte[] _cursorAfter;

    /// <summary>Initializes one validated remote change.</summary>
    /// <param name="changeId">Provider-stable identifier used for idempotent replay.</param>
    /// <param name="kind">The requested remote mutation.</param>
    /// <param name="remoteId">Provider-stable remote object identifier.</param>
    /// <param name="remoteRevision">New opaque revision represented by this change.</param>
    /// <param name="itemKind">Whether the remote object is a file or directory.</param>
    /// <param name="relativePath">Canonical destination path relative to the sync root.</param>
    /// <param name="itemId">
    /// Known or explicitly requested CfSharp item identifier. A distinct upsert at a retired path
    /// creates this identity while retaining the predecessor tombstone. A conflicting live identity
    /// is not replaced or silently substituted. Without an ID, a matching remote identity is reused;
    /// otherwise a new identity is allocated and retained before native creation.
    /// </param>
    /// <param name="previousRemoteRevision">Revision expected before this change, when known.</param>
    /// <param name="previousRelativePath">Source path for a move, otherwise null.</param>
    /// <param name="length">Logical file length for a file upsert, otherwise null.</param>
    /// <param name="metadata">Remote file-system metadata when the operation supplies it.</param>
    /// <param name="cursorAfter">Opaque cursor immediately after this entry, when available.</param>
    /// <exception cref="ArgumentException">An identifier, path, revision, or value is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An enum or file length is invalid.</exception>
    public CloudRemoteChange(
        string changeId,
        CloudRemoteChangeKind kind,
        string remoteId,
        string remoteRevision,
        CloudItemKind itemKind,
        string relativePath,
        Guid? itemId = null,
        string? previousRemoteRevision = null,
        string? previousRelativePath = null,
        long? length = null,
        CloudPlaceholderMetadata? metadata = null,
        ReadOnlyMemory<byte> cursorAfter = default)
    {
        ChangeId = RequireText(changeId, nameof(changeId));
        Kind = RequireDefined(kind, nameof(kind));
        RemoteId = RequireText(remoteId, nameof(remoteId));
        RemoteRevision = RequireText(remoteRevision, nameof(remoteRevision));
        ItemKind = RequireDefined(itemKind, nameof(itemKind));
        RelativePath = CloudRemotePathValidation.Canonicalize(relativePath, nameof(relativePath));
        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("The item identifier cannot be empty when supplied.", nameof(itemId));
        }

        if (previousRemoteRevision is not null)
        {
            previousRemoteRevision = RequireText(previousRemoteRevision, nameof(previousRemoteRevision));
        }

        if (previousRelativePath is not null)
        {
            previousRelativePath = CloudRemotePathValidation.Canonicalize(
                previousRelativePath,
                nameof(previousRelativePath));
        }

        if (length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The file length cannot be negative.");
        }

        ValidateShape(kind, itemKind, previousRelativePath, length, metadata);

        ItemId = itemId;
        PreviousRemoteRevision = previousRemoteRevision;
        PreviousRelativePath = previousRelativePath;
        Length = length;
        if (metadata is not null && metadata.Kind != itemKind)
        {
            throw new ArgumentException("Remote metadata must match the remote item kind.", nameof(metadata));
        }

        Metadata = metadata;
        _cursorAfter = cursorAfter.ToArray();
    }

    /// <summary>Gets the provider-stable idempotency identifier.</summary>
    public string ChangeId { get; }

    /// <summary>Gets the requested remote mutation.</summary>
    public CloudRemoteChangeKind Kind { get; }

    /// <summary>Gets the provider-stable remote object identifier.</summary>
    public string RemoteId { get; }

    /// <summary>Gets the opaque remote revision introduced by this change.</summary>
    public string RemoteRevision { get; }

    /// <summary>Gets the revision expected before this change, when supplied by the provider.</summary>
    public string? PreviousRemoteRevision { get; }

    /// <summary>Gets the known CfSharp item identifier, when the provider has one.</summary>
    public Guid? ItemId { get; }

    /// <summary>Gets whether the remote object is a file or directory.</summary>
    public CloudItemKind ItemKind { get; }

    /// <summary>Gets the canonical remote destination path relative to the sync root.</summary>
    public string RelativePath { get; }

    /// <summary>Gets the canonical source path for a move, otherwise null.</summary>
    public string? PreviousRelativePath { get; }

    /// <summary>Gets the logical file length when supplied for a file operation.</summary>
    public long? Length { get; }

    /// <summary>Gets immutable remote file-system metadata, when supplied.</summary>
    public CloudPlaceholderMetadata? Metadata { get; }

    /// <summary>
    /// Gets an opaque provider cursor after this entry. The value is copied and never interpreted.
    /// </summary>
    public ReadOnlyMemory<byte> CursorAfter => _cursorAfter;

    private static void ValidateShape(
        CloudRemoteChangeKind kind,
        CloudItemKind itemKind,
        string? previousRelativePath,
        long? length,
        CloudPlaceholderMetadata? metadata)
    {
        if (kind is CloudRemoteChangeKind.FileUpsert && itemKind is not CloudItemKind.File)
        {
            throw new ArgumentException("A file upsert must describe a file.", nameof(itemKind));
        }

        if (kind is CloudRemoteChangeKind.DirectoryUpsert && itemKind is not CloudItemKind.Directory)
        {
            throw new ArgumentException("A directory upsert must describe a directory.", nameof(itemKind));
        }

        if (kind is CloudRemoteChangeKind.Move && previousRelativePath is null)
        {
            throw new ArgumentException("A move requires a previous relative path.", nameof(previousRelativePath));
        }

        if (kind is not CloudRemoteChangeKind.Move && previousRelativePath is not null)
        {
            throw new ArgumentException(
                "Only a move may supply a previous relative path.",
                nameof(previousRelativePath));
        }

        if (itemKind is CloudItemKind.Directory && length is not null)
        {
            throw new ArgumentException("A directory cannot have a file length.", nameof(length));
        }

        if (kind is CloudRemoteChangeKind.FileUpsert or CloudRemoteChangeKind.DirectoryUpsert &&
            metadata is null)
        {
            throw new ArgumentException("An upsert requires remote metadata.", nameof(metadata));
        }

        if (kind is CloudRemoteChangeKind.FileUpsert && length is null)
        {
            throw new ArgumentException("A file upsert requires a logical length.", nameof(length));
        }

        if (kind is CloudRemoteChangeKind.MetadataUpdate && metadata is null)
        {
            throw new ArgumentException("A metadata update requires remote metadata.", nameof(metadata));
        }
    }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

    private static T RequireDefined<T>(T value, string parameterName)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, null);
        }

        return value;
    }
}

/// <summary>Represents an immutable ordered remote batch and its deterministic identity.</summary>
/// <remarks>
/// A batch may contain zero entries when a provider advances a cursor without changes. The
/// fingerprint covers every field, including opaque cursors and metadata, so a reused batch id
/// cannot accidentally resume a different remote stream.
/// </remarks>
public sealed class CloudRemoteChangeBatch
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly byte[] _initialCursor;
    private readonly byte[] _finalCursor;
    private readonly byte[] _fingerprint;

    /// <summary>Initializes an immutable remote batch and computes its SHA-256 fingerprint.</summary>
    /// <param name="batchId">Provider-stable idempotency identifier for this batch.</param>
    /// <param name="initialCursor">Opaque cursor before the first entry.</param>
    /// <param name="changes">Ordered remote entries, materialized exactly once.</param>
    /// <param name="finalCursor">Opaque cursor after the complete batch.</param>
    /// <exception cref="ArgumentException">The batch id is empty or change ids repeat.</exception>
    /// <exception cref="ArgumentNullException">The change sequence or an entry is null.</exception>
    public CloudRemoteChangeBatch(
        string batchId,
        ReadOnlyMemory<byte> initialCursor,
        IEnumerable<CloudRemoteChange> changes,
        ReadOnlyMemory<byte> finalCursor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        ArgumentNullException.ThrowIfNull(changes);
        BatchId = batchId;
        _initialCursor = initialCursor.ToArray();
        _finalCursor = finalCursor.ToArray();

        List<CloudRemoteChange> entries = [];
        HashSet<string> changeIds = new(StringComparer.Ordinal);
        foreach (CloudRemoteChange? change in changes)
        {
            ArgumentNullException.ThrowIfNull(change);
            if (!changeIds.Add(change.ChangeId))
            {
                throw new ArgumentException(
                    $"The remote change id '{change.ChangeId}' occurs more than once.",
                    nameof(changes));
            }

            entries.Add(change);
        }

        Changes = new ReadOnlyCollection<CloudRemoteChange>(entries);
        _fingerprint = ComputeFingerprint();
    }

    /// <summary>Gets the provider-stable batch identifier.</summary>
    public string BatchId { get; }

    /// <summary>Gets the opaque cursor before the first entry.</summary>
    public ReadOnlyMemory<byte> InitialCursor => _initialCursor;

    /// <summary>Gets the immutable entries in provider order.</summary>
    public IReadOnlyList<CloudRemoteChange> Changes { get; }

    /// <summary>Gets the opaque cursor after the complete batch.</summary>
    public ReadOnlyMemory<byte> FinalCursor => _finalCursor;

    /// <summary>Gets the owned SHA-256 fingerprint of the complete batch envelope.</summary>
    public ReadOnlyMemory<byte> Fingerprint => _fingerprint;

    private byte[] ComputeFingerprint()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, StrictUtf8, leaveOpen: true);
        writer.Write(1); // Fingerprint envelope version.
        WriteString(writer, BatchId);
        WriteBytes(writer, _initialCursor);
        writer.Write(Changes.Count);
        foreach (CloudRemoteChange change in Changes)
        {
            WriteString(writer, change.ChangeId);
            writer.Write((int)change.Kind);
            WriteString(writer, change.RemoteId);
            WriteString(writer, change.RemoteRevision);
            WriteNullableString(writer, change.PreviousRemoteRevision);
            WriteNullableGuid(writer, change.ItemId);
            writer.Write((int)change.ItemKind);
            WriteString(writer, change.RelativePath);
            WriteNullableString(writer, change.PreviousRelativePath);
            writer.Write(change.Length.HasValue);
            if (change.Length is long length)
            {
                writer.Write(length);
            }

            WriteMetadata(writer, change.Metadata);
            WriteBytes(writer, change.CursorAfter.Span);
        }

        WriteBytes(writer, _finalCursor);
        writer.Flush();
        return SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }

    private static void WriteMetadata(BinaryWriter writer, CloudPlaceholderMetadata? metadata)
    {
        writer.Write(metadata is not null);
        if (metadata is null)
        {
            return;
        }

        writer.Write((int)metadata.Kind);
        writer.Write((int)metadata.Attributes);
        WriteNullableDateTime(writer, metadata.CreationTime);
        WriteNullableDateTime(writer, metadata.LastAccessTime);
        WriteNullableDateTime(writer, metadata.LastWriteTime);
        WriteNullableDateTime(writer, metadata.ChangeTime);
    }

    private static void WriteNullableDateTime(BinaryWriter writer, DateTimeOffset? value)
    {
        writer.Write(value.HasValue);
        if (value is DateTimeOffset dateTime)
        {
            writer.Write(dateTime.UtcTicks);
        }
    }

    private static void WriteNullableGuid(BinaryWriter writer, Guid? value)
    {
        writer.Write(value.HasValue);
        if (value is Guid guid)
        {
            Span<byte> bytes = stackalloc byte[16];
            guid.TryWriteBytes(bytes, bigEndian: true, out _);
            writer.Write(bytes);
        }
    }

    private static void WriteNullableString(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            WriteString(writer, value);
        }
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = StrictUtf8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteBytes(BinaryWriter writer, ReadOnlySpan<byte> value)
    {
        writer.Write(value.Length);
        writer.Write(value);
    }
}

/// <summary>Configures one remote-batch application operation.</summary>
public sealed record CloudRemoteApplyOptions
{
    /// <summary>Gets the default conservative remote-apply options.</summary>
    public static CloudRemoteApplyOptions Default { get; } = new();

    /// <summary>Gets whether locally unsynchronized content is preserved by default.</summary>
    /// <remarks>
    /// Defaults to <see langword="true"/>. A Delete under this policy retains even a clean
    /// file or directory and records a durable Delete conflict: user-mode share exclusion
    /// cannot prevent concurrent attribute-only writes through native disposition.
    /// Explicit <see cref="CloudRemoteConflictDecision.KeepRemote"/> resolution, or setting
    /// this property to <see langword="false"/>, authorizes destructive deletion. Such
    /// authorization does not exclude concurrent external metadata writes; object identity,
    /// original evidence and recovery validation still apply. Other remote changes retain
    /// their existing preservation checks. A previously prepared protected Delete without
    /// native acceptance remains unresolved; changing this option cannot reauthorize it.
    /// </remarks>
    public bool PreserveUnsynchronizedLocalContent { get; init; } = true;

    /// <summary>Gets whether provider-generated local echoes must be suppressed.</summary>
    public bool SuppressLocalEcho { get; init; } = true;

    /// <summary>Gets how long an echo-suppression record remains active.</summary>
    public TimeSpan EchoSuppressionLifetime { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets the number of matching local watcher observations reserved for one remote mutation.
    /// </summary>
    public int EchoSuppressionObservationCount { get; init; } = 4;

    /// <summary>Gets the maximum entries one call may attempt before returning a partial result.</summary>
    public int MaximumEntries { get; init; } = 256;

    /// <summary>Gets the optional application conflict resolver; null means durable defer.</summary>
    public ICloudRemoteConflictResolver? ConflictResolver { get; init; }

    internal void Validate()
    {
        if (EchoSuppressionLifetime <= TimeSpan.Zero || EchoSuppressionLifetime > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(EchoSuppressionLifetime),
                EchoSuppressionLifetime,
                "The echo-suppression lifetime must be positive and no longer than one hour.");
        }

        if (MaximumEntries <= 0 || MaximumEntries > 65_536)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumEntries),
                MaximumEntries,
                "The maximum entry count must be positive and no larger than 65536.");
        }

        if (EchoSuppressionObservationCount <= 0 || EchoSuppressionObservationCount > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(EchoSuppressionObservationCount),
                EchoSuppressionObservationCount,
                "The echo-suppression observation count must be between 1 and 64.");
        }
    }
}

/// <summary>Describes one explicit decision returned by an application conflict resolver.</summary>
public sealed class CloudRemoteConflictResolution
{
    /// <summary>Initializes a conflict decision.</summary>
    /// <param name="decision">The explicit resolution action.</param>
    /// <param name="keepBothRelativePath">Required destination when the decision is KeepBoth.</param>
    /// <exception cref="ArgumentException">KeepBoth does not have a valid conflict path.</exception>
    public CloudRemoteConflictResolution(
        CloudRemoteConflictDecision decision,
        string? keepBothRelativePath = null)
    {
        if (!Enum.IsDefined(decision))
        {
            throw new ArgumentOutOfRangeException(nameof(decision), decision, null);
        }

        if (decision is CloudRemoteConflictDecision.KeepBoth)
        {
            KeepBothRelativePath = CloudRemotePathValidation.Canonicalize(
                keepBothRelativePath ?? throw new ArgumentException(
                    "KeepBoth requires a destination path.",
                    nameof(keepBothRelativePath)),
                nameof(keepBothRelativePath));
        }
        else if (keepBothRelativePath is not null)
        {
            throw new ArgumentException(
                "Only KeepBoth may supply a conflict destination path.",
                nameof(keepBothRelativePath));
        }

        Decision = decision;
    }

    /// <summary>Gets the selected resolution action.</summary>
    public CloudRemoteConflictDecision Decision { get; }

    /// <summary>Gets the validated destination path for KeepBoth, otherwise null.</summary>
    public string? KeepBothRelativePath { get; }
}

/// <summary>Describes one remote/local conflict before a resolution decision is made.</summary>
public sealed class CloudRemoteConflict
{
    /// <summary>Initializes a conflict envelope with immutable remote and local state.</summary>
    public CloudRemoteConflict(
        CloudRemoteChange change,
        CloudItemState? localState,
        CloudRemoteConflictReason reason,
        DateTimeOffset detectedAt)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason), reason, null);
        }

        Change = change;
        LocalState = localState;
        Reason = reason;
        DetectedAt = detectedAt.ToUniversalTime();
    }

    /// <summary>Gets the immutable remote change that caused the conflict.</summary>
    public CloudRemoteChange Change { get; }

    /// <summary>Gets the durable local state, when an item was resolved.</summary>
    public CloudItemState? LocalState { get; }

    /// <summary>Gets the explicit conflict classification.</summary>
    public CloudRemoteConflictReason Reason { get; }

    /// <summary>Gets the UTC time at which the conflict was detected.</summary>
    public DateTimeOffset DetectedAt { get; }
}

/// <summary>Resolves a remote/local conflict outside CfSharp transaction and native scopes.</summary>
public interface ICloudRemoteConflictResolver
{
    /// <summary>Chooses an explicit action for one conflict.</summary>
    /// <param name="conflict">Immutable remote and local state.</param>
    /// <param name="cancellationToken">Token that cancels the resolver callback.</param>
    /// <returns>A validated explicit decision. The default policy is durable defer.</returns>
    ValueTask<CloudRemoteConflictResolution> ResolveAsync(
        CloudRemoteConflict conflict,
        CancellationToken cancellationToken = default);
}

/// <summary>Reports one remote entry's durable apply outcome.</summary>
public sealed class CloudRemoteApplyEntryResult
{
    internal CloudRemoteApplyEntryResult(
        string changeId,
        CloudRemoteApplyEntryStatus status,
        CloudRemoteConflict? conflict = null,
        Exception? error = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(changeId);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, null);
        }

        ChangeId = changeId;
        Status = status;
        Conflict = conflict;
        Error = error;
    }

    /// <summary>Gets the remote change id associated with the result.</summary>
    public string ChangeId { get; }

    /// <summary>Gets the durable entry status.</summary>
    public CloudRemoteApplyEntryStatus Status { get; }

    /// <summary>Gets the conflict envelope when the entry produced a conflict result.</summary>
    public CloudRemoteConflict? Conflict { get; }

    /// <summary>Gets the non-sensitive failure captured for a failed entry, when available.</summary>
    public Exception? Error { get; }
}

/// <summary>Contains per-entry outcomes and the highest cursor safely committed by an apply call.</summary>
public sealed class CloudRemoteApplyResult
{
    private readonly byte[] _safeCursor;

    internal CloudRemoteApplyResult(
        IReadOnlyList<CloudRemoteApplyEntryResult> entries,
        CloudRemoteBatchStatus status,
        ReadOnlySpan<byte> safeCursor,
        bool requiresRetry,
        int appliedEntryCount,
        IEnumerable<Guid> conflictIds)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(conflictIds);
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, null);
        }

        ArgumentOutOfRangeException.ThrowIfNegative(appliedEntryCount);
        Entries = new ReadOnlyCollection<CloudRemoteApplyEntryResult>(entries.ToArray());
        Status = status;
        _safeCursor = safeCursor.ToArray();
        RequiresRetry = requiresRetry;
        AppliedEntryCount = appliedEntryCount;
        ConflictIds = new ReadOnlyCollection<Guid>(conflictIds.ToArray());
    }

    /// <summary>Gets ordered per-entry outcomes.</summary>
    public IReadOnlyList<CloudRemoteApplyEntryResult> Entries { get; }

    /// <summary>Gets the durable remote-batch status after this call.</summary>
    public CloudRemoteBatchStatus Status { get; }

    /// <summary>Gets the cursor after the last durably resolved entry.</summary>
    public ReadOnlyMemory<byte> SafeCursor => _safeCursor;

    /// <summary>Gets whether the caller should retry or resume the batch.</summary>
    public bool RequiresRetry { get; }

    /// <summary>
    /// Gets the committed entry prefix, including durable conflict outcomes. An applied batch
    /// may contain conflicts; inspect entry outcomes and conflict identifiers before treating
    /// every remote mutation as successful.
    /// </summary>
    public int AppliedEntryCount { get; }

    /// <summary>Gets durable conflict identifiers created or reused by this call.</summary>
    public IReadOnlyList<Guid> ConflictIds { get; }
}

internal static class CloudRemotePathValidation
{
    internal static string Canonicalize(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        string normalized = value.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (Path.IsPathFullyQualified(normalized) || normalized.StartsWith(Path.DirectorySeparatorChar))
        {
            throw new ArgumentException(
                "The remote path must be relative to the sync root.",
                parameterName);
        }

        string[] segments = normalized.Split(Path.DirectorySeparatorChar);
        if (segments.Length == 0 || segments.Any(static segment => segment.Length == 0))
        {
            throw new ArgumentException("The remote path contains an empty segment.", parameterName);
        }

        for (int index = 0; index < segments.Length; index++)
        {
            try
            {
                segments[index] = CloudPlaceholderSpec.ValidateName(segments[index]);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException(
                    "The remote path contains an invalid Windows file-name segment.",
                    parameterName,
                    exception);
            }
        }

        return string.Join(Path.DirectorySeparatorChar, segments);
    }
}
