namespace CfSharp;

public sealed partial class CloudLocalChangeFeed
{
    private readonly Guid _scanScope = Guid.NewGuid();

    /// <summary>Captures a finite boundary for non-waiting, bounded reads of pending local changes.</summary>
    /// <param name="cancellationToken">Token that cancels the read without changing durable state.</param>
    /// <returns>A handle-free scan owned by this running feed, including its reconciliation status.</returns>
    /// <remarks>
    /// Journal and recovery markers are read in one short transaction. Later enqueues lie outside
    /// the captured boundary. Call ReadPageAsync with zero initially; restarting requires a new
    /// scan. This Windows feed can have concurrent consumers; callers own their dispatch and ACK
    /// coordination. Capturing a scan does not wait for native notifications still being processed.
    /// </remarks>
    /// <exception cref="NotSupportedException">The custom journal does not implement optional paging.</exception>
    /// <exception cref="InvalidOperationException">The feed is not started or its worker failed.</exception>
    /// <exception cref="ObjectDisposedException">The feed has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public async ValueTask<CloudLocalChangeScan> BeginScanAsync(CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        ThrowIfFailed();
        await using ICloudStateTransaction transaction = await _stateStore.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        ICloudOperationJournalPaging paging = RequirePaging(transaction);
        long through = await paging.GetHighWaterSequenceAsync(cancellationToken).ConfigureAwait(false);
        bool rescan = await RequiresScanReconciliationAsync(transaction, cancellationToken).ConfigureAwait(false);
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return new CloudLocalChangeScan(_scanScope, through, rescan);
    }

    /// <summary>Reads one typed page inside a previously captured scan, without waiting or acknowledging.</summary>
    /// <param name="scan">The scan created by this feed during its current lifetime.</param>
    /// <param name="afterSequence">Exclusive lower bound, initially zero, then the preceding page's cursor.</param>
    /// <param name="limit">Maximum delivered changes, from one through 4096, independent of BatchSize.</param>
    /// <param name="cancellationToken">Token that cancels the query without advancing durable progress.</param>
    /// <returns>An owned page. Empty terminal pages return the scan boundary with HasMore false.</returns>
    /// <remarks>
    /// Reads preserve original journal identifiers, nullable item associations, sequences, paths,
    /// and observation times. An ACK between pages may remove rows; new writes never extend this
    /// scan. Stop on RequiresFullRescan even if HasMore is true. No remote-provider code is invoked.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The scan is null.</exception>
    /// <exception cref="ArgumentException">The scan belongs to a different feed or runtime.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The cursor is outside the scan or the limit is invalid.</exception>
    /// <exception cref="NotSupportedException">The journal lacks the optional paging capability.</exception>
    /// <exception cref="CloudLocalChangeJournalException">An official local change cannot be decoded; its row remains pending.</exception>
    /// <exception cref="InvalidOperationException">The feed is not started or its worker failed.</exception>
    /// <exception cref="ObjectDisposedException">The feed has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The read was canceled.</exception>
    public async ValueTask<CloudLocalChangePage> ReadPageAsync(CloudLocalChangeScan scan, long afterSequence,
        int limit, CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        ThrowIfFailed();
        ArgumentNullException.ThrowIfNull(scan);
        if (scan.StoreScope != _scanScope)
        {
            throw new ArgumentException("The scan belongs to another feed lifetime. Begin a new scan on this feed.", nameof(scan));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(afterSequence, scan.ThroughSequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 4096);
        await using ICloudStateTransaction transaction = await _stateStore.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        CloudOperationJournalPage journalPage = await RequirePaging(transaction)
            .ReadPageAsync(afterSequence, scan.ThroughSequence, limit, cancellationToken).ConfigureAwait(false);
        bool rescan = scan.RequiresFullRescan ||
            await RequiresScanReconciliationAsync(transaction, cancellationToken).ConfigureAwait(false);
        CloudLocalChange[] changes = rescan ? [] : journalPage.Operations.Select(DecodeScannedChange).ToArray();
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return new CloudLocalChangePage(changes, rescan ? afterSequence : journalPage.LastScannedSequence,
            rescan ? journalPage.Operations.Count != 0 : journalPage.HasMore, rescan);
    }

    private CloudLocalChange DecodeScannedChange(CloudOperationJournalEntry operation)
    {
        try
        {
            CloudLocalChange change = ToChange(operation);
            CloudItemPathResolver.Resolve(_syncRootPath, change.RelativePath, allowRoot: false);
            if (change.PreviousRelativePath is not null)
            {
                CloudItemPathResolver.Resolve(_syncRootPath, change.PreviousRelativePath, allowRoot: false);
            }

            if ((change.Kind == CloudLocalChangeKind.Move) != (change.PreviousRelativePath is not null))
            {
                throw new InvalidOperationException("The local-change move paths are inconsistent.");
            }

            return change;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            throw new CloudLocalChangeJournalException(operation, exception);
        }
    }

    private static ICloudOperationJournalPaging RequirePaging(ICloudStateTransaction transaction) =>
        transaction.Operations as ICloudOperationJournalPaging ??
        throw new NotSupportedException("This state store does not implement ICloudOperationJournalPaging. Legacy batch reads remain available.");

    private static async ValueTask<bool> RequiresScanReconciliationAsync(ICloudStateTransaction transaction,
        CancellationToken cancellationToken) =>
        (await ReadCheckpointAsync(transaction, cancellationToken).ConfigureAwait(false)).RequiresFullRescan ||
        (await transaction.Checkpoints.ListAsync(RemoteCreationIntent.ObservationsPrefix, cancellationToken)
            .ConfigureAwait(false)).Count != 0 ||
        (await transaction.Checkpoints.ListAsync(RemoteCreationIntent.Prefix, cancellationToken)
            .ConfigureAwait(false)).Any(value => !RemoteCreationIntent.Decode(value.Value).Committed);
}
