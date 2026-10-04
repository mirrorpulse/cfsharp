using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace CfSharp;

/// <summary>Represents an immutable, path-bound item within one cloud file system.</summary>
/// <remarks>
/// This object contains only its owning facade, canonical paths, and expected item kind. It owns
/// no native handle and caches no mutable file-system or durable state. Property reads are safe
/// from concurrent threads. Operations require the owning <see cref="CloudFileSystem"/> to remain
/// started.
/// </remarks>
[SupportedOSPlatform("windows10.0.16299")]
public abstract partial class CloudItem
{
    private readonly CloudFileSystem _owner;

    private protected CloudItem(
        CloudFileSystem owner,
        string fullPath,
        string relativePath,
        CloudItemKind kind)
    {
        _owner = owner;
        FullPath = fullPath;
        RelativePath = relativePath;
        Kind = kind;
    }

    /// <summary>Gets whether this reference expects a file or directory.</summary>
    public CloudItemKind Kind { get; }

    /// <summary>Gets the canonical absolute local path.</summary>
    public string FullPath { get; }

    /// <summary>Gets the canonical path relative to the owning sync root.</summary>
    public string RelativePath { get; }

    /// <summary>Gets the final path component, or the root directory name for the root item.</summary>
    public string Name => Path.GetFileName(FullPath);

    /// <summary>Gets the immutable parent-directory reference, or null for the sync root.</summary>
    public CloudDirectory? Parent
    {
        get
        {
            if (RelativePath.Length == 0)
            {
                return null;
            }

            string? parent = Path.GetDirectoryName(RelativePath);
            return _owner.GetDirectory(parent ?? string.Empty);
        }
    }

    private protected CloudFileSystem Owner => _owner;

    internal bool IsOwnedBy(CloudFileSystem owner) => ReferenceEquals(_owner, owner);

    internal string SyncRootPath => _owner.SyncRootPath;

    /// <summary>Reads fresh local and durable state without retaining a native handle.</summary>
    /// <param name="cancellationToken">Token that cancels state-store access.</param>
    /// <returns>A new immutable snapshot. Missing items are represented explicitly.</returns>
    /// <exception cref="InvalidOperationException">
    /// The owning file system has not started, or the existing item has another kind.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The owning file system is stopping or disposed.</exception>
    /// <exception cref="CloudFilesException">Windows cannot inspect Cloud Files metadata.</exception>
    public ValueTask<CloudItemSnapshot> InspectAsync(
        CancellationToken cancellationToken = default) =>
        _owner.InspectAsync(this, cancellationToken);

    /// <summary>Reads this file or directory's current file-system update sequence number (USN).</summary>
    /// <param name="cancellationToken">
    /// Token observed while acquiring the operation lease and before the synchronous Windows query.
    /// </param>
    /// <returns>
    /// The last USN recorded by Windows for the item. A zero value is unavailable for conditional
    /// synchronization; only a positive value may be used with <see cref="CloudInSyncChangeOptions"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// On Windows 10 version 1709 or later, this uses <c>FSCTL_READ_FILE_USN_DATA</c> on an
    /// attribute-only handle. It supports ordinary items and Cloud Files placeholders, does not
    /// hydrate content, and closes the handle before returning. The file system must support USN
    /// queries (NTFS or ReFS); unavailable journals and native failures are not silently replaced
    /// with unconditional synchronization. No journal is created or modified by this query.
    /// </para>
    /// <para>
    /// The owning file system must remain started. Calls participate in its operation coordination
    /// and may be made concurrently, but the returned observation does not lock out external writers
    /// or identify a content hash. Read a positive USN before verifying uploaded content, close any
    /// verification streams, and pass that same USN to <see cref="SetInSyncAsync"/>. If verification
    /// or the USN condition fails, reconcile and retry; never fall back to an unconditional mark.
    /// Tokens are scoped to the same existing item and current volume journal, not durable versions
    /// across deletion, replacement, or journal recreation. This reference remains path-bound.
    /// A positive file-system USN does not guarantee that the installed Cloud Files platform will
    /// accept a conditional mark; some Windows builds reject even a freshly verified token with
    /// <c>ERROR_CLOUD_FILE_NOT_IN_SYNC</c>. That native failure remains visible to the caller.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The owning file system has not started, the item has another kind, or it is a non-Cloud Files
    /// reparse point.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The owning file system is stopping or disposed.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested before the query.</exception>
    /// <exception cref="CloudFilesException">
    /// Windows cannot open or query the item, including missing items, access denial, or an
    /// unsupported or unavailable journal. The original Win32 error is preserved as an HRESULT.
    /// </exception>
    /// <exception cref="InvalidDataException">Windows returned a malformed or truncated USN record.</exception>
    /// <exception cref="NotSupportedException">Windows returned an unsupported USN record version.</exception>
    public ValueTask<long> ReadUsnAsync(CancellationToken cancellationToken = default) =>
        _owner.ReadUsnAsync(this, cancellationToken);

    /// <inheritdoc/>
    public override string ToString() => FullPath;
}

