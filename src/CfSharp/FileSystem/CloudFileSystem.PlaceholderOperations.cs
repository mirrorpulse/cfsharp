namespace CfSharp;

public sealed partial class CloudFileSystem
{
    private const int AlreadyExistsHResult = unchecked((int)0x800700B7);

    internal async ValueTask<CloudPlaceholderBatchResult> CreatePlaceholdersAsync(
        CloudDirectory directory,
        IEnumerable<CloudPlaceholderSpec> placeholders,
        CloudPlaceholderBatchOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(placeholders);
        ArgumentNullException.ThrowIfNull(options);
        List<PlaceholderCreationWorkEntry> entries = MaterializeCreationEntries(
            directory,
            placeholders);
        cancellationToken.ThrowIfCancellationRequested();

        using CloudFileSystemOperationLease operation = await AcquireOperationAsync(
            entries.Select(static entry => CloudItemOperationScope.Exact(entry.Path)),
            cancellationToken).ConfigureAwait(false);
        LocalCloudItemInspection directoryState = CloudItemInspector.Inspect(
            directory.FullPath,
            CloudItemKind.Directory);
        if (!directoryState.Exists)
        {
            throw new DirectoryNotFoundException(
                $"The placeholder parent directory does not exist: '{directory.FullPath}'.");
        }

        PreflightExistingEntries(entries);
        ApplyPreflightStop(entries, options.StopOnFirstFailure);
        cancellationToken.ThrowIfCancellationRequested();

        List<PlaceholderCreationWorkEntry> nativeWork = entries
            .Where(static entry => entry.RequiresNativeCreation)
            .ToList();
        CloudFilesException? batchError = null;
        if (nativeWork.Count != 0)
        {
            CloudPlaceholderNativeBatchResult nativeResult =
                CloudPlaceholderPlatform.CreatePlaceholders(
                    directory.FullPath,
                    nativeWork.Select(static entry => entry.Specification).ToArray(),
                    options.StopOnFirstFailure);
            if (nativeResult.HResult < 0)
            {
                batchError = CloudFilesException.FromHResult(
                    "CloudDirectory.CreatePlaceholders",
                    directory.FullPath,
                    nativeResult.HResult);
            }

            ApplyNativeResults(nativeWork, nativeResult);
            ApplyPreflightStop(entries, options.StopOnFirstFailure);
        }

        List<PlaceholderCreationWorkEntry> appliedEntries = entries
            .Where(static entry => entry.Status is CloudItemOperationStatus.Succeeded)
            .ToList();
        if (appliedEntries.Count != 0)
        {
            try
            {
                await PersistCreatedEntriesAsync(
                    operation.StateStore,
                    appliedEntries).ConfigureAwait(false);
                foreach (PlaceholderCreationWorkEntry entry in appliedEntries)
                {
                    entry.Progress |= CloudPlaceholderCreationProgress.DurableStatePersisted;
                }
            }
            catch (Exception exception)
            {
                throw new CloudPlaceholderPersistenceException(
                    CreateBatchResult(entries, batchError),
                    exception);
            }
        }

        foreach (PlaceholderCreationWorkEntry entry in appliedEntries)
        {
            if (entry.Specification is not CloudFilePlaceholderSpec file ||
                file.InitialAvailability is CloudAvailabilityTarget.OnlineOnly)
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                CloudPlaceholderPlatform.SetInitialPinState(
                    entry.Path,
                    file.InitialAvailability,
                    cancellationToken);
                entry.Progress |= CloudPlaceholderCreationProgress.PinStateApplied;
                cancellationToken.ThrowIfCancellationRequested();
                await CloudPlaceholderPlatform.HydrateWithTransientRetryAsync(
                        entry.Path,
                        cancellationToken)
                    .ConfigureAwait(false);
                entry.Progress |= CloudPlaceholderCreationProgress.ContentHydrated;
            }
            catch (CloudFilesException exception)
            {
                entry.Status = CloudItemOperationStatus.Failed;
                entry.Error = exception;
            }
            catch (OperationCanceledException exception)
            {
                entry.Status = CloudItemOperationStatus.NotProcessed;
                throw new CloudPlaceholderCreationCanceledException(
                    CreateBatchResult(entries, batchError),
                    exception,
                    cancellationToken);
            }
        }

