using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace CfSharp;

public sealed partial class CloudFileSystem
{
    /// <summary>
    /// Reads one page from an application-owned remote directory catalog without mutating the
    /// managed namespace.
    /// </summary>
    /// <param name="catalog">Application adapter that owns remote transport and consistency.</param>
    /// <param name="query">Validated remote directory path, filters, page size, and cursor.</param>
    /// <param name="cancellationToken">Token observed while the catalog reads its remote source.</param>
    /// <returns>A validated page of immediate remote children.</returns>
    /// <exception cref="ArgumentNullException">The catalog or query is null.</exception>
    /// <exception cref="InvalidDataException">The catalog returned an invalid page.</exception>
    /// <exception cref="InvalidOperationException">The file system has not started.</exception>
    /// <remarks>
    /// This method deliberately does not create placeholders, advance remote cursors, or invoke
    /// Windows Cloud Files APIs. The adapter callback runs outside CfSharp state transactions and
    /// operation leases, so it may perform network I/O or application-level retries safely.
    /// </remarks>
    public async ValueTask<CloudRemoteDirectoryPage> ReadRemoteDirectoryPageAsync(
        ICloudRemoteDirectoryCatalog catalog,
        CloudRemoteDirectoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(query);
        EnsureStarted();
        cancellationToken.ThrowIfCancellationRequested();
        CloudRemoteDirectoryPage page = await catalog
            .ReadPageAsync(query, cancellationToken)
            .ConfigureAwait(false);
        ArgumentNullException.ThrowIfNull(page);
        ValidateRemoteDirectoryPage(query, page);
        return page;
    }

    /// <summary>
    /// Reads one paged view that combines materialized local children with durable remote identity
    /// and revision state.
    /// </summary>
    /// <param name="query">Validated local path, filters, page size, tombstone policy, and cursor.</param>
    /// <param name="cancellationToken">Token observed during local enumeration and state reads.</param>
    /// <returns>A deterministic, side-effect-free synchronized directory page.</returns>
    /// <exception cref="ArgumentNullException">The query is null.</exception>
    /// <exception cref="DirectoryNotFoundException">The requested local directory is missing.</exception>
    /// <exception cref="InvalidDataException">The local namespace or durable state is inconsistent.</exception>
    /// <exception cref="InvalidOperationException">The file system has not started.</exception>
    /// <remarks>
    /// This view never calls a remote catalog and never applies a remote change. Callers first
    /// apply a remote batch explicitly, then use this method to inspect the durable/local result.
    /// The complete local enumeration and durable read are admitted as one operation so disposal
    /// cannot release the state store between the two phases.
    /// Online-only placeholders are represented as materialized entries; durable records whose
    /// local path is temporarily absent remain visible with <see cref="CloudSynchronizedDirectoryEntry.IsMaterialized"/>
    /// set to <see langword="false"/>.
    /// </remarks>
    public async ValueTask<CloudSynchronizedDirectoryPage> ReadSynchronizedDirectoryPageAsync(
        CloudSynchronizedDirectoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        EnsureStarted();
        cancellationToken.ThrowIfCancellationRequested();

        using CloudFileSystemOperationLease operation = await AcquireOperationAsync(
                [CloudItemOperationScope.Subtree(SyncRootPath)],
                cancellationToken)
            .ConfigureAwait(false);
        operation.EstablishContext();

        CloudDirectory directory = GetDirectory(query.RelativePath);
        CloudDirectoryEnumerationOptions options = CloudDirectoryEnumerationOptions
            .CreateBuilder()
            .WithEntryKinds(query.EntryKinds)
            .WithOrder(CloudDirectoryEnumerationOrder.NameAscending)
            .Build();
        List<CloudItem> localItems = [];
        await foreach (CloudItem item in directory
            .EnumerateLocalChildrenAsync(options, cancellationToken)
            .ConfigureAwait(false))
        {
            localItems.Add(item);
        }

        ICloudStateStore stateStore = _stateStore ??
            throw new InvalidOperationException("The cloud file system has no open state store.");
        IReadOnlyList<CloudItemState> durableItems;
        await using (ICloudStateTransaction transaction = await stateStore
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false))
        {
            durableItems = await transaction.Items
                .ListSubtreeAsync(query.RelativePath, cancellationToken)
                .ConfigureAwait(false);
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }

