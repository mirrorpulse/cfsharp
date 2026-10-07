namespace CfSharp;

// The caller owns the transaction and namespace-operation lifetime. This helper never commits,
// performs native I/O, acknowledges journal rows, or infers native ownership from durable paths.
internal static class CloudDirectoryStateProjection
{
    internal static async ValueTask ProjectAsync(ICloudStateTransaction transaction,
        IReadOnlyList<CloudItemState> sourceEntries, string sourceRelativePath,
        string destinationRelativePath, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        // Compute every path before writing any row. Segment validation prevents a similarly
        // named sibling (Docs2) from being treated as a descendant of Docs.
        (CloudItemState Item, string Path)[] entries = sourceEntries.Select(item =>
            (item, MapPath(item.RelativePath, sourceRelativePath, destinationRelativePath))).ToArray();
        foreach ((CloudItemState item, string path) in entries)
        {
            await transaction.Items.UpsertAsync(new CloudItemState(item.ItemId, item.RemoteId, path,
                item.Kind, item.RemoteRevision, item.LocalFileId, item.IsTombstone, updatedAt),
                cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string MapPath(string relativePath, string sourceRelativePath, string destinationRelativePath)
    {
        string normalizedPath = relativePath.Replace('/', '\\');
        string normalizedSource = sourceRelativePath.Replace('/', '\\');
        if (!string.Equals(normalizedPath, normalizedSource, StringComparison.OrdinalIgnoreCase) &&
            !normalizedPath.StartsWith(normalizedSource + "\\", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The durable item is outside the source subtree.", nameof(relativePath));
        }

        string suffix = relativePath.Length == sourceRelativePath.Length
            ? string.Empty
            : relativePath[sourceRelativePath.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return suffix.Length == 0 ? destinationRelativePath : Path.Combine(destinationRelativePath, suffix);
    }
}
