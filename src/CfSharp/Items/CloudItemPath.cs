namespace CfSharp;

internal readonly record struct CloudItemPath(string FullPath, string RelativePath);

internal static class CloudItemPathResolver
{
    internal static CloudItemPath Resolve(string syncRootPath, string relativePath, bool allowRoot)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        if (Path.IsPathFullyQualified(relativePath))
        {
            throw new ArgumentException("The item path must be relative to the sync root.", nameof(relativePath));
        }

        string fullPath = Path.GetFullPath(Path.Combine(syncRootPath, relativePath));
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(syncRootPath));
        if (!IsSameOrChild(normalizedRoot, fullPath))
        {
            throw new ArgumentException("The item path escapes the sync root.", nameof(relativePath));
        }

        string canonicalRelativePath = Path.GetRelativePath(normalizedRoot, fullPath);
        if (canonicalRelativePath == ".")
        {
            if (!allowRoot)
            {
                throw new ArgumentException("A file path cannot identify the sync root.", nameof(relativePath));
            }

            canonicalRelativePath = string.Empty;
        }

        foreach (string segment in canonicalRelativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                _ = CloudPlaceholderSpec.ValidateName(segment);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException(
                    "The item path contains an invalid Windows file-name segment.",
                    nameof(relativePath),
                    exception);
            }
        }

        string physicalRoot = ResolveExistingLinks(normalizedRoot);
        string physicalItem = ResolveExistingLinks(fullPath);
        if (!IsSameOrChild(physicalRoot, physicalItem))
        {
            throw new ArgumentException(
                "The item path resolves outside the sync root through a file-system link.",
                nameof(relativePath));
        }

        return new CloudItemPath(fullPath, canonicalRelativePath);
    }

    private static string ResolveExistingLinks(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath)!;
        string current = root;
        string remainder = fullPath[root.Length..];
        foreach (string segment in remainder.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(current, segment);
            FileSystemInfo? fileSystemInfo = Directory.Exists(next)
                ? new DirectoryInfo(next)
                : File.Exists(next)
                    ? new FileInfo(next)
                    : null;
            if (fileSystemInfo is null)
            {
                current = next;
                continue;
            }

            try
            {
                FileSystemInfo? resolved = fileSystemInfo.ResolveLinkTarget(returnFinalTarget: true);
                current = resolved?.FullName ?? next;
            }
            catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // Watcher paths and their ancestors may disappear after the existence check.
                // Treat them like an initially missing component, not a failed feed. This
                // lexical resolution is not a native mutation guard; callers still bind
                // existing ancestry and the actual object before namespace mutations.
                current = next;
            }
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
    }

    private static bool IsSameOrChild(string parentPath, string candidatePath)
    {
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath));
        string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        return string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
