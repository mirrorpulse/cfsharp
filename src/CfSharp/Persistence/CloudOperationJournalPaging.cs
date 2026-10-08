using System.Collections.ObjectModel;

namespace CfSharp;

/// <summary>Provides optional, bounded keyset reads of a transactional operation journal.</summary>
/// <remarks>
/// Implement this capability on <see cref="ICloudStateTransaction.Operations"/> to support local
/// change scans without changing the legacy journal contract. Calls and returned repositories
/// have the owning transaction's single-consumer lifetime. Reads never acknowledge operations,
/// change their sequence or payload, or advance item revisions. Custom implementations must query
/// a bounded range rather than load the complete journal. No Windows API is invoked by this contract.
/// </remarks>
public interface ICloudOperationJournalPaging
{
    /// <summary>Reads the largest sequence currently present in the transaction's journal.</summary>
    /// <param name="cancellationToken">Token that cancels the read without changing the journal.</param>
    /// <returns>The largest pending sequence, or zero for an empty journal.</returns>
    /// <remarks>
    /// Capture this value once per scan. Monotonically assigned enqueue sequences keep later writes
    /// outside that scan even when acknowledged rows disappear. This is a delivery boundary, not a
    /// snapshot of item associations or permission to execute an operation.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The owning transaction has terminated.</exception>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    ValueTask<long> GetHighWaterSequenceAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads an ascending page with an exclusive lower and inclusive upper bound.</summary>
    /// <param name="afterSequence">Nonnegative exclusive lower bound; use zero for the first page.</param>
    /// <param name="throughSequence">Nonnegative inclusive scan boundary, at least the lower bound.</param>
    /// <param name="limit">Maximum delivered row count, from one through 4096.</param>
    /// <param name="cancellationToken">Token that cancels the query and releases its reader.</param>
    /// <returns>An owned, handle-free page. Empty terminal pages never wait for another enqueue.</returns>
    /// <remarks>
    /// All operation kinds remain in sequence order. An implementation may read one additional row
    /// to determine continuation, but must not advance the cursor past an undelivered row. HasMore
    /// describes this transaction: concurrent acknowledgements may make the next page empty.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The bounds or limit are invalid.</exception>
    /// <exception cref="InvalidOperationException">The owning transaction has terminated.</exception>
    /// <exception cref="OperationCanceledException">The query was canceled.</exception>
    ValueTask<CloudOperationJournalPage> ReadPageAsync(
        long afterSequence,
        long throughSequence,
        int limit,
        CancellationToken cancellationToken = default);
}

/// <summary>Contains one immutable, bounded transactional journal delivery.</summary>
/// <remarks>
/// This value copies its row collection, owns no storage resources, and is safe for concurrent
/// reads after its originating transaction ends. ItemId retains the journal's nullable association
/// semantics; it is not native object proof. Storage implementations construct this value after
/// enforcing the requested scan bounds and limit.
/// </remarks>
public sealed class CloudOperationJournalPage
{
    /// <summary>Copies a validated ascending delivery and its continuation facts.</summary>
    /// <param name="operations">Delivered rows with positive, strictly ascending sequences.</param>
    /// <param name="lastScannedSequence">
    /// The last delivered sequence, or the scan's inclusive upper bound for a terminal empty page.
    /// </param>
    /// <param name="hasMore">Whether an undelivered row remains inside the bounded range.</param>
    /// <exception cref="ArgumentNullException">The collection is null.</exception>
    /// <exception cref="ArgumentException">Rows, ordering, or continuation facts are inconsistent.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The cursor is negative.</exception>
    public CloudOperationJournalPage(
        IReadOnlyList<CloudOperationJournalEntry> operations,
        long lastScannedSequence,
        bool hasMore)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentOutOfRangeException.ThrowIfNegative(lastScannedSequence);
        CloudOperationJournalEntry[] rows = operations.ToArray();
        long previous = 0;
        foreach (CloudOperationJournalEntry row in rows)
        {
            if (row is null || row.Sequence <= previous)
            {
                throw new ArgumentException("Journal page sequences must be positive and strictly ascending.", nameof(operations));
            }

            previous = row.Sequence;
        }

        if ((rows.Length != 0 && lastScannedSequence != previous) || (hasMore && rows.Length == 0))
        {
            throw new ArgumentException("Journal page continuation must follow the last delivered row.", nameof(lastScannedSequence));
        }

        Operations = new ReadOnlyCollection<CloudOperationJournalEntry>(rows);
        LastScannedSequence = lastScannedSequence;
        HasMore = hasMore;
    }

    /// <summary>Gets the owned, strictly ordered delivered operations.</summary>
    public IReadOnlyList<CloudOperationJournalEntry> Operations { get; }

    /// <summary>Gets the exclusive lower bound to use for the next page, without adding one.</summary>
    public long LastScannedSequence { get; }

    /// <summary>Gets whether another row remained in the range at the page's read point.</summary>
    public bool HasMore { get; }
}
