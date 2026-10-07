namespace CfSharp;

public sealed partial class CloudFileSystem
{
    internal async ValueTask<CloudItemMoveResult> MoveAsync(
        CloudItem item,
        CloudDirectory destination,
        string name,
        CloudMoveOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(options);
        string validatedName = CloudPlaceholderSpec.ValidateName(name);
        if (!destination.IsOwnedBy(this))
        {
            throw new ArgumentException(
                "The destination directory must belong to the same cloud file system.",
                nameof(destination));
        }

        if (item.RelativePath.Length == 0)
        {
            throw new InvalidOperationException("The sync root cannot be moved or renamed.");
        }

        if (item.Kind is CloudItemKind.Directory && options.ReplaceExisting)
        {
            throw new ArgumentException(
                "Directory moves cannot replace an existing destination.",
                nameof(options));
        }

        string destinationRelativePath = destination.RelativePath.Length == 0
            ? validatedName
            : Path.Combine(destination.RelativePath, validatedName);
        CloudItem movedItem = CreateItemReference(destinationRelativePath, item.Kind);
        if (item.Kind is CloudItemKind.Directory &&
            IsStrictDescendant(movedItem.FullPath, item.FullPath))
        {
            throw new InvalidOperationException(
                "A directory cannot be moved into itself or one of its descendants.");
        }

        CloudItemOperationScope sourceScope = item.Kind is CloudItemKind.Directory
            ? CloudItemOperationScope.Subtree(item.FullPath)
            : CloudItemOperationScope.Exact(item.FullPath);
        CloudItemOperationScope destinationScope = item.Kind is CloudItemKind.Directory
            ? CloudItemOperationScope.Subtree(movedItem.FullPath)
            : CloudItemOperationScope.Exact(movedItem.FullPath);
        using CloudFileSystemOperationLease operation = await AcquireOperationAsync(
            [sourceScope, destinationScope],
            cancellationToken).ConfigureAwait(false);

        LocalCloudItemInspection sourceState = CloudItemInspector.Inspect(item.FullPath, item.Kind);
        LocalCloudItemInspection destinationDirectoryState = CloudItemInspector.Inspect(
            destination.FullPath,
            CloudItemKind.Directory);
        if (!destinationDirectoryState.Exists)
        {
            throw new DirectoryNotFoundException(
                $"The destination directory does not exist: '{destination.FullPath}'.");
        }

        MoveStatePlan statePlan = await ReadMoveStatePlanAsync(
            operation.StateStore,
            item,
            movedItem,
            cancellationToken).ConfigureAwait(false);
        if (statePlan.HasUnrelatedDestinationState)
        {
            throw new InvalidOperationException(
                "Durable state already identifies an unrelated item at the destination path.");
        }

        if (!sourceState.Exists)
        {
            LocalCloudItemInspection destinationState = CloudItemInspector.Inspect(
                movedItem.FullPath,
                item.Kind);
            if (statePlan.SourceEntries.Count == 0 ||
                !destinationState.Exists ||
                !destinationState.PlaceholderState.HasFlag(CloudPlaceholderState.Placeholder))
            {
                throw new FileNotFoundException("The cloud item does not exist.", item.FullPath);
            }

            // A previous native move may have succeeded immediately before its durable-state
            // transaction failed. The source durable entries and destination namespace together
            // identify that retry state; complete only the missing durable rename and never issue
            // a second native move.
            try
            {
                await PersistMovedStateAsync(
                    operation.StateStore,
                    statePlan.SourceEntries,
                    item.RelativePath,
                    movedItem.RelativePath,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                throw new CloudItemCoordinationException(
                    "CloudItem.Move",
                    item.FullPath,
                    movedItem.FullPath,
                    operationUsn: null,
                    exception);
            }

            CloudItemSnapshot retriedSnapshot = await InspectCoreAsync(
                movedItem,
                operation.StateStore,
                CancellationToken.None).ConfigureAwait(false);
            return new CloudItemMoveResult(
                item.FullPath,
                movedItem.FullPath,
                movedItem,
                retriedSnapshot,
                statePlan.SourceEntries.Count);
        }

        if (string.Equals(item.FullPath, movedItem.FullPath, StringComparison.Ordinal))
        {
            CloudItemSnapshot unchangedSnapshot = await InspectCoreAsync(
                movedItem,
                operation.StateStore,
                cancellationToken).ConfigureAwait(false);
            return new CloudItemMoveResult(
                item.FullPath,
                movedItem.FullPath,
                movedItem,
                unchangedSnapshot,
                durableStateEntriesUpdated: 0);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (item.Kind is CloudItemKind.File)
            {
                File.Move(item.FullPath, movedItem.FullPath, options.ReplaceExisting);
            }
            else
            {
                Directory.Move(item.FullPath, movedItem.FullPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw CloudFilesException.FromException("CloudItem.Move", item.FullPath, exception);
        }

        try
        {
            await PersistMovedStateAsync(
                operation.StateStore,
                statePlan.SourceEntries,
                item.RelativePath,
                movedItem.RelativePath,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new CloudItemCoordinationException(
                "CloudItem.Move",
                item.FullPath,
                movedItem.FullPath,
                operationUsn: null,
                exception);
        }

        CloudItemSnapshot snapshot = await InspectCoreAsync(
            movedItem,
            operation.StateStore,
            CancellationToken.None).ConfigureAwait(false);
        return new CloudItemMoveResult(
            item.FullPath,
            movedItem.FullPath,
            movedItem,
            snapshot,
            statePlan.SourceEntries.Count);
    }

    internal async ValueTask<CloudItemDeleteResult> DeleteAsync(
        CloudItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.RelativePath.Length == 0)
        {
            throw new InvalidOperationException("The sync root cannot be deleted.");
        }

        CloudItemOperationScope scope = item.Kind is CloudItemKind.Directory
            ? CloudItemOperationScope.Subtree(item.FullPath)
            : CloudItemOperationScope.Exact(item.FullPath);
        using CloudFileSystemOperationLease operation = await AcquireOperationAsync(
            [scope],
            cancellationToken).ConfigureAwait(false);
        LocalCloudItemInspection current = CloudItemInspector.Inspect(item.FullPath, item.Kind);
        if (!current.Exists)
        {
            throw new FileNotFoundException("The cloud item does not exist.", item.FullPath);
        }

        cancellationToken.ThrowIfCancellationRequested();
        DeleteLocalItem(item.FullPath, item.Kind, "CloudItem.Delete");
        bool durableStateUpdated;
        try
        {
            durableStateUpdated = await PersistTombstonesAsync(
                operation.StateStore,
                item.RelativePath,
                includeDescendants: item.Kind is CloudItemKind.Directory,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new CloudItemCoordinationException(
                "CloudItem.Delete",
                item.FullPath,
                operationUsn: null,
                exception);
        }

        CloudItemSnapshot snapshot = await InspectCoreAsync(
            item,
            operation.StateStore,
            CancellationToken.None).ConfigureAwait(false);
        return new CloudItemDeleteResult(
            item.FullPath,
            item.Kind,
            durableStateUpdated,
            snapshot);
    }

    private static async ValueTask<MoveStatePlan> ReadMoveStatePlanAsync(
        ICloudStateStore stateStore,
        CloudItem source,
        CloudItem destination,
        CancellationToken cancellationToken)
    {
        await using ICloudStateTransaction transaction = await stateStore
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<CloudItemState> sourceEntries = source.Kind is CloudItemKind.Directory
            ? await transaction.Items.ListSubtreeAsync(source.RelativePath, cancellationToken)
                .ConfigureAwait(false)
            : await ReadExactStateAsync(
                transaction.Items,
                source.RelativePath,
                cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CloudItemState> destinationEntries = destination.Kind is CloudItemKind.Directory
            ? await transaction.Items.ListSubtreeAsync(destination.RelativePath, cancellationToken)
                .ConfigureAwait(false)
            : await ReadExactStateAsync(
                transaction.Items,
                destination.RelativePath,
                cancellationToken).ConfigureAwait(false);
        HashSet<Guid> sourceIds = sourceEntries.Select(static entry => entry.ItemId).ToHashSet();
        bool hasUnrelatedDestinationState = destinationEntries.Any(entry =>
            !sourceIds.Contains(entry.ItemId));
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return new MoveStatePlan(
            Array.AsReadOnly(sourceEntries.ToArray()),
            hasUnrelatedDestinationState);
    }

    private static async ValueTask<IReadOnlyList<CloudItemState>> ReadExactStateAsync(
        ICloudItemStateRepository items,
        string relativePath,
        CancellationToken cancellationToken)
    {
        CloudItemState? item = await items
            .GetByRelativePathAsync(relativePath, cancellationToken)
            .ConfigureAwait(false);
        return item is null ? [] : [item];
    }

    private static async ValueTask PersistMovedStateAsync(
        ICloudStateStore stateStore,
        IReadOnlyList<CloudItemState> sourceEntries,
        string sourceRelativePath,
        string destinationRelativePath,
        CancellationToken cancellationToken)
    {
        if (sourceEntries.Count == 0)
        {
            return;
        }

        await using ICloudStateTransaction transaction = await stateStore
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await CloudDirectoryStateProjection.ProjectAsync(transaction, sourceEntries,
            sourceRelativePath, destinationRelativePath, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> PersistTombstoneAsync(
        ICloudStateStore stateStore,
        string relativePath,
        CancellationToken cancellationToken)
        => await PersistTombstonesAsync(
            stateStore,
            relativePath,
            includeDescendants: false,
            cancellationToken).ConfigureAwait(false);

    private static async ValueTask<bool> PersistTombstonesAsync(
        ICloudStateStore stateStore,
        string relativePath,
        bool includeDescendants,
        CancellationToken cancellationToken)
    {
        await using ICloudStateTransaction transaction = await stateStore
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<CloudItemState> existing = includeDescendants
            ? await transaction.Items.ListSubtreeAsync(relativePath, cancellationToken)
                .ConfigureAwait(false)
            : await ReadExactStateAsync(transaction.Items, relativePath, cancellationToken)
                .ConfigureAwait(false);
        if (existing.Count == 0)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        DateTimeOffset updatedAt = DateTimeOffset.UtcNow;
        foreach (CloudItemState item in existing)
        {
            await transaction.Items.UpsertAsync(
                new CloudItemState(
                    item.ItemId,
                    item.RemoteId,
                    item.RelativePath,
                    item.Kind,
                    item.RemoteRevision,
                    item.LocalFileId,
                    isTombstone: true,
                    updatedAt),
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static void DeleteLocalItem(string path, CloudItemKind kind, string operation)
    {
        try
        {
            if (kind is CloudItemKind.File)
            {
                File.Delete(path);
            }
            else
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw CloudFilesException.FromException(operation, path, exception);
        }
    }

    private static bool IsStrictDescendant(string candidate, string ancestor)
    {
        if (string.Equals(candidate, ancestor, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string prefix = Path.TrimEndingDirectorySeparator(ancestor) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record MoveStatePlan(
        IReadOnlyList<CloudItemState> SourceEntries,
        bool HasUnrelatedDestinationState);
}
