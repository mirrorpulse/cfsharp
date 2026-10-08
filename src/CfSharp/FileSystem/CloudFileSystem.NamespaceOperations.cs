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
        CloudDirectoryMoveProof? directoryProof = options.DirectoryMoveProof;
        if (directoryProof is not null && (item.Kind != CloudItemKind.Directory ||
            !CloudDirectoryStateProjection.SamePath(item.RelativePath, directoryProof.SourceRelativePath) ||
            !CloudDirectoryStateProjection.SamePath(movedItem.RelativePath, directoryProof.DestinationRelativePath) ||
            validatedName != Path.GetFileName(directoryProof.DestinationRelativePath)))
        {
            throw new ArgumentException("The directory proof must identify this source and exact intended destination.", nameof(options));
        }
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
        if (operation.Covers([sourceScope, destinationScope]))
        {
            operation.EstablishContext();
        }

        LocalCloudItemInspection sourceState = CloudItemInspector.Inspect(item.FullPath, item.Kind);
        LocalCloudItemInspection destinationDirectoryState = CloudItemInspector.Inspect(
            destination.FullPath,
            CloudItemKind.Directory);
        if (!destinationDirectoryState.Exists)
        {
            throw new DirectoryNotFoundException(
                $"The destination directory does not exist: '{destination.FullPath}'.");
        }

        if (item is CloudDirectory)
        {
            // Resolve under the admitted lease, without entering a public reference API that
            // would reject Stopping. Its path scopes already cover the case-insensitive alias.
            movedItem = ResolveDirectoryMoveTarget(destination, validatedName);
            if (directoryProof is not null && movedItem.RelativePath != directoryProof.DestinationRelativePath)
            {
                throw new ArgumentException("The directory proof must identify this source and exact intended destination.", nameof(options));
            }
        }

        if (item is CloudDirectory directory && (directoryProof is not null || !sourceState.Exists))
        {
            directoryProof ??= await CloudDirectoryMoveEvidence.ReadIntentAsync(operation.StateStore,
                item.RelativePath, movedItem.RelativePath, cancellationToken).ConfigureAwait(false);
            if (directoryProof is null)
            {
                throw new FileNotFoundException("Directory recovery requires original durable pre-move evidence.", item.FullPath);
            }

            CloudDirectoryMoveReconciliationResult recovery = await ReconcileDirectoryMoveCoreAsync(directory,
                (CloudDirectory)movedItem, directoryProof, operation, cancellationToken).ConfigureAwait(false);
            if (IsCompletedDirectoryRecovery(recovery))
            {
                return await CreateRecoveredMoveResultAsync(item, movedItem, operation.StateStore, recovery).ConfigureAwait(false);
            }

            if (recovery.Outcome != CloudDirectoryMoveReconciliationOutcome.NotMoved || recovery.RequiresFullRescan)
            {
                ThrowDirectoryRecoveryFailure(item, movedItem, recovery, nativeMoveIssued: false, cancellationToken);
            }

            sourceState = CloudItemInspector.Inspect(item.FullPath, item.Kind);
            if (!sourceState.Exists)
            {
                throw new FileNotFoundException("The directory changed during move validation; retry its original proof.", item.FullPath);
            }
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

        string nativeSourcePath = item.FullPath;
        if (item is CloudDirectory samePathDirectory &&
            string.Equals(item.FullPath, movedItem.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            // A case-insensitive source reference alone cannot distinguish a no-op from a
            // case-only rename. Use the observed spelling, including for the native rename.
            nativeSourcePath = ReadDirectoryMovePath(samePathDirectory);
        }

        if (string.Equals(nativeSourcePath, movedItem.FullPath, StringComparison.Ordinal))
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
        if (item is CloudDirectory managedDirectory && directoryProof is null && HasManagedDirectoryRoot(sourceState, statePlan.SourceEntries, item.RelativePath))
        {
            try
            {
                directoryProof = await PrepareDirectoryMoveCoreAsync(managedDirectory, (CloudDirectory)movedItem,
                    operation, cancellationToken).ConfigureAwait(false);
            }
            catch (CloudDirectoryEvidenceUnavailableException)
            {
                // Additive recovery evidence cannot remove ordinary native move availability
                // on storage without complete IDs or beyond the bounded preparation capacity.
            }
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
                Directory.Move(nativeSourcePath, movedItem.FullPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw CloudFilesException.FromException("CloudItem.Move", item.FullPath, exception);
        }

        if (item is CloudDirectory movedDirectory && directoryProof is not null)
        {
            // Native success must finish its durable phase under the already admitted lease.
            // Public reference creation or reacquisition would reject Stopping while disposal
            // is waiting for this operation to release the still-live store and path scopes.
            CloudDirectoryMoveReconciliationResult recovery = await ReconcileDirectoryMoveCoreAsync(movedDirectory,
                (CloudDirectory)movedItem, directoryProof, operation, CancellationToken.None).ConfigureAwait(false);
            if (!IsCompletedDirectoryRecovery(recovery))
            {
                ThrowDirectoryRecoveryFailure(item, movedItem, recovery, nativeMoveIssued: true, CancellationToken.None);
            }

            return await CreateRecoveredMoveResultAsync(item, movedItem, operation.StateStore, recovery).ConfigureAwait(false);
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

    private static bool HasManagedDirectoryRoot(LocalCloudItemInspection native,
        IReadOnlyList<CloudItemState> entries, string relativePath)
    {
        if (!native.PlaceholderState.HasFlag(CloudPlaceholderState.Placeholder))
        {
            return false;
        }

        try
        {
            CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Decode(native.PlaceholderIdentity);
            return entries.Any(entry => entry.ItemId == identity.ItemId && entry.RemoteId == identity.RemoteId &&
                entry.Kind == CloudItemKind.Directory && !entry.IsTombstone &&
                CloudDirectoryStateProjection.SamePath(entry.RelativePath, relativePath));
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsCompletedDirectoryRecovery(CloudDirectoryMoveReconciliationResult recovery) =>
        recovery.Outcome is CloudDirectoryMoveReconciliationOutcome.Projected or CloudDirectoryMoveReconciliationOutcome.AlreadyProjected &&
        recovery.DurableProjectionCommitted;

    private static async ValueTask<CloudItemMoveResult> CreateRecoveredMoveResultAsync(CloudItem source,
        CloudItem target, ICloudStateStore store, CloudDirectoryMoveReconciliationResult recovery) =>
        new(source.FullPath, target.FullPath, target, await InspectCoreAsync(target, store, CancellationToken.None).ConfigureAwait(false),
            recovery.DurableStateEntriesUpdated, recovery);

    private static void ThrowDirectoryRecoveryFailure(CloudItem source, CloudItem target,
        CloudDirectoryMoveReconciliationResult recovery, bool nativeMoveIssued, CancellationToken cancellationToken)
    {
        Exception error = recovery.Error ?? new InvalidOperationException(
            $"The directory proof cannot coordinate this move ({recovery.Outcome}); reconcile the namespace before retrying.");
        if (nativeMoveIssued || recovery.NativeMoveObserved)
        {
            throw new CloudItemCoordinationException("CloudItem.Move", source.FullPath, target.FullPath, null, error, recovery);
        }

        if (recovery.Outcome == CloudDirectoryMoveReconciliationOutcome.Canceled)
        {
            throw new OperationCanceledException("Directory move validation was canceled.", error, cancellationToken);
        }

        throw new InvalidOperationException("The directory move proof could not be verified.", error);
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
        await CloudDirectoryProvenance.RelocateRetainedPathsAsync(transaction, sourceEntries,
            sourceRelativePath, destinationRelativePath, cancellationToken).ConfigureAwait(false);
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
