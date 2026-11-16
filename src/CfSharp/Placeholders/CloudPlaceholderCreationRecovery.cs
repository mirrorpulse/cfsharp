namespace CfSharp;

/// <summary>Identifies one recoverable native placeholder creation in a root's durable store.</summary>
/// <remarks>
/// Retain the identifier, parent and immutable specification before starting creation. Reuse them
/// through a new owner of the same root and store after interruption. The value has no native
/// lifetime and is safe for concurrent reads.
/// </remarks>
public sealed class CloudPlaceholderCreationRequest
{
    /// <summary>Creates a store-scoped request to create a new file or directory placeholder.</summary>
    /// <param name="operationId">Nonempty application-generated identifier for the original entry.</param>
    /// <param name="specification">Immutable specification with Fail collision behavior and online-only file availability.</param>
    /// <exception cref="ArgumentNullException">The specification is null.</exception>
    /// <exception cref="ArgumentException">The identifier is empty, or the specification requests supersede or hydration.</exception>
    public CloudPlaceholderCreationRequest(Guid operationId, CloudPlaceholderSpec specification)
    {
        ArgumentNullException.ThrowIfNull(specification);
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("An original creation operation ID is required.", nameof(operationId));
        }

        if (specification.CollisionBehavior != CloudPlaceholderCollisionBehavior.Fail ||
            specification is CloudFilePlaceholderSpec { InitialAvailability: not CloudAvailabilityTarget.OnlineOnly })
        {
            throw new ArgumentException("Recoverable creation requires Fail collision behavior and online-only files.", nameof(specification));
        }

        OperationId = operationId;
        Specification = specification;
    }

    /// <summary>Gets the store-scoped original operation ID.</summary>
    public Guid OperationId { get; }

    /// <summary>Gets the original immutable child specification used for creation and replay.</summary>
    public CloudPlaceholderSpec Specification { get; }
}

/// <summary>Describes the outcome of one recoverable creation entry.</summary>
public enum CloudPlaceholderCreationRecoveryStatus
{
    /// <summary>The original native object and its official projection were durably recorded.</summary>
    Created = 0,
    /// <summary>A read-only check authenticated the original placeholder using its committed receipt.</summary>
    Recovered = 1,
    /// <summary>The store has no tracked request with this ID. Older untracked rows supply no evidence.</summary>
    NotFound = 2,
    /// <summary>A preparation survives without a committed original binding. Keep the request pending.</summary>
    Pending = 3,
    /// <summary>The original target or its recorded parent is absent at the requested address.</summary>
    Missing = 4,
    /// <summary>The target or its recorded parent is a different native object or no longer matches the original identity.</summary>
    Replaced = 5,
    /// <summary>The saved request belongs to another store scope or native sync root.</summary>
    WrongRoot = 6,
    /// <summary>The request differs from its durable specification, or a native or official occupant blocks new creation.</summary>
    Conflict = 7,
    /// <summary>Native metadata, safe ancestry or creation correlation could not be verified.</summary>
    Unavailable = 8,
    /// <summary>Windows returned a creation failure. The saved preparation supplies no binding.</summary>
    Failed = 9,
    /// <summary>An earlier result stopped the batch before this entry ran.</summary>
    NotProcessed = 10,
}

/// <summary>Owns historical evidence for one committed native placeholder creation.</summary>
/// <remarks>
/// This immutable receipt has no handles and remains readable after owner disposal. It describes
/// the original creation and provides no lease on the current namespace or authorization to modify it.
/// </remarks>
public sealed class CloudPlaceholderCreationReceipt
{
    internal CloudPlaceholderCreationReceipt(Guid operationId, string relativePath, CloudItemKind kind,
        CloudLocalFileBinding binding, ReadOnlySpan<byte> identity, long createUsn)
    {
        OperationId = operationId;
        RelativePath = relativePath;
        Kind = kind;
        Binding = binding;
        PlaceholderIdentity = identity.ToArray();
        CreateUsn = createUsn;
    }

    /// <summary>Gets the original operation ID within the durable store.</summary>
    public Guid OperationId { get; }
    /// <summary>Gets the original normalized path relative to the owning sync root.</summary>
    public string RelativePath { get; }
    /// <summary>Gets the original file-system kind.</summary>
    public CloudItemKind Kind { get; }
    /// <summary>Gets the original 64-bit volume serial and the two opaque 128-bit native identifiers.</summary>
    public CloudLocalFileBinding Binding { get; }
    /// <summary>Gets the original exact opaque placeholder identity bytes owned by this receipt.</summary>
    public ReadOnlyMemory<byte> PlaceholderIdentity { get; }
    /// <summary>Gets the final USN returned by the original native create.</summary>
    public long CreateUsn { get; }
}

/// <summary>Contains the outcome of one original creation or read-only authentication.</summary>
/// <remarks>
/// Each result is immutable and safe to read after the owner closes. Acquire the application's own
/// namespace guard and verify the returned binding on that guard before taking later action.
/// </remarks>
public sealed class CloudPlaceholderCreationRecoveryResult
{
    internal CloudPlaceholderCreationRecoveryResult(Guid operationId, CloudPlaceholderCreationRecoveryStatus status,
        CloudPlaceholderCreationReceipt? receipt = null, Exception? error = null)
    {
        OperationId = operationId;
        Status = status;
        Receipt = receipt;
        Error = error;
    }

    /// <summary>Gets the operation ID of the input entry.</summary>
    public Guid OperationId { get; }
    /// <summary>Gets the typed result.</summary>
    public CloudPlaceholderCreationRecoveryStatus Status { get; }
    /// <summary>Gets authenticated original evidence for Created or Recovered results, otherwise null.</summary>
    public CloudPlaceholderCreationReceipt? Receipt { get; }
    /// <summary>Gets any preserved native failure or unavailable evidence diagnostic.</summary>
    public Exception? Error { get; }
}