        return CreateBatchResult(entries, batchError);
    }

    private List<PlaceholderCreationWorkEntry> MaterializeCreationEntries(
        CloudDirectory directory,
        IEnumerable<CloudPlaceholderSpec> placeholders)
    {
        List<PlaceholderCreationWorkEntry> entries = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        HashSet<Guid> itemIds = [];
        HashSet<string> remoteIds = new(StringComparer.Ordinal);
        foreach (CloudPlaceholderSpec specification in placeholders)
        {
            if (specification is null)
            {
                throw new ArgumentException(
                    "The placeholder sequence cannot contain null entries.",
                    nameof(placeholders));
            }

            if (!names.Add(specification.Name))
            {
                throw new ArgumentException(
                    $"The placeholder name '{specification.Name}' occurs more than once.",
                    nameof(placeholders));
            }

            if (!itemIds.Add(specification.Identity.ItemId))
            {
                throw new ArgumentException(
                    $"The item identifier '{specification.Identity.ItemId:D}' occurs more than once.",
                    nameof(placeholders));
            }

            if (!remoteIds.Add(specification.Identity.RemoteId))
            {
                throw new ArgumentException(
                    $"The remote identifier '{specification.Identity.RemoteId}' occurs more than once.",
                    nameof(placeholders));
            }

            string relativePath = directory.RelativePath.Length == 0
                ? specification.Name
                : Path.Combine(directory.RelativePath, specification.Name);
            CloudItem item = CreateItemReference(relativePath, specification.Kind);
            entries.Add(new PlaceholderCreationWorkEntry(specification, item));
        }

        if (entries.Count == 0)
        {
            throw new ArgumentException(
                "At least one placeholder specification is required.",
                nameof(placeholders));
        }

        return entries;
    }

    private static void PreflightExistingEntries(List<PlaceholderCreationWorkEntry> entries)
    {
        foreach (PlaceholderCreationWorkEntry entry in entries)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(entry.Path);
            }
            catch (FileNotFoundException)
            {
                entry.RequiresNativeCreation = true;
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                entry.RequiresNativeCreation = true;
                continue;
            }

            CloudItemKind existingKind = attributes.HasFlag(FileAttributes.Directory)
                ? CloudItemKind.Directory
                : CloudItemKind.File;
            bool identityMatches = false;
            if (existingKind == entry.Specification.Kind)
            {
                LocalCloudItemInspection local = CloudItemInspector.Inspect(
                    entry.Path,
                    existingKind);
                identityMatches = local.PlaceholderIdentity.AsSpan().SequenceEqual(
                    entry.Specification.Identity.Encode());
            }

            if (identityMatches)
            {
                entry.Status = CloudItemOperationStatus.Succeeded;
                entry.Progress = CloudPlaceholderCreationProgress.ExistingPlaceholderMatched;
                continue;
            }

            if (entry.Specification.CollisionBehavior is CloudPlaceholderCollisionBehavior.Supersede)
            {
                entry.RequiresNativeCreation = true;
                continue;
            }

            entry.Status = CloudItemOperationStatus.Failed;
            entry.Error = CloudFilesException.FromHResult(
                "CloudDirectory.CreatePlaceholders.Preflight",
                entry.Path,
                AlreadyExistsHResult);
        }
    }

    private static void ApplyPreflightStop(
        List<PlaceholderCreationWorkEntry> entries,
        bool stopOnFirstFailure)
    {
        if (!stopOnFirstFailure)
        {
            return;
        }

        int failureIndex = entries.FindIndex(static entry =>
            entry.Status is CloudItemOperationStatus.Failed);
        if (failureIndex < 0)
        {
            return;
        }

        for (int index = failureIndex + 1; index < entries.Count; index++)
        {
            entries[index].MarkNotProcessed();
        }
    }

    private static void ApplyNativeResults(
        IReadOnlyList<PlaceholderCreationWorkEntry> work,
        CloudPlaceholderNativeBatchResult result)
    {
        int processedCount = checked((int)Math.Min(result.EntriesProcessed, (uint)work.Count));
        for (int index = 0; index < work.Count; index++)
        {
            PlaceholderCreationWorkEntry entry = work[index];
            entry.RequiresNativeCreation = false;
            if (index >= processedCount)
            {
                entry.MarkNotProcessed();
                continue;
            }

            CloudPlaceholderNativeEntryResult nativeEntry = result.Entries[index];
            if (nativeEntry.HResult < 0)
            {
                entry.Status = CloudItemOperationStatus.Failed;
                entry.Error = CloudFilesException.FromHResult(
                    "CloudDirectory.CreatePlaceholders.Entry",
                    entry.Path,
                    nativeEntry.HResult);
                continue;
            }

            entry.Status = CloudItemOperationStatus.Succeeded;
            entry.Progress = CloudPlaceholderCreationProgress.PlaceholderCreated;
            entry.CreateUsn = nativeEntry.CreateUsn;
        }
    }

    private static async ValueTask PersistCreatedEntriesAsync(
        ICloudStateStore stateStore,
        IReadOnlyList<PlaceholderCreationWorkEntry> entries)
    {
        await using ICloudStateTransaction transaction = await stateStore
            .BeginTransactionAsync(CancellationToken.None)
            .ConfigureAwait(false);
        DateTimeOffset updatedAt = DateTimeOffset.UtcNow;
        foreach (PlaceholderCreationWorkEntry entry in entries)
        {
            CloudPlaceholderIdentity identity = entry.Specification.Identity;
            await transaction.Items.UpsertAsync(
                new CloudItemState(
                    identity.ItemId,
                    identity.RemoteId,
                    entry.Item.RelativePath,
                    entry.Specification.Kind,
                    identity.RemoteRevision,
                    localFileId: null,
                    isTombstone: false,
                    updatedAt),
                CancellationToken.None).ConfigureAwait(false);
        }

        foreach (PlaceholderCreationWorkEntry entry in entries)
        {
            await CloudDirectoryProvenance.RetainProjectionAsync(transaction, entry.Item.SyncRootPath,
                entry.Item.RelativePath, entry.Specification.Kind, CancellationToken.None).ConfigureAwait(false);
        }

        await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static CloudPlaceholderBatchResult CreateBatchResult(
        IEnumerable<PlaceholderCreationWorkEntry> entries,
        CloudFilesException? batchError) =>
        new(
            entries.Select(static entry => new CloudPlaceholderBatchEntryResult(
                entry.Specification,
                entry.Path,
                entry.Status,
                entry.Progress,
                entry.CreateUsn,
                entry.HasPlaceholder ? entry.Item : null,
                entry.Error)),
            batchError);

    private sealed class PlaceholderCreationWorkEntry
    {
        internal PlaceholderCreationWorkEntry(
            CloudPlaceholderSpec specification,
            CloudItem item)
        {
            Specification = specification;
            Item = item;
        }

        internal CloudPlaceholderSpec Specification { get; }

        internal CloudItem Item { get; }

        internal string Path => Item.FullPath;

        internal CloudItemOperationStatus Status { get; set; } =
            CloudItemOperationStatus.NotProcessed;

        internal CloudPlaceholderCreationProgress Progress { get; set; }

        internal long? CreateUsn { get; set; }

        internal CloudFilesException? Error { get; set; }

        internal bool RequiresNativeCreation { get; set; }

        internal bool HasPlaceholder => Progress.HasFlag(
            CloudPlaceholderCreationProgress.PlaceholderCreated) ||
            Progress.HasFlag(CloudPlaceholderCreationProgress.ExistingPlaceholderMatched);

        internal void MarkNotProcessed()
        {
            Status = CloudItemOperationStatus.NotProcessed;
            Progress = CloudPlaceholderCreationProgress.None;
            CreateUsn = null;
            Error = null;
            RequiresNativeCreation = false;
        }
    }
}
