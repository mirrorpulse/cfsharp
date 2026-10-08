namespace CfSharp;

/// <summary>Identifies a local file or directory object within one actual sync root and volume.</summary>
/// <remarks>
/// Immutable, serializable comparison data with no handle ownership or protection lifetime.
/// File IDs preserve all 128 native bits; their Guid representation is opaque, not a UUID.
/// Capture this value before uploading or moving a directory and retain the corresponding proof. A later
/// observation of a replacement cannot substitute for that proof. IDs can be reused after
/// deletion; this value does not establish remote identity, content equality, or a lock.
/// Supported on Windows 10 version 1709 and later with file-ID-capable storage.
/// </remarks>
public sealed record CloudLocalFileBinding
{
    /// <summary>Initializes an immutable native object binding.</summary>
    /// <param name="volumeSerialNumber">Native volume serial number.</param>
    /// <param name="syncRootFileId">Complete native ID of the owning root directory.</param>
    /// <param name="localFileId">Complete native ID of the file or directory object.</param>
    /// <exception cref="ArgumentException">An identifier is empty.</exception>
    public CloudLocalFileBinding(ulong volumeSerialNumber, Guid syncRootFileId, Guid localFileId)
    {
        if (volumeSerialNumber == 0 || syncRootFileId == Guid.Empty || localFileId == Guid.Empty)
        {
            throw new ArgumentException("A local binding requires nonempty volume, root, and file identifiers.");
        }

        VolumeSerialNumber = volumeSerialNumber;
        SyncRootFileId = syncRootFileId;
        LocalFileId = localFileId;
    }

    /// <summary>Gets the native volume serial number, scoped to this computer.</summary>
    public ulong VolumeSerialNumber { get; }

    /// <summary>Gets the opaque 128-bit native ID of the actual root directory.</summary>
    public Guid SyncRootFileId { get; }

    /// <summary>Gets the opaque 128-bit native ID of the file or directory object.</summary>
    public Guid LocalFileId { get; }
}
