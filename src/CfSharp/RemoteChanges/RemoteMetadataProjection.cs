using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace CfSharp;

// The checkpoint name binds the immutable batch entry or the durable conflict. Its payload
// links the attribution preparation to the official projection committed after native success.
internal readonly record struct RemoteMetadataProjection(Guid ItemId, Guid ReceiptId, long ProjectedAtTicks,
    string? OriginalRowFingerprint = null)
{
    internal const string Prefix = "cfsharp/remote-metadata-projection/v1/";
    internal const long NativeWriteNotAttempted = -1;
    internal const long NativeWriteCompleted = -2;

    internal static string Name(CloudRemoteChangeBatch batch, int index) =>
        Prefix + Convert.ToHexString(batch.Fingerprint.Span) + "/" + index.ToString(CultureInfo.InvariantCulture);

    internal static string ConflictName(Guid conflictId) => Prefix + "conflict/" + conflictId.ToString("N");

    internal string ReceiptName => RemoteMetadataIntent.ItemPrefix(ItemId) + ReceiptId.ToString("N");

    internal static string Fingerprint(CloudItemState state)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write(state.ItemId.ToByteArray());
        writer.Write(state.RemoteId);
        writer.Write(state.RelativePath);
        writer.Write((int)state.Kind);
        writer.Write(state.RemoteRevision is not null);
        if (state.RemoteRevision is { } revision) { writer.Write(revision); }
        writer.Write(state.LocalFileId.HasValue);
        if (state.LocalFileId is long localFileId) { writer.Write(localFileId); }
        writer.Write(state.IsTombstone);
        writer.Write(state.UpdatedAt.UtcTicks);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    internal static async ValueTask<HashSet<string>> ReadRetainedReceiptNamesAsync(ICloudCheckpointRepository checkpoints,
        CancellationToken cancellationToken) => (await checkpoints.ListAsync(Prefix, cancellationToken).ConfigureAwait(false))
        .Select(checkpoint => Decode(checkpoint).ReceiptName).ToHashSet(StringComparer.Ordinal);

    internal CloudStateCheckpoint ToCheckpoint(string name, DateTimeOffset now)
    {
        Span<byte> buffer = stackalloc byte[OriginalRowFingerprint is null ? 40 : 72];
        ItemId.TryWriteBytes(buffer);
        ReceiptId.TryWriteBytes(buffer[16..]);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[32..], ProjectedAtTicks);
        if (OriginalRowFingerprint is { } fingerprint)
        { Convert.FromHexString(fingerprint).CopyTo(buffer[40..]); }
        return new(name, buffer, now);
    }

    internal static RemoteMetadataProjection Decode(CloudStateCheckpoint checkpoint)
    {
        ReadOnlySpan<byte> bytes = checkpoint.Value.Span;
        if (bytes.Length is not (40 or 72)) { throw new InvalidDataException("Invalid remote metadata projection length."); }
        RemoteMetadataProjection projection = new(new(bytes[..16]), new(bytes[16..32]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[32..]), bytes.Length == 72 ? Convert.ToHexString(bytes[40..]) : null);
        if (projection.ItemId == Guid.Empty || projection.ReceiptId == Guid.Empty ||
            projection.ProjectedAtTicks < NativeWriteCompleted || projection.ProjectedAtTicks > DateTimeOffset.MaxValue.UtcTicks ||
            (projection.ProjectedAtTicks == NativeWriteCompleted && projection.OriginalRowFingerprint is null))
        {
            throw new InvalidDataException("Invalid remote metadata projection.");
        }
        return projection;
    }
}
