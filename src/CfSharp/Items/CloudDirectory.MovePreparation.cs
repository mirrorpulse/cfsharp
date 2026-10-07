namespace CfSharp;

public sealed partial class CloudDirectory
{
    /// <summary>Captures and durably prepares evidence before this placeholder directory is moved.</summary>
    /// <param name="destination">An existing directory in this file system.</param>
    /// <param name="name">The intended destination child name.</param>
    /// <param name="cancellationToken">Cancels lease acquisition, state access, or preparation before commit.</param>
    /// <returns>
    /// An owned serializable proof after the library's immutable preparation and known subtree
    /// membership commit. This method performs no rename. Retain the encoded proof across restart.
    /// </returns>
    /// <remarks>
    /// Requires Windows 10 version 1709, a managed live directory placeholder, complete native IDs,
    /// and the owning durable store. Metadata handles guard capture but are closed before returning;
    /// the returned value confers no continuing protection. Concurrent operations are coordinated
    /// within this file system. Cross-process identity and descendant changes still require recovery
    /// validation. Ordinary directories retain their existing normal move API.
    /// </remarks>
    /// <exception cref="ArgumentException">The destination belongs to another file system or the name is invalid.</exception>
    /// <exception cref="InvalidOperationException">The root, destination, or durable/native identity is unsuitable.</exception>
    /// <exception cref="NotSupportedException">
    /// Storage cannot supply the complete native binding, an encoded evidence path exceeds 32 KiB,
    /// known subtree metadata exceeds 64 MiB,
    /// or durable recovery records use an unsupported protocol version.
    /// </exception>
    /// <exception cref="IOException">Native metadata, path protection, or durable preparation failed.</exception>
    /// <exception cref="OperationCanceledException">Preparation was canceled; no proof is returned.</exception>
    public ValueTask<CloudDirectoryMoveProof> PrepareMoveAsync(CloudDirectory destination, string name,
        CancellationToken cancellationToken = default) =>
        Owner.PrepareDirectoryMoveAsync(this, destination, name, cancellationToken);
}
