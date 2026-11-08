namespace CfSharp;

internal readonly record struct CloudItemPath(string FullPath, string RelativePath);

internal static class CloudItemPathResolver
{
    internal static CloudItemPath Resolve(string syncRootPath, string relativePath, bool allowRoot)
    {
        CloudItemPath path = ResolveLexically(syncRootPath, relativePath, allowRoot);
        if (!IsPhysicallyContained(syncRootPath, path))
        {
            throw new ArgumentException(
                "The item path resolves outside the sync root through a file-system link.",
                nameof(relativePath));
        }

        return path;
    }

    internal static CloudItemPath ResolveLexically(string syncRootPath, string relativePath, bool allowRoot)
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

        return new CloudItemPath(fullPath, canonicalRelativePath);
    }

    internal static bool IsPhysicallyContained(string syncRootPath, CloudItemPath path) =>
        IsSameOrChild(ResolveExistingLinks(syncRootPath).FullPath, ResolveExistingLinks(path.FullPath).FullPath);

    private static (string FullPath, bool Exists) ResolveExistingLinks(string path, HashSet<string>? activeLinks = null)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            throw new PlatformNotSupportedException("Physical Cloud Files path validation requires Windows 10 version 1709 or later.");
        }
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath)!;
        string current = root;
        bool exists = true;
        string remainder = fullPath[root.Length..];
        foreach (string segment in remainder.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(current, segment);
            // After the first missing component, the remaining suffix is purely lexical.
            // Probing through that missing ancestor could request cloud directory population.
            if (!exists) { current = next; continue; }
            FileSystemInfo? fileSystemInfo = CloudItemInspector.ReadFileSystemLink(next, out exists);
            if (fileSystemInfo is null)
            {
                current = next;
                continue;
            }

            try
            {
                activeLinks ??= new(StringComparer.OrdinalIgnoreCase);
                if (activeLinks.Count >= 64 || !activeLinks.Add(next))
                {
                    throw new IOException("The item path contains a cyclic or excessively deep file-system link chain.");
                }
                try
                {
                    // Resolve one name-surrogate hop, then classify every component again.
                    // Final-target resolution can follow a genuine link into a Cloud Files
                    // object and require its provider merely to construct a reference.
                    FileSystemInfo? resolved = fileSystemInfo.ResolveLinkTarget(returnFinalTarget: false);
                    // The BCL can strip the NT prefix from a volume-GUID mount target,
                    // leaving "Volume{...}". FullName then silently roots it at the working
                    // directory. Validate the original target spelling before trusting it.
                    if (resolved is not null && !Path.IsPathFullyQualified(resolved.ToString()))
                    {
                        throw new IOException("The file-system link target cannot be represented as a fully qualified path.");
                    }
                    (current, exists) = resolved is null ? (next, true) : ResolveExistingLinks(resolved.FullName, activeLinks);
                }
                finally
                {
                    activeLinks.Remove(next);
                }
            }
            catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // Watcher paths and their ancestors may disappear after the existence check.
                // Treat them like an initially missing component, not a failed feed. This
                // lexical resolution is not a native mutation guard; callers still bind
                // existing ancestry and the actual object before namespace mutations.
                current = next;
                exists = false;
            }
        }

        return (Path.TrimEndingDirectorySeparator(Path.GetFullPath(current)), exists);
    }

    private static bool IsSameOrChild(string parentPath, string candidatePath)
    {
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath));
        string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        return string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
