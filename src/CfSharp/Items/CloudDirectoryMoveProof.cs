using System.Text;

namespace CfSharp;

/// <summary>Contains durable, pre-move evidence for one managed placeholder directory.</summary>
/// <remarks>
/// This immutable value owns copied metadata only; it holds no handle, transaction, or lock.
/// Persist <see cref="Encode"/> before authorizing an external rename. Decoding does not establish
/// authority: recovery also requires the original preparation in the same state store, the actual
/// sync-root binding, and the complete current placeholder identity. Native IDs may be reused after
/// deletion. The proof does not establish content equality or remote acceptance. Concurrent reads
/// are safe. Preparation requires Windows 10 version 1709 and file-ID-capable storage.
/// </remarks>
public sealed class CloudDirectoryMoveProof
{
    private readonly byte[] _identity;

    internal CloudDirectoryMoveProof(Guid proofId, Guid storeScope, string sourceRelativePath,
        string destinationRelativePath, Guid rootItemId, CloudLocalFileBinding expectedBinding,
        ReadOnlySpan<byte> expectedPlaceholderIdentity)
    {
        if (proofId == Guid.Empty || storeScope == Guid.Empty || rootItemId == Guid.Empty)
        {
            throw new ArgumentException("Proof, store, and item identifiers must be nonempty.");
        }

        ArgumentNullException.ThrowIfNull(expectedBinding);
        SourceRelativePath = CloudDirectoryEvidenceCodec.CanonicalPath(sourceRelativePath);
        DestinationRelativePath = CloudDirectoryEvidenceCodec.CanonicalPath(destinationRelativePath);
        if (SourceRelativePath == DestinationRelativePath ||
            DestinationRelativePath.StartsWith(SourceRelativePath + "\\", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A move requires a different destination outside the source subtree.");
        }

        if (expectedPlaceholderIdentity.Length > SyncRootRegistrationOptions.MaxFileIdentityLength ||
            CloudPlaceholderIdentity.Decode(expectedPlaceholderIdentity).ItemId != rootItemId)
        {
            throw new ArgumentException("The complete placeholder identity must identify the prepared root.");
        }

        ProofId = proofId;
        StoreScope = storeScope;
        RootItemId = rootItemId;
        ExpectedBinding = expectedBinding;
        _identity = expectedPlaceholderIdentity.ToArray();
    }

    /// <summary>Gets the supported serialization protocol version.</summary>
    public int Version { get; } = 1;

    /// <summary>Gets the unique identifier of this immutable preparation.</summary>
    public Guid ProofId { get; }

    /// <summary>Gets the durable scope of the store that captured this proof.</summary>
    public Guid StoreScope { get; }

    /// <summary>Gets the canonical source path relative to the owning sync root.</summary>
    public string SourceRelativePath { get; }

    /// <summary>Gets the canonical intended destination path relative to that root.</summary>
    public string DestinationRelativePath { get; }

    /// <summary>Gets the historical CfSharp identity of the directory root.</summary>
    public Guid RootItemId { get; }

    /// <summary>Gets the complete volume, root, and directory object binding captured before movement.</summary>
    public CloudLocalFileBinding ExpectedBinding { get; }

    /// <summary>Gets a copy of the complete opaque native placeholder identity captured before movement.</summary>
    public ReadOnlyMemory<byte> ExpectedPlaceholderIdentity => _identity.ToArray();

    /// <summary>Serializes this proof into an owned, bounded versioned envelope.</summary>
    /// <returns>A new byte array suitable for durable caller storage; no private subtree manifest is included.</returns>
    public byte[] Encode()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(0x31504D43); // CMP1, little endian; all integer fields use BinaryWriter's fixed convention.
        writer.Write(Version);
        writer.Write(ProofId.ToByteArray());
        writer.Write(StoreScope.ToByteArray());
        writer.Write(RootItemId.ToByteArray());
        writer.Write(ExpectedBinding.VolumeSerialNumber);
        writer.Write(ExpectedBinding.SyncRootFileId.ToByteArray());
        writer.Write(ExpectedBinding.LocalFileId.ToByteArray());
        CloudDirectoryEvidenceCodec.WriteText(writer, SourceRelativePath);
        CloudDirectoryEvidenceCodec.WriteText(writer, DestinationRelativePath);
        writer.Write(_identity.Length);
        writer.Write(_identity);
        return stream.ToArray();
    }

