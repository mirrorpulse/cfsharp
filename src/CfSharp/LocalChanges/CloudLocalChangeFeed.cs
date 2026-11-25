using System.ComponentModel;
using System.Text;
using System.Threading.Channels;

namespace CfSharp;

/// <summary>
/// Watches one sync root, normalizes local notifications, and durably journals upload candidates.
/// </summary>
/// <remarks>
/// <para>
/// The feed does not own the supplied state store. When created by <see cref="CloudFileSystem"/>,
/// the file system stops it before disposing that store. A feed never owns file content and never
/// invokes remote-provider code.
/// </para>
/// <para>
/// Windows directory notifications are advisory. Buffer overflow, watcher errors, inaccessible paths, and malformed
/// rename pairs produce <see cref="CloudLocalChangeBatch.RequiresFullRescan"/>. A permanently
/// failed metadata subscription stops this feed. Dispose and recreate it before resuming. Applications
/// must perform a full reconciliation before acknowledging loss; periodic reconciliation is
/// intentionally outside CfSharp.
/// </para>
/// <para>
/// Known file rename paths, retained source/destination ancestor membership, and the new journal
/// observation commit in one transaction. Ancestor native bindings, immutable directory recovery
/// evidence and existing pending operations remain unchanged. Shared ancestors are refreshed once.
/// First discovery of a local file or directory also refreshes captured ancestor membership in
/// its item/journal transaction, retaining its native file reference when that can be observed.
/// </para>
/// </remarks>
public sealed partial class CloudLocalChangeFeed : IDisposable, IAsyncDisposable
{
    internal const string CheckpointName = "cfsharp.local-change-feed.v1";

    private readonly string _syncRootPath;
    private readonly ICloudStateStore _stateStore;
    private readonly CloudLocalChangeFeedOptions _options;
    private readonly ILocalChangeSource _source;
    private readonly Action<CloudLocalChangeFeed>? _onDisposed;
    private readonly Func<IReadOnlyList<CloudItemOperationScope>, CancellationToken, ValueTask<IDisposable>>? _acquireOperation;
    private readonly Func<IReadOnlyList<CloudItemOperationScope>, CancellationToken, ValueTask<IDisposable>>? _acquireAttributeOperation;
    private readonly CloudSecurityInspection? _securityInspection;
    private readonly CloudMetadataEchoAuthority? _metadataEchoAuthority;
    private readonly Channel<LocalChangeSourceEvent> _sourceEvents;
    private readonly Channel<bool> _availability = Channel.CreateUnbounded<bool>(
        new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource<object?> _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private Task? _sourceTask;
    private Task? _processorTask;
    private LocalChangeSourceEvent? _pendingRename;
    private long _nextObservation;
    private int _rescanNoticeDelivered;
    private Exception? _failure;
    private int _terminalSourceSignaled;
    private int _overflowSignaled;
    private int _started;
    private int _disposed;

    internal CloudLocalChangeFeed(
        string syncRootPath,
        ICloudStateStore stateStore,
        CloudLocalChangeFeedOptions options,
        ILocalChangeSource source,
        Action<CloudLocalChangeFeed>? onDisposed = null,
        Func<IReadOnlyList<CloudItemOperationScope>, CancellationToken, ValueTask<IDisposable>>? acquireOperation = null,
        CloudSecurityInspection? securityInspection = null,
        Func<IReadOnlyList<CloudItemOperationScope>, CancellationToken, ValueTask<IDisposable>>? acquireAttributeOperation = null,
        CloudMetadataEchoAuthority? metadataEchoAuthority = null)
    {
        _syncRootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(syncRootPath));
        _stateStore = stateStore;
        _options = options;
        _options.Validate();
        _source = source;
        _onDisposed = onDisposed;
        _acquireOperation = acquireOperation;
        _acquireAttributeOperation = acquireAttributeOperation;
        _securityInspection = securityInspection;
        _metadataEchoAuthority = metadataEchoAuthority;
        _sourceEvents = Channel.CreateBounded<LocalChangeSourceEvent>(
            new BoundedChannelOptions(options.BufferCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
    }

    /// <summary>Gets the absolute sync-root path watched by this feed.</summary>
    public string SyncRootPath => _syncRootPath;

    /// <summary>Gets whether the feed has been started and can deliver journal entries.</summary>
    public bool IsStarted => Volatile.Read(ref _started) != 0 && Volatile.Read(ref _disposed) == 0;

    internal Task DisposeCompletion => _disposeCompletion.Task;

    /// <summary>Starts the native watcher and the durable normalization worker.</summary>
    /// <param name="cancellationToken">Token that cancels startup before the watcher is opened.</param>
    /// <remarks>
    /// Background notification consumption does not require pumping the starting synchronization context.
    /// Startup completes only after every native subscription has opened successfully. A startup
    /// failure disposes this feed and its acquired resources before propagating the original error;
    /// create a new feed before retrying.
    /// A cleanup failure is retained in the original exception's <see cref="Exception.Data"/>
    /// under <c>CfSharp.LocalChangeFeed.StartupCleanupFailure</c> and reported through
    /// <see cref="CloudDiagnostics"/>. Keep the store open and retry disposal if its loss marker
    /// could not be persisted. Cleanup and diagnostic failures do not replace the startup error.
    /// Security notifications use the NTFS extended directory API on Windows 10 version 1709 / Windows
    /// Server 2019 or later. The feed owns its security read issuer until native cancellation completes;
    /// callers do not need to retain the startup thread. Unprovable security associations require reconciliation.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The feed has already been started.</exception>
    /// <exception cref="ObjectDisposedException">The feed has been disposed.</exception>
    /// <exception cref="ArgumentException">The watched directory no longer exists.</exception>
    /// <exception cref="IOException">The native watcher cannot access the directory.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the watched directory is denied.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">Windows cannot open or start the native directory subscription; the native error is retained.</exception>
    /// <exception cref="EntryPointNotFoundException">Windows does not provide the required extended directory API.</exception>
    /// <exception cref="NotSupportedException">The journal does not declare or honor <see cref="ICloudOperationJournalItemAssociationUpdates"/>. No notification source is opened; recovery evidence remains pending.</exception>
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                throw new InvalidOperationException("The local-change feed has already been started.");
            }
        }

