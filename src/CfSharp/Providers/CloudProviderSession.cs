using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

using CfSharp.Native;

namespace CfSharp;

/// <summary>
/// Identifies one callback operation in the scope in which Windows guarantees its opaque keys.
/// </summary>
/// <remarks>
/// <see cref="CfRequestKey"/> is only unique for the cloud file identified by its transfer key;
/// using it by itself would merge concurrent callbacks that legitimately use the default request
/// key. The connection key is included so the registry remains correct if a session abstraction
/// is ever expanded to cover more than one native connection.
/// </remarks>
internal readonly record struct CloudProviderRequestRegistryKey(
    long ConnectionKey,
    long TransferKey,
    long RequestKey)
{
    internal static CloudProviderRequestRegistryKey Create(
        CfConnectionKey connectionKey,
        CfTransferKey transferKey,
        CfRequestKey requestKey) => new(
            connectionKey.Internal,
            transferKey.Internal,
            requestKey.Internal);
}

/// <summary>Owns a process-scoped provider connection that hydrates file placeholders.</summary>
/// <remarks>
/// <para>
/// A session owns its native callback table, callback context, cancellation registry, and
/// connection key. Create it with <see cref="Connect(CloudSyncRoot, ICloudFileContentProvider)"/>
/// and dispose it deterministically before
/// unregistering the sync root. One session may serve concurrent Windows requests.
/// </para>
/// <para>
/// Disposal stops new provider dispatch, cancels active requests, drains cooperative handlers,
/// disconnects from Windows, and only then releases callback memory. If a handler ignores the
/// configured shutdown timeout, callback memory remains owned until that handler exits. The
/// persistent sync-root registration is not removed.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.16299")]
public sealed class CloudProviderSession : IDisposable, IAsyncDisposable
{
    private const int CallbackRegistrationCount = 14;

    private readonly ICloudFileContentProvider _contentProvider;
    private readonly CloudProviderSessionOptions _options;
    private readonly ICloudStateStore? _stateStore;
    private readonly string _syncRootPath;
    private readonly object _lifecycleGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<CloudProviderRequestRegistryKey, ActiveRequest> _requests = new();
    private readonly ConcurrentDictionary<CloudProviderRequestRegistryKey, CallbackRequest> _callbackRequests = new();
    private readonly ConcurrentDictionary<long, NotificationRequest> _notificationRequests = new();
    private readonly ConcurrentDictionary<string, string> _directoryContinuations = new(
        StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _directoryPopulationGates = new(
        StringComparer.OrdinalIgnoreCase);
    private CloudProviderDispatcher? _dispatcher;
    private unsafe CfCallbackRegistration* _callbackTable;
    private GCHandle _callbackContext;
    private CfConnectionKey _connectionKey;
    private long _testingRequestKey;
    private long _notificationRegistryKey;
    private int _stopping;
    private int _disposed;
    private int _nativeStateReleased;
    private TaskCompletionSource<object?> _disposeCompletion = CreateDisposeCompletion();

    private CloudProviderSession(
        ICloudFileContentProvider contentProvider,
        CloudProviderSessionOptions options,
        ICloudStateStore? stateStore,
        string syncRootPath)
    {
        _contentProvider = contentProvider;
        _options = options;
        _stateStore = stateStore;
        _syncRootPath = syncRootPath;
    }

    /// <summary>Connects a content provider to a persistently registered sync root.</summary>
    /// <param name="syncRoot">Registered root that receives hydration callbacks.</param>
    /// <param name="contentProvider">Thread-safe source of complete logical file streams.</param>
    /// <returns>An owning session that must be disposed before the root is unregistered.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="PlatformNotSupportedException">The Cloud Files API is unavailable.</exception>
    /// <exception cref="CloudFilesException">Windows rejects the provider connection.</exception>
    public static CloudProviderSession Connect(
        CloudSyncRoot syncRoot,
        ICloudFileContentProvider contentProvider)
        => Connect(syncRoot, contentProvider, CloudProviderSessionOptions.Default);

    /// <summary>Connects a content provider with an explicit bounded runtime configuration.</summary>
    /// <param name="syncRoot">Registered root that receives hydration callbacks.</param>
    /// <param name="contentProvider">Thread-safe source of complete logical file streams.</param>
    /// <param name="options">Immutable queue, concurrency, transfer, and shutdown limits.</param>
    /// <returns>An owning session that must be disposed before the root is unregistered.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its supported bound.</exception>
    public static CloudProviderSession Connect(
        CloudSyncRoot syncRoot,
        ICloudFileContentProvider contentProvider,
        CloudProviderSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(syncRoot);
        ArgumentNullException.ThrowIfNull(contentProvider);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        CloudProviderSession session = new(contentProvider, options, null, syncRoot.Path);
        session.ConnectCore(syncRoot.Path);
        return session;
    }

    internal static CloudProviderSession Connect(
        CloudSyncRoot syncRoot,
        ICloudFileContentProvider contentProvider,
        ICloudStateStore stateStore)
    {
        ArgumentNullException.ThrowIfNull(syncRoot);
        ArgumentNullException.ThrowIfNull(contentProvider);
        ArgumentNullException.ThrowIfNull(stateStore);
        CloudProviderSession session = new(
            contentProvider,
            CloudProviderSessionOptions.Default,
            stateStore,
            syncRoot.Path);
        session.ConnectCore(syncRoot.Path);
        return session;
    }

    /// <summary>Gets the native connection status reported by Windows.</summary>
    /// <returns>A fresh status value for this connection.</returns>
    /// <exception cref="ObjectDisposedException">The session is stopping or disposed.</exception>
    /// <exception cref="CloudFilesException">Windows cannot query the connection.</exception>
    public CloudProviderStatus GetStatus()
    {
        ThrowIfStopping();
        int result = CfApi.CfQuerySyncProviderStatus(_connectionKey, out CfSyncProviderStatus status);
        ThrowIfFailed("CloudProviderSession.GetStatus", result);
        return (CloudProviderStatus)status;
    }

    /// <summary>Updates the activity or terminal status reported for this provider connection.</summary>
    /// <param name="status">Native-compatible activity flags or terminal status.</param>
    /// <exception cref="ObjectDisposedException">The session is stopping or disposed.</exception>
    /// <exception cref="CloudFilesException">Windows rejects the status update.</exception>
    [SupportedOSPlatform("windows10.0.16299")]
    public void UpdateStatus(CloudProviderStatus status)
    {
        ThrowIfStopping();
        int result = CfApi.CfUpdateSyncProviderStatus(
            _connectionKey,
            (CfSyncProviderStatus)status);
        ThrowIfFailed("CloudProviderSession.UpdateStatus", result);
    }

    internal CfConnectionKey ConnectionKey
    {
        get
        {
            ThrowIfStopping();
            return _connectionKey;
        }
    }

    /// <summary>Synchronously stops the provider session and releases its callback resources.</summary>
    /// <remarks>
    /// This compatibility method blocks the calling thread while handlers drain. The configured
    /// shutdown timeout controls cooperative cancellation; a handler that ignores cancellation
    /// keeps callback memory and its state-store ownership alive until it exits. Applications
    /// with a UI or single-threaded synchronization context should call <see cref="DisposeAsync"/>.
    /// </remarks>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    /// <summary>Stops the provider session and asynchronously drains cooperative handlers.</summary>
    /// <returns>A task that completes after disconnection and native-memory release.</returns>
    public async ValueTask DisposeAsync()
    {
        Task? existingAttempt = null;
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                existingAttempt = _disposeCompletion.Task;
            }
            else
            {
                Volatile.Write(ref _disposed, 1);
                _disposeCompletion = CreateDisposeCompletion();
            }
        }

        if (existingAttempt is not null)
        {
            await existingAttempt.ConfigureAwait(false);
            return;
        }

        lock (_lifecycleGate)
        {
            Volatile.Write(ref _stopping, 1);
            _shutdown.Cancel();
            foreach (ActiveRequest request in _requests.Values)
            {
                request.Cancel();
            }

            foreach (CallbackRequest request in _callbackRequests.Values)
            {
                request.Cancel();
            }

            foreach (NotificationRequest request in _notificationRequests.Values)
            {
                request.Cancel();
            }
        }

        CloudProviderDispatcher? dispatcher = _dispatcher;
        Exception? dispatcherFailure = null;
        try
        {
            if (dispatcher is not null)
            {
                await dispatcher.DisposeAsync(_options.ShutdownTimeout).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            dispatcherFailure = exception;
        }

        int disconnectResult = CfApi.CfDisconnectSyncRoot(_connectionKey);
        if (dispatcher is not null && !dispatcher.DisposeCompletion.IsCompleted)
        {
            _ = CompleteNativeDisposeAfterDispatcherAsync(dispatcher, disconnectResult);
            if (dispatcherFailure is not null)
            {
                throw dispatcherFailure;
            }

            if (disconnectResult < 0)
            {
                CloudDiagnostics.RecordNativeFailure(
                    "CloudProviderSession.Disconnect",
                    disconnectResult,
                    _connectionKey.Internal,
                    0,
                    _syncRootPath);
                throw CloudFilesException.FromHResult(
                    "CloudProviderSession.Disconnect",
                    _syncRootPath,
                    disconnectResult);
            }

            throw new TimeoutException(
                "Cloud provider handlers are still draining; callback state and the state store " +
                "must remain owned until they finish.");
        }

        if (disconnectResult < 0)
        {
            // The native connection may still dispatch callbacks after a failed disconnect.
            // Retaining the callback table is safer than freeing it and creating a UAF window;
            // reset the disposal claim so a caller can retry after the native failure clears.
            CloudDiagnostics.RecordNativeFailure(
                "CloudProviderSession.Disconnect",
                disconnectResult,
                _connectionKey.Internal,
                0,
                _syncRootPath);
            CloudFilesException failure = CloudFilesException.FromHResult(
                "CloudProviderSession.Disconnect",
                _syncRootPath,
                disconnectResult);
            FailDisposeAttempt(failure);
            throw failure;
        }

        FinalizeNativeDispose();
        if (dispatcherFailure is not null)
        {
            throw dispatcherFailure;
        }

        GC.SuppressFinalize(this);
    }

    internal Task DisposeCompletion => _disposeCompletion.Task;

    private async Task CompleteNativeDisposeAfterDispatcherAsync(
        CloudProviderDispatcher dispatcher,
        int initialDisconnectResult)
    {
        try
        {
            await dispatcher.DisposeCompletion.ConfigureAwait(false);
            int disconnectResult = initialDisconnectResult < 0
                ? CfApi.CfDisconnectSyncRoot(_connectionKey)
                : initialDisconnectResult;
            if (disconnectResult < 0)
            {
                CloudDiagnostics.RecordNativeFailure(
                    "CloudProviderSession.Disconnect",
                    disconnectResult,
                    _connectionKey.Internal,
                    0,
                    _syncRootPath);
                FailDisposeAttempt(CloudFilesException.FromHResult(
                    "CloudProviderSession.Disconnect",
                    _syncRootPath,
                    disconnectResult));
                return;
            }

            FinalizeNativeDispose();
        }
        catch (Exception exception)
        {
            // Keep callback memory and context owned if the deferred disconnect attempt itself
            // fails unexpectedly. The next explicit DisposeAsync call can retry the boundary.
            FailDisposeAttempt(exception);
        }
    }

