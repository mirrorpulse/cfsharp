namespace CfSharp;

/// <summary>Captures immutable local and durable state observed for one cloud item.</summary>
/// <remarks>
/// <para>
/// Each <see cref="CloudItem.InspectAsync"/> call creates a new snapshot. A snapshot owns its
/// placeholder-identity bytes and contains no native handles, mutable file-system objects, or
/// live database resources. It is safe for concurrent reads.
/// </para>
/// <para>
/// Missing local items produce a snapshot with <see cref="Exists"/> set to <see langword="false"/>
/// rather than throwing. Durable identity and tombstone properties may still be present. Nullable
/// metadata means that the corresponding fact was not available at inspection time.
/// </para>
/// </remarks>
public sealed class CloudItemSnapshot
{
    private readonly byte[] _placeholderIdentity;

    internal CloudItemSnapshot(
        CloudItemKind kind,
        bool exists,
        FileAttributes? attributes,
        long? length,
        DateTimeOffset? creationTime,
        DateTimeOffset? lastWriteTime,
        DateTimeOffset? lastAccessTime,
        CloudPlaceholderState placeholderState,
        CloudContentAvailability contentAvailability,
        CloudPinState pinState,
        CloudSynchronizationState synchronizationState,
        long? localFileId,
        long? syncRootFileId,
        long? onDiskDataSize,
        long? validatedDataSize,
        long? modifiedDataSize,
        long? propertyDataSize,
        ReadOnlySpan<byte> placeholderIdentity,
        CloudItemState? durableState,
        DateTimeOffset inspectedAt,
        CloudLocalFileBinding? localBinding = null)
    {
        Kind = kind;
        Exists = exists;
        Attributes = attributes;
        Length = length;
        CreationTime = creationTime;
        LastWriteTime = lastWriteTime;
        LastAccessTime = lastAccessTime;
        PlaceholderState = placeholderState;
        ContentAvailability = contentAvailability;
        PinState = pinState;
        SynchronizationState = synchronizationState;
        LocalFileId = localFileId ?? durableState?.LocalFileId;
        SyncRootFileId = syncRootFileId;
        LocalBinding = localBinding;
        OnDiskDataSize = onDiskDataSize;
        ValidatedDataSize = validatedDataSize;
        ModifiedDataSize = modifiedDataSize;
        PropertyDataSize = propertyDataSize;
        _placeholderIdentity = placeholderIdentity.ToArray();
        ItemId = durableState?.ItemId;
        RemoteId = durableState?.RemoteId;
        RemoteRevision = durableState?.RemoteRevision;
        IsTombstone = durableState?.IsTombstone ?? false;
        DurableStateUpdatedAt = durableState?.UpdatedAt;
        InspectedAt = inspectedAt.ToUniversalTime();
    }

    /// <summary>Gets whether this reference represents a file or directory.</summary>
    public CloudItemKind Kind { get; }

    /// <summary>Gets whether the item existed locally when inspected.</summary>
    public bool Exists { get; }

    /// <summary>Gets the observed Win32 attributes, or <see langword="null"/> when absent.</summary>
    public FileAttributes? Attributes { get; }

    /// <summary>Gets the logical file length, or <see langword="null"/> for directories or absence.</summary>
    public long? Length { get; }

    /// <summary>Gets the observed UTC creation time, when available.</summary>
    public DateTimeOffset? CreationTime { get; }

    /// <summary>Gets the observed UTC last-write time, when available.</summary>
    public DateTimeOffset? LastWriteTime { get; }

    /// <summary>Gets the observed UTC last-access time, when available.</summary>
    public DateTimeOffset? LastAccessTime { get; }

    /// <summary>Gets all independent Cloud Files state bits reported by Windows.</summary>
    public CloudPlaceholderState PlaceholderState { get; }

    /// <summary>Gets the observed local content availability derived from native state.</summary>
    public CloudContentAvailability ContentAvailability { get; }

    /// <summary>Gets the user-requested pin intent reported by Windows.</summary>
    public CloudPinState PinState { get; }

    /// <summary>Gets whether placeholder content and metadata agree with provider state.</summary>
    public CloudSynchronizationState SynchronizationState { get; }

    /// <summary>Gets the volume-local file identifier, when available.</summary>
    public long? LocalFileId { get; }

    /// <summary>Gets the volume-local identifier of the containing sync root, when available.</summary>
    public long? SyncRootFileId { get; }

    /// <summary>Gets the complete native volume, root, and file binding when available.</summary>
    /// <remarks>
    /// Available for ordinary files as well as Cloud Files placeholders. No content is read or
    /// hydrated. This observation holds no lock and is not a USN or content proof. Null denotes
    /// absence, directories, or unsupported reparse targets. Retain the upload-time value rather
    /// than replacing it with a later observation when confirming accepted content.
    /// </remarks>
    public CloudLocalFileBinding? LocalBinding { get; }

    /// <summary>Gets the number of placeholder content bytes physically present, when applicable.</summary>
    public long? OnDiskDataSize { get; }

    /// <summary>Gets the number of on-disk bytes validated against provider state, when applicable.</summary>
    public long? ValidatedDataSize { get; }

    /// <summary>Gets the number of locally modified on-disk bytes, when applicable.</summary>
    public long? ModifiedDataSize { get; }

    /// <summary>Gets the bytes used by placeholder properties, when applicable.</summary>
    public long? PropertyDataSize { get; }

    /// <summary>Gets an owned copy of the opaque placeholder identity reported by Windows.</summary>
    public ReadOnlyMemory<byte> PlaceholderIdentity => _placeholderIdentity;

    /// <summary>Gets the stable CfSharp item identifier from durable state, when known.</summary>
    public Guid? ItemId { get; }

    /// <summary>Gets the provider-defined stable remote identifier, when known.</summary>
    public string? RemoteId { get; }

    /// <summary>Gets the last mutually acknowledged remote revision, when known.</summary>
    public string? RemoteRevision { get; }

    /// <summary>Gets whether durable state preserves a deletion tombstone for this path.</summary>
    public bool IsTombstone { get; }

    /// <summary>Gets when durable item state was last updated, when such state exists.</summary>
    public DateTimeOffset? DurableStateUpdatedAt { get; }

    /// <summary>Gets the UTC instant at which this fresh inspection completed.</summary>
    public DateTimeOffset InspectedAt { get; }

    /// <summary>Gets whether Windows identified the item as a Cloud Files placeholder.</summary>
    public bool IsPlaceholder => PlaceholderState.HasFlag(CloudPlaceholderState.Placeholder);
}
