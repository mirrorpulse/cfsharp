using System.Collections.ObjectModel;

namespace CfSharp;

/// <summary>Retains a finite journal boundary belonging to one running local-change feed.</summary>
/// <remarks>
/// This immutable, concurrently readable value owns no transaction, reader, watcher, or native
/// handle. It is valid only with the feed that created it during that feed's current lifetime.
/// Retaining or serializing its scalar properties does not create a resumable cross-process scan.
/// After restarting, begin a new scan from zero against the durable pending journal.
/// </remarks>
public sealed class CloudLocalChangeScan
{
    internal CloudLocalChangeScan(Guid storeScope, long throughSequence, bool requiresFullRescan)
    {
        StoreScope = storeScope;
        ThroughSequence = throughSequence;
        RequiresFullRescan = requiresFullRescan;
    }

    /// <summary>Gets the opaque identity of this feed's running store-consumer lifetime.</summary>
    /// <remarks>This value is not a durable database identity or native object binding.</remarks>
    public Guid StoreScope { get; }

    /// <summary>Gets the inclusive highest pending sequence captured when the scan began.</summary>
    public long ThroughSequence { get; }

    /// <summary>Gets whether reconciliation was required when this scan began.</summary>
    /// <remarks>Pages also check for a reconciliation condition that arose after capture.</remarks>
    public bool RequiresFullRescan { get; }
}

/// <summary>Contains an owned, bounded typed journal page and its continuation facts.</summary>
/// <remarks>
/// The immutable collection is safe for concurrent reads after the originating transaction ends.
/// A page never acknowledges operations. When reconciliation is required, changes are withheld
/// and the cursor does not advance; stop scanning, reconcile, acknowledge the rescan, and start
/// a new scan. HasMore then describes pending rows, not permission to dispatch them.
/// </remarks>
public sealed class CloudLocalChangePage
{
    internal CloudLocalChangePage(IReadOnlyList<CloudLocalChange> changes, long lastScannedSequence,
        bool hasMore, bool requiresFullRescan)
    {
        Changes = new ReadOnlyCollection<CloudLocalChange>(changes.ToArray());
        LastScannedSequence = lastScannedSequence;
        HasMore = hasMore;
        RequiresFullRescan = requiresFullRescan;
    }

    /// <summary>Gets the immutable changes in strictly ascending durable sequence order.</summary>
    public IReadOnlyList<CloudLocalChange> Changes { get; }

    /// <summary>Gets the exclusive lower bound for the next read; use it without adding one.</summary>
    public long LastScannedSequence { get; }

    /// <summary>Gets whether an undelivered row remained in this scan's range during the read.</summary>
    /// <remarks>Concurrent acknowledgements may make the next page empty.</remarks>
    public bool HasMore { get; }

    /// <summary>Gets whether dispatch must stop until full reconciliation and a new scan.</summary>
    public bool RequiresFullRescan { get; }
}

/// <summary>Identifies a durable operation that cannot be decoded as an official local change.</summary>
/// <remarks>
/// The offending operation remains pending with its original payload, identifier, and sequence.
/// This exception owns no storage resources and preserves the decoding failure as InnerException.
/// Do not acknowledge the row merely to continue scanning; repair or reconcile its meaning first.
/// </remarks>
public sealed class CloudLocalChangeJournalException : InvalidOperationException
{
    internal CloudLocalChangeJournalException(CloudOperationJournalEntry operation, Exception innerException)
        : base($"Journal operation {operation.OperationId:D} at sequence {operation.Sequence} is not a valid local change.", innerException)
    {
        OperationId = operation.OperationId;
        Sequence = operation.Sequence;
    }

    /// <summary>Gets the unchanged identifier of the operation that failed decoding.</summary>
    public Guid OperationId { get; }

    /// <summary>Gets the unchanged durable sequence of that operation.</summary>
    public long Sequence { get; }
}
