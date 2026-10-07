using System.Runtime.Versioning;
using System.Text;
using CfSharp.Native;

namespace CfSharp;

// Evidence belongs to CfSharp, not to provider payloads. Keep each preparation independent:
// replacing a later binding must never rewrite an earlier proof or its historical membership.
internal static class CloudDirectoryMoveEvidence
{
    internal const string ScopeName = "cfsharp/namespace/scope";
    internal static string ProofName(Guid id) => $"cfsharp/namespace/preparations/{id:N}/proof";
    internal static string MembersName(Guid id) => $"cfsharp/namespace/preparations/{id:N}/members";

    internal static async ValueTask<Guid> GetScopeAsync(ICloudStateTransaction transaction,
        bool create, CancellationToken cancellationToken)
    {
        CloudStateCheckpoint? checkpoint = await transaction.Checkpoints.GetAsync(ScopeName, cancellationToken).ConfigureAwait(false);
        if (checkpoint is not null)
        {
            if (checkpoint.Value.Length != 20)
            {
                throw new InvalidDataException("The durable directory scope is malformed.");
            }

            using BinaryReader reader = new(new MemoryStream(checkpoint.Value.ToArray(), writable: false));
            if (reader.ReadInt32() != 1)
            {
                throw new NotSupportedException("The durable directory scope version is unsupported.");
            }

            Guid scope = CloudDirectoryEvidenceCodec.ReadGuid(reader);
            return scope != Guid.Empty ? scope : throw new InvalidDataException("The durable directory scope is empty.");
        }

        if (!create)
        {
            return Guid.Empty;
        }

        Guid created = Guid.NewGuid();
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write(1);
        writer.Write(created.ToByteArray());
        await transaction.Checkpoints.UpsertAsync(new CloudStateCheckpoint(ScopeName,
            stream.ToArray(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        return created;
    }

    [SupportedOSPlatform("windows10.0.16299")]
    internal static DirectoryNativeObservation Capture(nint handle, string syncRootPath)
    {
        NativeFileMetadata metadata = WindowsFileMetadata.Read(handle);
        if (!metadata.Directory || metadata.DeletePending || metadata.PlaceholderState == CfPlaceholderState.Invalid ||
            !metadata.PlaceholderState.HasFlag(CfPlaceholderState.Placeholder))
        {
            throw new InvalidOperationException($"Directory recovery requires a live managed placeholder directory (directory={metadata.Directory}, deletePending={metadata.DeletePending}, state={metadata.PlaceholderState}).");
        }

        _ = CloudPlaceholderIdentity.Decode(metadata.PlaceholderIdentity);
        return new DirectoryNativeObservation(CloudLocalFileBindingPlatform.Read(handle, syncRootPath),
            metadata.PlaceholderIdentity, WindowsFileMetadata.ReadFinalPath(handle));
    }

    internal static async ValueTask PrepareAsync(ICloudStateTransaction transaction, CloudDirectoryMoveProof proof,
        IReadOnlyList<CloudItemState> members, CancellationToken cancellationToken)
    {
        if (await transaction.Checkpoints.GetAsync(ProofName(proof.ProofId), cancellationToken).ConfigureAwait(false) is not null ||
            await transaction.Checkpoints.GetAsync(MembersName(proof.ProofId), cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new InvalidOperationException("A directory preparation is immutable.");
        }

        byte[] manifest = EncodeMembers(proof, members);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await transaction.Checkpoints.UpsertAsync(new CloudStateCheckpoint(ProofName(proof.ProofId), proof.Encode(), now),
            cancellationToken).ConfigureAwait(false);
        await transaction.Checkpoints.UpsertAsync(new CloudStateCheckpoint(MembersName(proof.ProofId), manifest, now),
            cancellationToken).ConfigureAwait(false);
    }

    internal static byte[] EncodeMembers(CloudDirectoryMoveProof proof, IReadOnlyList<CloudItemState> members)
        => EncodeMembers(proof.ProofId, proof.RootItemId, proof.SourceRelativePath, members);

    internal static byte[] EncodeMembers(Guid evidenceId, Guid rootItemId, string sourcePath, IReadOnlyList<CloudItemState> members)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(1);
        writer.Write(evidenceId.ToByteArray());
        writer.Write(members.Count);
        HashSet<Guid> ids = [];
        HashSet<string> suffixes = new(StringComparer.OrdinalIgnoreCase);
        bool rootPresent = false;
        foreach (CloudItemState item in members)
        {
            string suffix = CloudDirectoryStateProjection.MapPath(item.RelativePath, sourcePath, string.Empty);
            if (!ids.Add(item.ItemId) || !suffixes.Add(suffix) ||
                (suffix.Length == 0 && (item.ItemId != rootItemId || item.Kind != CloudItemKind.Directory || item.IsTombstone)))
            {
                throw new InvalidDataException("The prepared subtree has ambiguous membership.");
            }

            rootPresent |= suffix.Length == 0;
            writer.Write(item.ItemId.ToByteArray());
            writer.Write((int)item.Kind);
            CloudDirectoryEvidenceCodec.WriteText(writer, suffix.Replace('/', '\\'));
            if (stream.Length > 64 * 1024 * 1024)
            {
                throw new NotSupportedException("The prepared subtree exceeds the recovery metadata limit.");
            }
        }

        if (!rootPresent)
        {
            throw new InvalidOperationException("The prepared subtree must contain its live managed directory root.");
        }

        return stream.ToArray();
    }
}

internal sealed record DirectoryNativeObservation(CloudLocalFileBinding Binding, byte[] Identity, string ActualPath)
{
    internal bool Matches(DirectoryNativeObservation other) => Binding == other.Binding &&
        Identity.AsSpan().SequenceEqual(other.Identity) && string.Equals(ActualPath, other.ActualPath, StringComparison.Ordinal);
}
