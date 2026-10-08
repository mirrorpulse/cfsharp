using System.Collections.ObjectModel;
using System.Text;

namespace CfSharp;

/// <summary>Classifies one normalized local file-system change.</summary>
public enum CloudLocalChangeKind
{
    /// <summary>A previously absent item was created.</summary>
    Create = 0,

    /// <summary>The content of an existing file changed.</summary>
    ContentUpdate = 1,

    /// <summary>Metadata of an existing item changed.</summary>
    MetadataUpdate = 2,

    /// <summary>An item moved or was renamed within the sync root.</summary>
    Move = 3,

    /// <summary>An item was deleted and is represented by a durable tombstone when known.</summary>
    Delete = 4,
}

/// <summary>Configures one explicit local-change watcher and durable journal consumer.</summary>
public sealed record CloudLocalChangeFeedOptions
{
    /// <summary>Gets the default feed configuration.</summary>
    public static CloudLocalChangeFeedOptions Default { get; } = new();

    /// <summary>Gets the maximum number of native events buffered before overflow is reported.</summary>
    public int BufferCapacity { get; init; } = 256;

    /// <summary>Gets the maximum number of journal entries returned by one read.</summary>
    public int BatchSize { get; init; } = 64;

    /// <summary>Gets the maximum time allowed for the notification worker to drain during shutdown.</summary>
    /// <remarks>
    /// A timed-out drain persists a full-rescan fence before canceling the worker. Waiting for that
    /// durable store transaction and deferred cleanup can extend disposal beyond this timeout.
    /// </remarks>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        ValidatePositive(BufferCapacity, nameof(BufferCapacity), 64 * 1024);
        ValidatePositive(BatchSize, nameof(BatchSize), 4096);
        if (ShutdownTimeout <= TimeSpan.Zero || ShutdownTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ShutdownTimeout),
                ShutdownTimeout,
                "The local-change shutdown timeout must be positive and no longer than five minutes.");
        }
    }

    private static void ValidatePositive(int value, string parameterName, int maximum)
    {
        if (value <= 0 || value > maximum)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                $"The value must be positive and no larger than {maximum}.");
        }
    }
}

/// <summary>Represents one immutable, durable local change delivered to an application.</summary>
public sealed class CloudLocalChange
{
    internal CloudLocalChange(
        Guid operationId,
        long sequence,
        CloudLocalChangeKind kind,
        Guid? itemId,
        string relativePath,
        string? previousRelativePath,
        bool isDirectory,
        DateTimeOffset observedAt)
    {
        OperationId = operationId;
        Sequence = sequence;
        Kind = kind;
        ItemId = itemId;
        RelativePath = relativePath;
        PreviousRelativePath = previousRelativePath;
        IsDirectory = isDirectory;
        ObservedAt = observedAt.ToUniversalTime();
    }

    /// <summary>Gets the idempotency identifier stored in the operation journal.</summary>
    public Guid OperationId { get; }

    /// <summary>Gets the durable journal sequence assigned by the state store.</summary>
    public long Sequence { get; }

    /// <summary>Gets the normalized local-change kind.</summary>
    public CloudLocalChangeKind Kind { get; }

    /// <summary>Gets the stable item identity when one was already known.</summary>
    public Guid? ItemId { get; }

    /// <summary>Gets the canonical path relative to the owning sync root.</summary>
    public string RelativePath { get; }

    /// <summary>Gets the previous canonical path for a move, otherwise <see langword="null"/>.</summary>
    public string? PreviousRelativePath { get; }

    /// <summary>Gets whether the affected item is a directory.</summary>
    public bool IsDirectory { get; }

    /// <summary>Gets the UTC time at which the notification was normalized.</summary>
    public DateTimeOffset ObservedAt { get; }
}

/// <summary>Describes an application acknowledgement of one local operation.</summary>
public sealed class CloudLocalChangeAcknowledgement
{
    /// <summary>Initializes an acknowledgement with an optional remote revision.</summary>
    /// <param name="operationId">The operation identifier delivered by the feed.</param>
    /// <param name="remoteRevision">
    /// The remote revision confirmed after upload, or <see langword="null"/> when the caller only
    /// wants to remove the local journal entry.
    /// </param>
    public CloudLocalChangeAcknowledgement(Guid operationId, string? remoteRevision = null)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("The operation identifier cannot be empty.", nameof(operationId));
        }

        if (remoteRevision is not null && string.IsNullOrWhiteSpace(remoteRevision))
        {
            throw new ArgumentException(
                "The remote revision must be null or non-empty.",
                nameof(remoteRevision));
        }

        OperationId = operationId;
        RemoteRevision = remoteRevision;
    }

    /// <summary>Gets the acknowledged local operation identifier.</summary>
    public Guid OperationId { get; }

    /// <summary>
    /// Gets the remote revision confirmed by the application. A non-null value becomes the
    /// durable item's last mutually acknowledged revision, which represents the in-sync state.
    /// </summary>
    public string? RemoteRevision { get; }
}

