namespace CfSharp;

// The caller owns the transaction and namespace-operation lifetime. This helper never commits,
// performs native I/O, acknowledges journal rows, or infers native ownership from durable paths.
internal static class CloudDirectoryStateProjection
{
    internal static async ValueTask<VerifiedDirectoryProjection> ProjectVerifiedAsync(ICloudStateTransaction transaction,
        CloudDirectoryMoveProof proof, IReadOnlyList<CloudDirectoryMember> members,
        DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        Dictionary<Guid, CloudDirectoryMember> known = members.ToDictionary(member => member.ItemId);
        IReadOnlyList<CloudItemState> source = await transaction.Items.ListSubtreeAsync(proof.SourceRelativePath, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CloudItemState> target = await transaction.Items.ListSubtreeAsync(proof.DestinationRelativePath, cancellationToken).ConfigureAwait(false);
        foreach (CloudItemState item in source.Concat(target))
        {
            if (!known.TryGetValue(item.ItemId, out CloudDirectoryMember? member) || !MatchesMember(item, member, proof))
            {
                throw new CloudDirectoryProjectionConflictException("The current subtree contains unproven ownership or a changed member path.");
            }
        }

        List<CloudItemState> current = [];
        List<(CloudItemState Item, string Path)> updates = [];
        foreach (CloudDirectoryMember member in members)
        {
            CloudItemState? item = await transaction.Items.GetByItemIdAsync(member.ItemId, cancellationToken).ConfigureAwait(false);
            if (item is null)
            {
                if (member.ItemId == proof.RootItemId)
                {
                    throw new CloudDirectoryProjectionConflictException("The prepared directory root no longer has durable identity.");
                }

                // Removal is not permission to resurrect an old prepared snapshot.
                continue;
            }

            if (!MatchesMember(item, member, proof) || (member.ItemId == proof.RootItemId && item.IsTombstone))
            {
                throw new CloudDirectoryProjectionConflictException("A prepared member changed kind, ownership, or namespace location.");
            }

            string path = MemberPath(proof.DestinationRelativePath, member.Suffix);
            CloudItemState? collision = await transaction.Items.GetByRelativePathAsync(path, cancellationToken).ConfigureAwait(false);
            if (collision is not null && collision.ItemId != item.ItemId)
            {
                throw new CloudDirectoryProjectionConflictException("An unrelated item owns a destination path.");
            }

            current.Add(item);
            if (!string.Equals(item.RelativePath, path, StringComparison.Ordinal))
            {
                updates.Add((item, path));
            }
        }

        CloudStateCheckpoint? receipt = await transaction.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ReceiptName(proof.ProofId), cancellationToken).ConfigureAwait(false);
        byte[] receiptValue = proof.Encode();
        if (receipt is not null && (!receipt.Value.Span.SequenceEqual(receiptValue) || updates.Count != 0))
        {
            throw new CloudDirectoryProjectionConflictException("A historical completion receipt cannot regress later namespace state.");
        }

        // Complete every preflight before the first write. Preserve the current revision, local
        // ID and tombstone rather than restoring stale values from preparation. The journal is
        // deliberately untouched: historical payload paths and ACK identities remain meaningful.
        foreach ((CloudItemState item, string path) in updates)
        {
            await transaction.Items.UpsertAsync(new CloudItemState(item.ItemId, item.RemoteId, path,
                item.Kind, item.RemoteRevision, item.LocalFileId, item.IsTombstone, updatedAt), cancellationToken).ConfigureAwait(false);
        }

        if (receipt is null)
        {
            await transaction.Checkpoints.UpsertAsync(new CloudStateCheckpoint(CloudDirectoryMoveEvidence.ReceiptName(proof.ProofId),
                receiptValue, updatedAt), cancellationToken).ConfigureAwait(false);
        }

        return new(updates.Count, receipt is not null, current.AsReadOnly());
    }

    internal static string MemberPath(string root, string suffix) => suffix.Length == 0 ? root : Path.Combine(root, suffix);

    private static bool MatchesMember(CloudItemState item, CloudDirectoryMember member, CloudDirectoryMoveProof proof) =>
        item.Kind == member.Kind &&
        (SamePath(item.RelativePath, MemberPath(proof.SourceRelativePath, member.Suffix)) ||
         SamePath(item.RelativePath, MemberPath(proof.DestinationRelativePath, member.Suffix)));

    internal static bool SamePath(string first, string second) =>
        string.Equals(first.Replace('/', '\\'), second.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);

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

internal sealed record VerifiedDirectoryProjection(int UpdatedCount, bool AlreadyCompleted, IReadOnlyList<CloudItemState> CurrentItems);

internal sealed class CloudDirectoryProjectionConflictException(string message) : InvalidOperationException(message);