        Dictionary<string, CloudItemState> stateByPath = new(StringComparer.OrdinalIgnoreCase);
        foreach (CloudItemState state in CloudDirectoryStateProjection.CurrentGenerations(durableItems))
        {
            try
            {
                _ = CloudItemPathResolver.Resolve(SyncRootPath, state.RelativePath, allowRoot: false);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    $"Durable state contains an unsafe synchronized path '{state.RelativePath}'.",
                    exception);
            }

            if (!IsImmediateChild(query.RelativePath, state.RelativePath))
            {
                continue;
            }

            if (!MatchesEntryKind(query.EntryKinds, state.Kind))
            {
                continue;
            }

            if (!stateByPath.TryAdd(state.RelativePath, state))
            {
                throw new InvalidDataException(
                    $"Durable state contains duplicate synchronized paths near '{state.RelativePath}'.");
            }
        }

        Dictionary<string, CloudSynchronizedDirectoryEntry> entriesByPath =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (CloudItem item in localItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CloudItemState? state = stateByPath.GetValueOrDefault(item.RelativePath);
            if (state is not null && state.Kind != item.Kind)
            {
                throw new InvalidDataException(
                    $"Durable state identifies '{item.RelativePath}' as {state.Kind}, not {item.Kind}.");
            }

            entriesByPath[item.RelativePath] = new CloudSynchronizedDirectoryEntry(
                item.RelativePath,
                item.Kind,
                state,
                isMaterialized: true);
        }

        foreach ((string path, CloudItemState state) in stateByPath)
        {
            if (state.IsTombstone && !query.IncludeTombstones)
            {
                continue;
            }

            if (!entriesByPath.ContainsKey(path))
            {
                entriesByPath[path] = new CloudSynchronizedDirectoryEntry(
                    path,
                    state.Kind,
                    state,
                    isMaterialized: false);
            }
        }

        List<CloudSynchronizedDirectoryEntry> ordered = entriesByPath.Values
            .OrderBy(static entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.RelativePath, StringComparer.Ordinal)
            .ToList();
        byte[] snapshotFingerprint = CloudSynchronizedDirectoryCursor.CreateFingerprint(query, ordered);
        int offset = CloudSynchronizedDirectoryCursor.Parse(
            query.ContinuationCursor.Span,
            ordered.Count,
            snapshotFingerprint);
        int count = Math.Min(query.PageSize, ordered.Count - offset);
        IReadOnlyList<CloudSynchronizedDirectoryEntry> pageEntries =
            new ReadOnlyCollection<CloudSynchronizedDirectoryEntry>(
                ordered.GetRange(offset, count));
        int nextOffset = offset + count;
        bool isComplete = nextOffset >= ordered.Count;
        return new CloudSynchronizedDirectoryPage(
            pageEntries,
            isComplete
                ? ReadOnlyMemory<byte>.Empty
                : CloudSynchronizedDirectoryCursor.Create(nextOffset, snapshotFingerprint),
            isComplete);
    }

    private static void ValidateRemoteDirectoryPage(
        CloudRemoteDirectoryQuery query,
        CloudRemoteDirectoryPage page)
    {
        if (page.Entries.Count > query.PageSize)
        {
            throw new InvalidDataException(
                $"The remote catalog returned {page.Entries.Count} entries for a page size of {query.PageSize}.");
        }

        foreach (CloudRemoteDirectoryEntry entry in page.Entries)
        {
            if (!IsImmediateChild(query.RelativePath, entry.RelativePath))
            {
                throw new InvalidDataException(
                    $"Remote catalog entry '{entry.RelativePath}' is not an immediate child of " +
                    $"'{query.RelativePath}'.");
            }

            if (!MatchesEntryKind(query.EntryKinds, entry.ItemKind))
            {
                throw new InvalidDataException(
                    $"Remote catalog entry '{entry.RelativePath}' does not match the requested kind filter.");
            }
        }
    }

    private static bool MatchesEntryKind(CloudDirectoryEntryKinds kinds, CloudItemKind itemKind) =>
        itemKind is CloudItemKind.File
            ? kinds.HasFlag(CloudDirectoryEntryKinds.Files)
            : kinds.HasFlag(CloudDirectoryEntryKinds.Directories);

    private static bool IsImmediateChild(string directoryPath, string childPath)
    {
        string? parent = Path.GetDirectoryName(childPath);
        parent ??= string.Empty;
        return string.Equals(parent, directoryPath, StringComparison.OrdinalIgnoreCase);
    }
}