/// <summary>Represents an immutable path-bound file reference.</summary>
[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class CloudFile : CloudItem
{
    internal CloudFile(CloudFileSystem owner, string fullPath, string relativePath)
        : base(owner, fullPath, relativePath, CloudItemKind.File)
    {
    }
}

/// <summary>Represents an immutable path-bound directory reference.</summary>
[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class CloudDirectory : CloudItem
{
    internal CloudDirectory(CloudFileSystem owner, string fullPath, string relativePath)
        : base(owner, fullPath, relativePath, CloudItemKind.Directory)
    {
    }

    /// <summary>Creates a file reference relative to this directory without opening it.</summary>
    /// <param name="relativePath">
    /// Path relative to this directory. Parent navigation is allowed only while it remains within
    /// the owning sync root. The item need not exist.
    /// </param>
    /// <returns>An immutable file reference.</returns>
    public CloudFile GetFile(string relativePath) =>
        Owner.GetFile(CombineRelativePath(relativePath));

    /// <summary>Creates a directory reference relative to this directory without opening it.</summary>
    /// <param name="relativePath">
    /// Path relative to this directory, or an empty string for this directory. Parent navigation
    /// is allowed only while it remains within the owning sync root.
    /// </param>
    /// <returns>An immutable directory reference.</returns>
    public CloudDirectory GetDirectory(string relativePath) =>
        Owner.GetDirectory(CombineRelativePath(relativePath));

    /// <summary>Resolves an existing local path to a typed file or directory reference.</summary>
    /// <param name="relativePath">Path relative to this directory.</param>
    /// <returns>A <see cref="CloudFile"/> or <see cref="CloudDirectory"/> matching current local state.</returns>
    /// <exception cref="FileNotFoundException">No local item exists at the resolved path.</exception>
    /// <exception cref="ArgumentException">The path escapes the sync root.</exception>
    public CloudItem Resolve(string relativePath) =>
        Owner.ResolveExistingItem(CombineRelativePath(relativePath));

    /// <summary>Streams currently materialized local children without remote side effects.</summary>
    /// <param name="options">Immutable filter, recursion, and ordering options, or null for defaults.</param>
    /// <param name="cancellationToken">Token checked before each directory and yielded entry.</param>
    /// <returns>
    /// An asynchronous stream of immutable path references. File-system enumeration itself is
    /// synchronous between asynchronous yields because Windows exposes no asynchronous directory
    /// enumeration primitive.
    /// </returns>
    /// <exception cref="DirectoryNotFoundException">This directory no longer exists.</exception>
    /// <exception cref="InvalidOperationException">This path currently identifies a file.</exception>
    /// <exception cref="InvalidDataException">
    /// A local entry resolves outside the sync root through a symbolic link or junction.
    /// </exception>
    public async IAsyncEnumerable<CloudItem> EnumerateLocalChildrenAsync(
        CloudDirectoryEnumerationOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        CloudDirectoryEnumerationOptions selected =
            options ?? CloudDirectoryEnumerationOptions.Default;
        CloudItemSnapshot snapshot = await InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Exists)
        {
            throw new DirectoryNotFoundException(
                $"The cloud directory does not exist: '{FullPath}'.");
        }

        Queue<string> pendingDirectories = new();
        pendingDirectories.Enqueue(FullPath);
        while (pendingDirectories.TryDequeue(out string? currentDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(currentDirectory);
            }
            catch (DirectoryNotFoundException) when (!string.Equals(
                currentDirectory,
                FullPath,
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            OrderEntries(entries, selected.Order);
            foreach (string entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (FileNotFoundException)
                {
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }

                bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
                string relativeToRoot = Path.GetRelativePath(Owner.SyncRootPath, entry);
                CloudItem item;
                try
                {
                    item = Owner.CreateItemReference(
                        relativeToRoot,
                        isDirectory ? CloudItemKind.Directory : CloudItemKind.File);
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException(
                        $"The local entry '{entry}' resolves outside the sync root.",
                        exception);
                }

                if (selected.Recursive &&
                    isDirectory &&
                    !attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    pendingDirectories.Enqueue(entry);
                }

                bool kindIncluded = isDirectory
                    ? selected.EntryKinds.HasFlag(CloudDirectoryEntryKinds.Directories)
                    : selected.EntryKinds.HasFlag(CloudDirectoryEntryKinds.Files);
                if (kindIncluded && FileSystemName.MatchesSimpleExpression(
                        selected.SearchPattern,
                        Path.GetFileName(entry),
                        ignoreCase: true))
                {
                    yield return item;
                    await Task.Yield();
                }
            }
        }
    }

    private string CombineRelativePath(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        return RelativePath.Length == 0
            ? relativePath
            : Path.Combine(RelativePath, relativePath);
    }

    private static void OrderEntries(
        string[] entries,
        CloudDirectoryEnumerationOrder order)
    {
        if (order is CloudDirectoryEnumerationOrder.FileSystem)
        {
            return;
        }

        Array.Sort(entries, static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(
            Path.GetFileName(left),
            Path.GetFileName(right)));
        if (order is CloudDirectoryEnumerationOrder.NameDescending)
        {
            Array.Reverse(entries);
        }
    }
}