/// <summary>Contains one bounded local-change delivery and any required full-rescan signal.</summary>
public sealed class CloudLocalChangeBatch
{
    internal CloudLocalChangeBatch(
        IReadOnlyList<CloudLocalChange> changes,
        bool requiresFullRescan)
    {
        ArgumentNullException.ThrowIfNull(changes);
        Changes = new ReadOnlyCollection<CloudLocalChange>(changes.ToArray());
        RequiresFullRescan = requiresFullRescan;
    }

    /// <summary>Gets the immutable ordered changes in this batch.</summary>
    public IReadOnlyList<CloudLocalChange> Changes { get; }

    /// <summary>
    /// Gets whether the notification stream lost information and the application must reconcile
    /// the complete sync root before relying on subsequent changes.
    /// </summary>
    public bool RequiresFullRescan { get; }
}

internal enum LocalChangeSourceAction
{
    Created,
    Modified,
    Deleted,
    RenamedOldName,
    RenamedNewName,
    Overflow,
    Error,
}

internal readonly record struct LocalChangeSourceEvent(
    LocalChangeSourceAction Action,
    string RelativePath,
    int ErrorCode = 0);

internal sealed record LocalChangePayload(
    string RelativePath,
    string? PreviousRelativePath,
    bool IsDirectory,
    DateTimeOffset ObservedAt)
{
    private const int MaximumEncodedPathBytes = 32767 * sizeof(char);

    internal byte[] Encode()
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(RelativePath);
            writer.Write(PreviousRelativePath ?? string.Empty);
            writer.Write(IsDirectory);
            writer.Write(ObservedAt.UtcDateTime.Ticks);
        }

        return stream.ToArray();
    }

    internal static LocalChangePayload Decode(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using MemoryStream stream = new(payload.ToArray(), writable: false);
            using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: true);
            string relativePath = ReadBoundedString(reader, stream);
            string previousRelativePath = ReadBoundedString(reader, stream);
            bool isDirectory = reader.ReadBoolean();
            long ticks = reader.ReadInt64();
            if (relativePath.Length == 0 ||
                ticks < DateTime.MinValue.Ticks ||
                ticks > DateTime.MaxValue.Ticks ||
                stream.Position != stream.Length)
            {
                throw new InvalidOperationException("The local-change journal payload is invalid.");
            }

            return new LocalChangePayload(
                relativePath,
                previousRelativePath.Length == 0 ? null : previousRelativePath,
                isDirectory,
                new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc)));
        }
        catch (Exception exception) when (
            exception is EndOfStreamException or IOException or ArgumentException or
            InvalidDataException)
        {
            throw new InvalidOperationException("The local-change journal payload is invalid.", exception);
        }
    }

    private static string ReadBoundedString(BinaryReader reader, Stream stream)
    {
        int byteCount = Read7BitEncodedInt(reader);
        if (byteCount < 0 || byteCount > MaximumEncodedPathBytes ||
            byteCount > stream.Length - stream.Position)
        {
            throw new InvalidDataException("The local-change journal string length is invalid.");
        }

        byte[] bytes = reader.ReadBytes(byteCount);
        if (bytes.Length != byteCount)
        {
            throw new EndOfStreamException();
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static int Read7BitEncodedInt(BinaryReader reader)
    {
        int value = 0;
        int shift = 0;
        for (int index = 0; index < 5; index++)
        {
            byte next = reader.ReadByte();
            value |= (next & 0x7f) << shift;
            if ((next & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }

        throw new InvalidDataException("The local-change journal string length is invalid.");
    }
}

internal readonly record struct LocalChangeCheckpoint(long Observation, bool RequiresFullRescan)
{
    internal byte[] Encode()
    {
        byte[] value = new byte[9];
        BitConverter.TryWriteBytes(value.AsSpan(0, sizeof(long)), Observation);
        value[sizeof(long)] = RequiresFullRescan ? (byte)1 : (byte)0;
        return value;
    }

    internal static LocalChangeCheckpoint Decode(ReadOnlyMemory<byte> value)
    {
        if (value.Length != 9)
        {
            throw new InvalidOperationException("The local-change checkpoint payload is invalid.");
        }

        return new LocalChangeCheckpoint(
            BitConverter.ToInt64(value.Span[..sizeof(long)]),
            value.Span[sizeof(long)] != 0);
    }
}
