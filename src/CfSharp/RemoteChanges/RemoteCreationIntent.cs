using System.Security.Cryptography;
using System.Text;

namespace CfSharp;

internal sealed record RemoteCreationIntent(
    string RelativePath, CloudItemKind Kind, CloudPlaceholderIdentity Identity, byte[] Fingerprint, bool Committed,
    Guid StoreScope = default, string? RootPath = null, IReadOnlyList<RemoteNamespaceObject>? Parents = null,
    RemoteNamespaceObject? CreatedObject = null, bool PreserveContent = true,
    CloudRemoteConflictDecision? Resolution = null)
{
    internal const string Prefix = "cfsharp/remote-creation/v1";
    internal const string ObservationsPrefix = "cfsharp/remote-creation-observations/v1";
    private static ReadOnlySpan<byte> EchoMarker => "cfsharp-create/v1:"u8;

    internal static string Name(CloudRemoteChange change) => Prefix + "/" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(change.RemoteId + "\0" + change.ChangeId)));

    internal static string IdentityConflictName(string batchId, int index) => "cfsharp/remote-identity-conflict/v1/" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(batchId))) + "/" +
        index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static byte[] GetFingerprint(CloudRemoteChange change) =>
        new CloudRemoteChangeBatch("creation", Array.Empty<byte>(), [change], Array.Empty<byte>()).Fingerprint.ToArray();

    internal CloudRemoteChange EffectiveChange(CloudRemoteChange original) => Resolution is null ? original :
        new(Resolution == CloudRemoteConflictDecision.KeepBoth ? original.ChangeId + ":keep-both" : original.ChangeId,
            original.Kind, Identity.RemoteId, original.RemoteRevision, original.ItemKind, RelativePath,
            Resolution == CloudRemoteConflictDecision.KeepBoth ? Identity.ItemId : original.ItemId,
            previousRemoteRevision: null,
            previousRelativePath: Resolution == CloudRemoteConflictDecision.KeepBoth ? null : original.PreviousRelativePath,
            original.Length, original.Metadata, original.CursorAfter);

    internal byte[] Encode()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, CloudDirectoryEvidenceCodec.Utf8, leaveOpen: true);
        writer.Write(Parents is null ? 1 : 3);
        writer.Write(RelativePath);
        writer.Write((int)Kind);
        writer.Write(Committed);
        writer.Write(Fingerprint.Length);
        writer.Write(Fingerprint);
        byte[] identity = Identity.Encode();
        writer.Write(identity.Length);
        writer.Write(identity);
        if (Parents is not null)
        {
            writer.Write(StoreScope.ToByteArray());
            writer.Write(RootPath!);
            writer.Write(PreserveContent);
            writer.Write(Parents.Count);
            foreach (RemoteNamespaceObject parent in Parents) { RemoteNamespaceEvidence.WriteObject(writer, parent); }
            writer.Write(CreatedObject is not null);
            if (CreatedObject is not null) { RemoteNamespaceEvidence.WriteObject(writer, CreatedObject); }
            writer.Write(Resolution is null ? -1 : (int)Resolution.Value);
        }
        long preparedLength = stream.Length;
        if (Parents is not null && CreatedObject is null)
        {
            if (Parents.Count is < 1 or > 1024) { throw new InvalidDataException("Invalid creation parent count."); }
            // Reserve the entire future native receipt before an irreversible creation. Native
            // bindings/timestamps have fixed widths; guarded parents supply their actual spelling.
            // This sizing object is discarded and never becomes durable ownership evidence.
            string parent = Parents.MaxBy(value => value.RelativePath.Length)!.RelativePath;
            RemoteNamespaceEvidence.WriteObject(writer, new(Path.Combine(parent, Path.GetFileName(RelativePath)),
                Parents[0].Binding, identity, 0, 0, 0));
        }
        if (stream.Length > 4 * 1024 * 1024) { throw new InvalidDataException("The creation evidence exceeds its bounded size including native receipt capacity."); }
        stream.SetLength(preparedLength);
        return stream.ToArray();
    }

    internal static RemoteCreationIntent Decode(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            if (bytes.Length is < 20 or > 4 * 1024 * 1024)
            {
                throw new InvalidDataException("Invalid creation evidence length.");
            }
            using MemoryStream stream = new(bytes.ToArray(), writable: false);
            using BinaryReader reader = new(stream, CloudDirectoryEvidenceCodec.Utf8);
            int version = reader.ReadInt32();
            if (version is not 1 and not 2 and not 3)
            {
                throw new InvalidDataException("Unsupported remote creation intent version.");
            }

            string path = CloudRemotePathValidation.Canonicalize(reader.ReadString(), "path");
            CloudItemKind kind = CloudStateModelValidation.RequireDefined((CloudItemKind)reader.ReadInt32(), "kind");
            bool committed = reader.ReadBoolean();
            int fingerprintLength = reader.ReadInt32();
            if (fingerprintLength != 32)
            {
                throw new InvalidDataException("Invalid creation fingerprint length.");
            }

            byte[] fingerprint = reader.ReadBytes(fingerprintLength);
            int identityLength = reader.ReadInt32();
            if (identityLength <= 0 || identityLength > 4096 || identityLength > stream.Length - stream.Position)
            {
                throw new InvalidDataException("Invalid creation identity length.");
            }

            CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Decode(reader.ReadBytes(identityLength));
            Guid scope = default;
            string? root = null;
            RemoteNamespaceObject[]? parents = null;
            RemoteNamespaceObject? created = null;
            bool preserve = true;
            CloudRemoteConflictDecision? resolution = null;
            if (version >= 2)
            {
                scope = CloudDirectoryEvidenceCodec.ReadGuid(reader);
                root = reader.ReadString();
                preserve = reader.ReadBoolean();
                int count = reader.ReadInt32();
                if (scope == Guid.Empty || !Path.IsPathFullyQualified(root) || count is < 1 or > 1024)
                {
                    throw new InvalidDataException("Invalid creation ancestry evidence.");
                }
                parents = new RemoteNamespaceObject[count];
                for (int index = 0; index < count; index++) { parents[index] = RemoteNamespaceEvidence.ReadObject(reader); }
                created = reader.ReadBoolean() ? RemoteNamespaceEvidence.ReadObject(reader) : null;
                if ((committed && created is null) || (created is not null &&
                    (created.RelativePath.Length == 0 || !created.Identity.AsSpan().SequenceEqual(identity.Encode()))) ||
                    parents.Select(parent => parent.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != count)
                {
                    throw new InvalidDataException("Invalid original creation object evidence.");
                }
            }
            if (version == 3)
            {
                int decision = reader.ReadInt32();
                if (decision != -1 && decision != (int)CloudRemoteConflictDecision.KeepRemote && decision != (int)CloudRemoteConflictDecision.KeepBoth)
                {
                    throw new InvalidDataException("Invalid durable creation authorization.");
                }
                resolution = decision == -1 ? null : (CloudRemoteConflictDecision)decision;
                if (resolution.HasValue && preserve) { throw new InvalidDataException("A creation authorization has inconsistent preservation policy."); }
            }
            if (stream.Position != stream.Length || fingerprint.Length != 32)
            {
                throw new InvalidDataException("Invalid creation evidence boundary.");
            }
            return new(path, kind, identity, fingerprint, committed, scope, root, parents, created, preserve, resolution);
        }
        catch (Exception exception) when (exception is EndOfStreamException or ArgumentException or NotSupportedException or FormatException)
        {
            throw new InvalidDataException("The remote creation intent is corrupt.", exception);
        }
    }

    internal static byte[] EchoPayload(CloudPlaceholderIdentity identity) =>
        [.. EchoMarker, .. identity.Encode()];

    internal static bool IsCreationEcho(CloudEchoSuppressionState suppression) =>
        suppression.Payload.Span.StartsWith(EchoMarker);

    internal static CloudPlaceholderIdentity EchoIdentity(CloudEchoSuppressionState suppression) =>
        CloudPlaceholderIdentity.Decode(suppression.Payload.Span[EchoMarker.Length..]);
}