        bool sourceMayHaveStarted = false;
        try
        {
            await LoadCheckpointAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            sourceMayHaveStarted = true;
            _sourceTask = _source.StartAsync(PublishAsync, _shutdown.Token);
            await _sourceTask.ConfigureAwait(false);
            _processorTask = ProcessEventsAsync();
        }
        catch (Exception startupFailure)
        {
            Interlocked.Exchange(ref _started, 0);
            // Startup has already observed this task. Do not observe its error again as a
            // disposal error. Only an attempted source can have accepted notifications. A
            // canceled checkpoint read must not wait for a writer to record nonexistent loss.
            _sourceTask = null;
            if (sourceMayHaveStarted) { SignalLoss(); }
            try { await DisposeAsync().ConfigureAwait(false); }
            catch (Exception cleanupFailure)
            {
                LocalChangeStartupFailure.Preserve(startupFailure, cleanupFailure, source: false);
            }
            throw;
        }
    }

    /// <summary>
    /// Reads the earliest durable local operations, waiting until at least one operation or a
    /// full-rescan signal is available.
    /// </summary>
    /// <param name="cancellationToken">Token that cancels the read without acknowledging anything.</param>
    /// <returns>An ordered bounded batch. The batch is immutable and safe to retain.</returns>
    /// <remarks>
    /// Loss pending before or received during the journal read withholds changes and requires
    /// full reconciliation, even if the worker persists its marker before the read finishes.
    /// Reading does not clear that marker or acknowledge any pending operation.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The feed has not been started or has stopped with a failure.</exception>
    /// <exception cref="ObjectDisposedException">The feed has been disposed.</exception>
    public async ValueTask<CloudLocalChangeBatch> ReadBatchAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        while (true)
        {
            ThrowIfFailed();
            // Capture the deadline before reading the receipts. If another reader
            // prunes the grant during this read, we still wake by its deadline.
            long? metadataExpiration = _metadataEchoAuthority?.GetNextExpirationTimestamp();
            long generation = Volatile.Read(ref _persistedLossGeneration);
            long acknowledgementGeneration = Volatile.Read(ref _rescanAcknowledgementGeneration);
            await using ICloudStateTransaction transaction = await _stateStore
                .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            LocalChangeCheckpoint checkpoint = await ReadCheckpointAsync(transaction, cancellationToken)
                .ConfigureAwait(false);
            bool recovery = await RequiresCreationReconciliationAsync(transaction, cancellationToken).ConfigureAwait(false) ||
                await RequiresMetadataReconciliationAsync(transaction, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<CloudOperationJournalEntry> operations = await transaction.Operations
                .ListAsync(_options.BatchSize, cancellationToken).ConfigureAwait(false);
            int validatedCount = await ValidatedMetadataPrefixLengthAsync(transaction, operations, cancellationToken).ConfigureAwait(false);
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            ThrowIfFailed();
            // Persistence can clear HasUnpersistedLoss after this snapshot was read. Capture
            // the durable boundary before reading so loss already pending at entry is also
            // retained across that handoff, rather than mistaken for part of this snapshot.
            bool rescan = recovery || checkpoint.RequiresFullRescan || HasUnpersistedLoss ||
                generation != Volatile.Read(ref _lossGeneration);
            if (rescan && (recovery || operations.Count != 0 ||
                Interlocked.CompareExchange(ref _rescanNoticeDelivered, 1, 0) == 0))
            {
                RememberObservedRescan(acknowledgementGeneration);
                return new CloudLocalChangeBatch([], requiresFullRescan: true);
            }

            if (!rescan && validatedCount != 0)
            {
                return new CloudLocalChangeBatch(operations.Take(validatedCount).Select(ToChange).ToArray(), requiresFullRescan: false);
            }

            try
            {
                await WaitForAvailabilityAsync(metadataExpiration, cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                ThrowIfFailed();
                throw;
            }
        }
    }

    /// <summary>
    /// Acknowledges journal entries after the application has durably accepted their meaning.
    /// </summary>
    /// <param name="operationIds">One or more operation identifiers from a delivered batch.</param>
    /// <param name="cancellationToken">Token that cancels before the acknowledgement commits.</param>
    /// <remarks>
    /// Acknowledgement is idempotent: an already removed operation is ignored. The overload
    /// accepting revision-aware acknowledgements can atomically update the durable revision.
    /// An operation still awaiting post-commit metadata validation cannot be acknowledged.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A supplied operation still awaits metadata validation.</exception>
    public async ValueTask AcknowledgeAsync(
        IEnumerable<Guid> operationIds,
        CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(operationIds);
        await AcknowledgeAsync(
            operationIds
                .Where(id => id != Guid.Empty)
                .Distinct()
                .Select(id => new CloudLocalChangeAcknowledgement(id)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Acknowledges local operations and optionally records the remote revision confirmed after
    /// upload. The journal removal and revision update commit atomically.
    /// </summary>
    /// <param name="acknowledgements">Operation acknowledgements from a delivered batch.</param>
    /// <param name="cancellationToken">Token that cancels before the acknowledgement commits.</param>
    /// <exception cref="InvalidOperationException">A supplied operation still awaits metadata validation.</exception>
    public async ValueTask AcknowledgeAsync(
        IEnumerable<CloudLocalChangeAcknowledgement> acknowledgements,
        CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        ArgumentNullException.ThrowIfNull(acknowledgements);
        CloudLocalChangeAcknowledgement[] values = acknowledgements
            .Where(acknowledgement => acknowledgement is not null)
            .GroupBy(acknowledgement => acknowledgement.OperationId)
            .Select(group => group.Last())
            .ToArray();
        if (values.Length == 0)
        {
            return;
        }

        // A revision acknowledgement changes the same official row that remote
        // metadata recovery verifies and repairs. Order these writes on custom stores too.
        using IDisposable? publication = values.Any(value => value.RemoteRevision is not null)
            ? await AcquirePublicationAsync(cancellationToken).ConfigureAwait(false) : null;
        await using ICloudStateTransaction transaction = await _stateStore
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (CloudLocalChangeAcknowledgement acknowledgement in values)
        {
            CloudOperationJournalEntry? operation = await transaction.Operations
                .GetAsync(acknowledgement.OperationId, cancellationToken)
                .ConfigureAwait(false);
            if (operation is not null)
            {
                if (await ValidatedMetadataPrefixLengthAsync(transaction, [operation], cancellationToken).ConfigureAwait(false) == 0)
                {
                    throw new InvalidOperationException("The local metadata observation has not completed validation.");
                }
                if (acknowledgement.RemoteRevision is not null && operation.ItemId is Guid itemId)
                {
                    CloudItemState? item = await transaction.Items
                        .GetByItemIdAsync(itemId, cancellationToken).ConfigureAwait(false);
                    if (item is not null)
                    {
                        await transaction.Items.UpsertAsync(
                            new CloudItemState(
                                item.ItemId,
                                item.RemoteId,
                                item.RelativePath,
                                item.Kind,
                                acknowledgement.RemoteRevision,
                                item.LocalFileId,
                                item.IsTombstone,
                                DateTimeOffset.UtcNow),
                            cancellationToken).ConfigureAwait(false);
                    }
                }

                await transaction.Operations.RemoveAsync(acknowledgement.OperationId, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.Checkpoints.RemoveAsync(SecurityValidationName(acknowledgement.OperationId), cancellationToken).ConfigureAwait(false);
            }
        }

        ThrowIfFailed();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the durable rescan-required marker after the application has completed a full
    /// reconciliation of the sync root.
    /// </summary>
    /// <param name="cancellationToken">Token that cancels before the marker commit.</param>
    /// <remarks>
    /// For remote metadata this overload uses the first rescan requirement delivered since the
    /// last acknowledgement. After a stale acknowledgement, read a fresh rescan requirement
    /// before rescanning. For a caller-initiated rescan, call <see cref="BeginFullRescan"/>
    /// before scanning and acknowledge its returned boundary.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The feed has not started or has stopped with a failure, a remote creation, file move or Delete
    /// remains pending, a loss signal is not yet durable, or new loss or metadata evidence
    /// invalidated the reconciliation boundary.
    /// Finish recovery and retry after the worker has persisted its signal.
    /// </exception>
    public ValueTask AcknowledgeFullRescanAsync(CancellationToken cancellationToken = default) =>
        AcknowledgeFullRescanCoreAsync(Volatile.Read(ref _observedRescan), validateRescanLoss: false, cancellationToken);

    /// <summary>Acknowledges a full rescan completed from the supplied reconciliation boundary.</summary>
    /// <param name="rescan">The boundary captured by <see cref="BeginFullRescan"/> before reconciliation.</param>
    /// <param name="cancellationToken">Token that cancels before the marker commit.</param>
    /// <remarks>
    /// Only the feed that created the boundary can accept it. A new loss signal, another
    /// acknowledgement, or new metadata evidence can invalidate it. Receipts from calls
    /// in flight and grants still valid when reconciliation started remain protected.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The boundary is null.</exception>
    /// <exception cref="ArgumentException">The boundary belongs to another feed lifetime.</exception>
    /// <exception cref="InvalidOperationException">The boundary is stale, recovery is pending, or the feed is not running.</exception>
    /// <exception cref="ObjectDisposedException">The feed has been disposed.</exception>
    public ValueTask AcknowledgeFullRescanAsync(CloudLocalChangeRescan rescan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rescan);
        if (rescan.StoreScope != _scanScope)
        {
            throw new ArgumentException("The rescan belongs to another feed lifetime.", nameof(rescan));
        }
        return AcknowledgeFullRescanCoreAsync(rescan, validateRescanLoss: true, cancellationToken);
    }

    private async ValueTask AcknowledgeFullRescanCoreAsync(CloudLocalChangeRescan? rescan, bool validateRescanLoss,
        CancellationToken cancellationToken)
    {
        EnsureStarted();
        // Legacy readers can report an overflow before its marker becomes durable.
        // The metadata generation below separately protects changes to receipts.
        long generation = validateRescanLoss ? rescan!.LossGeneration : Volatile.Read(ref _lossGeneration);
        CloudLocalChangeRescan boundary = rescan ?? CaptureRescanBoundary();
        using IDisposable? publication = await AcquirePublicationAsync(cancellationToken).ConfigureAwait(false);
        if (boundary.MetadataGeneration != _metadataEchoAuthority?.ReconciliationGeneration)
        {
            if (!validateRescanLoss && rescan is not null)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_observedRescan, rescan))
                    {
                        Volatile.Write(ref _observedRescan, null);
                        Volatile.Write(ref _rescanNoticeDelivered, 0);
                    }
                }
            }
            throw new InvalidOperationException("Metadata reconciliation changed after the rescan boundary. Repeat reconciliation from a new boundary.");
        }
        if (rescan is not null && boundary.AcknowledgementGeneration != Volatile.Read(ref _rescanAcknowledgementGeneration))
        {
            throw new InvalidOperationException("Another acknowledgement ended this rescan boundary. Begin a new full rescan.");
        }
        await using ICloudStateTransaction transaction = await _stateStore
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (rescan is null && _metadataEchoAuthority is not null &&
            ((await transaction.Checkpoints.ListAsync(RemoteMetadataIntent.PendingPrefix, cancellationToken).ConfigureAwait(false)).Count != 0 ||
             (await transaction.Checkpoints.ListAsync(RemoteMetadataIntent.PendingObservationPrefix, cancellationToken).ConfigureAwait(false)).Count != 0))
        {
            throw new InvalidOperationException("Capture a boundary with BeginFullRescan before reconciling pending remote metadata.");
        }
        IReadOnlyList<CloudStateCheckpoint> creations = await transaction.Checkpoints
            .ListAsync(RemoteCreationIntent.Prefix, cancellationToken).ConfigureAwait(false);
        if (creations.Count != 0 ||
            (await transaction.Checkpoints.ListAsync(RemoteMoveIntent.Prefix, cancellationToken).ConfigureAwait(false)).Count != 0 ||
            (await transaction.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix, cancellationToken).ConfigureAwait(false)).Count != 0)
        {
            throw new InvalidOperationException("Replay pending remote creations, moves and deletions before acknowledging reconciliation.");
        }
        await ClearReconciledMetadataIntentsAsync(transaction, boundary.RetainReceipt, cancellationToken).ConfigureAwait(false);
        foreach (CloudStateCheckpoint observation in await transaction.Checkpoints
            .ListAsync(RemoteMetadataIntent.PendingObservationPrefix, cancellationToken).ConfigureAwait(false))
        {
            await transaction.Checkpoints.RemoveAsync(observation.Name, cancellationToken).ConfigureAwait(false);
        }

        foreach (CloudStateCheckpoint observation in await transaction.Checkpoints
            .ListAsync(RemoteCreationIntent.ObservationsPrefix, cancellationToken).ConfigureAwait(false))
        {
            await transaction.Checkpoints.RemoveAsync(observation.Name, cancellationToken).ConfigureAwait(false);
        }

        foreach (CloudStateCheckpoint observation in await transaction.Checkpoints
            .ListAsync(NamespaceObservationsPrefix, cancellationToken).ConfigureAwait(false))
        {
            // Reconciliation clears the fence, not the original pending journal operation.
            await transaction.Checkpoints.RemoveAsync(observation.Name, cancellationToken).ConfigureAwait(false);
        }

        LocalChangeCheckpoint checkpoint = await ReadCheckpointAsync(
            transaction,
            cancellationToken).ConfigureAwait(false);
        if (HasUnpersistedLoss || generation != Volatile.Read(ref _lossGeneration))
        {
            throw new InvalidOperationException("A new loss signal must be persisted and reconciled before acknowledging the rescan.");
        }
        ThrowIfFailed();

        await transaction.Checkpoints.UpsertAsync(
            new CloudStateCheckpoint(
                CheckpointName,
                new LocalChangeCheckpoint(checkpoint.Observation, RequiresFullRescan: false).Encode(),
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        // Full reconciliation establishes a new consumer boundary even when pending creation
        // recovery cleared its records before this feed read them. Pre-reconciliation scans must
        // not resume after an acknowledgement makes the durable marker disappear.
        lock (_gate)
        {
            Interlocked.Increment(ref _rescanAcknowledgementGeneration);
            Volatile.Write(ref _observedRescan, null);
        }
        Volatile.Write(ref _rescanNoticeDelivered, 0);
    }

    /// <summary>
    /// Records a provider-originated write suppression window so its local echo is not journaled.
    /// </summary>
    /// <param name="kind">Expected local operation kind.</param>
    /// <param name="relativePath">Canonical path relative to the sync root.</param>
    /// <param name="expiresAt">UTC time after which the suppression is ignored.</param>
    /// <param name="itemId">Known item identity, when available.</param>
    /// <param name="cancellationToken">Token that cancels before the suppression commits.</param>
    /// <exception cref="NotSupportedException">Metadata suppression requires an explicit remote attribute provenance policy.</exception>
    public async ValueTask SuppressProviderEchoAsync(
        CloudStateOperationKind kind,
        string relativePath,
        DateTimeOffset expiresAt,
        Guid? itemId = null,
        CancellationToken cancellationToken = default) =>
        await SuppressProviderEchoAsync(
            kind,
            relativePath,
            expiresAt,
            itemId,
            previousRelativePath: null,
            expectedObservationCount: 1,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Records a provider-originated write suppression window with optional source-path and
    /// multi-observation matching data.
    /// </summary>
    /// <param name="kind">Expected local operation kind.</param>
    /// <param name="relativePath">Canonical path relative to the sync root.</param>
    /// <param name="expiresAt">UTC time after which the suppression is ignored.</param>
    /// <param name="itemId">Known item identity, when available.</param>
    /// <param name="previousRelativePath">
    /// Optional second canonical path associated with the provider operation, such as a move source.
    /// </param>
    /// <param name="expectedObservationCount">
    /// Number of matching watcher observations to consume before the suppression is removed.
    /// </param>
    /// <param name="cancellationToken">Token that cancels before the suppression commits.</param>
    /// <remarks>
    /// Independent Windows security and attribute notifications do not consume path/kind tickets.
    /// For remote metadata, use <see cref="CloudRemoteApplyOptions.AttributeEchoPolicy"/> and its
    /// explicit caller-coordination contract. Existing metadata tickets never authorize suppression.
    /// </remarks>
    /// <exception cref="NotSupportedException"><paramref name="kind"/> is metadata.</exception>
    public async ValueTask SuppressProviderEchoAsync(
        CloudStateOperationKind kind,
        string relativePath,
        DateTimeOffset expiresAt,
        Guid? itemId,
        string? previousRelativePath,
        int expectedObservationCount = 1,
        CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        if (kind == CloudStateOperationKind.MetadataUpdate)
        { throw new NotSupportedException("Metadata echo suppression requires CloudRemoteApplyOptions.AttributeEchoPolicy and its explicit provenance contract."); }
        CloudItemPath path = CloudItemPathResolver.Resolve(_syncRootPath, relativePath, allowRoot: false);
        CloudItemPath? previousPath = previousRelativePath is null
            ? null
            : CloudItemPathResolver.Resolve(_syncRootPath, previousRelativePath, allowRoot: false);
        await using ICloudStateTransaction transaction = await _stateStore
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await transaction.EchoSuppressions.UpsertAsync(
            new CloudEchoSuppressionState(
                Guid.NewGuid(),
                itemId,
                 kind,
                 path.RelativePath,
                 Encoding.UTF8.GetBytes("local-change-feed/v1"),
                 expiresAt,
                 previousPath?.RelativePath,
                 expectedObservationCount),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Synchronously stops the watcher and processor.</summary>
    /// <remarks>
    /// Use <see cref="DisposeAsync"/> on UI or single-threaded contexts. If the processor does
    /// not stop within the configured shutdown timeout, this method blocks until the timeout and
    /// propagates <see cref="TimeoutException"/> while deferred cleanup continues in the
    /// background.
    /// </remarks>
    /// <exception cref="TimeoutException">
    /// The processor did not stop within the configured shutdown timeout.
    /// </exception>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    /// <summary>Stops the watcher and releases all feed-owned resources.</summary>
    /// <remarks>
    /// Accepted notifications drain before worker cancellation. A failed or timed-out drain persists
    /// a full-rescan fence before cancellation; this store transaction may outlast the worker timeout.
    /// If fence persistence fails, the worker remains uncanceled and disposal must be retried while
    /// the state store remains open. Concurrent disposal callers wait for worker cleanup.
    /// </remarks>
    /// <exception cref="TimeoutException">
    /// The processor did not stop within the configured shutdown timeout. Deferred cleanup keeps
    /// feed-owned resources alive until the processor exits.
    /// </exception>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            if (HasUnpersistedLoss)
            {
                await MarkRescanRequiredAsync(CancellationToken.None).ConfigureAwait(false);
                _onDisposed?.Invoke(this);
            }
            return;
        }

        Exception? failure = null;
        bool cancelProcessor = true;
        try
        {
            await _source.DisposeAsync().ConfigureAwait(false);
            if (_sourceTask is not null)
            {
                await _sourceTask.ConfigureAwait(false);
            }

            // Complete the producer first and let the processor drain notifications that were
            // already copied into the bounded channel. This is the journal's last safe point:
            // canceling the processor before draining would silently lose a queued change during
            // an otherwise clean feed shutdown.
            _sourceEvents.Writer.TryComplete();
            if (_processorTask is not null)
            {
                await _processorTask.WaitAsync(_options.ShutdownTimeout).ConfigureAwait(false);
            }

            if (HasUnpersistedLoss)
            {
                await MarkRescanRequiredAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            // Cancellation can abandon a notification before it creates a journal row. Fence
            // durable consumers first, while the owner still keeps the store alive. If persistence
            // fails, leave the worker uncanceled and retain ownership for a later marker retry.
            cancelProcessor = false;
            try
            {
                await MarkRescanRequiredAsync(CancellationToken.None).ConfigureAwait(false);
                cancelProcessor = true;
            }
            catch (Exception markerFailure)
            {
                failure = new AggregateException("The local-change feed could not persist shutdown loss.", exception, markerFailure);
            }
        }
        finally
        {
            if (cancelProcessor)
            {
                _shutdown.Cancel();
            }
            _sourceEvents.Writer.TryComplete();
            if (_processorTask is null || _processorTask.IsCompleted)
            {
                CompleteDispose();
            }
            else
            {
                _ = CompleteDisposeAfterProcessorAsync(_processorTask);
            }
        }

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private async Task CompleteDisposeAfterProcessorAsync(Task processorTask)
    {
        try
        {
            await processorTask.ConfigureAwait(false);
        }
        catch
        {
            // The first DisposeAsync caller observes the processor failure. The deferred cleanup
            // must still release feed-owned synchronization resources and notify the owner.
        }
        finally
        {
            CompleteDispose();
        }
    }

    private void CompleteDispose()
    {
        _availability.Writer.TryComplete();
        _shutdown.Dispose();
        if (!HasUnpersistedLoss)
        {
            _onDisposed?.Invoke(this);
        }
        _disposeCompletion.TrySetResult(null);
    }

    internal static CloudLocalChangeFeed CreateForTesting(
        string syncRootPath,
        ICloudStateStore stateStore,
        CloudLocalChangeFeedOptions options,
        ILocalChangeSource source,
        CloudSecurityInspection? securityInspection = null) =>
        new(syncRootPath, stateStore, options, source, securityInspection: securityInspection);

    private async Task ProcessEventsAsync()
    {
        try
        {
            // The processor outlives StartAsync. Its next notification must not depend on the
            // caller's UI/test context remaining available to commit loss or finish draining.
            await foreach (LocalChangeSourceEvent change in _sourceEvents.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                await ProcessEventAsync(change, _shutdown.Token).ConfigureAwait(false);
                if (Interlocked.Exchange(ref _overflowSignaled, 0) != 0)
                {
                    await MarkRescanRequiredAsync(_shutdown.Token).ConfigureAwait(false);
                }
            }

            // A producer can lose the race with channel completion after the last queued event
            // has been drained. Persist the overflow marker at this final clean-shutdown boundary
            // instead of leaving it only in process memory.
            if (Interlocked.Exchange(ref _overflowSignaled, 0) != 0)
            {
                await MarkRescanRequiredAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (_pendingRename is not null)
            {
                _pendingRename = null;
                await MarkRescanRequiredAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SignalLoss();
            try
            {
                await MarkRescanRequiredAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the worker's original failure when the store also cannot record loss.
            }
            Interlocked.CompareExchange(ref _failure, exception, null);
            _availability.Writer.TryComplete();
        }
    }

    private async ValueTask ProcessEventAsync(
        LocalChangeSourceEvent sourceEvent,
        CancellationToken cancellationToken)
    {
        if (sourceEvent.Action == LocalChangeSourceAction.RenamedOldName)
        {
            if (_pendingRename is not null)
            {
                await PersistChangeAsync(
                    CloudLocalChangeKind.Delete,
                    _pendingRename.Value.RelativePath,
                    previousRelativePath: null,
                    cancellationToken).ConfigureAwait(false);
            }

            _pendingRename = sourceEvent;
            return;
        }

        if (sourceEvent.Action == LocalChangeSourceAction.RenamedNewName)
        {
            if (_pendingRename is LocalChangeSourceEvent oldName)
            {
                _pendingRename = null;
                await PersistChangeAsync(
                    CloudLocalChangeKind.Move,
                    sourceEvent.RelativePath,
                    oldName.RelativePath,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await PersistChangeAsync(
                CloudLocalChangeKind.Create,
                sourceEvent.RelativePath,
                previousRelativePath: null,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_pendingRename is LocalChangeSourceEvent pending)
        {
            _pendingRename = null;
            await PersistChangeAsync(
                CloudLocalChangeKind.Delete,
                pending.RelativePath,
                previousRelativePath: null,
                cancellationToken).ConfigureAwait(false);
        }

        switch (sourceEvent.Action)
        {
            case LocalChangeSourceAction.Created:
                await PersistChangeAsync(
                    CloudLocalChangeKind.Create,
                    sourceEvent.RelativePath,
                    previousRelativePath: null,
                    cancellationToken).ConfigureAwait(false);
                break;
            case LocalChangeSourceAction.Modified:
            case LocalChangeSourceAction.SizeModified:
                await PersistChangeAsync(
                    CloudLocalChangeKind.ContentUpdate,
                    sourceEvent.RelativePath,
                    previousRelativePath: null,
                    cancellationToken,
                    sizeChange: sourceEvent.SizeChange).ConfigureAwait(false);
                break;
            case LocalChangeSourceAction.SecurityModified:
            case LocalChangeSourceAction.AttributesModified:
                await PersistChangeAsync(
                    CloudLocalChangeKind.MetadataUpdate,
                    sourceEvent.RelativePath,
                    previousRelativePath: null,
                    cancellationToken, sourceEvent).ConfigureAwait(false);
                break;
            case LocalChangeSourceAction.Deleted:
                await PersistChangeAsync(
                    CloudLocalChangeKind.Delete,
                    sourceEvent.RelativePath,
                    previousRelativePath: null,
                    cancellationToken).ConfigureAwait(false);
                break;
            case LocalChangeSourceAction.Overflow:
            case LocalChangeSourceAction.Error:
                await MarkRescanRequiredAsync(cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async ValueTask PersistChangeAsync(
        CloudLocalChangeKind kind,
        string relativePath,
        string? previousRelativePath,
        CancellationToken cancellationToken,
        LocalChangeSourceEvent? securityObservation = null,
        LocalSizeChange? sizeChange = null)
    {
        CloudItemPath path;
        try
        {
            path = securityObservation is null && sizeChange is null
                ? CloudItemPathResolver.Resolve(_syncRootPath, relativePath, allowRoot: false)
                : CloudItemPathResolver.ResolveLexically(_syncRootPath, relativePath, allowRoot: false);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // A path query can lose access without invalidating the root subscription.
            // Fence delivery for reconciliation before accepting another observation. The
            // catch covers only resolution; durable-store failures must still stop the worker.
            if (kind == CloudLocalChangeKind.ContentUpdate &&
                await TryRetainUnresolvedTimeObservationAsync(relativePath, cancellationToken).ConfigureAwait(false))
            { return; }
            await MarkRescanRequiredAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        CloudItemPath? previousPath = null;
        if (previousRelativePath is not null)
        {
            try
            {
                previousPath = CloudItemPathResolver.Resolve(
                    _syncRootPath,
                    previousRelativePath,
                    allowRoot: false);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
            {
                await MarkRescanRequiredAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        Guid operationId = Guid.NewGuid();
        // Acquire facade scopes before the store gate, preserving the facade's lock order.
        // Standalone deterministic feeds still use the native guards inside the projector.
        using IDisposable? operation = kind == CloudLocalChangeKind.Move && previousPath is not null && _acquireOperation is not null
            ? await _acquireOperation([CloudItemOperationScope.Subtree(previousPath.Value.FullPath),
                CloudItemOperationScope.Subtree(path.FullPath)], cancellationToken).ConfigureAwait(false)
            : null;
        try
        {
            using IDisposable? securityOperation = securityObservation is not null
                ? await AcquireSecurityOperationAsync(path, securityObservation.Value, cancellationToken).ConfigureAwait(false)
                : null;
            await PersistChangeAttemptAsync(kind, path, previousPath, operationId, observedAt, null,
                cancellationToken, securityObservation, sizeChange).ConfigureAwait(false);
        }
        catch (CloudNamespaceObservationUncertainException exception)
        {
            // Reuse the observation identity whether the original transaction rolled back
            // or staged the observation before a late native check failed.
            SignalLoss();
            if (kind == CloudLocalChangeKind.ContentUpdate && exception.IsDirectory)
            { kind = CloudLocalChangeKind.MetadataUpdate; }
            await PersistChangeAttemptAsync(kind, path, previousPath, operationId, observedAt, exception.IsDirectory,
                cancellationToken, securityObservation, sizeChange).ConfigureAwait(false);
        }
        catch (IOException) when (kind == CloudLocalChangeKind.Move)
        {
            SignalLoss();
            try
            {
                await PersistChangeAttemptAsync(kind, path, previousPath, operationId, observedAt,
                    Directory.Exists(path.FullPath), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // A persistent store failure may prevent durable capture. The original error
                // still terminates the worker and requires recovery rather than reporting success.
            }

            throw;
        }
    }

    private async ValueTask<bool> TryRetainUnresolvedTimeObservationAsync(string relativePath, CancellationToken cancellationToken)
    {
        CloudItemPath path;
        try { path = CloudItemPathResolver.ResolveLexically(_syncRootPath, relativePath, allowRoot: false); }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        { return false; }
        await using (ICloudStateTransaction transaction = await _stateStore.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            if ((await transaction.Checkpoints.ListAsync(RemoteMetadataIntent.FileTimeObservationName(path.RelativePath),
                    cancellationToken).ConfigureAwait(false)).Count == 0 &&
                await transaction.Checkpoints.GetAsync(RemoteMetadataIntent.DirectoryTimeObservationName(path.RelativePath),
                    cancellationToken).ConfigureAwait(false) is null)
            { return false; }
        }
        await PersistChangeAttemptAsync(CloudLocalChangeKind.ContentUpdate, path, null, Guid.NewGuid(),
            DateTimeOffset.UtcNow, uncertainDirectory: false, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async ValueTask PersistChangeAttemptAsync(CloudLocalChangeKind kind, CloudItemPath path,
        CloudItemPath? previousPath, Guid operationId, DateTimeOffset observedAt, bool? uncertainDirectory,
        CancellationToken cancellationToken, LocalChangeSourceEvent? securityObservation = null, LocalSizeChange? sizeChange = null)
    {
        using IDisposable? publication = await AcquirePublicationAsync(cancellationToken).ConfigureAwait(false);
        StagedSecurityObservation? staged = null;
        try
        {
            await using (ICloudStateTransaction transaction = await _stateStore.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                staged = await PersistChangeCoreAsync(transaction, kind, path, previousPath, operationId, observedAt,
                    uncertainDirectory, securityObservation, sizeChange, cancellationToken).ConfigureAwait(false);
            }

            if (staged is not null)
            {
                staged.Projection.Validate();
                await PublishValidatedSecurityObservationAsync(staged.Entry, staged.Projection, staged.Observation, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            staged?.Dispose();
        }
    }

    private async ValueTask<StagedSecurityObservation?> PersistChangeCoreAsync(ICloudStateTransaction transaction,
        CloudLocalChangeKind kind, CloudItemPath path, CloudItemPath? previousPath, Guid operationId, DateTimeOffset observedAt,
        bool? uncertainDirectory, LocalChangeSourceEvent? securityObservation, LocalSizeChange? sizeChange,
        CancellationToken cancellationToken)
    {
        LocalChangeCheckpoint checkpoint = await ReadCheckpointAsync(transaction, cancellationToken)
            .ConfigureAwait(false);
        long observation = Math.Max(_nextObservation + 1, checkpoint.Observation + 1);
        _nextObservation = observation;
        CloudItemState? current = await transaction.Items
            .GetByRelativePathAsync(path.RelativePath, cancellationToken).ConfigureAwait(false);
        CloudItemState? previous = previousPath is null
            ? null
            : await transaction.Items.GetByRelativePathAsync(previousPath.Value.RelativePath, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CloudStateCheckpoint> creations = await transaction.Checkpoints
            .ListAsync(RemoteCreationIntent.Prefix, cancellationToken).ConfigureAwait(false);
        bool pendingCreation = creations.Select(value => RemoteCreationIntent.Decode(value.Value)).Any(intent =>
            string.Equals(intent.RelativePath, path.RelativePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(intent.RelativePath, previousPath?.RelativePath, StringComparison.OrdinalIgnoreCase));
        IReadOnlyList<CloudStateCheckpoint> moves = await transaction.Checkpoints
            .ListAsync(RemoteMoveIntent.Prefix, cancellationToken).ConfigureAwait(false);
        bool pendingMove = moves.Select(value => RemoteMoveIntent.Decode(value.Value)).Any(intent =>
            CloudDirectoryStateProjection.SamePath(intent.Object.RelativePath, path.RelativePath) ||
            CloudDirectoryStateProjection.SamePath(intent.TargetRelativePath, path.RelativePath) ||
            (previousPath is not null && (CloudDirectoryStateProjection.SamePath(intent.Object.RelativePath, previousPath.Value.RelativePath) ||
                CloudDirectoryStateProjection.SamePath(intent.TargetRelativePath, previousPath.Value.RelativePath))));
        IReadOnlyList<CloudStateCheckpoint> deletes = await transaction.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix, cancellationToken).ConfigureAwait(false);
        bool pendingDelete = deletes.Select(value => RemoteDeleteIntent.Decode(value.Value)).Any(intent =>
            intent.Contains(path.RelativePath) || (previousPath is not null && intent.Contains(previousPath.Value.RelativePath)));
        CloudLocalFileBinding? sizeBinding = null;
        if (sizeChange is { } size && uncertainDirectory is null)
        {
            if (pendingCreation || pendingMove || pendingDelete)
            {
                throw new CloudNamespaceObservationUncertainException(size.Identity.IsDirectory);
            }
            SizeObservationDisposition disposition = InspectSizeObservation(path, current, size, out sizeBinding);
            if (disposition == SizeObservationDisposition.CleanPlaceholder) { return null; }
            if (disposition == SizeObservationDisposition.MissingOrdinaryObject)
            {
                // The independent size source can lag the ordered name/time source.
                // A missing ordinary name is not proof of watcher loss. Keep the
                // historical observation without rediscovering the old name.
                LocalChangePayload historical = new(path.RelativePath, null, IsDirectory: false, observedAt);
                await transaction.Operations.EnqueueAsync(new(operationId, CloudStateOperationKind.ContentUpdate, null,
                    historical.Encode(), observedAt), cancellationToken).ConfigureAwait(false);
                await UpsertCheckpointAsync(transaction, observation, checkpoint.RequiresFullRescan, observedAt,
                    cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                SignalAvailable();
                return null;
            }
        }
        bool uncertainFileContent = false;
        if (kind == CloudLocalChangeKind.ContentUpdate)
        {
            // The generic action describes a historical path. Read its watcher-lifetime
            // history before using the current native kind to classify the notification.
            bool historicalFile = (await transaction.Checkpoints.ListAsync(RemoteMetadataIntent.FileTimeObservationName(path.RelativePath),
                cancellationToken).ConfigureAwait(false)).Count != 0;
            bool historicalDirectory = await transaction.Checkpoints.GetAsync(RemoteMetadataIntent.DirectoryTimeObservationName(path.RelativePath),
                cancellationToken).ConfigureAwait(false) is not null;
            if (!historicalFile && uncertainDirectory is null && (sizeChange?.Identity.IsDirectory ?? Directory.Exists(path.FullPath)))
            {
                kind = CloudLocalChangeKind.MetadataUpdate;
            }
            else
            {
                uncertainFileContent = historicalFile || historicalDirectory || (sizeChange is not null && uncertainDirectory is not null);
            }
        }
        bool metadataObservation = kind == CloudLocalChangeKind.MetadataUpdate &&
            securityObservation?.Action != LocalChangeSourceAction.SecurityModified;
        bool genericDirectoryMetadata = metadataObservation && securityObservation is null;
        RemoteMetadataIntent[] metadataIntents = genericDirectoryMetadata
            ? await ReadMetadataObservationIntentsAsync(transaction, path.RelativePath, current?.ItemId, cancellationToken).ConfigureAwait(false)
            : [];
        if (!genericDirectoryMetadata && (metadataObservation || uncertainFileContent) && current is not null)
        {
            metadataIntents = (await ReadMetadataIntentsAsync(transaction, cancellationToken, current.ItemId).ConfigureAwait(false))
                .Where(intent => securityObservation?.SecurityIdentity is not { } identity || intent.Matches(identity, intent.ItemId) ||
                    ((intent.Binding is null || (intent.RequiresReconciliation && !intent.Reconciled)) && intent.Covers(path.RelativePath))).ToArray();
        }
        // A path-only event from a retired item cannot redeem its successor's authority.
        // The same official ID can also have more than one native generation at this path.
        bool ambiguousDirectoryGeneration = genericDirectoryMetadata &&
            (uncertainDirectory is not null || metadataIntents.Any(intent => intent.ItemId != current?.ItemId) ||
             metadataIntents.Where(intent => intent.Covers(path.RelativePath)).Select(intent => intent.Binding).Distinct().Skip(1).Any());
        bool canPassThrough = metadataIntents.Any(intent => intent.IsConfirmedPassthrough);
        bool pendingMetadata = uncertainFileContent || ambiguousDirectoryGeneration || metadataIntents.Length != 0 && (metadataIntents.Any(NeedsMetadataReconciliation) ||
            (!canPassThrough && !metadataIntents.Any(intent => OwnsMetadataObservation(intent, securityObservation, current?.ItemId))));
        // A delayed observation may arrive after a remote Delete's terminal commit consumed
        // its preparation. Never resurrect that historical identity for a replacement at the
        // same path. Retain the observation without assigning an identity until reconciliation.
        bool tombstonedGeneration = kind != CloudLocalChangeKind.Delete &&
            (current?.IsTombstone == true || previous?.IsTombstone == true);
        // Watcher deletes carry a path, not the retired object's identity. A successor may
        // already occupy that location when an old notification is delivered. Its continued
        // native presence cannot authorize tombstoning the current generation or retargeting ACKs.
        bool supersededDelete = kind == CloudLocalChangeKind.Delete && current is { IsTombstone: false } &&
            (File.Exists(path.FullPath) || Directory.Exists(path.FullPath));
        if (tombstonedGeneration && kind == CloudLocalChangeKind.Move)
        {
            // Preserve the existing unassigned move journal and its acknowledgement identity.
            // Neither historical endpoint can establish the replacement's current ownership.
            uncertainDirectory ??= (previous ?? current)?.Kind == CloudItemKind.Directory || Directory.Exists(path.FullPath);
        }
        bool pendingNamespace = pendingCreation || pendingMove || pendingDelete || supersededDelete ||
            (tombstonedGeneration && kind != CloudLocalChangeKind.Move);
        if (!pendingNamespace && !pendingMetadata && genericDirectoryMetadata && canPassThrough)
        {
            SecurityFeedProjection? projection = uncertainDirectory is null ? CaptureDirectoryPassthroughOwner(path, current, metadataIntents) : null;
            if (projection is not null)
            {
                return await StageMetadataObservationAsync(transaction, projection, current!, path,
                    new(LocalChangeSourceAction.Modified, path.RelativePath), operationId, observation, observedAt, checkpoint,
                    cancellationToken).ConfigureAwait(false);
            }
            ambiguousDirectoryGeneration = true;
            pendingMetadata = true;
        }
        if (pendingNamespace || pendingMetadata)
        {
            long generation = SignalLoss();
            // Do not assign or relocate a local identity while native remote work is pending. Retain
            // the observation durably and require reconciliation after remote replay; its
            // origin cannot be proved solely from a path, timestamp, or notification kind.
            bool directoryObservation = !uncertainFileContent && (uncertainDirectory ?? (securityObservation is { } security
                    ? security.SecurityIdentity?.IsDirectory ?? current?.Kind == CloudItemKind.Directory
                    : Directory.Exists(path.FullPath)));
            LocalChangePayload uncertain = new(path.RelativePath, previousPath?.RelativePath, directoryObservation, observedAt);
            if (uncertainFileContent)
            {
                await transaction.Operations.EnqueueAsync(new(operationId, CloudStateOperationKind.ContentUpdate, null,
                    uncertain.Encode(), observedAt), cancellationToken).ConfigureAwait(false);
            }
            else if (securityObservation is not null || pendingMetadata)
            {
                await RetainUnboundSecurityObservationAsync(transaction, operationId, uncertain.Encode(), observedAt,
                    cancellationToken).ConfigureAwait(false);
            }
            if (uncertainFileContent || ambiguousDirectoryGeneration)
            {
                await RetainMetadataObservationProtectionAsync(transaction.Checkpoints, path.RelativePath, current, observedAt,
                    cancellationToken).ConfigureAwait(false);
            }
            if (pendingMetadata)
            {
                _metadataEchoAuthority?.TrackReconciliationChange();
                foreach (RemoteMetadataIntent intent in metadataIntents)
                {
                    RemoteMetadataIntent retained = intent with { RequiresReconciliation = true, Reconciled = false };
                    await retained.PersistAsync(transaction.Checkpoints, observedAt, cancellationToken).ConfigureAwait(false);
                }
            }
            await transaction.Checkpoints.UpsertAsync(new CloudStateCheckpoint(
                (pendingMove || pendingDelete || pendingMetadata || tombstonedGeneration || supersededDelete ? NamespaceObservationsPrefix : RemoteCreationIntent.ObservationsPrefix) + "/" + kind + "/" + operationId.ToString("N"),
                uncertain.Encode(), observedAt), cancellationToken).ConfigureAwait(false);
            await UpsertCheckpointAsync(transaction, observation, true, observedAt, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _persistedLossGeneration, generation);
            SignalAvailable();
            return null;
        }

        if (securityObservation is { } securityEvent)
        {
            SecurityFeedProjection? projection = uncertainDirectory is null
                ? TryBindSecurityObservation(path, current, securityEvent.SecurityIdentity,
                    attributeObservation: securityEvent.Action == LocalChangeSourceAction.AttributesModified) : null;
            if (projection is not null)
            {
                return await StageMetadataObservationAsync(transaction, projection, current!, path, securityEvent,
                    operationId, observation, observedAt, checkpoint, cancellationToken).ConfigureAwait(false);
            }

            // Independent native subscriptions have no common delivery order. A historical
            // security record cannot discover or establish the identity of a replacement.
            uncertainDirectory ??= securityEvent.SecurityIdentity?.IsDirectory ?? current?.Kind == CloudItemKind.Directory;
        }

        CloudStateOperationKind operationKind = kind switch
        {
            CloudLocalChangeKind.Create => CloudStateOperationKind.Create,
            CloudLocalChangeKind.ContentUpdate => CloudStateOperationKind.ContentUpdate,
            CloudLocalChangeKind.MetadataUpdate => CloudStateOperationKind.MetadataUpdate,
            CloudLocalChangeKind.Move => CloudStateOperationKind.Move,
            CloudLocalChangeKind.Delete => CloudStateOperationKind.Delete,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        CloudItemState? observedState = kind == CloudLocalChangeKind.Delete
            ? current
            : previous ?? current;
        // A retained uncertain kind must not be reclassified through a rejected namespace.
        bool directory = uncertainDirectory ?? sizeChange?.Identity.IsDirectory ??
            (observedState?.Kind == CloudItemKind.Directory || Directory.Exists(path.FullPath));
        using DirectoryFeedProjection? directoryProjection = uncertainDirectory is null && kind == CloudLocalChangeKind.Move && previousPath is not null
            ? await TryProjectDirectoryAsync().ConfigureAwait(false) : null;
        if (uncertainDirectory is null && kind == CloudLocalChangeKind.Move && previous is not null && current is not null &&
            previous.ItemId != current.ItemId)
        {
            throw new CloudNamespaceObservationUncertainException(directory);
        }

        if (uncertainDirectory is null && kind == CloudLocalChangeKind.Move && directoryProjection is null &&
            observedState is null && !File.Exists(path.FullPath))
        {
            throw new CloudNamespaceObservationUncertainException(directory);
        }

        // Path/kind echo records cannot attribute an independent DACL notification to a
        // provider write. Uncertain observations also must not revisit a rejected path.
        CloudEchoSuppressionState? suppression = null;
        if (securityObservation is null && uncertainDirectory is null && directoryProjection?.RequiresRescan != true)
        {
            IReadOnlyList<CloudEchoSuppressionState> suppressions = await transaction.EchoSuppressions
                .ListActiveAsync(observedAt, cancellationToken).ConfigureAwait(false);
            // Creation tickets also match ContentUpdate for the original identity. A
            // same-revision passthrough write can leave that exact identity in place.
            bool bypassCreationContentEcho = operationKind == CloudStateOperationKind.ContentUpdate &&
                observedState is { Kind: CloudItemKind.File, IsTombstone: false } &&
                suppressions.Any(candidate => candidate.ItemId == observedState.ItemId && RemoteCreationIntent.IsCreationEcho(candidate) &&
                    CloudDirectoryStateProjection.SamePath(candidate.RelativePath, path.RelativePath)) &&
                (await ReadMetadataIntentsAsync(transaction, cancellationToken, observedState.ItemId).ConfigureAwait(false))
                    .Any(intent => intent.Passthrough && !intent.Superseded && intent.Covers(path.RelativePath));
            suppression = suppressions.FirstOrDefault(candidate => candidate.Kind != CloudStateOperationKind.MetadataUpdate && (RemoteCreationIntent.IsCreationEcho(candidate)
                ? !bypassCreationContentEcho && MatchesRemoteCreationEcho(candidate, operationKind, path, observedState)
                : candidate.Matches(operationKind, path.RelativePath, previousPath?.RelativePath, observedState?.ItemId)));
        }

        if (suppression is not null)
        {
            if (suppression.RemainingObservations == 1)
            {
                await transaction.EchoSuppressions.RemoveAsync(
                        suppression.SuppressionId,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await transaction.EchoSuppressions.UpsertAsync(
                        suppression.Consume(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            await UpsertCheckpointAsync(
                transaction,
                observation,
                checkpoint.RequiresFullRescan,
                observedAt,
                cancellationToken).ConfigureAwait(false);
            directoryProjection?.Validate();
            if (sizeBinding is not null) { ValidateSizeObservationBinding(path, sizeBinding, directory); }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            directoryProjection?.Validate();
            SignalAvailable();
            return null;
        }

        // A pending operation may already be in an uploader's hands. Each subsequent
        // observation needs its own acknowledgement identity; otherwise acknowledging the
        // earlier snapshot would also erase changes made while that snapshot was uploading.
        CloudItemState? state = kind == CloudLocalChangeKind.Delete
            ? current
            : previous ?? current;
        Guid? itemId = uncertainDirectory is not null ? null : directoryProjection?.ItemId ?? state?.ItemId;
        bool discovered = state is null && kind != CloudLocalChangeKind.Delete;

        if (uncertainDirectory is null && directoryProjection is null && discovered)
        {
            itemId = Guid.NewGuid();
            bool discoveredDirectory = sizeChange?.Identity.IsDirectory ?? Directory.Exists(path.FullPath);
            state = new CloudItemState(
                itemId.Value,
                "local:" + itemId.Value.ToString("N"),
                path.RelativePath,
                discoveredDirectory ? CloudItemKind.Directory : CloudItemKind.File,
                remoteRevision: null,
                // An independent size notice names the object captured by Windows.
                // Ordinary discovery cannot substitute the path's current occupant.
                localFileId: sizeBinding is not null ? NtfsFileId(sizeBinding) : TryReadDiscoveredFileId(path),
                isTombstone: false,
                observedAt);
        }

        if (securityObservation is null && uncertainDirectory is null && directoryProjection is null && state is not null)
        {
            CloudItemState updated = new(
                state.ItemId,
                state.RemoteId,
                path.RelativePath,
                state.Kind,
                state.RemoteRevision,
                state.LocalFileId,
                isTombstone: kind == CloudLocalChangeKind.Delete,
                observedAt);
            await transaction.Items.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
            if (kind == CloudLocalChangeKind.Move && updated.Kind == CloudItemKind.File && previousPath is not null)
            {
                await CloudDirectoryProvenance.RefreshMoveAncestorsAsync(transaction, previousPath.Value.RelativePath,
                    path.RelativePath, cancellationToken).ConfigureAwait(false);
            }
            else if (discovered)
            {
                await CloudDirectoryProvenance.RefreshProjectionAncestorsAsync(transaction, path.RelativePath,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        LocalChangePayload payload = new(
            path.RelativePath,
            previousPath?.RelativePath,
            uncertainDirectory ?? (directoryProjection is not null || directory),
            observedAt);
        byte[] encoded = payload.Encode();
        CloudOperationJournalEntry entry = new(operationId, operationKind, itemId, encoded, observedAt);
        if (securityObservation is not null)
        {
            await RetainUnboundSecurityObservationAsync(transaction, operationId, encoded, observedAt, cancellationToken).ConfigureAwait(false);
        }
        else if (await transaction.Operations.GetAsync(operationId, cancellationToken).ConfigureAwait(false) is null)
        {
            await transaction.Operations.EnqueueAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        bool requiresRescan = uncertainDirectory is not null || directoryProjection?.RequiresRescan == true;
        long namespaceGeneration = requiresRescan ? SignalLoss() : Volatile.Read(ref _lossGeneration);
        if (requiresRescan)
        {
            await transaction.Checkpoints.UpsertAsync(new CloudStateCheckpoint(NamespaceObservationsPrefix + "/" + operationId.ToString("N"),
                encoded, observedAt), cancellationToken).ConfigureAwait(false);
        }

        await UpsertCheckpointAsync(
            transaction,
            observation,
            checkpoint.RequiresFullRescan || requiresRescan,
            observedAt,
            cancellationToken).ConfigureAwait(false);
        directoryProjection?.Validate();
        if (sizeBinding is not null) { ValidateSizeObservationBinding(path, sizeBinding, directory); }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (requiresRescan)
        {
            Volatile.Write(ref _persistedLossGeneration, namespaceGeneration);
        }

        directoryProjection?.Validate();
        SignalAvailable();
        return null;

        async ValueTask<DirectoryFeedProjection?> TryProjectDirectoryAsync()
        {
            CloudDirectoryMoveProof? proof = await CloudDirectoryMoveEvidence.GetOrPrepareRetainedIntentAsync(transaction,
                previousPath!.Value.RelativePath, path.RelativePath, cancellationToken).ConfigureAwait(false);
            if (proof is null && !directory)
            {
                return null;
            }

            if (proof is null || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
            {
                throw new CloudNamespaceObservationUncertainException(true);
            }

            return await ProjectDirectoryObservationAsync(transaction, proof, path, previousPath.Value, observedAt,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool MatchesRemoteCreationEcho(CloudEchoSuppressionState suppression,
        CloudStateOperationKind kind, CloudItemPath path, CloudItemState? state)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299) || state is null ||
            kind is not (CloudStateOperationKind.Create or CloudStateOperationKind.ContentUpdate or CloudStateOperationKind.MetadataUpdate) ||
            !string.Equals(suppression.RelativePath, path.RelativePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        CloudPlaceholderIdentity expected = RemoteCreationIntent.EchoIdentity(suppression);
        if (state.ItemId != expected.ItemId)
        {
            return false;
        }

        LocalCloudItemInspection local = CloudItemInspector.Inspect(path.FullPath, state.Kind);
        return local.Exists && local.SynchronizationState == CloudSynchronizationState.InSync &&
            local.PlaceholderIdentity.AsSpan().SequenceEqual(expected.Encode());
    }

    private async ValueTask MarkRescanRequiredAsync(CancellationToken cancellationToken)
    {
        long generation = SignalLoss();
        using IDisposable? publication = await AcquirePublicationAsync(cancellationToken).ConfigureAwait(false);
        await using ICloudStateTransaction transaction = await _stateStore
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        LocalChangeCheckpoint checkpoint = await ReadCheckpointAsync(transaction, cancellationToken)
            .ConfigureAwait(false);
        long observation = Math.Max(_nextObservation + 1, checkpoint.Observation + 1);
        _nextObservation = observation;
        await UpsertCheckpointAsync(
            transaction,
            observation,
            RequiresFullRescan: true,
            DateTimeOffset.UtcNow,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _persistedLossGeneration, generation);
        SignalAvailable();
    }

    private async ValueTask PublishAsync(LocalChangeSourceEvent sourceEvent)
    {
        if (sourceEvent.Action is LocalChangeSourceAction.SecuritySubscriptionFailed or LocalChangeSourceAction.AttributeSubscriptionFailed or
            LocalChangeSourceAction.SizeSubscriptionFailed)
        {
            if (Interlocked.Exchange(ref _terminalSourceSignaled, 1) != 0) { return; }
            // Fail readers out of band, but close the input only after every subscription
            // drains. Another native pump may already hold a decoded historical batch.
            // Completing the channel here would discard that batch when subscriptions
            // terminate concurrently (for example, after the watched root is deleted).
            Win32Exception failure = new(sourceEvent.ErrorCode);
            SignalLoss();
            Interlocked.CompareExchange(ref _failure, failure, null);
            _availability.Writer.TryComplete();
            _ = CompleteFailedSourceAsync(failure);
            return;
        }

        if (Volatile.Read(ref _failure) is not null && Volatile.Read(ref _terminalSourceSignaled) == 0) { return; }

        if (sourceEvent.Action is LocalChangeSourceAction.Overflow or LocalChangeSourceAction.Error)
        {
            SignalLoss();
        }

        if (_sourceEvents.Writer.TryWrite(sourceEvent))
        {
            return;
        }

        Interlocked.Exchange(ref _overflowSignaled, 1);
        SignalLoss();
        await ValueTask.CompletedTask;
    }

    private async Task CompleteFailedSourceAsync(Exception failure)
    {
        try { await _source.DisposeAsync().ConfigureAwait(false); }
        catch
        {
            // Keep the first native error. The terminal channel completion persists the
            // full-rescan fence even if stopping another subscription also fails.
        }
        finally { _sourceEvents.Writer.TryComplete(failure); }
    }

    private async ValueTask LoadCheckpointAsync(CancellationToken cancellationToken)
    {
        using IDisposable? publication = await AcquirePublicationAsync(cancellationToken).ConfigureAwait(false);
        await using ICloudStateTransaction transaction = await _stateStore
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Negotiate before recovery or source opening, and again on publication transactions.
        // A wrapper must not return a less capable journal after accepting startup.
        RequireJournalAssociationUpdates(transaction);
        LocalChangeCheckpoint checkpoint = await ReadCheckpointAsync(transaction, cancellationToken)
            .ConfigureAwait(false);
        (bool metadataChanged, bool metadataRequiresReconciliation) =
            await RecoverMetadataIntentsAsync(transaction, cancellationToken).ConfigureAwait(false);
        (bool retiredTimeMarkers, bool timeMarkersRequireReconciliation) = await RetireTimeObservationsBeforeStartingAsync(transaction.Checkpoints,
            cancellationToken).ConfigureAwait(false);
        bool recoveredObservations = await RecoverStagedSecurityObservationsAsync(transaction, cancellationToken).ConfigureAwait(false);
        if (recoveredObservations || metadataRequiresReconciliation || timeMarkersRequireReconciliation)
        {
            await UpsertCheckpointAsync(transaction, checkpoint.Observation, true, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        if (recoveredObservations || metadataChanged || metadataRequiresReconciliation || retiredTimeMarkers)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        _nextObservation = checkpoint.Observation;
        Volatile.Write(ref _rescanNoticeDelivered, 0);
    }

    private static async ValueTask<LocalChangeCheckpoint> ReadCheckpointAsync(
        ICloudStateTransaction transaction,
        CancellationToken cancellationToken)
    {
        CloudStateCheckpoint? value = await transaction.Checkpoints
            .GetAsync(CheckpointName, cancellationToken).ConfigureAwait(false);
        return value is null ? default : LocalChangeCheckpoint.Decode(value.Value);
    }

    private static ValueTask UpsertCheckpointAsync(
        ICloudStateTransaction transaction,
        long observation,
        bool RequiresFullRescan,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken) =>
        transaction.Checkpoints.UpsertAsync(
            new CloudStateCheckpoint(
                CheckpointName,
                new LocalChangeCheckpoint(observation, RequiresFullRescan).Encode(),
                observedAt),
            cancellationToken);

    private static CloudLocalChange ToChange(CloudOperationJournalEntry operation)
    {
        LocalChangePayload payload = LocalChangePayload.Decode(operation.Payload);
        CloudLocalChangeKind kind = operation.Kind switch
        {
            CloudStateOperationKind.Create => CloudLocalChangeKind.Create,
            CloudStateOperationKind.ContentUpdate => CloudLocalChangeKind.ContentUpdate,
            CloudStateOperationKind.MetadataUpdate => CloudLocalChangeKind.MetadataUpdate,
            CloudStateOperationKind.Move => CloudLocalChangeKind.Move,
            CloudStateOperationKind.Delete => CloudLocalChangeKind.Delete,
            _ => throw new InvalidOperationException("The local-change journal kind is invalid."),
        };
        return new CloudLocalChange(
            operation.OperationId,
            operation.Sequence,
            kind,
            operation.ItemId,
            payload.RelativePath,
            payload.PreviousRelativePath,
            payload.IsDirectory,
            payload.ObservedAt);
    }

    private void SignalAvailable() => _availability.Writer.TryWrite(true);

    private void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _started) == 0)
        {
            throw new InvalidOperationException("The local-change feed has not been started.");
        }
        ThrowIfFailed();
    }

    private void ThrowIfFailed()
    {
        Exception? failure = Volatile.Read(ref _failure);
        if (failure is not null)
        {
            throw new InvalidOperationException("The local-change feed stopped unexpectedly.", failure);
        }
    }
}
