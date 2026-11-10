using System.Security.Cryptography;
using System.Text;

namespace CfSharp;

// This is a receipt for a durable rejection, never a native completion certificate.
// Its namespace is deliberately separate from RemoteDeleteIntent and native recovery.
internal sealed record RemoteDeleteReconciliationReceipt(Guid StoreScope, string RootPath, string BatchId,
    int EntryIndex, byte[] Fingerprint, Guid ConflictId, CloudItemState Tombstone, DateTimeOffset DetectedAt,
    bool SuppressEcho, long EchoLifetimeTicks, int EchoObservations, int MaximumEntries, byte[] Cursor)
{
    internal const string Prefix = "cfsharp/remote-delete-reconciliation/v1/";
    internal const int MaximumBytes = 1024 * 1024;

    internal static string Name(string batchId, int index) => Prefix +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(batchId))) + "/" +
        index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal bool MatchesPolicy(CloudRemoteApplyOptions options) => options.PreserveUnsynchronizedLocalContent &&
        options.ConflictResolver is null && SuppressEcho == options.SuppressLocalEcho &&
        EchoLifetimeTicks == options.EchoSuppressionLifetime.Ticks && EchoObservations == options.EchoSuppressionObservationCount &&
        MaximumEntries == options.MaximumEntries;

    internal byte[] Encode()
    {
        if (!Tombstone.IsTombstone || StoreScope == Guid.Empty || ConflictId == Guid.Empty || Fingerprint.Length != 32)
        {
            throw new InvalidDataException("The Delete reconciliation receipt cannot certify a malformed rejection.");
        }
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, CloudDirectoryEvidenceCodec.Utf8, leaveOpen: true);
        writer.Write(1);
        writer.Write(StoreScope.ToByteArray());
        WriteText(writer, RootPath);
        WriteText(writer, BatchId);
        writer.Write(EntryIndex);
        writer.Write(Fingerprint);
        writer.Write(ConflictId.ToByteArray());
        writer.Write(Tombstone.ItemId.ToByteArray());
        WriteText(writer, Tombstone.RemoteId);
        WriteText(writer, Tombstone.RelativePath);
        writer.Write((int)Tombstone.Kind);
        writer.Write(Tombstone.RemoteRevision is not null);
        if (Tombstone.RemoteRevision is not null) { WriteText(writer, Tombstone.RemoteRevision); }
        writer.Write(Tombstone.LocalFileId.HasValue);
        if (Tombstone.LocalFileId.HasValue) { writer.Write(Tombstone.LocalFileId.Value); }
        writer.Write(Tombstone.UpdatedAt.UtcTicks);
        writer.Write(DetectedAt.UtcTicks);
        writer.Write(SuppressEcho);
        writer.Write(EchoLifetimeTicks);
        writer.Write(EchoObservations);
        writer.Write(MaximumEntries);
        if (Cursor.Length > MaximumBytes) { throw new NotSupportedException("The reconciliation cursor exceeds its metadata budget."); }
        writer.Write(Cursor.Length);
        writer.Write(Cursor);
        writer.Flush();
        if (stream.Length > MaximumBytes) { throw new NotSupportedException("The reconciliation receipt exceeds its one-MiB metadata budget."); }
        return stream.ToArray();
    }

    internal static RemoteDeleteReconciliationReceipt Decode(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > MaximumBytes) { throw new InvalidDataException("The Delete reconciliation receipt exceeds its metadata budget."); }
        try
        {
            using MemoryStream stream = new(payload.ToArray(), writable: false);
            using BinaryReader reader = new(stream, CloudDirectoryEvidenceCodec.Utf8);
            if (reader.ReadInt32() != 1) { throw new InvalidDataException("The Delete reconciliation receipt version is unsupported."); }
            Guid scope = CloudDirectoryEvidenceCodec.ReadGuid(reader);
            string root = CloudDirectoryEvidenceCodec.ReadText(reader);
            string batch = CloudDirectoryEvidenceCodec.ReadText(reader);
            int index = reader.ReadInt32();
            byte[] fingerprint = reader.ReadBytes(32);
            Guid conflict = CloudDirectoryEvidenceCodec.ReadGuid(reader);
            Guid item = CloudDirectoryEvidenceCodec.ReadGuid(reader);
            string remote = CloudDirectoryEvidenceCodec.ReadText(reader);
            string path = CloudDirectoryEvidenceCodec.ReadText(reader);
            CloudItemKind kind = (CloudItemKind)reader.ReadInt32();
            string? revision = reader.ReadBoolean() ? CloudDirectoryEvidenceCodec.ReadText(reader) : null;
            long? localId = reader.ReadBoolean() ? reader.ReadInt64() : null;
            CloudItemState tombstone = new(item, remote, path, kind, revision, localId, true,
                new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero));
            DateTimeOffset detected = new(reader.ReadInt64(), TimeSpan.Zero);
            bool echo = reader.ReadBoolean();
            long lifetime = reader.ReadInt64();
            int observations = reader.ReadInt32();
            int maximum = reader.ReadInt32();
            byte[] cursor = CloudDirectoryEvidenceCodec.ReadBytes(reader, MaximumBytes);
            if (scope == Guid.Empty || conflict == Guid.Empty || !Path.IsPathFullyQualified(root) ||
                string.IsNullOrWhiteSpace(batch) || index < 0 || fingerprint.Length != 32 || path.Length == 0 ||
                lifetime <= 0 || lifetime > TimeSpan.FromHours(1).Ticks || observations is < 1 or > 64 ||
                maximum is < 1 or > 65536 || stream.Position != stream.Length)
            {
                throw new InvalidDataException("The Delete reconciliation receipt is malformed.");
            }
            RemoteDeleteReconciliationReceipt receipt = new(scope, root, batch, index, fingerprint, conflict,
                tombstone, detected, echo, lifetime, observations, maximum, cursor);
            if (!receipt.Encode().AsSpan().SequenceEqual(payload.Span))
            {
                throw new InvalidDataException("The Delete reconciliation receipt is not canonically encoded.");
            }
            return receipt;
        }
        catch (Exception exception) when (exception is ArgumentException or EndOfStreamException or DecoderFallbackException)
        {
            throw new InvalidDataException("The Delete reconciliation receipt is malformed.", exception);
        }
    }

    private static void WriteText(BinaryWriter writer, string text)
    {
        if (CloudDirectoryEvidenceCodec.Utf8.GetByteCount(text) > 32768)
        {
            throw new NotSupportedException("A reconciliation metadata string exceeds its 32768-byte UTF-8 budget.");
        }
        CloudDirectoryEvidenceCodec.WriteText(writer, text);
    }
}
