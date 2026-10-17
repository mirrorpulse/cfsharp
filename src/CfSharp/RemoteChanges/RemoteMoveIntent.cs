using System.Security.Cryptography;
using System.Text;

namespace CfSharp;

// This is SDK coordination state, not provider data. The key identifies the original batch
// entry; the batch fingerprint also binds both paths, opaque revisions, IDs, and cursor envelope.
internal sealed record RemoteMoveIntent(
    Guid StoreScope, string RootPath, string BatchId, int EntryIndex, byte[] Fingerprint, string TargetRelativePath,
    bool PreserveContent, bool SuppressEcho, long EchoLifetimeTicks, int EchoObservations,
    CloudItemState Original, RemoteMoveObject Object, IReadOnlyList<RemoteMoveObject> Parents,
    long? ProjectedAtTicks, bool RequireInSync, byte[] LocalOperationsFingerprint, long? ProjectedUsn)
{
    internal const string Prefix = "cfsharp/remote-move/v1/";

    internal static string Name(string batchId, int index) => Prefix +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(batchId))) + "/" +
        index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal bool Matches(CloudRemoteChangeBatch batch, int index, CloudRemoteApplyOptions options) =>
        BatchId == batch.BatchId && EntryIndex == index && Fingerprint.AsSpan().SequenceEqual(batch.Fingerprint.Span) &&
        PreserveContent == options.PreserveUnsynchronizedLocalContent && SuppressEcho == options.SuppressLocalEcho &&
        EchoLifetimeTicks == options.EchoSuppressionLifetime.Ticks && EchoObservations == options.EchoSuppressionObservationCount;

    internal byte[] Encode()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, CloudDirectoryEvidenceCodec.Utf8, leaveOpen: true);
        writer.Write(1);
        writer.Write(StoreScope.ToByteArray());
        WriteText(writer, RootPath);
        WriteText(writer, BatchId);
        writer.Write(EntryIndex);
        writer.Write(Fingerprint);
        WriteText(writer, TargetRelativePath);
        writer.Write(RequireInSync);
        writer.Write(LocalOperationsFingerprint);
        writer.Write(PreserveContent);
        writer.Write(SuppressEcho);
        writer.Write(EchoLifetimeTicks);
        writer.Write(EchoObservations);
        writer.Write(Original.ItemId.ToByteArray());
        WriteText(writer, Original.RemoteId);
        WriteText(writer, Original.RelativePath);
        writer.Write(Original.RemoteRevision is not null);
        if (Original.RemoteRevision is not null)
        {
            WriteText(writer, Original.RemoteRevision);
        }

        writer.Write(Original.LocalFileId.HasValue);
        if (Original.LocalFileId is long localId)
        {
            writer.Write(localId);
        }

        writer.Write(Original.UpdatedAt.UtcTicks);
        WriteObject(writer, Object);
        writer.Write(Parents.Count);
        foreach (RemoteMoveObject parent in Parents)
        {
            WriteObject(writer, parent);
        }

        writer.Write(ProjectedAtTicks.HasValue);
        if (ProjectedAtTicks is long projected)
        {
            writer.Write(projected);
            writer.Write(ProjectedUsn!.Value);
        }

        return stream.ToArray();
    }

    internal static RemoteMoveIntent Decode(ReadOnlyMemory<byte> value)
    {
        if (value.Length is < 200 or > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("The remote move preparation length is invalid.");
        }

        try
        {
            using MemoryStream stream = new(value.ToArray(), writable: false);
            using BinaryReader reader = new(stream, CloudDirectoryEvidenceCodec.Utf8);
            if (reader.ReadInt32() != 1)
            {
                throw new InvalidDataException("The remote move preparation version is unsupported.");
            }

            Guid scope = CloudDirectoryEvidenceCodec.ReadGuid(reader);
            string root = ReadText(reader);
            string batch = ReadText(reader);
            int index = reader.ReadInt32();
            byte[] fingerprint = reader.ReadBytes(32);
            string target = CloudRemotePathValidation.Canonicalize(ReadText(reader), "target");
            bool requireInSync = reader.ReadBoolean();
            byte[] operations = reader.ReadBytes(32);
            bool preserve = reader.ReadBoolean();
            bool echo = reader.ReadBoolean();
            long lifetime = reader.ReadInt64();
            int observations = reader.ReadInt32();
            Guid id = CloudDirectoryEvidenceCodec.ReadGuid(reader);
            string remote = ReadText(reader);
            string path = ReadText(reader);
            string? revision = reader.ReadBoolean() ? ReadText(reader) : null;
            long? localId = reader.ReadBoolean() ? reader.ReadInt64() : null;
            CloudItemState original = new(id, remote, path, CloudItemKind.File, revision, localId, false,
                new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero));
            RemoteMoveObject item = ReadObject(reader);
            int count = reader.ReadInt32();
            if (count is < 1 or > 1024)
            {
                throw new InvalidDataException("The remote move parent count is invalid.");
            }

            List<RemoteMoveObject> parents = new(count);
            for (int parent = 0; parent < count; parent++)
            {
                parents.Add(ReadObject(reader));
            }

            long? projected = reader.ReadBoolean() ? reader.ReadInt64() : null;
            long? projectedUsn = projected.HasValue ? reader.ReadInt64() : null;
            if (stream.Position != stream.Length || scope == Guid.Empty || !Path.IsPathFullyQualified(root) ||
                string.IsNullOrWhiteSpace(batch) || index < 0 || lifetime <= 0 || observations <= 0 ||
                fingerprint.Length != 32 || item.Identity.Length == 0 ||
                CloudPlaceholderIdentity.Decode(item.Identity).ItemId != id ||
                parents.Select(parent => parent.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != count)
            {
                throw new InvalidDataException("The remote move preparation is malformed.");
            }

            return new(scope, root, batch, index, fingerprint, target, preserve, echo, lifetime, observations,
                original, item, parents.AsReadOnly(), projected, requireInSync, operations, projectedUsn);
        }
        catch (Exception exception) when (exception is ArgumentException or EndOfStreamException or DecoderFallbackException)
        {
            throw new InvalidDataException("The remote move preparation is malformed.", exception);
        }
    }

    private static void WriteObject(BinaryWriter writer, RemoteMoveObject item)
    {
        WriteText(writer, item.RelativePath);
        writer.Write(item.Binding.VolumeSerialNumber);
        writer.Write(item.Binding.SyncRootFileId.ToByteArray());
        writer.Write(item.Binding.LocalFileId.ToByteArray());
        writer.Write(item.Identity.Length);
        writer.Write(item.Identity);
        writer.Write(item.CreationTime);
        writer.Write(item.LastWriteTime);
        writer.Write(item.Length);
    }

    private static RemoteMoveObject ReadObject(BinaryReader reader)
    {
        string path = ReadText(reader);
        if (path.Length != 0)
        {
            path = CloudRemotePathValidation.Canonicalize(path, "path");
        }

        CloudLocalFileBinding binding = new(reader.ReadUInt64(), CloudDirectoryEvidenceCodec.ReadGuid(reader),
            CloudDirectoryEvidenceCodec.ReadGuid(reader));
        byte[] identity = CloudDirectoryEvidenceCodec.ReadBytes(reader, SyncRootRegistrationOptions.MaxFileIdentityLength);
        return new(path, binding, identity, reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64());
    }

    private static void WriteText(BinaryWriter writer, string value) => CloudDirectoryEvidenceCodec.WriteText(writer, value);
    private static string ReadText(BinaryReader reader) => CloudDirectoryEvidenceCodec.ReadText(reader);

    internal static byte[] FingerprintOperations(IReadOnlyList<CloudOperationJournalEntry> operations)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        foreach (CloudOperationJournalEntry operation in operations.OrderBy(operation => operation.OperationId))
        {
            writer.Write(operation.OperationId.ToByteArray());
            writer.Write((int)operation.Kind);
            writer.Write(operation.Sequence);
            writer.Write(operation.CreatedAt.UtcTicks);
            writer.Write(operation.Payload.Length);
            writer.Write(operation.Payload.Span);
        }

        return SHA256.HashData(stream.ToArray());
    }
}

internal sealed record RemoteMoveObject(string RelativePath, CloudLocalFileBinding Binding, byte[] Identity,
    long CreationTime, long LastWriteTime, long Length);

internal sealed record RemoteMoveRequest(CloudRemoteChangeBatch Batch, int EntryIndex, CloudRemoteApplyOptions Options);
