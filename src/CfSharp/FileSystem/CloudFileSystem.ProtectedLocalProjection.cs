namespace CfSharp;

public sealed partial class CloudFileSystem
{
    internal static async ValueTask CheckProtectedLocalIdentityAsync(ICloudStateStore store, CloudItem item,
        CloudPlaceholderIdentity identity, CancellationToken token)
    {
        await using ICloudStateTransaction transaction = await store.BeginTransactionAsync(token).ConfigureAwait(false);
        await ValidateProtectedLocalIdentityAsync(transaction, item, identity, token).ConfigureAwait(false);
    }

    internal static ValueTask PersistProtectedLocalIdentityAsync(ICloudStateStore store, CloudItem item,
        CloudPlaceholderIdentity identity, long? localFileId, Action validate, Action committed) =>
        PersistIdentityAsync(store, item, identity, CancellationToken.None, validate, committed,
            preserveExistingState: true, observedLocalFileId: localFileId);

    private static async ValueTask ValidateProtectedLocalIdentityAsync(ICloudStateTransaction transaction, CloudItem item,
        CloudPlaceholderIdentity identity, CancellationToken token)
    {
        CloudItemState? byPath = await transaction.Items.GetByRelativePathAsync(item.RelativePath, token).ConfigureAwait(false);
        CloudItemState? byId = await transaction.Items.GetByItemIdAsync(identity.ItemId, token).ConfigureAwait(false);
        CloudItemState? byRemote = await transaction.Items.GetByRemoteIdAsync(identity.RemoteId, token).ConfigureAwait(false);
        if (byPath is not null && (byPath.ItemId != identity.ItemId || byPath.RemoteId != identity.RemoteId ||
                byPath.Kind != item.Kind || byPath.IsTombstone) ||
            byId is not null && !string.Equals(byId.RelativePath, item.RelativePath, StringComparison.OrdinalIgnoreCase) ||
            byRemote is not null && byRemote.ItemId != identity.ItemId)
        {
            // A local initializer cannot rekey pending journal/conflict references or adopt
            // another official item. Preserve the original row and require explicit recovery.
            throw new CloudProtectedLocalRejectedException(CloudProtectedLocalOperationOutcome.NotApplicable,
                "Local preparation must preserve any known official item identity, path and pending state.");
        }
    }
}
