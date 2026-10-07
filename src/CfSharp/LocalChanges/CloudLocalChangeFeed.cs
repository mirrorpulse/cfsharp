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
/// Windows directory notifications are advisory. Buffer overflow, watcher errors, and malformed
/// rename pairs produce <see cref="CloudLocalChangeBatch.RequiresFullRescan"/>. Applications must
/// perform a full reconciliation before acknowledging that condition; periodic reconciliation is
/// intentionally outside CfSharp.
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
    private int _overflowSignaled;
    private int _started;
    private int _disposed;

    internal CloudLocalChangeFeed(
        string syncRootPath,
        ICloudStateStore stateStore,
        CloudLocalChangeFeedOptions options,
        ILocalChangeSource source,
        Action<CloudLocalChangeFeed>? onDisposed = null)
    {
        _syncRootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(syncRootPath));
        _stateStore = stateStore;
        _options = options;
        _options.Validate();
        _source = source;
        _onDisposed = onDisposed;
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
    /// <exception cref="InvalidOperationException">The feed has already been started.</exception>
    /// <exception cref="ObjectDisposedException">The feed has been disposed.</exception>
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

        try
        {
            await LoadCheckpointAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _sourceTask = _source.StartAsync(PublishAsync, _shutdown.Token);
            _processorTask = ProcessEventsAsync();
        }
        catch
        {
            Interlocked.Exchange(ref _started, 0);
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Reads the earliest durable local operations, waiting until at least one operation or a
    /// full-rescan signal is available.
    /// </summary>
    /// <param name="cancellationToken">Token that cancels the read without acknowledging anything.</param>
    /// <returns>An ordered bounded batch. The batch is immutable and safe to retain.</returns>
    /// <exception cref="InvalidOperationException">The feed has not been started.</exception>
    /// <exception cref="ObjectDisposedException">The feed has been disposed.</exception>
    public async ValueTask<CloudLocalChangeBatch> ReadBatchAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        while (true)
        {
            ThrowIfFailed();
            if (await RequiresCreationReconciliationAsync(cancellationToken).ConfigureAwait(false))
            {
                return new CloudLocalChangeBatch([], requiresFullRescan: true);
            }

            IReadOnlyList<CloudOperationJournalEntry> operations = await ListOperationsAsync(
                cancellationToken).ConfigureAwait(false);
            if (operations.Count != 0)
            {
                return new CloudLocalChangeBatch(
                    operations.Select(ToChange).ToArray(),
                    requiresFullRescan: false);
            }

            LocalChangeCheckpoint checkpoint = await ReadCheckpointAsync(cancellationToken)
                .ConfigureAwait(false);
            if (checkpoint.RequiresFullRescan &&
                Interlocked.CompareExchange(ref _rescanNoticeDelivered, 1, 0) == 0)
            {
                return new CloudLocalChangeBatch([], requiresFullRescan: true);
            }

            await _availability.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
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
    /// </remarks>
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

        await using ICloudStateTransaction transaction = await _stateStore
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (CloudLocalChangeAcknowledgement acknowledgement in values)
        {
            CloudOperationJournalEntry? operation = await transaction.Operations
                .GetAsync(acknowledgement.OperationId, cancellationToken)
                .ConfigureAwait(false);
            if (operation is not null)
            {
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
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Clears the durable rescan-required marker after the application has completed a full
    /// reconciliation of the sync root.
    /// </summary>
    /// <param name="cancellationToken">Token that cancels before the marker commit.</param>
    public async ValueTask AcknowledgeFullRescanAsync(CancellationToken cancellationToken = default)
    {
        EnsureStarted();
        await using ICloudStateTransaction transaction = await _stateStore
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CloudStateCheckpoint> creations = await transaction.Checkpoints
            .ListAsync(RemoteCreationIntent.Prefix, cancellationToken).ConfigureAwait(false);
        if (creations.Any(value => !RemoteCreationIntent.Decode(value.Value).Committed))
        {
            throw new InvalidOperationException("Replay pending remote creations before acknowledging reconciliation.");
        }

        foreach (CloudStateCheckpoint observation in await transaction.Checkpoints
            .ListAsync(RemoteCreationIntent.ObservationsPrefix, cancellationToken).ConfigureAwait(false))
        {
            await transaction.Checkpoints.RemoveAsync(observation.Name, cancellationToken).ConfigureAwait(false);
        }

        LocalChangeCheckpoint checkpoint = await ReadCheckpointAsync(
            transaction,
            cancellationToken).ConfigureAwait(false);
        await transaction.Checkpoints.UpsertAsync(
            new CloudStateCheckpoint(
                CheckpointName,
                new LocalChangeCheckpoint(checkpoint.Observation, RequiresFullRescan: false).Encode(),
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
    /// <exception cref="TimeoutException">
    /// The processor did not stop within the configured shutdown timeout. Deferred cleanup keeps
    /// feed-owned resources alive until the processor exits.
    /// </exception>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            return;
        }

        Exception? failure = null;
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
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            _shutdown.Cancel();
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
        _onDisposed?.Invoke(this);
        _disposeCompletion.TrySetResult(null);
    }

    internal static CloudLocalChangeFeed CreateForTesting(
        string syncRootPath,
        ICloudStateStore stateStore,
        CloudLocalChangeFeedOptions options,
        ILocalChangeSource source) =>
        new(syncRootPath, stateStore, options, source);

    private async Task ProcessEventsAsync()
    {
        try
        {
            await foreach (LocalChangeSourceEvent change in _sourceEvents.Reader.ReadAllAsync(_shutdown.Token))
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
            Volatile.Write(ref _failure, exception);
            _availability.Writer.TryWrite(true);
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
                await PersistChangeAsync(
                    ClassifyModifiedChange(sourceEvent.RelativePath),
                    sourceEvent.RelativePath,
                    previousRelativePath: null,
                    cancellationToken).ConfigureAwait(false);
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

    private CloudLocalChangeKind ClassifyModifiedChange(string relativePath)
    {
        try
        {
            CloudItemPath path = CloudItemPathResolver.Resolve(
                _syncRootPath,
                relativePath,
                allowRoot: false);
            return Directory.Exists(path.FullPath)
                ? CloudLocalChangeKind.MetadataUpdate
                : CloudLocalChangeKind.ContentUpdate;
        }
        catch (ArgumentException)
        {
            return CloudLocalChangeKind.ContentUpdate;
        }
    }

    private async ValueTask PersistChangeAsync(
        CloudLocalChangeKind kind,
        string relativePath,
        string? previousRelativePath,
        CancellationToken cancellationToken)
    {
        CloudItemPath path;
        try
        {
            path = CloudItemPathResolver.Resolve(_syncRootPath, relativePath, allowRoot: false);
        }
        catch (ArgumentException)
        {
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
            catch (ArgumentException)
            {
                await MarkRescanRequiredAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        await using ICloudStateTransaction transaction = await _stateStore
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        LocalChangeCheckpoint checkpoint = await ReadCheckpointAsync(transaction, cancellationToken)
            .ConfigureAwait(false);
        long observation = Math.Max(_nextObservation + 1, checkpoint.Observation + 1);
        _nextObservation = observation;
        IReadOnlyList<CloudStateCheckpoint> creations = await transaction.Checkpoints
            .ListAsync(RemoteCreationIntent.Prefix, cancellationToken).ConfigureAwait(false);
        bool pendingCreation = creations.Select(value => RemoteCreationIntent.Decode(value.Value)).Any(intent =>
            !intent.Committed && (string.Equals(intent.RelativePath, path.RelativePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(intent.RelativePath, previousPath?.RelativePath, StringComparison.OrdinalIgnoreCase)));
        if (pendingCreation)
        {
            // Do not assign a local identity while native creation is uncommitted. Retain
            // the observation durably and require reconciliation after remote replay; its
            // origin cannot be proved solely from a path, timestamp, or notification kind.
            LocalChangePayload uncertain = new(path.RelativePath, previousPath?.RelativePath,
                Directory.Exists(path.FullPath), observedAt);
            await transaction.Checkpoints.UpsertAsync(new CloudStateCheckpoint(
                RemoteCreationIntent.ObservationsPrefix + "/" + kind + "/" + Guid.NewGuid().ToString("N"),
                uncertain.Encode(), observedAt), cancellationToken).ConfigureAwait(false);
            await UpsertCheckpointAsync(transaction, observation, true, observedAt, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            SignalAvailable();
            return;
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
        CloudItemState? current = await transaction.Items
            .GetByRelativePathAsync(path.RelativePath, cancellationToken).ConfigureAwait(false);
        CloudItemState? previous = previousPath is null
            ? null
            : await transaction.Items
                .GetByRelativePathAsync(previousPath.Value.RelativePath, cancellationToken)
                .ConfigureAwait(false);
        CloudItemState? observedState = kind == CloudLocalChangeKind.Delete
            ? current
            : previous ?? current;
        IReadOnlyList<CloudEchoSuppressionState> suppressions = await transaction.EchoSuppressions
            .ListActiveAsync(observedAt, cancellationToken).ConfigureAwait(false);
        CloudEchoSuppressionState? suppression = suppressions.FirstOrDefault(candidate =>
            RemoteCreationIntent.IsCreationEcho(candidate)
                ? MatchesRemoteCreationEcho(candidate, operationKind, path, observedState)
                : candidate.Matches(operationKind, path.RelativePath, previousPath?.RelativePath, observedState?.ItemId));
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
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            SignalAvailable();
            return;
        }

        // A pending operation may already be in an uploader's hands. Each subsequent
        // observation needs its own acknowledgement identity; otherwise acknowledging the
        // earlier snapshot would also erase changes made while that snapshot was uploading.
        CloudItemState? state = kind == CloudLocalChangeKind.Delete
            ? current
            : previous ?? current;
        Guid? itemId = state?.ItemId;
        if (kind == CloudLocalChangeKind.Move && previous is not null && current is not null &&
            previous.ItemId != current.ItemId)
        {
            await transaction.Items.RemoveAsync(current.ItemId, cancellationToken).ConfigureAwait(false);
        }

        if (state is null && kind != CloudLocalChangeKind.Delete)
        {
            itemId = Guid.NewGuid();
            state = new CloudItemState(
                itemId.Value,
                "local:" + itemId.Value.ToString("N"),
                path.RelativePath,
                Directory.Exists(path.FullPath) ? CloudItemKind.Directory : CloudItemKind.File,
                remoteRevision: null,
                localFileId: null,
                isTombstone: false,
                observedAt);
        }

        if (state is not null)
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
        }

        LocalChangePayload payload = new(
            path.RelativePath,
            previousPath?.RelativePath,
            state?.Kind == CloudItemKind.Directory ||
                (state is null && Directory.Exists(path.FullPath)),
            observedAt);
        await transaction.Operations.EnqueueAsync(
            new CloudOperationJournalEntry(
                Guid.NewGuid(),
                operationKind,
                itemId,
                payload.Encode(),
                observedAt),
            cancellationToken).ConfigureAwait(false);
        await UpsertCheckpointAsync(
            transaction,
            observation,
            checkpoint.RequiresFullRescan,
            observedAt,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        SignalAvailable();
    }

    private async ValueTask<bool> RequiresCreationReconciliationAsync(CancellationToken cancellationToken)
    {
        await using ICloudStateTransaction transaction = await _stateStore.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if ((await transaction.Checkpoints.ListAsync(RemoteCreationIntent.ObservationsPrefix, cancellationToken)
            .ConfigureAwait(false)).Count != 0)
        {
            return true;
        }

        return (await transaction.Checkpoints.ListAsync(RemoteCreationIntent.Prefix, cancellationToken)
            .ConfigureAwait(false)).Any(value => !RemoteCreationIntent.Decode(value.Value).Committed);
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
        SignalAvailable();
    }

    private async ValueTask PublishAsync(LocalChangeSourceEvent sourceEvent)
    {
        if (_sourceEvents.Writer.TryWrite(sourceEvent))
        {
            return;
        }

        Interlocked.Exchange(ref _overflowSignaled, 1);
        await ValueTask.CompletedTask;
    }

    private async ValueTask LoadCheckpointAsync(CancellationToken cancellationToken)
    {
        await using ICloudStateTransaction transaction = await _stateStore
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        LocalChangeCheckpoint checkpoint = await ReadCheckpointAsync(transaction, cancellationToken)
            .ConfigureAwait(false);
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        _nextObservation = checkpoint.Observation;
        Volatile.Write(ref _rescanNoticeDelivered, 0);
    }

    private async ValueTask<IReadOnlyList<CloudOperationJournalEntry>> ListOperationsAsync(
        CancellationToken cancellationToken)
    {
        await using ICloudStateTransaction transaction = await _stateStore
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CloudOperationJournalEntry> operations = await transaction.Operations
            .ListAsync(_options.BatchSize, cancellationToken).ConfigureAwait(false);
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return operations;
    }

    private async ValueTask<LocalChangeCheckpoint> ReadCheckpointAsync(
        CancellationToken cancellationToken)
    {
        await using ICloudStateTransaction transaction = await _stateStore
            .BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        LocalChangeCheckpoint checkpoint = await ReadCheckpointAsync(transaction, cancellationToken)
            .ConfigureAwait(false);
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return checkpoint;
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