    private void FinalizeNativeDispose()
    {
        if (Interlocked.Exchange(ref _nativeStateReleased, 1) != 0)
        {
            return;
        }

        ReleaseNativeState();
        _shutdown.Dispose();
        Volatile.Read(ref _disposeCompletion).TrySetResult(null);
    }

    private void FailDisposeAttempt(Exception exception)
    {
        lock (_lifecycleGate)
        {
            _disposeCompletion.TrySetException(exception);
            Volatile.Write(ref _disposed, 0);
            _disposeCompletion = CreateDisposeCompletion();
        }
    }

    private static TaskCompletionSource<object?> CreateDisposeCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private unsafe void ConnectCore(string path)
    {
        _dispatcher = new CloudProviderDispatcher(_options);
        _callbackTable = (CfCallbackRegistration*)NativeMemory.Alloc(
            (nuint)CallbackRegistrationCount,
            (nuint)sizeof(CfCallbackRegistration));
        if (_callbackTable is null)
        {
            _dispatcher.DisposeAsync(_options.ShutdownTimeout).AsTask().GetAwaiter().GetResult();
            _dispatcher = null;
            throw new InvalidOperationException(
                "Unable to allocate the Cloud Files callback table.");
        }

        _callbackTable[0] = new CfCallbackRegistration
        {
            Type = CfCallbackType.FetchData,
            Callback = &FetchDataCallback,
        };
        _callbackTable[1] = new CfCallbackRegistration
        {
            Type = CfCallbackType.CancelFetchData,
            Callback = &CancelFetchDataCallback,
        };
        _callbackTable[2] = new CfCallbackRegistration
        {
            Type = CfCallbackType.FetchPlaceholders,
            Callback = &FetchPlaceholdersCallback,
        };
        _callbackTable[3] = new CfCallbackRegistration
        {
            Type = CfCallbackType.CancelFetchPlaceholders,
            Callback = &CancelFetchPlaceholdersCallback,
        };
        _callbackTable[4] = new CfCallbackRegistration
        {
            Type = CfCallbackType.ValidateData,
            Callback = &ValidateDataCallback,
        };
        _callbackTable[5] = new CfCallbackRegistration
        {
            Type = CfCallbackType.NotifyFileOpenCompletion,
            Callback = &NotifyFileOpenCompletionCallback,
        };
        _callbackTable[6] = new CfCallbackRegistration
        {
            Type = CfCallbackType.NotifyFileCloseCompletion,
            Callback = &NotifyFileCloseCompletionCallback,
        };
        _callbackTable[7] = new CfCallbackRegistration
        {
            Type = CfCallbackType.NotifyDehydrate,
            Callback = &NotifyDehydrateCallback,
        };
        _callbackTable[8] = new CfCallbackRegistration
        {
            Type = CfCallbackType.NotifyDehydrateCompletion,
            Callback = &NotifyDehydrateCompletionCallback,
        };
        _callbackTable[9] = new CfCallbackRegistration
        {
            Type = CfCallbackType.NotifyDelete,
            Callback = &NotifyDeleteCallback,
        };
        _callbackTable[10] = new CfCallbackRegistration
        {
            Type = CfCallbackType.NotifyDeleteCompletion,
            Callback = &NotifyDeleteCompletionCallback,
        };
        _callbackTable[11] = new CfCallbackRegistration
        {
            Type = CfCallbackType.NotifyRename,
            Callback = &NotifyRenameCallback,
        };
        _callbackTable[12] = new CfCallbackRegistration
        {
            Type = CfCallbackType.NotifyRenameCompletion,
            Callback = &NotifyRenameCompletionCallback,
        };
        _callbackTable[13] = new CfCallbackRegistration
        {
            Type = CfCallbackType.None,
            Callback = null,
        };
        _callbackContext = GCHandle.Alloc(this, GCHandleType.Normal);

        int result;
        fixed (char* pathPointer = path)
        {
            result = CfApi.CfConnectSyncRoot(
                pathPointer,
                _callbackTable,
                (void*)GCHandle.ToIntPtr(_callbackContext),
                CreateConnectFlags(_options),
                out _connectionKey);
        }

        if (result < 0)
        {
            ReleaseNativeState();
            _dispatcher.DisposeAsync(_options.ShutdownTimeout).AsTask().GetAwaiter().GetResult();
            _dispatcher = null;
            ThrowIfFailed("CloudProviderSession.Connect", result);
        }
    }

    private static CfConnectFlags CreateConnectFlags(CloudProviderSessionOptions options)
    {
        CfConnectFlags flags = CfConnectFlags.None;
        if (options.RequireFullFilePath)
        {
            flags |= CfConnectFlags.RequireFullFilePath;
        }

        if (options.RequireProcessInfo)
        {
            flags |= CfConnectFlags.RequireProcessInfo;
        }

        if (options.BlockSelfImplicitHydration)
        {
            flags |= CfConnectFlags.BlockSelfImplicitHydration;
        }

        return flags;
    }

    private unsafe void DispatchFetch(
        CfCallbackInfo* callbackInfo,
        CfCallbackParameters* parameters)
    {
        CfCallbackFetchDataParameters nativeRequest = parameters->FetchData;
        int identityLength = checked((int)callbackInfo->FileIdentityLength);
        byte[] identity = identityLength == 0
            ? []
            : new ReadOnlySpan<byte>(callbackInfo->FileIdentity, identityLength).ToArray();
        string path = callbackInfo->NormalizedPath is null
            ? string.Empty
            : new string(callbackInfo->NormalizedPath);
        CfConnectionKey connectionKey = callbackInfo->ConnectionKey;
        CfTransferKey transferKey = callbackInfo->TransferKey;
        CfRequestKey requestKey = ReadRequestKey(callbackInfo);
        long fileSize = callbackInfo->FileSize;
        CloudCorrelationVector? correlationVector = CopyCorrelationVector(callbackInfo);
        CloudFileFetchRequest request = new(
            path,
            identity,
            fileSize,
            nativeRequest.RequiredFileOffset,
            nativeRequest.RequiredLength,
            new CloudProviderProgressReporter((target, completed, total) =>
                ReportProgress(
                    connectionKey,
                    transferKey,
                    requestKey,
                    target,
                    completed,
                    total,
                    _options.ProgressFallbackPolicy)),
            (replacement, markInSync) => RestartHydrationAsync(
                connectionKey,
                transferKey,
                requestKey,
                nativeRequest.RequiredFileOffset,
                nativeRequest.RequiredLength,
                replacement,
                markInSync),
            correlationVector,
            new CloudProviderRequestId(requestKey.Internal));
        CancellationTokenSource cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        ActiveRequest activeRequest = new(
            connectionKey,
            transferKey,
            requestKey,
            request,
            cancellation);

        CloudProviderRequestRegistryKey registryKey = CloudProviderRequestRegistryKey.Create(
            connectionKey,
            transferKey,
            requestKey);
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                CompleteRequest(activeRequest, NtStatus.CloudFileRequestAborted);
                return;
            }

            if (!_requests.TryAdd(registryKey, activeRequest))
            {
                CompleteRequest(activeRequest, NtStatus.CloudFileUnsuccessful);
                return;
            }

