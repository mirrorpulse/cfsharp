using System.ComponentModel;
using System.Runtime.Versioning;

using CfSharp.Native;

using Microsoft.Win32.SafeHandles;

namespace CfSharp;

internal sealed record CloudDirectoryProvenance(Guid EvidenceId, Guid StoreScope, Guid RootItemId,
    string RelativePath, CloudLocalFileBinding Binding, byte[] Identity)
{
    internal static string BindingName(Guid id) => $"cfsharp/namespace/directories/{id:N}/binding";
    internal static string MembersName(Guid id) => $"cfsharp/namespace/directories/{id:N}/members";

    internal byte[] Encode()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write(1);
        writer.Write(EvidenceId.ToByteArray());
        writer.Write(StoreScope.ToByteArray());
        writer.Write(RootItemId.ToByteArray());
        writer.Write(Binding.VolumeSerialNumber);
        writer.Write(Binding.SyncRootFileId.ToByteArray());
        writer.Write(Binding.LocalFileId.ToByteArray());
        CloudDirectoryEvidenceCodec.WriteText(writer, RelativePath);
        writer.Write(Identity.Length);
        writer.Write(Identity);
        return stream.ToArray();
    }

    internal static CloudDirectoryProvenance Decode(ReadOnlyMemory<byte> value)
    {
        if (value.Length is < 100 or > 65536)
        {
            throw new InvalidDataException("The directory provenance length is invalid.");
        }

        try
        {
            using MemoryStream stream = new(value.ToArray(), writable: false);
            using BinaryReader reader = new(stream);
            if (reader.ReadInt32() != 1)
            {
                throw new NotSupportedException("The directory provenance version is unsupported.");
            }

            Guid evidenceId = CloudDirectoryEvidenceCodec.ReadGuid(reader);
            Guid scope = CloudDirectoryEvidenceCodec.ReadGuid(reader);
            Guid rootId = CloudDirectoryEvidenceCodec.ReadGuid(reader);
            CloudLocalFileBinding binding = new(reader.ReadUInt64(), CloudDirectoryEvidenceCodec.ReadGuid(reader),
                CloudDirectoryEvidenceCodec.ReadGuid(reader));
            string path = CloudDirectoryEvidenceCodec.CanonicalPath(CloudDirectoryEvidenceCodec.ReadText(reader));
            byte[] identity = CloudDirectoryEvidenceCodec.ReadBytes(reader, SyncRootRegistrationOptions.MaxFileIdentityLength);
            if (stream.Position != stream.Length || evidenceId == Guid.Empty || scope == Guid.Empty ||
                rootId == Guid.Empty || CloudPlaceholderIdentity.Decode(identity).ItemId != rootId)
            {
                throw new InvalidDataException("The directory provenance is malformed.");
            }

            return new(evidenceId, scope, rootId, path, binding, identity);
        }
        catch (Exception exception) when (exception is ArgumentException or EndOfStreamException)
        {
            throw new InvalidDataException("The directory provenance is malformed.", exception);
        }
    }

    [SupportedOSPlatform("windows10.0.16299")]
    internal static ValueTask RetainProjectionAsync(ICloudStateTransaction transaction, string syncRootPath,
        string relativePath, CloudItemKind kind, CancellationToken cancellationToken) =>
        RetainProjectionsAsync(transaction, syncRootPath, [(relativePath, kind)], cancellationToken);

    [SupportedOSPlatform("windows10.0.16299")]
    internal static async ValueTask RetainProjectionsAsync(ICloudStateTransaction transaction, string syncRootPath,
        IEnumerable<(string RelativePath, CloudItemKind Kind)> projections, CancellationToken cancellationToken)
    {
        Dictionary<string, CloudDirectoryProvenance> captured = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> ancestors = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string relativePath, CloudItemKind kind) in projections)
        {
            if (kind == CloudItemKind.Directory && relativePath.Length != 0 && !captured.ContainsKey(relativePath))
            {
                CloudDirectoryProvenance? provenance = await CaptureProjectionAsync(transaction, syncRootPath,
                    relativePath, cancellationToken).ConfigureAwait(false);
                if (provenance is null)
                {
                    continue;
                }

                captured.Add(relativePath, provenance);
            }

            AddAncestors(ancestors, relativePath);
        }

        // All item rows must already be written. Capture each new native binding first, then
        // encode each affected directory's complete membership once in this transaction. A
        // newly captured directory can also be another entry's ancestor; do not scan it twice.
        foreach (CloudDirectoryProvenance provenance in captured.Values)
        {
            await RetainMetadataAsync(transaction, provenance, cancellationToken).ConfigureAwait(false);
        }

        ancestors.ExceptWith(captured.Keys);
        await RefreshAncestorsAsync(transaction, ancestors, cancellationToken).ConfigureAwait(false);
    }

    private static void AddAncestors(HashSet<string> ancestors, string relativePath)
    {
        string? parent = Path.GetDirectoryName(relativePath);
        while (!string.IsNullOrEmpty(parent) && ancestors.Add(parent))
        {
            parent = Path.GetDirectoryName(parent);
        }
    }

    private static async ValueTask RefreshAncestorsAsync(ICloudStateTransaction transaction,
        IEnumerable<string> ancestors, CancellationToken cancellationToken)
    {
        // Refresh existing bindings without traversing native subtrees or manufacturing new
        // historical evidence. Shared ancestors use the same final row snapshot once.
        foreach (string parent in ancestors)
        {
            CloudItemState? directory = await transaction.Items.GetByRelativePathAsync(parent, cancellationToken).ConfigureAwait(false);
            if (directory is { Kind: CloudItemKind.Directory, IsTombstone: false })
            {
                CloudStateCheckpoint? checkpoint = await transaction.Checkpoints.GetAsync(BindingName(directory.ItemId), cancellationToken).ConfigureAwait(false);
                if (checkpoint is not null)
                {
                    CloudDirectoryProvenance provenance = Decode(checkpoint.Value);
                    if (provenance.RootItemId == directory.ItemId &&
                        string.Equals(provenance.RelativePath, parent, StringComparison.OrdinalIgnoreCase))
                    {
                        await RefreshMembersAsync(transaction, provenance, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
    }

    [SupportedOSPlatform("windows10.0.16299")]
    private static async ValueTask<CloudDirectoryProvenance?> CaptureProjectionAsync(ICloudStateTransaction transaction,
        string syncRootPath, string relativePath, CancellationToken cancellationToken)
    {
        using SafeFileHandle guard = WindowsFileMetadata.Open(Path.Combine(syncRootPath, relativePath), preventDelete: true);
        DirectoryNativeObservation captured;
        try
        {
            captured = CloudDirectoryMoveEvidence.Capture(guard.DangerousGetHandle(), syncRootPath);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 1 or 50 or 87)
        {
            // Complete file IDs are optional for existing creation APIs. Do not fabricate
            // recovery evidence on storage that cannot report them.
            return null;
        }
        catch (CloudDirectoryEvidenceUnavailableException)
        {
            return null;
        }

        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Decode(captured.Identity);
        CloudItemState? item = await transaction.Items.GetByItemIdAsync(identity.ItemId, cancellationToken).ConfigureAwait(false);
        if (item is null || item.Kind != CloudItemKind.Directory || item.IsTombstone || item.RemoteId != identity.RemoteId ||
            !string.Equals(item.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(captured.ActualPath, Path.Combine(syncRootPath, relativePath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Directory provenance does not match the projected native item.");
        }

        Guid scope = await CloudDirectoryMoveEvidence.GetScopeAsync(transaction, true, cancellationToken).ConfigureAwait(false);
        return new(Guid.NewGuid(), scope, item.ItemId, Path.GetRelativePath(syncRootPath, captured.ActualPath), captured.Binding, captured.Identity);
    }

    internal static async ValueTask ProjectPathsAsync(ICloudStateTransaction transaction, CloudDirectoryMoveProof proof,
        IReadOnlyList<CloudDirectoryMember> members, IReadOnlyList<CloudItemState> current, CancellationToken cancellationToken)
    {
        Dictionary<Guid, CloudDirectoryMember> known = members.ToDictionary(member => member.ItemId);
        foreach (CloudItemState item in current.Where(item => item.Kind == CloudItemKind.Directory && !item.IsTombstone))
        {
            CloudDirectoryProvenance? provenance;
            if (item.ItemId == proof.RootItemId)
            {
                provenance = new(Guid.NewGuid(), proof.StoreScope, item.ItemId, proof.DestinationRelativePath,
                    proof.ExpectedBinding, proof.ExpectedPlaceholderIdentity.ToArray());
            }
            else
            {
                CloudStateCheckpoint? checkpoint = await transaction.Checkpoints.GetAsync(BindingName(item.ItemId), cancellationToken).ConfigureAwait(false);
                if (checkpoint is null)
                {
                    continue;
                }

                provenance = Decode(checkpoint.Value);
                string sourcePath = CloudDirectoryStateProjection.MemberPath(proof.SourceRelativePath, known[item.ItemId].Suffix);
                string targetPath = CloudDirectoryStateProjection.MemberPath(proof.DestinationRelativePath, known[item.ItemId].Suffix);
                if (provenance.RootItemId != item.ItemId || provenance.StoreScope != proof.StoreScope ||
                    (!CloudDirectoryStateProjection.SamePath(provenance.RelativePath, sourcePath) &&
                     !CloudDirectoryStateProjection.SamePath(provenance.RelativePath, targetPath)))
                {
                    throw new CloudDirectoryProjectionConflictException("Historical directory provenance belongs to another namespace location.");
                }

                provenance = provenance with { EvidenceId = Guid.NewGuid(), RelativePath = targetPath };
            }

            await RetainMetadataAsync(transaction, provenance, cancellationToken).ConfigureAwait(false);
        }

        await RefreshMoveAncestorsAsync(transaction, proof.SourceRelativePath, proof.DestinationRelativePath,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask RefreshProjectionAncestorsAsync(ICloudStateTransaction transaction,
        string relativePath, CancellationToken cancellationToken)
    {
        HashSet<string> ancestors = new(StringComparer.OrdinalIgnoreCase);
        AddAncestors(ancestors, relativePath);
        await RefreshAncestorsAsync(transaction, ancestors, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask RefreshMoveAncestorsAsync(ICloudStateTransaction transaction,
        string sourceRelativePath, string destinationRelativePath, CancellationToken cancellationToken)
    {
        // A namespace move also changes membership outside the moved item/subtree. Refresh
        // both ancestor chains from final official rows in the caller's projection transaction,
        // retaining native bindings and every immutable preparation, manifest and receipt.
        HashSet<string> ancestors = new(StringComparer.OrdinalIgnoreCase);
        AddAncestors(ancestors, sourceRelativePath);
        AddAncestors(ancestors, destinationRelativePath);
        await RefreshAncestorsAsync(transaction, ancestors, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask RetainMetadataAsync(ICloudStateTransaction transaction, CloudDirectoryProvenance provenance,
        CancellationToken cancellationToken)
    {
        byte[] binding;
        byte[] manifest;
        try
        {
            // Compute both bounded records before replacing either existing record. Recovery
            // metadata limits must not fail an otherwise valid legacy namespace operation.
            binding = provenance.Encode();
            IReadOnlyList<CloudItemState> members = await transaction.Items.ListSubtreeAsync(provenance.RelativePath, cancellationToken).ConfigureAwait(false);
            manifest = CloudDirectoryMoveEvidence.EncodeMembers(provenance.EvidenceId, provenance.RootItemId, provenance.RelativePath, members);
        }
        catch (CloudDirectoryEvidenceUnavailableException)
        {
            await RemoveUnusableMetadataAsync(transaction, provenance.RootItemId, cancellationToken).ConfigureAwait(false);
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await transaction.Checkpoints.UpsertAsync(new(BindingName(provenance.RootItemId), binding, now), cancellationToken).ConfigureAwait(false);
        await transaction.Checkpoints.UpsertAsync(new(MembersName(provenance.RootItemId), manifest, now), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask RefreshMembersAsync(ICloudStateTransaction transaction, CloudDirectoryProvenance provenance,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CloudItemState> members = await transaction.Items.ListSubtreeAsync(provenance.RelativePath, cancellationToken).ConfigureAwait(false);
        byte[] manifest;
        try
        {
            manifest = CloudDirectoryMoveEvidence.EncodeMembers(provenance.EvidenceId, provenance.RootItemId, provenance.RelativePath, members);
        }
        catch (CloudDirectoryEvidenceUnavailableException)
        {
            await RemoveUnusableMetadataAsync(transaction, provenance.RootItemId, cancellationToken).ConfigureAwait(false);
            return;
        }

        await transaction.Checkpoints.UpsertAsync(new CloudStateCheckpoint(MembersName(provenance.RootItemId), manifest, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask RemoveUnusableMetadataAsync(ICloudStateTransaction transaction, Guid itemId, CancellationToken cancellationToken)
    {
        // Remove only mutable live provenance. Original immutable preparations and receipts,
        // official item rows, and pending journal entries remain in their caller's transaction.
        await transaction.Checkpoints.RemoveAsync(BindingName(itemId), cancellationToken).ConfigureAwait(false);
        await transaction.Checkpoints.RemoveAsync(MembersName(itemId), cancellationToken).ConfigureAwait(false);
    }
}
