using System.Security.Cryptography;
using System.Text;

namespace CfSharp;

// Shared binary evidence and correlation rules for durable remote namespace mutations.
internal static class RemoteNamespaceEvidence
{
    internal static void WriteObject(BinaryWriter writer, RemoteNamespaceObject item)
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

    internal static RemoteNamespaceObject ReadObject(BinaryReader reader)
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

internal sealed record RemoteNamespaceObject(string RelativePath, CloudLocalFileBinding Binding, byte[] Identity,
    long CreationTime, long LastWriteTime, long Length);

internal sealed record RemoteChangeRequest(CloudRemoteChangeBatch Batch, int EntryIndex, CloudRemoteApplyOptions Options);
