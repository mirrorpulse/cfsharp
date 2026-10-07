using System.Runtime.Versioning;
using System.Security.Cryptography;
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
    internal static string ReceiptName(Guid id) => $"cfsharp/namespace/preparations/{id:N}/receipt";
    internal static string IntentName(string source, string target) => "cfsharp/namespace/intents/" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            CloudRemotePathValidation.Canonicalize(source, nameof(source)).Replace('/', '\\').ToUpperInvariant() + "\0" +
            CloudRemotePathValidation.Canonicalize(target, nameof(target)).Replace('/', '\\'))));

    internal static async ValueTask<CloudDirectoryMoveProof?> ReadIntentAsync(ICloudStateStore store,
        string source, string target, CancellationToken cancellationToken)
    {
        await using ICloudStateTransaction transaction = await store.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        CloudDirectoryMoveProof? proof = await GetOrPrepareRetainedIntentAsync(transaction, source, target, cancellationToken).ConfigureAwait(false);
        if (proof is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return proof;
    }

    internal static async ValueTask<CloudDirectoryMoveProof?> GetOrPrepareRetainedIntentAsync(ICloudStateTransaction transaction,
        string source, string target, CancellationToken cancellationToken)
    {
        CloudStateCheckpoint? checkpoint = await transaction.Checkpoints.GetAsync(IntentName(source, target), cancellationToken).ConfigureAwait(false);
        if (checkpoint is not null)
        {
            CloudDirectoryMoveProof indexed = CloudDirectoryMoveProof.Decode(checkpoint.Value.Span);
            return CloudDirectoryStateProjection.SamePath(source, indexed.SourceRelativePath) && target == indexed.DestinationRelativePath
                ? indexed : throw new InvalidDataException("The directory intent index identifies another move.");
        }

        Guid scope = await GetScopeAsync(transaction, false, cancellationToken).ConfigureAwait(false);
        if (scope == Guid.Empty)
        {
            return null;
        }

        CloudItemState? from = await transaction.Items.GetByRelativePathAsync(source, cancellationToken).ConfigureAwait(false);
        CloudItemState? to = await transaction.Items.GetByRelativePathAsync(target, cancellationToken).ConfigureAwait(false);
        foreach (CloudItemState item in new[] { from, to }.OfType<CloudItemState>().DistinctBy(item => item.ItemId))
        {
            if (item.Kind != CloudItemKind.Directory || item.IsTombstone)
            {
                continue;
            }

            CloudStateCheckpoint? retained = await transaction.Checkpoints.GetAsync(CloudDirectoryProvenance.BindingName(item.ItemId), cancellationToken).ConfigureAwait(false);
            if (retained is null)
            {
                continue;
            }

            CloudDirectoryProvenance provenance = CloudDirectoryProvenance.Decode(retained.Value);
            if (provenance.StoreScope != scope || provenance.RootItemId != item.ItemId ||
                !CloudDirectoryStateProjection.SamePath(source, provenance.RelativePath))
            {
                continue;
            }

            CloudStateCheckpoint? manifest = await transaction.Checkpoints.GetAsync(CloudDirectoryProvenance.MembersName(item.ItemId), cancellationToken).ConfigureAwait(false);
            if (manifest is null)
            {
                throw new InvalidDataException("Retained directory provenance has no historical membership.");
            }

            _ = DecodeMembers(manifest.Value, provenance.EvidenceId, provenance.RootItemId);
            CloudDirectoryMoveProof prepared = new(Guid.NewGuid(), scope, provenance.RelativePath, target,
                provenance.RootItemId, provenance.Binding, provenance.Identity);
            // Promote library-captured historical evidence, never a binding read from today's
            // target. Keep the private suffixes unchanged; only rebind the manifest owner GUID
            // to this independent preparation (the same opaque 16-byte Guid encoding).
            byte[] members = manifest.Value.ToArray();
            prepared.ProofId.TryWriteBytes(members.AsSpan(4, 16));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (await transaction.Checkpoints.GetAsync(ProofName(prepared.ProofId), cancellationToken).ConfigureAwait(false) is not null ||
                await transaction.Checkpoints.GetAsync(MembersName(prepared.ProofId), cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new InvalidOperationException("A directory preparation is immutable.");
            }

            await transaction.Checkpoints.UpsertAsync(new(ProofName(prepared.ProofId), prepared.Encode(), now), cancellationToken).ConfigureAwait(false);
            await transaction.Checkpoints.UpsertAsync(new(MembersName(prepared.ProofId), members, now), cancellationToken).ConfigureAwait(false);
            await transaction.Checkpoints.UpsertAsync(new(IntentName(source, target), prepared.Encode(), now), cancellationToken).ConfigureAwait(false);
            return prepared;
        }

        return null;
    }

    internal static async ValueTask<IReadOnlyList<CloudDirectoryMember>?> AuthenticateAsync(ICloudStateTransaction transaction,
        CloudDirectoryMoveProof proof, CancellationToken cancellationToken)
    {
        if (await GetScopeAsync(transaction, false, cancellationToken).ConfigureAwait(false) != proof.StoreScope)
        {
            return null;
        }

        CloudStateCheckpoint? original = await transaction.Checkpoints.GetAsync(ProofName(proof.ProofId), cancellationToken).ConfigureAwait(false);
        if (original is null || !original.Value.Span.SequenceEqual(proof.Encode()))
        {
            return null;
        }

        CloudStateCheckpoint? members = await transaction.Checkpoints.GetAsync(MembersName(proof.ProofId), cancellationToken).ConfigureAwait(false);
        return members is not null ? DecodeMembers(members.Value, proof.ProofId, proof.RootItemId) :
            throw new InvalidDataException("The immutable directory preparation has no membership record.");
    }

    internal static IReadOnlyList<CloudDirectoryMember> DecodeMembers(ReadOnlyMemory<byte> value, Guid evidenceId, Guid rootItemId)
    {
        if (value.Length is < 48 or > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("The directory membership length is invalid.");
        }

        try
        {
            using MemoryStream stream = new(value.ToArray(), writable: false);
            using BinaryReader reader = new(stream);
            if (reader.ReadInt32() != 1)
            {
                throw new NotSupportedException("The directory membership version is unsupported.");
            }

            if (CloudDirectoryEvidenceCodec.ReadGuid(reader) != evidenceId)
            {
                throw new InvalidDataException("The directory membership belongs to another preparation.");
            }

            int count = reader.ReadInt32();
            if (count <= 0 || count > (value.Length - 24) / 24)
            {
                throw new InvalidDataException("The directory membership count is invalid.");
            }

            List<CloudDirectoryMember> members = new(count);
            HashSet<Guid> ids = [];
            HashSet<string> suffixes = new(StringComparer.OrdinalIgnoreCase);
            bool hasRoot = false;
            for (int index = 0; index < count; index++)
            {
                Guid id = CloudDirectoryEvidenceCodec.ReadGuid(reader);
                CloudItemKind kind = (CloudItemKind)reader.ReadInt32();
                string suffix = CloudDirectoryEvidenceCodec.ReadText(reader);
                if (suffix.Length != 0 && CloudDirectoryEvidenceCodec.CanonicalPath(suffix) != suffix)
                {
                    throw new InvalidDataException("The directory membership suffix is not canonical.");
                }

                if (id == Guid.Empty || !Enum.IsDefined(kind) || !ids.Add(id) || !suffixes.Add(suffix) ||
                    (suffix.Length == 0 && (id != rootItemId || kind != CloudItemKind.Directory)))
                {
                    throw new InvalidDataException("The directory membership is ambiguous.");
                }

                hasRoot |= suffix.Length == 0;
                members.Add(new(id, kind, suffix));
            }

            if (!hasRoot || stream.Position != stream.Length)
            {
                throw new InvalidDataException("The directory membership is incomplete.");
            }

            return members.AsReadOnly();
        }
        catch (Exception exception) when (exception is ArgumentException or EndOfStreamException)
        {
            throw new InvalidDataException("The directory membership is malformed.", exception);
        }
    }

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
        NativeFileMetadata metadata;
        try
        {
            metadata = WindowsFileMetadata.Read(handle);
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode is 1 or 50 or 87)
        {
            throw new CloudDirectoryEvidenceUnavailableException("Storage cannot supply complete directory metadata.", exception);
        }
        if (!metadata.Directory || metadata.DeletePending || metadata.PlaceholderState == CfPlaceholderState.Invalid ||
            !metadata.PlaceholderState.HasFlag(CfPlaceholderState.Placeholder))
        {
            throw new InvalidOperationException($"Directory recovery requires a live managed placeholder directory (directory={metadata.Directory}, deletePending={metadata.DeletePending}, state={metadata.PlaceholderState}).");
        }

        _ = CloudPlaceholderIdentity.Decode(metadata.PlaceholderIdentity);
        CloudLocalFileBinding binding;
        try
        {
            binding = CloudLocalFileBindingPlatform.Read(handle, syncRootPath);
        }
        catch (NotSupportedException exception)
        {
            throw new CloudDirectoryEvidenceUnavailableException("Storage cannot supply a complete directory binding.", exception);
        }

        return new DirectoryNativeObservation(binding, metadata.PlaceholderIdentity, WindowsFileMetadata.ReadFinalPath(handle));
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
                throw new CloudDirectoryEvidenceUnavailableException("The prepared subtree exceeds the recovery metadata limit.");
            }
        }

        if (!rootPresent)
        {
            throw new InvalidOperationException("The prepared subtree must contain its live managed directory root.");
        }

        return stream.ToArray();
    }
}

internal sealed record CloudDirectoryMember(Guid ItemId, CloudItemKind Kind, string Suffix);

internal sealed class CloudDirectoryEvidenceUnavailableException(string message, Exception? innerException = null)
    : NotSupportedException(message, innerException);

internal sealed record DirectoryNativeObservation(CloudLocalFileBinding Binding, byte[] Identity, string ActualPath)
{
    internal bool Matches(DirectoryNativeObservation other) => Binding == other.Binding &&
        Identity.AsSpan().SequenceEqual(other.Identity) && string.Equals(ActualPath, other.ActualPath, StringComparison.Ordinal);
}