            CloudProviderWorkItem workItem = new(
                CloudProviderRequestKind.FetchData,
                cancellation,
                _ => new ValueTask(ProcessRequestAsync(activeRequest)),
                () => CompleteRequest(activeRequest, NtStatus.CloudFileRequestAborted),
                _ => CompleteRequest(activeRequest, NtStatus.CloudFileUnsuccessful));
            if (_dispatcher is null || !_dispatcher.TryEnqueue(workItem))
            {
                CompleteRequest(activeRequest, NtStatus.CloudFileUnsuccessful);
            }
        }
    }

    private async Task ProcessRequestAsync(ActiveRequest activeRequest)
    {
        try
        {
            CancellationToken cancellationToken = activeRequest.Cancellation.Token;
            await using Stream source = await _contentProvider
                .OpenReadAsync(activeRequest.Request, cancellationToken)
                .ConfigureAwait(false);
            if (!source.CanRead)
            {
                throw new InvalidOperationException("The content provider returned an unreadable stream.");
            }

            if (activeRequest.Request.Offset < 0 ||
                activeRequest.Request.Length <= 0 ||
                activeRequest.Request.Offset > activeRequest.Request.FileSize ||
                activeRequest.Request.Length > activeRequest.Request.FileSize - activeRequest.Request.Offset)
            {
                throw new InvalidDataException("The requested range exceeds the logical file size.");
            }

            if (activeRequest.Request.Offset != 0)
            {
                if (!source.CanSeek)
                {
                    throw new InvalidOperationException(
                        "A non-seekable content stream cannot satisfy a non-zero file offset.");
                }

                source.Seek(activeRequest.Request.Offset, SeekOrigin.Begin);
            }

            byte[] buffer = ArrayPool<byte>.Shared.Rent(
                GetTransferBufferSize(_options.TransferChunkSize, activeRequest.Request.Length));
            try
            {
                long offset = activeRequest.Request.Offset;
                long remaining = activeRequest.Request.Length;
                while (remaining != 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int requested = checked((int)Math.Min(buffer.Length, remaining));
                    int read = await ReadExactlyAsync(
                        source,
                        buffer.AsMemory(0, requested),
                        cancellationToken).ConfigureAwait(false);
                    SendData(activeRequest, buffer.AsSpan(0, read), offset);
                    offset += read;
                    remaining -= read;
                }

                activeRequest.MarkSuccessful();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException exception)
        {
            CloudDiagnostics.RecordProviderFailure(
                "CloudProviderSession.FetchData",
                exception,
                activeRequest.ConnectionKey.Internal,
                activeRequest.TransferKey.Internal,
                activeRequest.RequestKey.Internal,
                activeRequest.Request.NormalizedPath);
            SendFailure(activeRequest, NtStatus.CloudFileRequestCanceled);
        }
        catch (Exception exception)
        {
            CloudDiagnostics.RecordProviderFailure(
                "CloudProviderSession.FetchData",
                exception,
                activeRequest.ConnectionKey.Internal,
                activeRequest.TransferKey.Internal,
                activeRequest.RequestKey.Internal,
                activeRequest.Request.NormalizedPath);
            SendFailure(activeRequest, NtStatus.CloudFileUnsuccessful);
        }
        finally
        {
            RemoveRequest(activeRequest);
        }
    }

    private unsafe void DispatchFetchPlaceholders(
        CfCallbackInfo* callbackInfo,
        CfCallbackParameters* parameters)
    {
        int identityLength = checked((int)callbackInfo->FileIdentityLength);
        byte[] identity = identityLength == 0
            ? []
            : new ReadOnlySpan<byte>(callbackInfo->FileIdentity, identityLength).ToArray();
        string path = callbackInfo->NormalizedPath is null
            ? string.Empty
            : new string(callbackInfo->NormalizedPath);
        string pattern = parameters->FetchPlaceholders.Pattern is null
            ? string.Empty
            : new string(parameters->FetchPlaceholders.Pattern);
        _directoryContinuations.TryGetValue(path, out string? continuationToken);
        CloudCorrelationVector? correlationVector = CopyCorrelationVector(callbackInfo);
        CloudProviderFetchPlaceholdersRequest request = new(
            path,
            identity,
            pattern,
            continuationToken,
            correlationVector);
        CancellationTokenSource cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        PlaceholderRequest activeRequest = new(
            callbackInfo->ConnectionKey,
            callbackInfo->TransferKey,
            ReadRequestKey(callbackInfo),
            request,
            cancellation);

        CloudProviderRequestRegistryKey registryKey = CloudProviderRequestRegistryKey.Create(
            callbackInfo->ConnectionKey,
            callbackInfo->TransferKey,
            ReadRequestKey(callbackInfo));
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                CompletePlaceholderRequest(activeRequest, NtStatus.CloudFileRequestAborted);
                return;
            }

            if (!_callbackRequests.TryAdd(registryKey, activeRequest))
            {
                CompletePlaceholderRequest(activeRequest, NtStatus.CloudFileUnsuccessful);
                return;
            }

            CloudProviderWorkItem workItem = new(
                CloudProviderRequestKind.FetchPlaceholders,
                cancellation,
                _ => new ValueTask(ProcessPlaceholdersAsync(activeRequest)),
                () => CompletePlaceholderRequest(activeRequest, NtStatus.CloudFileRequestAborted),
                _ => CompletePlaceholderRequest(activeRequest, NtStatus.CloudFileUnsuccessful));
            if (_dispatcher is null || !_dispatcher.TryEnqueue(workItem))
            {
                CompletePlaceholderRequest(activeRequest, NtStatus.CloudFileUnsuccessful);
            }
        }
    }

    private async Task ProcessPlaceholdersAsync(PlaceholderRequest activeRequest)
    {
        SemaphoreSlim? populationGate = null;
        bool gateEntered = false;
        ICloudStateTransaction? stateTransaction = null;
        try
        {
            if (_contentProvider is not ICloudDemandProvider demandProvider)
            {
                throw new NotSupportedException("The provider does not support directory population.");
            }

            CloudProviderFetchPlaceholdersRequest request =
                await LoadContinuationAsync(activeRequest.Request).ConfigureAwait(false);
            activeRequest.Request = request;
            populationGate = _directoryPopulationGates.GetOrAdd(
                request.NormalizedPath,
                static _ => new SemaphoreSlim(1, 1));
            await populationGate.WaitAsync(activeRequest.Cancellation.Token).ConfigureAwait(false);
            gateEntered = true;
            request = await LoadContinuationAsync(request).ConfigureAwait(false);
            activeRequest.Request = request;
            CloudProviderDirectoryPage page = await demandProvider
                .FetchChildrenAsync(request, activeRequest.Cancellation.Token)
                .ConfigureAwait(false);
            ValidateDirectoryPage(request, page);
            if (_stateStore is not null)
            {
                stateTransaction = await _stateStore
                    .BeginTransactionAsync(_shutdown.Token)
                    .ConfigureAwait(false);
            }

            // Keep the durable transaction open across the native transfer, but only write the
            // page after Windows reports that every entry was processed successfully. A native
            // partial result therefore cannot publish mappings or a continuation for entries
            // that Windows did not accept.
            SendPlaceholders(activeRequest, page);
            if (stateTransaction is not null)
            {
                await PersistDirectoryPageAsync(
                    request,
                    page,
                    stateTransaction).ConfigureAwait(false);
                await stateTransaction.CommitAsync(_shutdown.Token).ConfigureAwait(false);
            }

            if (page.ContinuationToken is null)
            {
                _directoryContinuations.TryRemove(request.NormalizedPath, out _);
            }
            else
            {
                _directoryContinuations[request.NormalizedPath] = page.ContinuationToken;
            }
        }
        catch (OperationCanceledException exception)
        {
            CloudDiagnostics.RecordProviderFailure(
                "CloudProviderSession.FetchPlaceholders",
                exception,
                activeRequest.ConnectionKey.Internal,
                activeRequest.TransferKey.Internal,
                activeRequest.RequestKey.Internal,
                activeRequest.Request.NormalizedPath);
            // A canceled population request did not publish a page. Drop its in-memory token so
            // a later request restarts from the durable checkpoint (if one exists) instead of
            // retaining an abandoned continuation indefinitely.
            TryRemoveDirectoryContinuation(
                _directoryContinuations,
                activeRequest.Request.NormalizedPath);
            SendPlaceholderFailure(activeRequest, NtStatus.CloudFileRequestCanceled);
        }
        catch (Exception exception)
        {
            CloudDiagnostics.RecordProviderFailure(
                "CloudProviderSession.FetchPlaceholders",
                exception,
                activeRequest.ConnectionKey.Internal,
                activeRequest.TransferKey.Internal,
                activeRequest.RequestKey.Internal,
                activeRequest.Request.NormalizedPath);
            SendPlaceholderFailure(activeRequest, NtStatus.CloudFileUnsuccessful);
        }
        finally
        {
            if (stateTransaction is not null)
            {
                await stateTransaction.DisposeAsync().ConfigureAwait(false);
            }

            if (gateEntered)
            {
                // Keep the per-directory gate in the session cache until native shutdown. Removing
                // it here permits a queued request to observe a newly-created semaphore while the
                // old one is still held, defeating population serialization.
                populationGate!.Release();
            }

            RemovePlaceholderRequest(activeRequest);
        }
    }

    private static void ValidateDirectoryPage(
        CloudProviderFetchPlaceholdersRequest request,
        CloudProviderDirectoryPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.ContinuationToken is not null &&
            string.Equals(page.ContinuationToken, request.ContinuationToken, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The provider returned the same directory continuation token twice.");
        }

        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        HashSet<Guid> identities = [];
        foreach (CloudPlaceholderSpec child in page.Children)
        {
            if (!names.Add(child.Name))
            {
                throw new InvalidDataException(
                    $"The provider returned duplicate child name '{child.Name}'.");
            }

            if (!identities.Add(child.Identity.ItemId))
            {
                throw new InvalidDataException(
                    $"The provider returned duplicate item identity '{child.Identity.ItemId}'.");
            }
        }
    }

    private async ValueTask<CloudProviderFetchPlaceholdersRequest> LoadContinuationAsync(
        CloudProviderFetchPlaceholdersRequest request)
    {
        if (_directoryContinuations.TryGetValue(
            request.NormalizedPath,
            out string? inMemoryContinuation))
        {
            return request.WithContinuationToken(inMemoryContinuation);
        }

        if (_stateStore is null)
        {
            return request;
        }

        await using ICloudStateTransaction transaction = await _stateStore
            .BeginTransactionAsync(_shutdown.Token)
            .ConfigureAwait(false);
        CloudStateCheckpoint? checkpoint = await transaction.Checkpoints
            .GetAsync(GetDirectoryCheckpointName(request.NormalizedPath), _shutdown.Token)
            .ConfigureAwait(false);
        if (checkpoint is null)
        {
            return request;
        }

        string continuation = Encoding.UTF8.GetString(checkpoint.Value.Span);
        if (string.IsNullOrWhiteSpace(continuation))
        {
            throw new InvalidDataException("The durable directory continuation is empty.");
        }

        _directoryContinuations[request.NormalizedPath] = continuation;
        return request.WithContinuationToken(continuation);
    }

    private async ValueTask PersistDirectoryPageAsync(
        CloudProviderFetchPlaceholdersRequest request,
        CloudProviderDirectoryPage page,
        ICloudStateTransaction transaction)
    {
        string directoryPath = ResolveCallbackPath(request.NormalizedPath) ??
            throw new InvalidDataException(
                $"The directory callback path is outside the connected sync root: '{request.NormalizedPath}'.");

        string relativeDirectory = Path.GetRelativePath(_syncRootPath, directoryPath);
        if (relativeDirectory == ".")
        {
            relativeDirectory = string.Empty;
        }

        DateTimeOffset updatedAt = DateTimeOffset.UtcNow;
        foreach (CloudPlaceholderSpec child in page.Children)
        {
            string relativePath = relativeDirectory.Length == 0
                ? child.Name
                : Path.Combine(relativeDirectory, child.Name);
            await transaction.Items.UpsertAsync(
                new CloudItemState(
                    child.Identity.ItemId,
                    child.Identity.RemoteId,
                    relativePath,
                    child.Kind,
                    child.Identity.RemoteRevision,
                    localFileId: null,
                    isTombstone: false,
                    updatedAt),
                _shutdown.Token).ConfigureAwait(false);
        }

        await CloudDirectoryProvenance.RetainProjectionsAsync(transaction, _syncRootPath,
            page.Children.Select(child => (relativeDirectory.Length == 0 ? child.Name : Path.Combine(relativeDirectory, child.Name), child.Kind)),
            _shutdown.Token).ConfigureAwait(false);

        string checkpointName = GetDirectoryCheckpointName(request.NormalizedPath);
        if (page.ContinuationToken is null)
        {
            await transaction.Checkpoints.RemoveAsync(checkpointName, _shutdown.Token)
                .ConfigureAwait(false);
        }
        else
        {
            await transaction.Checkpoints.UpsertAsync(
                new CloudStateCheckpoint(
                    checkpointName,
                    Encoding.UTF8.GetBytes(page.ContinuationToken),
                    updatedAt),
                _shutdown.Token).ConfigureAwait(false);
        }

    }

    private string? ResolveCallbackPath(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath) ||
            (normalizedPath.Length == 1 &&
                (normalizedPath[0] == Path.DirectorySeparatorChar ||
                    normalizedPath[0] == Path.AltDirectorySeparatorChar)))
        {
            return _syncRootPath;
        }

        string rootPrefix = _syncRootPath + Path.DirectorySeparatorChar;
        string candidate;
        if (Path.IsPathFullyQualified(normalizedPath))
        {
            candidate = Path.GetFullPath(normalizedPath);
        }
        else if (normalizedPath.StartsWith(Path.DirectorySeparatorChar) ||
            normalizedPath.StartsWith(Path.AltDirectorySeparatorChar))
        {
            string relative = normalizedPath.TrimStart(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string driveCandidate = Path.GetFullPath(Path.Combine(
                Path.GetPathRoot(_syncRootPath) ?? _syncRootPath,
                relative));
            candidate = driveCandidate.Equals(_syncRootPath, StringComparison.OrdinalIgnoreCase) ||
                driveCandidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
                ? driveCandidate
                : Path.GetFullPath(Path.Combine(_syncRootPath, relative));
        }
        else
        {
            candidate = Path.GetFullPath(Path.Combine(_syncRootPath, normalizedPath));
        }

        return candidate.Equals(_syncRootPath, StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }

    private static string GetDirectoryCheckpointName(string normalizedPath) =>
        "cfsharp.directory." + normalizedPath;

    private unsafe void DispatchValidateData(
        CfCallbackInfo* callbackInfo,
        CfCallbackParameters* parameters)
    {
        int identityLength = checked((int)callbackInfo->FileIdentityLength);
        byte[] identity = identityLength == 0
            ? []
            : new ReadOnlySpan<byte>(callbackInfo->FileIdentity, identityLength).ToArray();
        string path = callbackInfo->NormalizedPath is null
            ? string.Empty
            : new string(callbackInfo->NormalizedPath);
        CfCallbackValidateDataParameters nativeRequest = parameters->ValidateData;
        CloudCorrelationVector? correlationVector = CopyCorrelationVector(callbackInfo);
        CloudProviderValidateDataRequest request = new(
            path,
            identity,
            callbackInfo->FileSize,
            nativeRequest.RequiredFileOffset,
            nativeRequest.RequiredLength,
            nativeRequest.Flags.HasFlag(CfCallbackValidateDataFlags.ExplicitHydration),
            correlationVector);
        CancellationTokenSource cancellation =
            CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        ValidationRequest activeRequest = new(
            callbackInfo->ConnectionKey,
            callbackInfo->TransferKey,
            ReadRequestKey(callbackInfo),
            request,
            cancellation);
        CloudProviderRequestRegistryKey registryKey = CloudProviderRequestRegistryKey.Create(
            callbackInfo->ConnectionKey,
            callbackInfo->TransferKey,
            ReadRequestKey(callbackInfo));
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                CompleteValidationRequest(activeRequest, NtStatus.CloudFileRequestAborted);
                return;
            }

            if (!_callbackRequests.TryAdd(registryKey, activeRequest))
            {
                CompleteValidationRequest(activeRequest, NtStatus.CloudFileUnsuccessful);
                return;
            }

            CloudProviderWorkItem workItem = new(
                CloudProviderRequestKind.ValidateData,
                cancellation,
                _ => new ValueTask(ProcessValidationAsync(activeRequest)),
                () => CompleteValidationRequest(activeRequest, NtStatus.CloudFileRequestAborted),
                _ => CompleteValidationRequest(activeRequest, NtStatus.CloudFileUnsuccessful));
            if (_dispatcher is null || !_dispatcher.TryEnqueue(workItem))
            {
                CompleteValidationRequest(activeRequest, NtStatus.CloudFileUnsuccessful);
            }
        }
    }

    private async Task ProcessValidationAsync(ValidationRequest activeRequest)
    {
        try
        {
            if (_contentProvider is not ICloudDemandProvider demandProvider)
            {
                throw new NotSupportedException("The provider does not support data validation.");
            }

            CloudProviderValidationResult result = await demandProvider
                .ValidateDataAsync(activeRequest.Request, activeRequest.Cancellation.Token)
                .ConfigureAwait(false);
            NtStatus status = result.Status is CloudProviderValidationStatus.Accepted
                ? NtStatus.Success
                : NtStatus.CloudFileUnsuccessful;
            SendAckData(activeRequest, status);
        }
        catch (OperationCanceledException exception)
        {
            CloudDiagnostics.RecordProviderFailure(
                "CloudProviderSession.ValidateData",
                exception,
                activeRequest.ConnectionKey.Internal,
                activeRequest.TransferKey.Internal,
                activeRequest.RequestKey.Internal,
                activeRequest.Request.NormalizedPath);
            SendAckData(activeRequest, NtStatus.CloudFileRequestCanceled);
        }
        catch (Exception exception)
        {
            CloudDiagnostics.RecordProviderFailure(
                "CloudProviderSession.ValidateData",
                exception,
                activeRequest.ConnectionKey.Internal,
                activeRequest.TransferKey.Internal,
                activeRequest.RequestKey.Internal,
                activeRequest.Request.NormalizedPath);
            SendAckData(activeRequest, NtStatus.CloudFileUnsuccessful);
        }
        finally
        {
            RemoveCallbackRequest(activeRequest);
        }
    }

    private unsafe void DispatchDehydrate(
        CfCallbackInfo* callbackInfo,
        CfCallbackParameters* parameters)
    {
        if (_contentProvider is not ICloudDemandProvider demandProvider)
        {
            SendPolicyFailure(callbackInfo, CfOperationType.AckDehydrate);
            return;
        }

        string path = callbackInfo->NormalizedPath is null
            ? string.Empty
            : new string(callbackInfo->NormalizedPath);
        byte[] identity = CopyIdentity(callbackInfo);
        CloudCorrelationVector? correlationVector = CopyCorrelationVector(callbackInfo);
        CfCallbackDehydrateParameters nativeRequest = parameters->Dehydrate;
        CloudProviderDehydrateRequest request = new(
            path,
            identity,
            nativeRequest.Flags.HasFlag(CfCallbackDehydrateFlags.Background),
            (CloudProviderDehydrationReason)nativeRequest.Reason,
            correlationVector);
        PolicyRequest activeRequest = new(
            callbackInfo->ConnectionKey,
            callbackInfo->TransferKey,
            ReadRequestKey(callbackInfo),
            CfOperationType.AckDehydrate,
            CloudProviderRequestKind.Dehydrate,
            request,
            cancellationToken => demandProvider.ApproveDehydrateAsync(request, cancellationToken),
            CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
        EnqueuePolicyRequest(activeRequest);
    }

    internal unsafe void DispatchPolicyCallbackForTesting(
        CloudProviderRequestKind kind,
        string normalizedPath,
        string? targetPath = null,
        bool isDirectory = false,
        bool isUndelete = false,
        bool isBackground = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedPath);
        if (kind is not (CloudProviderRequestKind.Dehydrate or
            CloudProviderRequestKind.Delete or
            CloudProviderRequestKind.Rename))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The callback is not a policy callback.");
        }

        char[] path = (normalizedPath + '\0').ToCharArray();
        char[] target = targetPath is null ? ['\0'] : (targetPath + '\0').ToCharArray();
        byte[] identity = [1, 2, 3, 4];
        fixed (char* pathPointer = path)
        fixed (char* targetPointer = target)
        fixed (byte* identityPointer = identity)
        {
            CfCallbackInfo callbackInfo = new()
            {
                StructSize = (uint)sizeof(CfCallbackInfo),
                ConnectionKey = new CfConnectionKey { Internal = 1 },
                FileIdentity = identityPointer,
                FileIdentityLength = (uint)identity.Length,
                NormalizedPath = pathPointer,
                TransferKey = new CfTransferKey { Internal = 2 },
                RequestKey = new CfRequestKey
                {
                    Internal = Interlocked.Increment(ref _testingRequestKey),
                },
            };
            CfCallbackParameters callbackParameters = default;
            switch (kind)
            {
                case CloudProviderRequestKind.Dehydrate:
                    callbackParameters.Dehydrate = new CfCallbackDehydrateParameters
                    {
                        Flags = isBackground
                            ? CfCallbackDehydrateFlags.Background
                            : CfCallbackDehydrateFlags.None,
                        Reason = CfCallbackDehydrationReason.UserManual,
                    };
                    DispatchDehydrate(&callbackInfo, &callbackParameters);
                    break;
                case CloudProviderRequestKind.Delete:
                    callbackParameters.Delete = new CfCallbackDeleteParameters
                    {
                        Flags = (isDirectory ? CfCallbackDeleteFlags.IsDirectory : CfCallbackDeleteFlags.None) |
                            (isUndelete ? CfCallbackDeleteFlags.IsUndelete : CfCallbackDeleteFlags.None),
                    };
                    DispatchDelete(&callbackInfo, &callbackParameters);
                    break;
                case CloudProviderRequestKind.Rename:
                    callbackParameters.Rename = new CfCallbackRenameParameters
                    {
                        Flags = (isDirectory ? CfCallbackRenameFlags.IsDirectory : CfCallbackRenameFlags.None) |
                            CfCallbackRenameFlags.SourceInScope |
                            CfCallbackRenameFlags.TargetInScope,
                        TargetPath = targetPointer,
                    };
                    DispatchRename(&callbackInfo, &callbackParameters);
                    break;
            }
        }
    }

    internal unsafe void DispatchCompletionCallbackForTesting(
        CloudProviderNotificationKind kind,
        string normalizedPath,
        string? relatedPath = null,
        uint flags = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedPath);
        char[] path = (normalizedPath + '\0').ToCharArray();
        fixed (char* pathPointer = path)
        {
            CfCallbackInfo callbackInfo = new()
            {
                StructSize = (uint)sizeof(CfCallbackInfo),
                ConnectionKey = new CfConnectionKey { Internal = 1 },
                NormalizedPath = pathPointer,
                TransferKey = new CfTransferKey { Internal = 3 },
                RequestKey = new CfRequestKey
                {
                    Internal = Interlocked.Increment(ref _testingRequestKey),
                },
            };
            DispatchCompletion(
                &callbackInfo,
                new CloudProviderCompletionNotification(
                    kind,
                    normalizedPath,
                    ReadOnlySpan<byte>.Empty,
                    flags,
                    relatedPath));
        }
    }

    private unsafe void DispatchDelete(
        CfCallbackInfo* callbackInfo,
        CfCallbackParameters* parameters)
    {
        if (_contentProvider is not ICloudDemandProvider demandProvider)
        {
            SendPolicyFailure(callbackInfo, CfOperationType.AckDelete);
            return;
        }

        string path = callbackInfo->NormalizedPath is null
            ? string.Empty
            : new string(callbackInfo->NormalizedPath);
        CfCallbackDeleteParameters nativeRequest = parameters->Delete;
        CloudCorrelationVector? correlationVector = CopyCorrelationVector(callbackInfo);
        CloudProviderDeleteRequest request = new(
            path,
            CopyIdentity(callbackInfo),
            nativeRequest.Flags.HasFlag(CfCallbackDeleteFlags.IsDirectory),
            nativeRequest.Flags.HasFlag(CfCallbackDeleteFlags.IsUndelete),
            correlationVector);
        PolicyRequest activeRequest = new(
            callbackInfo->ConnectionKey,
            callbackInfo->TransferKey,
            ReadRequestKey(callbackInfo),
            CfOperationType.AckDelete,
            CloudProviderRequestKind.Delete,
            request,
            cancellationToken => demandProvider.ApproveDeleteAsync(request, cancellationToken),
            CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
        EnqueuePolicyRequest(activeRequest);
    }

    private unsafe void DispatchRename(
        CfCallbackInfo* callbackInfo,
        CfCallbackParameters* parameters)
    {
        if (_contentProvider is not ICloudDemandProvider demandProvider)
        {
            SendPolicyFailure(callbackInfo, CfOperationType.AckRename);
            return;
        }

        string path = callbackInfo->NormalizedPath is null
            ? string.Empty
            : new string(callbackInfo->NormalizedPath);
        CfCallbackRenameParameters nativeRequest = parameters->Rename;
        string targetPath = nativeRequest.TargetPath is null
            ? string.Empty
            : new string(nativeRequest.TargetPath);
        CloudCorrelationVector? correlationVector = CopyCorrelationVector(callbackInfo);
        CloudProviderRenameRequest request = new(
            path,
            CopyIdentity(callbackInfo),
            targetPath,
            nativeRequest.Flags.HasFlag(CfCallbackRenameFlags.IsDirectory),
            nativeRequest.Flags.HasFlag(CfCallbackRenameFlags.SourceInScope),
            nativeRequest.Flags.HasFlag(CfCallbackRenameFlags.TargetInScope),
            correlationVector);
        PolicyRequest activeRequest = new(
            callbackInfo->ConnectionKey,
            callbackInfo->TransferKey,
            ReadRequestKey(callbackInfo),
            CfOperationType.AckRename,
            CloudProviderRequestKind.Rename,
            request,
            cancellationToken => demandProvider.ApproveRenameAsync(request, cancellationToken),
            CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
        EnqueuePolicyRequest(activeRequest);
    }

    private void EnqueuePolicyRequest(PolicyRequest activeRequest)
    {
        CloudProviderRequestRegistryKey registryKey = activeRequest.RegistryKey;
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                CompletePolicyRequest(activeRequest, NtStatus.CloudFileRequestAborted);
                return;
            }

            if (!_callbackRequests.TryAdd(registryKey, activeRequest))
            {
                CompletePolicyRequest(activeRequest, NtStatus.CloudFileUnsuccessful);
                return;
            }

            CloudProviderWorkItem workItem = new(
                activeRequest.Kind,
                activeRequest.Cancellation,
                _ => new ValueTask(ProcessPolicyAsync(activeRequest)),
                () => CompletePolicyRequest(activeRequest, NtStatus.CloudFileRequestAborted),
                _ => CompletePolicyRequest(activeRequest, NtStatus.CloudFileUnsuccessful));
            if (_dispatcher is null || !_dispatcher.TryEnqueue(workItem))
            {
                CompletePolicyRequest(activeRequest, NtStatus.CloudFileUnsuccessful);
            }
        }
    }

    private async Task ProcessPolicyAsync(PolicyRequest activeRequest)
    {
        try
        {
            CloudProviderPolicyDecision decision = await activeRequest.Handler(
                activeRequest.Cancellation.Token).ConfigureAwait(false);
            SendPolicyResult(
                activeRequest,
                decision is CloudProviderPolicyDecision.Allow
                    ? NtStatus.Success
                    : NtStatus.CloudFileUnsuccessful);
        }
        catch (OperationCanceledException exception)
        {
            CloudDiagnostics.RecordProviderFailure(
                "CloudProviderSession.Policy",
                exception,
                activeRequest.ConnectionKey.Internal,
                activeRequest.TransferKey.Internal,
                activeRequest.RequestKey.Internal,
                GetPolicyPath(activeRequest.Request));
            SendPolicyResult(activeRequest, NtStatus.CloudFileRequestCanceled);
        }
        catch (Exception exception)
        {
            CloudDiagnostics.RecordProviderFailure(
                "CloudProviderSession.Policy",
                exception,
                activeRequest.ConnectionKey.Internal,
                activeRequest.TransferKey.Internal,
                activeRequest.RequestKey.Internal,
                GetPolicyPath(activeRequest.Request));
            SendPolicyResult(activeRequest, NtStatus.CloudFileUnsuccessful);
        }
        finally
        {
            RemoveCallbackRequest(activeRequest);
        }
    }

    private static unsafe byte[] CopyIdentity(CfCallbackInfo* callbackInfo)
    {
        int length = checked((int)callbackInfo->FileIdentityLength);
        return length == 0
            ? []
            : new ReadOnlySpan<byte>(callbackInfo->FileIdentity, length).ToArray();
    }

    internal static unsafe CfRequestKey ReadRequestKey(CfCallbackInfo* callbackInfo)
    {
        int offset = Marshal.OffsetOf<CfCallbackInfo>(nameof(CfCallbackInfo.RequestKey)).ToInt32();
        uint requiredSize = checked((uint)(offset + sizeof(CfRequestKey)));
        return callbackInfo->StructSize >= requiredSize
            ? callbackInfo->RequestKey
            : default;
    }

    private static unsafe CloudCorrelationVector? CopyCorrelationVector(CfCallbackInfo* callbackInfo)
    {
        if (callbackInfo->CorrelationVector is null)
        {
            return null;
        }

        return CloudCorrelationVector.FromNative(
            *(CfCorrelationVector*)callbackInfo->CorrelationVector);
    }

    private unsafe void DispatchCompletion(
        CfCallbackInfo* callbackInfo,
        CloudProviderCompletionNotification notification)
    {
        if (_contentProvider is not ICloudDemandProvider demandProvider)
        {
            return;
        }

        long registryKey = Interlocked.Increment(ref _notificationRegistryKey);
        NotificationRequest activeRequest = new(
            callbackInfo->ConnectionKey,
            callbackInfo->TransferKey,
            ReadRequestKey(callbackInfo),
            notification,
            CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token),
            registryKey);
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                activeRequest.Cancel();
                activeRequest.DisposeCancellation();
                return;
            }

            if (!_notificationRequests.TryAdd(registryKey, activeRequest))
            {
                activeRequest.DisposeCancellation();
                return;
            }

            CloudProviderWorkItem workItem = new(
                CloudProviderRequestKind.CompletionNotification,
                activeRequest.Cancellation,
                _ => new ValueTask(ProcessCompletionAsync(activeRequest, demandProvider)),
                () => RemoveNotificationRequest(activeRequest),
                _ => RemoveNotificationRequest(activeRequest));
            if (_dispatcher is null || !_dispatcher.TryEnqueue(workItem))
            {
                RemoveNotificationRequest(activeRequest);
            }
        }
    }

    private async Task ProcessCompletionAsync(
        NotificationRequest activeRequest,
        ICloudDemandProvider demandProvider)
    {
        try
        {
            await InvalidateDirectoryContinuationsAsync(
                activeRequest.Notification).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // State invalidation is best effort during shutdown; the notification still gets
            // offered to the provider below with its canceled token.
        }
        catch (Exception)
        {
            // A state-store failure must not suppress the completed native notification.
        }

        try
        {
            await demandProvider.OnCompletionAsync(
                activeRequest.Notification,
                activeRequest.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Notification delivery is best effort; cancellation is observed during shutdown.
        }
        catch (Exception)
        {
            // Notification failures must not affect the completed native operation.
        }
        finally
        {
            RemoveNotificationRequest(activeRequest);
        }
    }

    private void RemoveNotificationRequest(NotificationRequest activeRequest)
    {
        ((ICollection<KeyValuePair<long, NotificationRequest>>)_notificationRequests).Remove(
            new KeyValuePair<long, NotificationRequest>(
                activeRequest.NotificationRegistryKey,
                activeRequest));
        activeRequest.DisposeCancellation();
    }

    private async ValueTask InvalidateDirectoryContinuationsAsync(
        CloudProviderCompletionNotification notification)
    {
        if (notification.Kind is not (CloudProviderNotificationKind.DeleteCompleted or
            CloudProviderNotificationKind.RenameCompleted))
        {
            return;
        }

        List<string> paths = [notification.NormalizedPath];
        if (notification.RelatedPath is not null)
        {
            paths.Add(notification.RelatedPath);
        }

        HashSet<string> invalidationPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            invalidationPaths.Add(NormalizeContinuationPath(path));
            string? resolved = ResolveCallbackPath(path);
            if (resolved is not null)
            {
                invalidationPaths.Add(NormalizeContinuationPath(resolved));
            }
        }

        if (invalidationPaths.Count == 0)
        {
            return;
        }

        List<string> staleMemoryKeys = [];
        foreach (string existingPath in _directoryContinuations.Keys)
        {
            string normalizedExisting = NormalizeContinuationPath(existingPath);
            if (invalidationPaths.Any(path => IsContinuationPathOrDescendant(
                normalizedExisting,
                path)))
            {
                staleMemoryKeys.Add(existingPath);
            }
        }

        if (_stateStore is null)
        {
            foreach (string staleMemoryKey in staleMemoryKeys)
            {
                _directoryContinuations.TryRemove(staleMemoryKey, out _);
            }

            return;
        }

        await using ICloudStateTransaction transaction = await _stateStore
            .BeginTransactionAsync(_shutdown.Token)
            .ConfigureAwait(false);
        HashSet<string> removedCheckpoints = new(StringComparer.Ordinal);
        foreach (string path in invalidationPaths)
        {
            string checkpointPrefix = GetDirectoryCheckpointName(path);
            IReadOnlyList<CloudStateCheckpoint> checkpoints = await transaction.Checkpoints
                .ListAsync(checkpointPrefix, _shutdown.Token)
                .ConfigureAwait(false);
            foreach (CloudStateCheckpoint checkpoint in checkpoints)
            {
                string checkpointPath = NormalizeContinuationPath(
                    checkpoint.Name["cfsharp.directory.".Length..]);
                if (IsContinuationPathOrDescendant(checkpointPath, path) &&
                    removedCheckpoints.Add(checkpoint.Name))
                {
                    await transaction.Checkpoints.RemoveAsync(
                        checkpoint.Name,
                        _shutdown.Token).ConfigureAwait(false);
                }
            }
        }

        await transaction.CommitAsync(_shutdown.Token).ConfigureAwait(false);
        foreach (string staleMemoryKey in staleMemoryKeys)
        {
            _directoryContinuations.TryRemove(staleMemoryKey, out _);
        }
    }

    private static string NormalizeContinuationPath(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);

    private static bool IsContinuationPathOrDescendant(string candidate, string parent)
    {
        if (candidate.Equals(parent, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = parent.EndsWith(Path.DirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    internal void SetDirectoryContinuationForTesting(string path, string token) =>
        _directoryContinuations[path] = token;

    internal bool HasDirectoryContinuationForTesting(string path) =>
        _directoryContinuations.ContainsKey(path);

    internal static bool TryRemoveDirectoryContinuation(
        ConcurrentDictionary<string, string> continuations,
        string path) =>
        continuations.TryRemove(path, out _);

    private static async ValueTask<int> ReadExactlyAsync(
        Stream source,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = await source.ReadAsync(destination[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The content source ended before the requested range.");
            }

            total += read;
        }

        return total;
    }

    internal static int GetTransferBufferSize(int transferChunkSize, long requestLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(transferChunkSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestLength);
        return checked((int)Math.Min((long)transferChunkSize, requestLength));
    }

    private static unsafe void SendData(
        ActiveRequest request,
        ReadOnlySpan<byte> data,
        long offset)
    {
        fixed (byte* buffer = data)
        {
            CfOperationInfo operationInfo = request.CreateOperationInfo();
            CfOperationParameters parameters = default;
            parameters.ParamSize = checked((uint)(8 + sizeof(CfOperationTransferDataParameters)));
            parameters.TransferData = new CfOperationTransferDataParameters
            {
                CompletionStatus = NtStatus.Success,
                Buffer = buffer,
                Offset = offset,
                Length = data.Length,
            };

            int result = CfApi.CfExecute(&operationInfo, &parameters);
            ThrowIfFailed("CloudProviderSession.TransferData", result);
        }
    }

    private static CloudProgressReportResult ReportProgress(
        CfConnectionKey connectionKey,
        CfTransferKey transferKey,
        CfRequestKey requestKey,
        CloudProgressTarget target,
        long completed,
        long total,
        CloudProgressFallbackPolicy fallbackPolicy)
    {
        if (target.IsCurrentHydration)
        {
            return ReportProgressV1(connectionKey, transferKey, total, completed);
        }

        if (!OperatingSystem.IsWindowsVersionAtLeast(
            10,
            0,
            checked((int)CloudFilesPlatformInfo.ProviderProgressV2MinimumWindowsBuild)))
        {
            return fallbackPolicy is CloudProgressFallbackPolicy.FallbackToV1
                ? ReportProgressV1(connectionKey, transferKey, total, completed)
                : new CloudProgressReportResult(
                    CloudProgressReportState.Unsupported,
                    UsedV2: true,
                    HResult: unchecked((int)0x80070032));
        }

        try
        {
            int result = CfApi.CfReportProviderProgress2(
                connectionKey,
                transferKey,
                new CfRequestKey { Internal = target.RequestId!.Value.ToNativeValue() },
                total,
                completed,
                target.TargetSessionId);
            return result < 0
                ? new CloudProgressReportResult(CloudProgressReportState.NativeFailure, true, result)
                : new CloudProgressReportResult(CloudProgressReportState.Reported, true, result);
        }
        catch (EntryPointNotFoundException)
        {
            return fallbackPolicy is CloudProgressFallbackPolicy.FallbackToV1
                ? ReportProgressV1(connectionKey, transferKey, total, completed)
                : new CloudProgressReportResult(
                    CloudProgressReportState.Unsupported,
                    UsedV2: true,
                    HResult: unchecked((int)0x8007007F));
        }
    }

    private static CloudProgressReportResult ReportProgressV1(
        CfConnectionKey connectionKey,
        CfTransferKey transferKey,
        long total,
        long completed)
    {
        int result = CfApi.CfReportProviderProgress(
            connectionKey,
            transferKey,
            total,
            completed);
        return result < 0
            ? new CloudProgressReportResult(CloudProgressReportState.NativeFailure, false, result)
            : new CloudProgressReportResult(CloudProgressReportState.Reported, false, result);
    }

    private static unsafe ValueTask RestartHydrationAsync(
        CfConnectionKey connectionKey,
        CfTransferKey transferKey,
        CfRequestKey requestKey,
        long requiredOffset,
        long requiredLength,
        CloudPlaceholderSpec replacement,
        bool markInSync)
    {
        if (replacement is not CloudFilePlaceholderSpec replacementFile)
        {
            return ValueTask.FromException(new ArgumentException(
                "Hydration restart requires a file placeholder specification.",
                nameof(replacement)));
        }

        if (replacementFile.Length < requiredOffset ||
            requiredLength > replacementFile.Length - requiredOffset)
        {
            return ValueTask.FromException(new ArgumentOutOfRangeException(
                nameof(replacement),
                "The replacement file is smaller than the active hydration range."));
        }

        byte[] identity = replacement.Identity.Encode();
        CfFsMetadata metadata = CloudPlaceholderPlatform.CreateMetadata(replacement);
        CfOperationInfo operationInfo = new()
        {
            StructSize = (uint)sizeof(CfOperationInfo),
            Type = CfOperationType.RestartHydration,
            ConnectionKey = connectionKey,
            TransferKey = transferKey,
            RequestKey = requestKey,
        };
        fixed (byte* identityPointer = identity)
        {
            CfOperationParameters parameters = default;
            parameters.ParamSize = checked((uint)(8 + sizeof(CfOperationRestartHydrationParameters)));
            parameters.RestartHydration = new CfOperationRestartHydrationParameters
            {
                Flags = markInSync
                    ? CfOperationRestartHydrationFlags.MarkInSync
                    : CfOperationRestartHydrationFlags.None,
                FsMetadata = &metadata,
                FileIdentity = identityPointer,
                FileIdentityLength = checked((uint)identity.Length),
            };
            int result = CfApi.CfExecute(&operationInfo, &parameters);
            ThrowIfFailed("CloudProviderSession.RestartHydration", result);
        }

        return ValueTask.CompletedTask;
    }

    private static unsafe void SendPlaceholders(
        PlaceholderRequest request,
        CloudProviderDirectoryPage page)
    {
        CloudPlaceholderSpec[] specifications = page.Children.ToArray();
        CfPlaceholderCreateInfo[] entries = new CfPlaceholderCreateInfo[specifications.Length];
        List<GCHandle> pinned = new(specifications.Length * 2);
        try
        {
            for (int index = 0; index < specifications.Length; index++)
            {
                CloudPlaceholderSpec specification = specifications[index];
                char[] name = (specification.Name + '\0').ToCharArray();
                byte[] identity = specification.Identity.Encode();
                GCHandle nameHandle = GCHandle.Alloc(name, GCHandleType.Pinned);
                pinned.Add(nameHandle);
                GCHandle identityHandle = GCHandle.Alloc(identity, GCHandleType.Pinned);
                pinned.Add(identityHandle);
                entries[index] = new CfPlaceholderCreateInfo
                {
                    RelativeFileName = (char*)nameHandle.AddrOfPinnedObject(),
                    FsMetadata = CloudPlaceholderPlatform.CreateMetadata(specification),
                    FileIdentity = (void*)identityHandle.AddrOfPinnedObject(),
                    FileIdentityLength = checked((uint)identity.Length),
                    Flags = CloudPlaceholderPlatform.CreateFlags(specification),
                };
            }

            if (!request.TryMarkTerminal())
            {
                return;
            }

            fixed (CfPlaceholderCreateInfo* entriesPointer = entries)
            {
                CfOperationInfo operationInfo = request.CreateOperationInfo(
                    CfOperationType.TransferPlaceholders);
                CfOperationParameters parameters = default;
                parameters.ParamSize = checked((uint)(8 + sizeof(CfOperationTransferPlaceholdersParameters)));
                parameters.TransferPlaceholders = new CfOperationTransferPlaceholdersParameters
                {
                    Flags = page.IsComplete &&
                        (page.TotalCount is null || page.TotalCount <= entries.Length)
                        ? CfOperationTransferPlaceholdersFlags.DisableOnDemandPopulation
                        : CfOperationTransferPlaceholdersFlags.None,
                    CompletionStatus = NtStatus.Success,
                    PlaceholderTotalCount = page.TotalCount ?? -1,
                    PlaceholderArray = entriesPointer,
                    PlaceholderCount = checked((uint)entries.Length),
                };
                int result;
                try
                {
                    result = CfApi.CfExecute(&operationInfo, &parameters);
                }
                catch
                {
                    request.ReleaseTerminalClaim();
                    throw;
                }

                if (result < 0)
                {
                    request.ReleaseTerminalClaim();
                    CloudDiagnostics.RecordNativeFailure(
                        "CloudProviderSession.TransferPlaceholders",
                        result,
                        request.ConnectionKey.Internal,
                        request.RequestKey.Internal,
                        request.Request.NormalizedPath);
                    throw CloudFilesException.FromHResult(
                        "CloudProviderSession.TransferPlaceholders",
                        request.Request.NormalizedPath,
                        result);
                }

                int[] entryResults = new int[entries.Length];
                for (int index = 0; index < entries.Length; index++)
                {
                    entryResults[index] = entries[index].Result;
                }

                ValidatePlaceholderTransferResultsOrReleaseTerminalClaim(
                    entryResults,
                    parameters.TransferPlaceholders.EntriesProcessed,
                    request.Request.NormalizedPath,
                    request.ReleaseTerminalClaim);
            }
        }
        finally
        {
            for (int index = pinned.Count - 1; index >= 0; index--)
            {
                pinned[index].Free();
            }
        }
    }

    internal static void ValidatePlaceholderTransferResults(
        ReadOnlySpan<int> entryResults,
        uint entriesProcessed,
        string? path)
    {
        if (entriesProcessed > entryResults.Length)
        {
            throw new InvalidDataException(
                $"Windows returned {entriesProcessed} processed placeholder entries for a " +
                $"batch containing {entryResults.Length} entries.");
        }

        for (int index = 0; index < entriesProcessed; index++)
        {
            int entryResult = entryResults[index];
            if (entryResult < 0)
            {
                throw CloudFilesException.FromHResult(
                    "CloudProviderSession.TransferPlaceholders.Entry",
                    path,
                    entryResult);
            }
        }

        if (entriesProcessed != entryResults.Length)
        {
            throw new InvalidDataException(
                $"Windows processed {entriesProcessed} of {entryResults.Length} placeholder entries.");
        }
    }

    internal static void ValidatePlaceholderTransferResultsOrReleaseTerminalClaim(
        ReadOnlySpan<int> entryResults,
        uint entriesProcessed,
        string? path,
        Action releaseTerminalClaim)
    {
        ArgumentNullException.ThrowIfNull(releaseTerminalClaim);
        try
        {
            ValidatePlaceholderTransferResults(entryResults, entriesProcessed, path);
        }
        catch
        {
            releaseTerminalClaim();
            throw;
        }
    }

    private static unsafe void SendFailure(ActiveRequest request, NtStatus status)
    {
        if (!request.TryMarkTerminal())
        {
            return;
        }

        CfOperationInfo operationInfo = request.CreateOperationInfo();
        CfOperationParameters parameters = default;
        parameters.ParamSize = checked((uint)(8 + sizeof(CfOperationTransferDataParameters)));
        parameters.TransferData = new CfOperationTransferDataParameters
        {
            CompletionStatus = status,
            Buffer = null,
            Offset = request.Request.Offset,
            Length = request.Request.Length,
        };
        int result = CfApi.CfExecute(&operationInfo, &parameters);
        ObserveCompletionResult(
            "CloudProviderSession.TransferDataFailure",
            result,
            request.ConnectionKey.Internal,
            request.RequestKey.Internal,
            request.Request.NormalizedPath);
    }

    private static unsafe void SendPlaceholderFailure(
        PlaceholderRequest request,
        NtStatus status)
    {
        if (!request.TryMarkTerminal())
        {
            return;
        }

        CfOperationInfo operationInfo = request.CreateOperationInfo(
            CfOperationType.TransferPlaceholders);
        CfOperationParameters parameters = default;
        parameters.ParamSize = checked((uint)(8 + sizeof(CfOperationTransferPlaceholdersParameters)));
        parameters.TransferPlaceholders = new CfOperationTransferPlaceholdersParameters
        {
            CompletionStatus = status,
            PlaceholderTotalCount = 0,
            PlaceholderArray = null,
            PlaceholderCount = 0,
        };
        int result = CfApi.CfExecute(&operationInfo, &parameters);
        ObserveCompletionResult(
            "CloudProviderSession.TransferPlaceholdersFailure",
            result,
            request.ConnectionKey.Internal,
            request.RequestKey.Internal,
            request.Request.NormalizedPath);
    }

    private static unsafe void SendAckData(
        ValidationRequest request,
        NtStatus status)
    {
        if (!request.TryMarkTerminal())
        {
            return;
        }

        CfOperationInfo operationInfo = request.CreateOperationInfo(CfOperationType.AckData);
        CfOperationParameters parameters = default;
        parameters.ParamSize = checked((uint)(8 + sizeof(CfOperationAckDataParameters)));
        parameters.AckData = new CfOperationAckDataParameters
        {
            CompletionStatus = status,
            Offset = request.Request.Range.Offset,
            Length = request.Request.Range.Length,
        };
        int result = CfApi.CfExecute(&operationInfo, &parameters);
        ObserveCompletionResult(
            "CloudProviderSession.AcknowledgeData",
            result,
            request.ConnectionKey.Internal,
            request.RequestKey.Internal,
            request.Request.NormalizedPath);
    }

    private static unsafe void SendPolicyResult(PolicyRequest request, NtStatus status)
    {
        if (!request.TryMarkTerminal())
        {
            return;
        }

        CfOperationInfo operationInfo = request.CreateOperationInfo(request.OperationType);
        CfOperationParameters parameters = default;
        switch (request.OperationType)
        {
            case CfOperationType.AckDehydrate:
                parameters.ParamSize = checked((uint)(8 + sizeof(CfOperationAckDehydrateParameters)));
                parameters.AckDehydrate = new CfOperationAckDehydrateParameters
                {
                    CompletionStatus = status,
                };
                break;
            case CfOperationType.AckDelete:
                parameters.ParamSize = checked((uint)(8 + sizeof(CfOperationAckDeleteParameters)));
                parameters.AckDelete = new CfOperationAckDeleteParameters
                {
                    CompletionStatus = status,
                };
                break;
            case CfOperationType.AckRename:
                parameters.ParamSize = checked((uint)(8 + sizeof(CfOperationAckRenameParameters)));
                parameters.AckRename = new CfOperationAckRenameParameters
                {
                    CompletionStatus = status,
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.OperationType, "Unsupported acknowledgement operation.");
        }

        int result = CfApi.CfExecute(&operationInfo, &parameters);
        ObserveCompletionResult(
            "CloudProviderSession.PolicyResult",
            result,
            request.ConnectionKey.Internal,
            request.RequestKey.Internal,
            GetPolicyPath(request.Request));
    }

    private static unsafe void SendPolicyFailure(
        CfCallbackInfo* callbackInfo,
        CfOperationType operationType)
    {
        CfOperationInfo operationInfo = new()
        {
            StructSize = (uint)sizeof(CfOperationInfo),
            Type = operationType,
            ConnectionKey = callbackInfo->ConnectionKey,
            TransferKey = callbackInfo->TransferKey,
            RequestKey = ReadRequestKey(callbackInfo),
        };
        CfOperationParameters parameters = default;
        switch (operationType)
        {
            case CfOperationType.AckDehydrate:
                parameters.ParamSize = checked((uint)(8 + sizeof(CfOperationAckDehydrateParameters)));
                parameters.AckDehydrate.CompletionStatus = NtStatus.CloudFileUnsuccessful;
                break;
            case CfOperationType.AckDelete:
                parameters.ParamSize = checked((uint)(8 + sizeof(CfOperationAckDeleteParameters)));
                parameters.AckDelete.CompletionStatus = NtStatus.CloudFileUnsuccessful;
                break;
            case CfOperationType.AckRename:
                parameters.ParamSize = checked((uint)(8 + sizeof(CfOperationAckRenameParameters)));
                parameters.AckRename.CompletionStatus = NtStatus.CloudFileUnsuccessful;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operationType));
        }

        int result = CfApi.CfExecute(&operationInfo, &parameters);
        string path = callbackInfo->NormalizedPath is null
            ? string.Empty
            : new string(callbackInfo->NormalizedPath);
        ObserveCompletionResult(
            "CloudProviderSession.PolicyFailure",
            result,
            callbackInfo->ConnectionKey.Internal,
            ReadRequestKey(callbackInfo).Internal,
            path);
    }

    private static void ObserveCompletionResult(
        string operation,
        int hresult,
        long connectionKey,
        long requestKey,
        string? path)
    {
        if (hresult < 0)
        {
            CloudDiagnostics.RecordNativeFailure(
                operation,
                hresult,
                connectionKey,
                requestKey,
                path);
        }
    }

    private static string? GetPolicyPath(object request) => request switch
    {
        CloudProviderDehydrateRequest dehydrate => dehydrate.NormalizedPath,
        CloudProviderDeleteRequest delete => delete.NormalizedPath,
        CloudProviderRenameRequest rename => rename.NormalizedPath,
        _ => null,
    };

    private void CompleteRequest(ActiveRequest activeRequest, NtStatus status)
    {
        SendFailure(activeRequest, status);
        RemoveRequest(activeRequest);
    }

    private void RemoveRequest(ActiveRequest activeRequest)
    {
        ((ICollection<KeyValuePair<CloudProviderRequestRegistryKey, ActiveRequest>>)_requests).Remove(
            new KeyValuePair<CloudProviderRequestRegistryKey, ActiveRequest>(
                activeRequest.RegistryKey,
                activeRequest));
        activeRequest.DisposeCancellation();
    }

    private void CompletePlaceholderRequest(PlaceholderRequest activeRequest, NtStatus status)
    {
        SendPlaceholderFailure(activeRequest, status);
        RemovePlaceholderRequest(activeRequest);
    }

    private void RemovePlaceholderRequest(PlaceholderRequest activeRequest)
        => RemoveCallbackRequest(activeRequest);

    private void CompleteValidationRequest(ValidationRequest activeRequest, NtStatus status)
    {
        SendAckData(activeRequest, status);
        RemoveCallbackRequest(activeRequest);
    }

    private void CompletePolicyRequest(PolicyRequest activeRequest, NtStatus status)
    {
        SendPolicyResult(activeRequest, status);
        RemoveCallbackRequest(activeRequest);
    }

    private void RemoveCallbackRequest(CallbackRequest activeRequest)
    {
        ((ICollection<KeyValuePair<CloudProviderRequestRegistryKey, CallbackRequest>>)_callbackRequests).Remove(
            new KeyValuePair<CloudProviderRequestRegistryKey, CallbackRequest>(
                activeRequest.RegistryKey,
                activeRequest));
        activeRequest.DisposeCancellation();
    }

    private unsafe void CancelRequest(CfCallbackInfo* callbackInfo)
    {
        CloudProviderRequestRegistryKey registryKey = CloudProviderRequestRegistryKey.Create(
            callbackInfo->ConnectionKey,
            callbackInfo->TransferKey,
            ReadRequestKey(callbackInfo));
        if (_requests.TryGetValue(registryKey, out ActiveRequest? request))
        {
            request.Cancel();
        }

        if (_callbackRequests.TryGetValue(registryKey, out CallbackRequest? callbackRequest))
        {
            callbackRequest.Cancel();
        }
    }

    private static unsafe CloudProviderSession? GetSession(CfCallbackInfo* callbackInfo)
    {
        if (callbackInfo is null || callbackInfo->CallbackContext is null)
        {
            return null;
        }

        return GCHandle.FromIntPtr((nint)callbackInfo->CallbackContext).Target
            as CloudProviderSession;
    }

    private static unsafe void RecordCallbackFailure(
        string callbackType,
        CfCallbackInfo* callbackInfo,
        Exception exception)
    {
        try
        {
            long connectionKey = callbackInfo is null ? 0 : callbackInfo->ConnectionKey.Internal;
            long transferKey = callbackInfo is null ? 0 : callbackInfo->TransferKey.Internal;
            long requestKey = callbackInfo is null ? 0 : ReadRequestKey(callbackInfo).Internal;
            string? path = callbackInfo is null || callbackInfo->NormalizedPath is null
                ? null
                : new string(callbackInfo->NormalizedPath);
            CloudDiagnostics.RecordProviderFailure(
                "CloudProviderSession.Callback." + callbackType,
                exception,
                connectionKey,
                transferKey,
                requestKey,
                path);
        }
        catch
        {
            // A malformed native callback must not turn diagnostics into a second unmanaged
            // boundary failure. The original exception remains intentionally contained.
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void FetchDataCallback(
        CfCallbackInfo* callbackInfo,
        CfCallbackParameters* callbackParameters)
    {
        try
        {
            if (callbackInfo is not null && callbackParameters is not null)
            {
                GetSession(callbackInfo)?.DispatchFetch(callbackInfo, callbackParameters);
            }
        }
        catch (Exception exception)
        {
            RecordCallbackFailure("FetchData", callbackInfo, exception);
            // Exceptions must never cross the unmanaged callback boundary.
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void CancelFetchDataCallback(
        CfCallbackInfo* callbackInfo,
        CfCallbackParameters* callbackParameters)
    {
        _ = callbackParameters;
        try
        {
            if (callbackInfo is not null)
            {
                GetSession(callbackInfo)?.CancelRequest(callbackInfo);
            }
        }
        catch (Exception exception)
        {
            RecordCallbackFailure("CancelFetchData", callbackInfo, exception);
            // Exceptions must never cross the unmanaged callback boundary.
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void FetchPlaceholdersCallback(
        CfCallbackInfo* callbackInfo,
        CfCallbackParameters* callbackParameters)
    {
        try
        {
            if (callbackInfo is not null && callbackParameters is not null)
            {
                GetSession(callbackInfo)?.DispatchFetchPlaceholders(callbackInfo, callbackParameters);
            }
        }
        catch (Exception exception)
        {
            RecordCallbackFailure("FetchPlaceholders", callbackInfo, exception);
            // Exceptions must never cross the unmanaged callback boundary.
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void CancelFetchPlaceholdersCallback(
        CfCallbackInfo* callbackInfo,
        CfCallbackParameters* callbackParameters)
    {
        _ = callbackParameters;
        try
        {
            if (callbackInfo is not null)
            {
                GetSession(callbackInfo)?.CancelRequest(callbackInfo);
            }
        }
        catch (Exception exception)
        {
            RecordCallbackFailure("CancelFetchPlaceholders", callbackInfo, exception);
            // Exceptions must never cross the unmanaged callback boundary.
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void ValidateDataCallback(
        CfCallbackInfo* callbackInfo,
        CfCallbackParameters* callbackParameters)
    {
        try
        {
            if (callbackInfo is not null && callbackParameters is not null)
            {
                GetSession(callbackInfo)?.DispatchValidateData(callbackInfo, callbackParameters);
            }
        }
        catch (Exception exception)
        {
            RecordCallbackFailure("ValidateData", callbackInfo, exception);
            // Exceptions must never cross the unmanaged callback boundary.
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void NotifyFileOpenCompletionCallback(
        CfCallbackInfo* callbackInfo,
        CfCallbackParameters* callbackParameters)
    {
        try
        {
            if (callbackInfo is not null && callbackParameters is not null)
            {
                CloudProviderSession? session = GetSession(callbackInfo);
                if (session is not null)
                {
                    string path = callbackInfo->NormalizedPath is null
                        ? string.Empty
                        : new string(callbackInfo->NormalizedPath);
                    session.DispatchCompletion(
                        callbackInfo,
                        new CloudProviderCompletionNotification(
                            CloudProviderNotificationKind.FileOpenCompleted,
                            path,
                            CopyIdentity(callbackInfo),
                            (uint)callbackParameters->OpenCompletion.Flags,
                            relatedPath: null,
                            correlationVector: CopyCorrelationVector(callbackInfo)));
                }
            }
        }
        catch (Exception exception)
        {
            RecordCallbackFailure("NotifyFileOpenCompletion", callbackInfo, exception);
            // Exceptions must never cross the unmanaged callback boundary.
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void NotifyFileCloseCompletionCallback(
        CfCallbackInfo* callbackInfo,
        CfCallbackParameters* callbackParameters)
    {
        try
        {
            if (callbackInfo is not null && callbackParameters is not null)
            {
                CloudProviderSession? session = GetSession(callbackInfo);
                if (session is not null)
                {
                    string path = callbackInfo->NormalizedPath is null
                        ? string.Empty
                        : new string(callbackInfo->NormalizedPath);
                    session.DispatchCompletion(
                        callbackInfo,
                        new CloudProviderCompletionNotification(
                            CloudProviderNotificationKind.FileCloseCompleted,
                            path,
                            CopyIdentity(callbackInfo),
                            (uint)callbackParameters->CloseCompletion.Flags,
                            relatedPath: null,
                            correlationVector: CopyCorrelationVector(callbackInfo)));
                }
            }
        }
        catch (Exception exception)
        {
            RecordCallbackFailure("NotifyFileCloseCompletion", callbackInfo, exception);
            // Exceptions must never cross the unmanaged callback boundary.
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void NotifyDehydrateCallback(
        CfCallbackInfo* callbackInfo,
        CfCallbackParameters* callbackParameters)
    {
        try
        {
            if (callbackInfo is not null && callbackParameters is not null)
            {
                GetSession(callbackInfo)?.DispatchDehydrate(callbackInfo, callbackParameters);
            }
        }
        catch (Exception exception)
        {
            RecordCallbackFailure("NotifyDehydrate", callbackInfo, exception);
            // Exceptions must never cross the unmanaged callback boundary.
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void NotifyDehydrateCompletionCallback(
        CfCallbackInfo* callbackInfo,
        CfCallbackParameters* callbackParameters)
    {
        try
        {
            if (callbackInfo is not null && callbackParameters is not null)
            {
                CloudProviderSession? session = GetSession(callbackInfo);
                if (session is not null)
                {
                    string path = callbackInfo->NormalizedPath is null
                        ? string.Empty
                        : new string(callbackInfo->NormalizedPath);
                    session.DispatchCompletion(
                        callbackInfo,
                        new CloudProviderCompletionNotification(
                            CloudProviderNotificationKind.DehydrateCompleted,
                            path,
                            CopyIdentity(callbackInfo),
                            (uint)callbackParameters->DehydrateCompletion.Flags,
                            relatedPath: null,
                            correlationVector: CopyCorrelationVector(callbackInfo)));
                }
            }
        }
        catch (Exception exception)
        {
            RecordCallbackFailure("NotifyDehydrateCompletion", callbackInfo, exception);
            // Exceptions must never cross the unmanaged callback boundary.
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void NotifyDeleteCallback(
        CfCallbackInfo* callbackInfo,
        CfCallbackParameters* callbackParameters)
    {
        try
        {
            if (callbackInfo is not null && callbackParameters is not null)
            {
                GetSession(callbackInfo)?.DispatchDelete(callbackInfo, callbackParameters);
            }
        }
        catch (Exception exception)
        {
            RecordCallbackFailure("NotifyDelete", callbackInfo, exception);
            // Exceptions must never cross the unmanaged callback boundary.
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void NotifyDeleteCompletionCallback(
        CfCallbackInfo* callbackInfo,
        CfCallbackParameters* callbackParameters)
    {
        try
        {
            if (callbackInfo is not null && callbackParameters is not null)
            {
                CloudProviderSession? session = GetSession(callbackInfo);
                if (session is not null)
                {
                    string path = callbackInfo->NormalizedPath is null
                        ? string.Empty
                        : new string(callbackInfo->NormalizedPath);
                    session.DispatchCompletion(
                        callbackInfo,
                        new CloudProviderCompletionNotification(
                            CloudProviderNotificationKind.DeleteCompleted,
                            path,
                            CopyIdentity(callbackInfo),
                            (uint)callbackParameters->DeleteCompletion.Flags,
                            relatedPath: null,
                            correlationVector: CopyCorrelationVector(callbackInfo)));
                }
            }
        }
        catch (Exception exception)
        {
            RecordCallbackFailure("NotifyDeleteCompletion", callbackInfo, exception);
            // Exceptions must never cross the unmanaged callback boundary.
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void NotifyRenameCallback(
        CfCallbackInfo* callbackInfo,
        CfCallbackParameters* callbackParameters)
    {
        try
        {
            if (callbackInfo is not null && callbackParameters is not null)
            {
                GetSession(callbackInfo)?.DispatchRename(callbackInfo, callbackParameters);
            }
        }
        catch (Exception exception)
        {
            RecordCallbackFailure("NotifyRename", callbackInfo, exception);
            // Exceptions must never cross the unmanaged callback boundary.
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static unsafe void NotifyRenameCompletionCallback(
        CfCallbackInfo* callbackInfo,
        CfCallbackParameters* callbackParameters)
    {
        try
        {
            if (callbackInfo is not null && callbackParameters is not null)
            {
                CloudProviderSession? session = GetSession(callbackInfo);
                if (session is not null)
                {
                    string path = callbackInfo->NormalizedPath is null
                        ? string.Empty
                        : new string(callbackInfo->NormalizedPath);
                    char* sourcePointer = callbackParameters->RenameCompletion.SourcePath;
                    string? sourcePath = sourcePointer is null ? null : new string(sourcePointer);
                    session.DispatchCompletion(
                        callbackInfo,
                        new CloudProviderCompletionNotification(
                            CloudProviderNotificationKind.RenameCompleted,
                            path,
                            CopyIdentity(callbackInfo),
                            (uint)callbackParameters->RenameCompletion.Flags,
                            sourcePath,
                            CopyCorrelationVector(callbackInfo)));
                }
            }
        }
        catch (Exception exception)
        {
            RecordCallbackFailure("NotifyRenameCompletion", callbackInfo, exception);
            // Exceptions must never cross the unmanaged callback boundary.
        }
    }

    private unsafe void ReleaseNativeState()
    {
        foreach (SemaphoreSlim gate in _directoryPopulationGates.Values)
        {
            gate.Dispose();
        }

        _directoryPopulationGates.Clear();

        if (_callbackContext.IsAllocated)
        {
            _callbackContext.Free();
        }

        if (_callbackTable is not null)
        {
            NativeMemory.Free(_callbackTable);
            _callbackTable = null;
        }
    }

    private void ThrowIfStopping()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopping) != 0, this);
    }

    private static void ThrowIfFailed(string operation, int hresult)
    {
        if (hresult < 0)
        {
            throw CloudFilesException.FromHResult(operation, hresult);
        }
    }

    private sealed class ActiveRequest
    {
        private int _terminal;
        private int _cancellationDisposed;

        internal ActiveRequest(
            CfConnectionKey connectionKey,
            CfTransferKey transferKey,
            CfRequestKey requestKey,
            CloudFileFetchRequest request,
            CancellationTokenSource cancellation)
        {
            ConnectionKey = connectionKey;
            TransferKey = transferKey;
            RequestKey = requestKey;
            Request = request;
            Cancellation = cancellation;
        }

        internal CfConnectionKey ConnectionKey { get; }

        internal CfTransferKey TransferKey { get; }

        internal CfRequestKey RequestKey { get; }

        internal CloudProviderRequestRegistryKey RegistryKey =>
            CloudProviderRequestRegistryKey.Create(ConnectionKey, TransferKey, RequestKey);

        internal CloudFileFetchRequest Request { get; }

        internal CancellationTokenSource Cancellation { get; }

        internal void Cancel()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Completion won the race and already released this request token.
            }
        }

        internal unsafe CfOperationInfo CreateOperationInfo() => new()
        {
            StructSize = (uint)sizeof(CfOperationInfo),
            Type = CfOperationType.TransferData,
            ConnectionKey = ConnectionKey,
            TransferKey = TransferKey,
            RequestKey = RequestKey,
        };

        internal void MarkSuccessful() => Interlocked.Exchange(ref _terminal, 1);

        internal bool TryMarkTerminal() => Interlocked.Exchange(ref _terminal, 1) == 0;

        internal void DisposeCancellation()
        {
            if (Interlocked.Exchange(ref _cancellationDisposed, 1) == 0)
            {
                Cancellation.Dispose();
            }
        }
    }

    private abstract class CallbackRequest
    {
        private int _terminal;
        private int _cancellationDisposed;

        protected CallbackRequest(
            CfConnectionKey connectionKey,
            CfTransferKey transferKey,
            CfRequestKey requestKey,
            CancellationTokenSource cancellation)
        {
            ConnectionKey = connectionKey;
            TransferKey = transferKey;
            RequestKey = requestKey;
            Cancellation = cancellation;
        }

        internal CfConnectionKey ConnectionKey { get; }

        internal CfTransferKey TransferKey { get; }

        internal CfRequestKey RequestKey { get; }

        internal CloudProviderRequestRegistryKey RegistryKey =>
            CloudProviderRequestRegistryKey.Create(ConnectionKey, TransferKey, RequestKey);

        internal CancellationTokenSource Cancellation { get; }

        internal void Cancel()
        {
            try
            {
                Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Completion won the race and already released this request token.
            }
        }

        internal unsafe CfOperationInfo CreateOperationInfo(CfOperationType type) => new()
        {
            StructSize = (uint)sizeof(CfOperationInfo),
            Type = type,
            ConnectionKey = ConnectionKey,
            TransferKey = TransferKey,
            RequestKey = RequestKey,
        };

        internal bool TryMarkTerminal() => Interlocked.Exchange(ref _terminal, 1) == 0;

        internal void ReleaseTerminalClaim() => Interlocked.CompareExchange(ref _terminal, 0, 1);

        internal void DisposeCancellation()
        {
            if (Interlocked.Exchange(ref _cancellationDisposed, 1) == 0)
            {
                Cancellation.Dispose();
            }
        }
    }

    private sealed class PlaceholderRequest : CallbackRequest
    {
        internal PlaceholderRequest(
            CfConnectionKey connectionKey,
            CfTransferKey transferKey,
            CfRequestKey requestKey,
            CloudProviderFetchPlaceholdersRequest request,
            CancellationTokenSource cancellation)
            : base(connectionKey, transferKey, requestKey, cancellation)
        {
            Request = request;
        }

        internal CloudProviderFetchPlaceholdersRequest Request { get; set; }
    }

    private sealed class ValidationRequest : CallbackRequest
    {
        internal ValidationRequest(
            CfConnectionKey connectionKey,
            CfTransferKey transferKey,
            CfRequestKey requestKey,
            CloudProviderValidateDataRequest request,
            CancellationTokenSource cancellation)
            : base(connectionKey, transferKey, requestKey, cancellation)
        {
            Request = request;
        }

        internal CloudProviderValidateDataRequest Request { get; }
    }

    private sealed class PolicyRequest : CallbackRequest
    {
        internal PolicyRequest(
            CfConnectionKey connectionKey,
            CfTransferKey transferKey,
            CfRequestKey requestKey,
            CfOperationType operationType,
            CloudProviderRequestKind kind,
            object request,
            Func<CancellationToken, ValueTask<CloudProviderPolicyDecision>> handler,
            CancellationTokenSource cancellation)
            : base(connectionKey, transferKey, requestKey, cancellation)
        {
            OperationType = operationType;
            Kind = kind;
            Request = request;
            Handler = handler;
        }

        internal CfOperationType OperationType { get; }

        internal CloudProviderRequestKind Kind { get; }

        internal object Request { get; }

        internal Func<CancellationToken, ValueTask<CloudProviderPolicyDecision>> Handler { get; }
    }

    private sealed class NotificationRequest : CallbackRequest
    {
        internal NotificationRequest(
            CfConnectionKey connectionKey,
            CfTransferKey transferKey,
            CfRequestKey requestKey,
            CloudProviderCompletionNotification notification,
            CancellationTokenSource cancellation,
            long registryKey)
            : base(connectionKey, transferKey, requestKey, cancellation)
        {
            Notification = notification;
            NotificationRegistryKey = registryKey;
        }

        internal CloudProviderCompletionNotification Notification { get; }

        internal long NotificationRegistryKey { get; }
    }
}
