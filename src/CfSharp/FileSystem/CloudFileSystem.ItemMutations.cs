namespace CfSharp;

public sealed partial class CloudFileSystem
{
    internal async ValueTask<CloudPlaceholderMutationResult> ConvertToPlaceholderAsync(
        CloudItem item,
        CloudPlaceholderIdentity identity,
        CloudPlaceholderConversionOptions options,
        CancellationToken cancellationToken)
    {
        if (s_protectedLocalContext.Value is { } protectedContext)
        {
            return await protectedContext.ConvertItemAsync(item, identity, options, cancellationToken).ConfigureAwait(false);
        }
        ValidateConversionOptions(item, options);
        using CloudFileSystemOperationLease operation = await AcquireOperationAsync(
            [CloudItemOperationScope.Exact(item.FullPath)],
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        LocalCloudItemInspection before = CloudItemInspector.Inspect(item.FullPath, item.Kind);
        if (!before.Exists)
        {
            throw new FileNotFoundException("The cloud item does not exist.", item.FullPath);
        }

        long? operationUsn = null;
        bool identityMatches = before.PlaceholderIdentity.AsSpan().SequenceEqual(identity.Encode());
        if (!identityMatches)
        {
            operationUsn = CloudPlaceholderMutationPlatform.Convert(
                item.FullPath,
                identity,
                options);
        }

        try
        {
            await PersistIdentityAsync(
                operation.StateStore,
                item,
                identity,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new CloudItemCoordinationException(
                "CloudItem.ConvertToPlaceholder",
                item.FullPath,
                operationUsn,
                exception);
        }

        CloudItemSnapshot snapshot = await InspectCoreAsync(
            item,
            operation.StateStore,
            CancellationToken.None).ConfigureAwait(false);
        return new CloudPlaceholderMutationResult(
            item.FullPath,
            operationUsn,
            snapshot,
            durableStateUpdated: true);
    }

    internal async ValueTask<CloudPlaceholderMutationResult> UpdatePlaceholderAsync(
        CloudItem item,
        CloudPlaceholderPatch patch,
        CancellationToken cancellationToken,
        Func<CancellationToken, ValueTask<IDisposable>>? acquireUpdateGuard = null,
        Action<nint>? verifyRemoteTarget = null,
        Func<ValueTask>? recordNativeWrite = null,
        Func<ICloudStateTransaction, DateTimeOffset, CancellationToken, ValueTask>? stageIdentityProjection = null)
    {
        ValidatePatch(item, patch);
        using CloudFileSystemOperationLease operation = await AcquireOperationAsync(
            [CloudItemOperationScope.Exact(item.FullPath)],
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        LocalCloudItemInspection before = CloudItemInspector.Inspect(item.FullPath, item.Kind);
        if (!before.Exists)
        {
            throw new FileNotFoundException("The cloud item does not exist.", item.FullPath);
        }

        long operationUsn;
        using (IDisposable? guard = acquireUpdateGuard is null ? null : await acquireUpdateGuard(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            operationUsn = CloudPlaceholderMutationPlatform.Update(item.FullPath, patch, verifyRemoteTarget);
        }
        bool durableStateUpdated = patch.IdentityChange is not
            CloudPlaceholderIdentityChange.Unchanged;
        if (durableStateUpdated)
        {
            try
            {
                // Native completion and official identity projection have independent
                // durability boundaries. Keep completion visible if projection rolls back.
                if (recordNativeWrite is not null) { await recordNativeWrite().ConfigureAwait(false); }
                await PersistIdentityPatchAsync(
                    operation.StateStore,
                    item,
                    patch,
                    requireCurrentOwner: verifyRemoteTarget is not null,
                    stageIdentityProjection,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                throw new CloudItemCoordinationException(
                    "CloudItem.UpdatePlaceholder",
                    item.FullPath,
                    operationUsn,
                    exception);
            }
        }

        CloudItemSnapshot snapshot = await InspectCoreAsync(
            item,
            operation.StateStore,
            CancellationToken.None).ConfigureAwait(false);
        return new CloudPlaceholderMutationResult(
            item.FullPath,
            operationUsn,
            snapshot,
            durableStateUpdated);
    }

    internal async ValueTask<CloudPlaceholderMutationResult> RevertToRegularItemAsync(
        CloudItem item,
        CancellationToken cancellationToken)
    {
        using CloudFileSystemOperationLease operation = await AcquireOperationAsync(
            [CloudItemOperationScope.Exact(item.FullPath)],
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        CloudPlaceholderMutationPlatform.Revert(item.FullPath);

        bool durableStateUpdated;
        try
        {
            durableStateUpdated = await RemoveIdentityAsync(
                operation.StateStore,
                item.RelativePath,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new CloudItemCoordinationException(
                "CloudItem.RevertToRegularItem",
                item.FullPath,
                operationUsn: null,
                exception);
        }

        CloudItemSnapshot snapshot = await InspectCoreAsync(
            item,
            operation.StateStore,
            CancellationToken.None).ConfigureAwait(false);
        return new CloudPlaceholderMutationResult(
            item.FullPath,
            operationUsn: null,
            snapshot,
            durableStateUpdated);
    }

    private static void ValidateConversionOptions(
        CloudItem item,
        CloudPlaceholderConversionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (item.Kind is CloudItemKind.File && options.PopulationState is not null)
        {
            throw new ArgumentException(
                "Directory population state cannot be applied to a file.",
                nameof(options));
        }

        if (item.Kind is CloudItemKind.Directory &&
            (options.Dehydrate || options.ContentMode is CloudFileContentMode.AlwaysFull))
        {
            throw new ArgumentException(
                "File content options cannot be applied to a directory.",
                nameof(options));
        }
    }

    private static void ValidatePatch(CloudItem item, CloudPlaceholderPatch patch)
    {
        if (patch.Metadata is not null && patch.Metadata.Kind != item.Kind)
        {
            throw new ArgumentException(
                "Patch metadata must match the target item kind.",
                nameof(patch));
        }

        if (patch.FileSize is not null && item.Kind is not CloudItemKind.File)
        {
            throw new ArgumentException(
                "A replacement file size can only be applied to a file.",
                nameof(patch));
        }

        if (item.Kind is CloudItemKind.File && patch.PopulationState is not null)
        {
            throw new ArgumentException(
                "Directory population state cannot be applied to a file.",
                nameof(patch));
        }

        if (item.Kind is CloudItemKind.Directory &&
            (patch.ContentMode is not null ||
             patch.DehydrateWholeFile ||
             patch.DehydrateRanges.Count != 0))
        {
            throw new ArgumentException(
                "File content changes cannot be applied to a directory.",
                nameof(patch));
        }

        foreach (CloudFileRange range in patch.DehydrateRanges)
        {
            if (range.Offset % Environment.SystemPageSize != 0 ||
                (!range.ExtendsToEnd && range.Length % Environment.SystemPageSize != 0))
            {
                throw new ArgumentException(
                    $"Atomic dehydration ranges must be aligned to the " +
                    $"{Environment.SystemPageSize}-byte system page size.",
                    nameof(patch));
            }
        }
    }

    private static async ValueTask PersistIdentityAsync(
        ICloudStateStore stateStore,
        CloudItem item,
        CloudPlaceholderIdentity identity,
        CancellationToken cancellationToken,
        Action? validateBeforeCommit = null,
        Action? onCommitted = null,
        bool preserveExistingState = false,
        long? observedLocalFileId = null,
        bool requireCurrentOwner = false,
        Func<ICloudStateTransaction, DateTimeOffset, CancellationToken, ValueTask>? stageIdentityProjection = null)
    {
        await using ICloudStateTransaction transaction = await stateStore
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        CloudItemState? existing = await transaction.Items
            .GetByRelativePathAsync(item.RelativePath, cancellationToken)
            .ConfigureAwait(false);
        if (requireCurrentOwner && (existing is null || existing.IsTombstone || existing.ItemId != identity.ItemId))
        {
            throw new InvalidOperationException("The remote placeholder no longer owns its durable path.");
        }
        if (preserveExistingState)
        {
            await ValidateProtectedLocalIdentityAsync(transaction, item, identity, cancellationToken).ConfigureAwait(false);
        }
        if (existing is not null && existing.ItemId != identity.ItemId)
        {
            await transaction.Items.RemoveAsync(existing.ItemId, cancellationToken)
                .ConfigureAwait(false);
        }

        DateTimeOffset projectedAt = DateTimeOffset.UtcNow;
        await WriteIdentityProjectionAsync(transaction, item, identity,
            preserveExistingState ? existing?.RemoteRevision : identity.RemoteRevision,
            preserveExistingState ? existing?.LocalFileId ?? observedLocalFileId : null,
            projectedAt, cancellationToken).ConfigureAwait(false);
        if (stageIdentityProjection is not null)
        { await stageIdentityProjection(transaction, projectedAt, cancellationToken).ConfigureAwait(false); }
        validateBeforeCommit?.Invoke();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        // Record success before transaction disposal, which can fail after a real commit.
        onCommitted?.Invoke();
    }

    private static async ValueTask WriteIdentityProjectionAsync(ICloudStateTransaction transaction, CloudItem item,
        CloudPlaceholderIdentity identity, string? remoteRevision, long? localFileId, DateTimeOffset projectedAt, CancellationToken cancellationToken)
    {
        await transaction.Items.UpsertAsync(
            new CloudItemState(
                identity.ItemId,
                identity.RemoteId,
                item.RelativePath,
                item.Kind,
                remoteRevision,
                localFileId,
                isTombstone: false,
                projectedAt),
            cancellationToken).ConfigureAwait(false);
        await CloudDirectoryProvenance.RetainProjectionAsync(transaction, item.SyncRootPath,
            item.RelativePath, item.Kind, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask PersistIdentityPatchAsync(
        ICloudStateStore stateStore,
        CloudItem item,
        CloudPlaceholderPatch patch,
        bool requireCurrentOwner,
        Func<ICloudStateTransaction, DateTimeOffset, CancellationToken, ValueTask>? stageIdentityProjection,
        CancellationToken cancellationToken)
    {
        if (patch.IdentityChange is CloudPlaceholderIdentityChange.Replace)
        {
            await PersistIdentityAsync(
                stateStore,
                item,
                patch.ReplacementIdentity!,
                cancellationToken,
                requireCurrentOwner: requireCurrentOwner,
                stageIdentityProjection: stageIdentityProjection).ConfigureAwait(false);
            return;
        }

        _ = await RemoveIdentityAsync(
            stateStore,
            item.RelativePath,
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> RemoveIdentityAsync(
        ICloudStateStore stateStore,
        string relativePath,
        CancellationToken cancellationToken)
    {
        await using ICloudStateTransaction transaction = await stateStore
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        CloudItemState? existing = await transaction.Items
            .GetByRelativePathAsync(relativePath, cancellationToken)
            .ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        await transaction.Items.RemoveAsync(existing.ItemId, cancellationToken)
            .ConfigureAwait(false);
        if (existing.Kind == CloudItemKind.Directory)
        {
            await transaction.Checkpoints.RemoveAsync(CloudDirectoryProvenance.BindingName(existing.ItemId), cancellationToken).ConfigureAwait(false);
            await transaction.Checkpoints.RemoveAsync(CloudDirectoryProvenance.MembersName(existing.ItemId), cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