    /// <summary>Restores a serialized proof without granting trusted provenance.</summary>
    /// <param name="encoded">The complete envelope, at most 131072 bytes.</param>
    /// <returns>An immutable owned value; recovery must verify the library's original durable record.</returns>
    /// <exception cref="InvalidDataException">The envelope, paths, lengths, or identity are invalid.</exception>
    /// <exception cref="NotSupportedException">The envelope has an unsupported protocol version.</exception>
    public static CloudDirectoryMoveProof Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length is < 108 or > 131072)
        {
            throw new InvalidDataException("The directory move proof has an invalid length.");
        }

        try
        {
            using MemoryStream stream = new(encoded.ToArray(), writable: false);
            using BinaryReader reader = new(stream, CloudDirectoryEvidenceCodec.Utf8);
            if (reader.ReadInt32() != 0x31504D43)
            {
                throw new InvalidDataException("The value is not a directory move proof.");
            }

            int version = reader.ReadInt32();
            if (version != 1)
            {
                throw new NotSupportedException($"Directory move proof version {version} is unsupported.");
            }

            Guid proofId = CloudDirectoryEvidenceCodec.ReadGuid(reader);
            Guid scope = CloudDirectoryEvidenceCodec.ReadGuid(reader);
            Guid rootId = CloudDirectoryEvidenceCodec.ReadGuid(reader);
            CloudLocalFileBinding binding = new(reader.ReadUInt64(),
                CloudDirectoryEvidenceCodec.ReadGuid(reader), CloudDirectoryEvidenceCodec.ReadGuid(reader));
            string source = CloudDirectoryEvidenceCodec.ReadText(reader);
            string destination = CloudDirectoryEvidenceCodec.ReadText(reader);
            byte[] identity = CloudDirectoryEvidenceCodec.ReadBytes(reader, SyncRootRegistrationOptions.MaxFileIdentityLength);
            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException("The proof contains trailing data.");
            }

            CloudDirectoryMoveProof proof = new(proofId, scope, source, destination, rootId, binding, identity);
            if (!proof.Encode().AsSpan().SequenceEqual(encoded))
            {
                throw new InvalidDataException("The proof does not use canonical serialization.");
            }

            return proof;
        }
        catch (Exception exception) when (exception is EndOfStreamException or ArgumentException or DecoderFallbackException)
        {
            throw new InvalidDataException("The directory move proof is malformed.", exception);
        }
    }
}

internal static class CloudDirectoryEvidenceCodec
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    internal static string CanonicalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string canonical = CloudRemotePathValidation.Canonicalize(path, nameof(path)).Replace('/', '\\');
        if (Utf8.GetByteCount(canonical) > 32768)
        {
            throw new CloudDirectoryEvidenceUnavailableException("The directory evidence path exceeds the supported UTF-8 metadata limit.");
        }

        return canonical;
    }

    internal static void WriteText(BinaryWriter writer, string value)
    {
        byte[] bytes = Utf8.GetBytes(value);
        if (bytes.Length > 32768)
        {
            throw new CloudDirectoryEvidenceUnavailableException("The directory evidence path exceeds the supported UTF-8 metadata limit.");
        }

        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    internal static string ReadText(BinaryReader reader) => Utf8.GetString(ReadBytes(reader, 32768));

    internal static byte[] ReadBytes(BinaryReader reader, int maximumLength)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > maximumLength || length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new InvalidDataException("The directory evidence length is invalid.");
        }

        return reader.ReadBytes(length);
    }

    internal static Guid ReadGuid(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(16);
        if (bytes.Length != 16)
        {
            throw new InvalidDataException("The directory evidence identifier is truncated.");
        }

        return new Guid(bytes);
    }
}
