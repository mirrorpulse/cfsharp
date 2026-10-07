using CfSharp.Native;

using Microsoft.Win32.SafeHandles;

namespace CfSharp;

public sealed partial class CloudFileSystem
{
    internal async ValueTask<CloudDirectoryMoveProof> PrepareDirectoryMoveAsync(CloudDirectory source,
        CloudDirectory destination, string name, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        string validatedName = CloudPlaceholderSpec.ValidateName(name);
        if (!destination.IsOwnedBy(this))
        {
            throw new ArgumentException("The destination must belong to this file system.", nameof(destination));
        }

        CloudDirectory target = destination.GetDirectory(validatedName);
        if (source.RelativePath.Length == 0 ||
            string.Equals(source.FullPath, target.FullPath, StringComparison.Ordinal) ||
            IsStrictDescendant(target.FullPath, source.FullPath))
        {
            throw new InvalidOperationException("The sync root, same path, or descendant cannot be selected for a move.");
        }

        using CloudFileSystemOperationLease operation = await AcquireOperationAsync(
            [CloudItemOperationScope.Subtree(source.FullPath), CloudItemOperationScope.Subtree(target.FullPath)],
            cancellationToken).ConfigureAwait(false);
        if (!CloudItemInspector.Inspect(destination.FullPath, CloudItemKind.Directory).Exists)
        {
            throw new DirectoryNotFoundException("The destination parent does not exist.");
        }

        // A metadata-only no-delete guard stabilizes this object during preparation. It does not
        // freeze descendant writes or placeholder identity changes and is never CFAPI protection.
        using SafeFileHandle guard = WindowsFileMetadata.Open(source.FullPath, preventDelete: true);
        DirectoryNativeObservation captured = CloudDirectoryMoveEvidence.Capture(guard.DangerousGetHandle(), SyncRootPath);
        if (!string.Equals(captured.ActualPath, source.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The captured directory is outside the requested namespace path.");
        }

        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Decode(captured.Identity);
        await using ICloudStateTransaction transaction = await operation.StateStore.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        CloudItemState? root = await transaction.Items.GetByItemIdAsync(identity.ItemId, cancellationToken).ConfigureAwait(false);
        if (root is null || root.IsTombstone || root.Kind != CloudItemKind.Directory ||
            !string.Equals(root.RelativePath.Replace('/', '\\'), source.RelativePath.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase) ||
            root.RemoteId != identity.RemoteId)
        {
            throw new InvalidOperationException("The native directory identity does not match its live durable source.");
        }

        Guid scope = await CloudDirectoryMoveEvidence.GetScopeAsync(transaction, create: true, cancellationToken).ConfigureAwait(false);
        string actualSourceRelativePath = Path.GetRelativePath(SyncRootPath, captured.ActualPath);
        CloudDirectoryMoveProof proof = new(Guid.NewGuid(), scope, actualSourceRelativePath, target.RelativePath,
            root.ItemId, captured.Binding, captured.Identity);
        IReadOnlyList<CloudItemState> members = await transaction.Items.ListSubtreeAsync(source.RelativePath, cancellationToken).ConfigureAwait(false);
        await CloudDirectoryMoveEvidence.PrepareAsync(transaction, proof, members, cancellationToken).ConfigureAwait(false);
        if (!captured.Matches(CloudDirectoryMoveEvidence.Capture(guard.DangerousGetHandle(), SyncRootPath)))
        {
            throw new InvalidOperationException("The native directory changed during preparation.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (!captured.Matches(CloudDirectoryMoveEvidence.Capture(guard.DangerousGetHandle(), SyncRootPath)))
        {
            throw new InvalidOperationException("The native directory changed after preparation committed; capture a new proof.");
        }

        return proof;
    }
}
