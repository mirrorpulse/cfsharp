using System.Runtime.Versioning;
using System.Security.Cryptography;
using CfSharp.Native;

namespace CfSharp;

internal sealed record PlaceholderCreationEvidence(Guid OperationId, Guid StoreScope, string RootPath, string RelativePath,
    CloudItemKind Kind, CloudLocalFileBinding Parent, byte[] Identity, byte[] SpecificationHash, Guid ObjectId = default,
    long CreateUsn = 0)
{
    private const int Version = 1;
    private const int MaxBytes = 72 * 1024;

    internal static string Name(Guid operationId) => $"cfsharp/placeholder-creation/v1/{operationId:N}";

    internal bool IsCommitted => ObjectId != Guid.Empty;

    internal CloudPlaceholderCreationReceipt ToReceipt() => new(OperationId, RelativePath, Kind,
        new(Parent.VolumeSerialNumber, Parent.SyncRootFileId, ObjectId), Identity, CreateUsn);

    [SupportedOSPlatform("windows10.0.16299")]
    internal static byte[] Fingerprint(CloudPlaceholderSpec specification)
    {
        CfFsMetadata metadata = CloudPlaceholderPlatform.CreateMetadata(specification);
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write((uint)CloudPlaceholderPlatform.CreateFlags(specification));
        writer.Write(metadata.BasicInfo.FileAttributes);
        writer.Write(metadata.BasicInfo.CreationTime);
        writer.Write(metadata.BasicInfo.LastAccessTime);
        writer.Write(metadata.BasicInfo.LastWriteTime);
        writer.Write(metadata.BasicInfo.ChangeTime);
        writer.Write(metadata.FileSize);
        writer.Flush();
        return SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length));
    }

    internal byte[] Encode()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, CloudDirectoryEvidenceCodec.Utf8);
        writer.Write(Version);
        writer.Write(OperationId.ToByteArray());
        writer.Write(StoreScope.ToByteArray());
        CloudDirectoryEvidenceCodec.WriteText(writer, RootPath);
        CloudDirectoryEvidenceCodec.WriteText(writer, RelativePath);
        writer.Write((int)Kind);
        writer.Write(Parent.VolumeSerialNumber);
        writer.Write(Parent.SyncRootFileId.ToByteArray());
        writer.Write(Parent.LocalFileId.ToByteArray());
        writer.Write(Identity.Length);
        writer.Write(Identity);
        writer.Write(SpecificationHash);
        writer.Write(ObjectId.ToByteArray());
        writer.Write(CreateUsn);
        writer.Flush();
        return stream.ToArray();
    }

    internal static PlaceholderCreationEvidence Decode(ReadOnlyMemory<byte> value, Guid operationId)
    {
        if (value.Length > MaxBytes)
        {
            throw new InvalidDataException("The placeholder creation evidence exceeds its size limit.");
        }

        try
        {
            using MemoryStream stream = new(value.ToArray(), writable: false);
            using BinaryReader reader = new(stream, CloudDirectoryEvidenceCodec.Utf8);
            if (reader.ReadInt32() != Version)
            {
                throw new NotSupportedException("The placeholder creation evidence version is unsupported.");
            }

            Guid id = CloudDirectoryEvidenceCodec.ReadGuid(reader);
            Guid scope = CloudDirectoryEvidenceCodec.ReadGuid(reader);
            string root = CloudDirectoryEvidenceCodec.ReadText(reader);
            string path = CloudDirectoryEvidenceCodec.ReadText(reader);
            CloudItemKind kind = (CloudItemKind)reader.ReadInt32();
            CloudLocalFileBinding parent = new(reader.ReadUInt64(), CloudDirectoryEvidenceCodec.ReadGuid(reader),
                CloudDirectoryEvidenceCodec.ReadGuid(reader));
            byte[] identity = CloudDirectoryEvidenceCodec.ReadBytes(reader, SyncRootRegistrationOptions.MaxFileIdentityLength);
            byte[] fingerprint = reader.ReadBytes(32);
            Guid objectId = CloudDirectoryEvidenceCodec.ReadGuid(reader);
            long createUsn = reader.ReadInt64();
            if (id != operationId || scope == Guid.Empty || !Path.IsPathFullyQualified(root) ||
                CloudItemPathResolver.ResolveLexically(root, path, allowRoot: false).RelativePath != path ||
                !Enum.IsDefined(kind) || identity.Length == 0 || fingerprint.Length != 32 || objectId == parent.LocalFileId ||
                objectId == parent.SyncRootFileId || (objectId == Guid.Empty ? createUsn != 0 : createUsn <= 0) ||
                stream.Position != stream.Length)
            {
                throw new InvalidDataException("The retained placeholder creation evidence is inconsistent.");
            }

            return new(id, scope, root, path, kind, parent, identity, fingerprint, objectId, createUsn);
        }
        catch (Exception exception) when (exception is ArgumentException or EndOfStreamException)
        {
            throw new InvalidDataException("The retained placeholder creation evidence is malformed.", exception);
        }
    }
}
