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
    internal static async ValueTask RetainProjectionAsync(ICloudStateTransaction transaction, string syncRootPath,
        string relativePath, CloudItemKind kind, CancellationToken cancellationToken)
    {
        if (kind == CloudItemKind.Directory && relativePath.Length != 0)
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
                return;
            }
            catch (CloudDirectoryEvidenceUnavailableException)
            {
                return;
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
            CloudDirectoryProvenance provenance = new(Guid.NewGuid(), scope, item.ItemId,
                Path.GetRelativePath(syncRootPath, captured.ActualPath), captured.Binding, captured.Identity);
            await transaction.Checkpoints.UpsertAsync(new CloudStateCheckpoint(BindingName(item.ItemId), provenance.Encode(), DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
            await RefreshMembersAsync(transaction, provenance, cancellationToken).ConfigureAwait(false);
        }

        // Refresh known membership of already captured ancestors using official transactional
        // rows, without traversing native subtrees or manufacturing a new historical binding.
        string? parent = Path.GetDirectoryName(relativePath);
        while (!string.IsNullOrEmpty(parent))
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

            parent = Path.GetDirectoryName(parent);
        }
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

            await transaction.Checkpoints.UpsertAsync(new CloudStateCheckpoint(BindingName(item.ItemId), provenance.Encode(), DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
            await RefreshMembersAsync(transaction, provenance, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask RefreshMembersAsync(ICloudStateTransaction transaction, CloudDirectoryProvenance provenance,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CloudItemState> members = await transaction.Items.ListSubtreeAsync(provenance.RelativePath, cancellationToken).ConfigureAwait(false);
        await transaction.Checkpoints.UpsertAsync(new CloudStateCheckpoint(MembersName(provenance.RootItemId),
            CloudDirectoryMoveEvidence.EncodeMembers(provenance.EvidenceId, provenance.RootItemId, provenance.RelativePath, members), DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
    }
}
