using System.Buffers.Binary;
using System.Globalization;

namespace CfSharp;

// The checkpoint name binds the immutable batch and entry. Its payload links the
// attribution preparation to the exact official projection committed after native success.
internal readonly record struct RemoteMetadataProjection(Guid ItemId, Guid ReceiptId, long ProjectedAtTicks)
{
    internal const string Prefix = "cfsharp/remote-metadata-projection/v1/";
    internal const long NativeWriteNotAttempted = -1;

    internal static string Name(CloudRemoteChangeBatch batch, int index) =>
        Prefix + Convert.ToHexString(batch.Fingerprint.Span) + "/" + index.ToString(CultureInfo.InvariantCulture);

    internal string ReceiptName => RemoteMetadataIntent.ItemPrefix(ItemId) + ReceiptId.ToString("N");

    internal static async ValueTask<HashSet<string>> ReadRetainedReceiptNamesAsync(ICloudCheckpointRepository checkpoints,
        CancellationToken cancellationToken) => (await checkpoints.ListAsync(Prefix, cancellationToken).ConfigureAwait(false))
        .Select(checkpoint => Decode(checkpoint).ReceiptName).ToHashSet(StringComparer.Ordinal);

    internal CloudStateCheckpoint ToCheckpoint(string name, DateTimeOffset now)
    {
        Span<byte> buffer = stackalloc byte[40];
        ItemId.TryWriteBytes(buffer);
        ReceiptId.TryWriteBytes(buffer[16..]);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[32..], ProjectedAtTicks);
        return new(name, buffer, now);
    }

    internal static RemoteMetadataProjection Decode(CloudStateCheckpoint checkpoint)
    {
        ReadOnlySpan<byte> bytes = checkpoint.Value.Span;
        if (bytes.Length != 40) { throw new InvalidDataException("Invalid remote metadata projection length."); }
        RemoteMetadataProjection projection = new(new(bytes[..16]), new(bytes[16..32]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[32..]));
        if (projection.ItemId == Guid.Empty || projection.ReceiptId == Guid.Empty ||
            projection.ProjectedAtTicks < NativeWriteNotAttempted || projection.ProjectedAtTicks > DateTimeOffset.MaxValue.UtcTicks)
        {
            throw new InvalidDataException("Invalid remote metadata projection.");
        }
        return projection;
    }
}
