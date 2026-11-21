using System.Buffers.Binary;
using System.Text;

namespace CfSharp;

internal sealed record RemoteMetadataIntent(Guid Id, Guid CallId, Guid ItemId, string RelativePath,
    CloudLocalFileBinding? Binding, bool Coordinated, bool Completed)
{
    internal bool RequiresReconciliation { get; init; }
    internal bool Reconciled { get; init; }
    internal bool Superseded { get; init; }
    internal bool Passthrough { get; init; }
    internal const string Prefix = "cfsharp/remote-metadata/v1/";
    internal static string ItemPrefix(Guid itemId) => Prefix + itemId.ToString("N") + "/";
    internal string Name => ItemPrefix(ItemId) + Id.ToString("N");
    internal bool Covers(string path) => string.Equals(RelativePath, path, StringComparison.OrdinalIgnoreCase);

    internal bool Matches(LocalSecurityChangeIdentity identity, Guid? itemId)
    {
        if (Binding is null || itemId != ItemId) { return false; }
        Span<byte> file = stackalloc byte[16];
        Binding.LocalFileId.TryWriteBytes(file);
        return identity.VolumeSerialNumber == Binding.VolumeSerialNumber && identity.RootFileId == Binding.SyncRootFileId &&
            identity.FileId == BinaryPrimitives.ReadUInt64LittleEndian(file) && BinaryPrimitives.ReadUInt64LittleEndian(file[8..]) == 0;
    }

    internal byte[] Encode()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Id.ToByteArray());
        writer.Write(CallId.ToByteArray());
        writer.Write(ItemId.ToByteArray());
        writer.Write(RelativePath);
        writer.Write(Binding is not null);
        if (Binding is { } binding)
        {
            writer.Write(binding.VolumeSerialNumber);
            writer.Write(binding.SyncRootFileId.ToByteArray());
            writer.Write(binding.LocalFileId.ToByteArray());
        }
        writer.Write(Coordinated);
        writer.Write(Completed);
        writer.Write(RequiresReconciliation);
        writer.Write(Reconciled);
        writer.Write(Superseded);
        writer.Write(Passthrough);
        return stream.ToArray();
    }

    internal static RemoteMetadataIntent Decode(CloudStateCheckpoint checkpoint)
    {
        if (checkpoint.Value.Length > 256 * 1024) { throw new InvalidDataException("Remote metadata evidence exceeds its size limit."); }
        try
        {
            using MemoryStream stream = new(checkpoint.Value.ToArray(), writable: false);
            using BinaryReader reader = new(stream, Encoding.UTF8);
            Guid id = new(reader.ReadBytes(16));
            Guid call = new(reader.ReadBytes(16));
            Guid item = new(reader.ReadBytes(16));
            string path = reader.ReadString();
            CloudLocalFileBinding? binding = ReadBoolean(reader)
                ? new(reader.ReadUInt64(), new(reader.ReadBytes(16)), new(reader.ReadBytes(16))) : null;
            bool coordinated = ReadBoolean(reader);
            bool completed = ReadBoolean(reader);
            bool reconcile = ReadBoolean(reader);
            bool reconciled = ReadBoolean(reader);
            bool superseded = ReadBoolean(reader);
            bool passthrough = ReadBoolean(reader);
            RemoteMetadataIntent intent = new(id, call, item, path, binding, coordinated, completed)
            { RequiresReconciliation = reconcile, Reconciled = reconciled, Superseded = superseded, Passthrough = passthrough };
            if (id == Guid.Empty || call == Guid.Empty || item == Guid.Empty || checkpoint.Name != intent.Name || stream.Position != stream.Length)
            { throw new InvalidDataException("Invalid remote metadata evidence."); }
            if (CloudStateModelValidation.CanonicalizeRelativePath(path, nameof(path)) != path)
            { throw new InvalidDataException("Invalid remote metadata path."); }
            return intent;
        }
        catch (Exception error) when (error is EndOfStreamException or ArgumentException or FormatException)
        { throw new InvalidDataException("Malformed remote metadata evidence.", error); }
    }

    private static bool ReadBoolean(BinaryReader reader) => reader.ReadByte() switch
    {
        0 => false,
        1 => true,
        _ => throw new InvalidDataException("Invalid metadata evidence flag."),
    };
}
