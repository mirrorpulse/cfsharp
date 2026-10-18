using System.Security.Cryptography;
using System.Text;

namespace CfSharp;

// The complete sealed inventory is SDK coordination evidence. No provider content or
// business metadata is retained. A preparation is not a receipt for native completion.
internal sealed record RemoteDeleteIntent(
    Guid StoreScope, string RootPath, string BatchId, int EntryIndex, byte[] Fingerprint,
    bool PreserveContent, bool RequirePreservation, bool SuppressEcho, long EchoLifetimeTicks,
    int EchoObservations, IReadOnlyList<RemoteNamespaceObject> Parents, IReadOnlyList<RemoteDeleteMember> Members)
{
    internal const string Prefix = "cfsharp/remote-delete/v1/";
    internal const int MaximumMembers = 4096;
    internal const int MaximumBytes = 8 * 1024 * 1024;

    internal static string Name(string batchId, int index) =>
        Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(batchId))) + "/" +
        index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal string RelativePath => Members[^1].Object.RelativePath;

    internal bool Contains(string path) => CloudDirectoryStateProjection.SamePath(RelativePath, path) ||
        (Members[^1].Kind == CloudItemKind.Directory && path.StartsWith(RelativePath + "\\", StringComparison.OrdinalIgnoreCase));

    internal bool Matches(CloudRemoteChangeBatch batch, int index, CloudRemoteApplyOptions options) =>
        BatchId == batch.BatchId && EntryIndex == index && Fingerprint.AsSpan().SequenceEqual(batch.Fingerprint.Span) &&
        PreserveContent == options.PreserveUnsynchronizedLocalContent && SuppressEcho == options.SuppressLocalEcho &&
        EchoLifetimeTicks == options.EchoSuppressionLifetime.Ticks && EchoObservations == options.EchoSuppressionObservationCount;

    internal byte[] Encode()
    {
        // Presence flags are always encoded, but each later completion adds an Int64.
        // Reserve every missing payload before accepting preparation, so neither receipt
        // can exhaust the evidence budget after its destructive effect has completed.
        int remainingGrowth = Members.Sum(member =>
            (member.DeletedAtTicks.HasValue ? 0 : sizeof(long)) +
            (member.ProjectedAtTicks.HasValue ? 0 : sizeof(long)));
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, CloudDirectoryEvidenceCodec.Utf8, leaveOpen: true);
        writer.Write(1);
        writer.Write(StoreScope.ToByteArray());
        CloudDirectoryEvidenceCodec.WriteText(writer, RootPath);
        CloudDirectoryEvidenceCodec.WriteText(writer, BatchId);
        writer.Write(EntryIndex);
        writer.Write(Fingerprint);
        writer.Write(PreserveContent);
        writer.Write(RequirePreservation);
        writer.Write(SuppressEcho);
        writer.Write(EchoLifetimeTicks);
        writer.Write(EchoObservations);
        writer.Write(Parents.Count);
        foreach (RemoteNamespaceObject parent in Parents)
        {
            RemoteNamespaceEvidence.WriteObject(writer, parent);
        }

        writer.Write(Members.Count);
        foreach (RemoteDeleteMember member in Members)
        {
            writer.Write((int)member.Kind);
            RemoteNamespaceEvidence.WriteObject(writer, member.Object);
            writer.Write(member.Original is not null);
            if (member.Original is CloudItemState original)
            {
                writer.Write(original.ItemId.ToByteArray());
                CloudDirectoryEvidenceCodec.WriteText(writer, original.RemoteId);
                writer.Write(original.RemoteRevision is not null);
                if (original.RemoteRevision is not null)
                {
                    CloudDirectoryEvidenceCodec.WriteText(writer, original.RemoteRevision);
                }

                writer.Write(original.LocalFileId.HasValue);
                if (original.LocalFileId is long localId)
                {
                    writer.Write(localId);
                }

                writer.Write(original.UpdatedAt.UtcTicks);
            }

            writer.Write(member.OperationsFingerprint);
            writer.Write(member.Usn.HasValue);
            if (member.Usn is long usn)
            {
                writer.Write(usn);
            }
            writer.Write(member.DeletedAtTicks.HasValue);
            if (member.DeletedAtTicks is long deleted)
            {
                writer.Write(deleted);
            }

            writer.Write(member.ProjectedAtTicks.HasValue);
            if (member.ProjectedAtTicks is long projected)
            {
                writer.Write(projected);
            }

            if (stream.Length + remainingGrowth > MaximumBytes)
            {
                throw new IOException("The remote Delete inventory exceeds the 8 MiB durable evidence budget.");
            }
        }

        if (stream.Length + remainingGrowth > MaximumBytes)
        {
            throw new IOException("The remote Delete inventory exceeds the 8 MiB durable evidence budget.");
        }

        return stream.ToArray();
    }

    internal static RemoteDeleteIntent Decode(ReadOnlyMemory<byte> value)
    {
        if (value.Length is < 100 or > MaximumBytes)
        {
            throw new InvalidDataException("The remote Delete preparation length is invalid.");
        }

        try
        {
            using MemoryStream stream = new(value.ToArray(), writable: false);
            using BinaryReader reader = new(stream, CloudDirectoryEvidenceCodec.Utf8);
            if (reader.ReadInt32() != 1)
            {
                throw new InvalidDataException("The remote Delete preparation version is unsupported.");
            }

            Guid scope = CloudDirectoryEvidenceCodec.ReadGuid(reader);
            string root = CloudDirectoryEvidenceCodec.ReadText(reader);
            string batch = CloudDirectoryEvidenceCodec.ReadText(reader);
            int index = reader.ReadInt32();
            byte[] fingerprint = reader.ReadBytes(32);
            bool preserve = reader.ReadBoolean();
            bool require = reader.ReadBoolean();
            bool echo = reader.ReadBoolean();
            long lifetime = reader.ReadInt64();
            int observations = reader.ReadInt32();
            int parentCount = reader.ReadInt32();
            if (parentCount is < 1 or > 1024)
            {
                throw new InvalidDataException("The remote Delete parent count is invalid.");
            }

            List<RemoteNamespaceObject> parents = new(parentCount);
            for (int parent = 0; parent < parentCount; parent++)
            {
                parents.Add(RemoteNamespaceEvidence.ReadObject(reader));
            }

            int count = reader.ReadInt32();
            if (count is < 1 or > MaximumMembers)
            {
                throw new InvalidDataException("The remote Delete inventory count is invalid.");
            }

            List<RemoteDeleteMember> members = new(count);
            for (int memberIndex = 0; memberIndex < count; memberIndex++)
            {
                CloudItemKind kind = (CloudItemKind)reader.ReadInt32();
                RemoteNamespaceObject item = RemoteNamespaceEvidence.ReadObject(reader);
                CloudItemState? original = null;
                if (reader.ReadBoolean())
                {
                    Guid id = CloudDirectoryEvidenceCodec.ReadGuid(reader);
                    string remote = CloudDirectoryEvidenceCodec.ReadText(reader);
                    string? revision = reader.ReadBoolean() ? CloudDirectoryEvidenceCodec.ReadText(reader) : null;
                    long? localId = reader.ReadBoolean() ? reader.ReadInt64() : null;
                    original = new(id, remote, item.RelativePath, kind, revision, localId, false,
                        new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero));
                }

                byte[] operations = reader.ReadBytes(32);
                long? usn = reader.ReadBoolean() ? reader.ReadInt64() : null;
                long? deleted = reader.ReadBoolean() ? reader.ReadInt64() : null;
                long? projected = reader.ReadBoolean() ? reader.ReadInt64() : null;
                if (!Enum.IsDefined(kind) || item.RelativePath.Length == 0 || operations.Length != 32 ||
                    (projected.HasValue && !deleted.HasValue))
                {
                    throw new InvalidDataException("The remote Delete member is malformed.");
                }

                members.Add(new(kind, item, original, operations, deleted, projected, usn));
            }

            if (stream.Position != stream.Length || scope == Guid.Empty || !Path.IsPathFullyQualified(root) ||
                string.IsNullOrWhiteSpace(batch) || index < 0 || fingerprint.Length != 32 || lifetime <= 0 || observations <= 0 ||
                members[^1].Original is null || members.Select(member => member.Object.RelativePath)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != count)
            {
                throw new InvalidDataException("The remote Delete preparation is malformed.");
            }

            RemoteDeleteIntent intent = new(scope, root, batch, index, fingerprint, preserve, require, echo,
                lifetime, observations, parents.AsReadOnly(), members.AsReadOnly());
            if (members.Any(member => !intent.Contains(member.Object.RelativePath)))
            {
                throw new InvalidDataException("The remote Delete inventory escapes its original scope.");
            }

            return intent;
        }
        catch (Exception exception) when (exception is ArgumentException or EndOfStreamException or DecoderFallbackException)
        {
            throw new InvalidDataException("The remote Delete preparation is malformed.", exception);
        }
    }
}

internal sealed record RemoteDeleteMember(CloudItemKind Kind, RemoteNamespaceObject Object,
    CloudItemState? Original, byte[] OperationsFingerprint, long? DeletedAtTicks, long? ProjectedAtTicks, long? Usn = null);
