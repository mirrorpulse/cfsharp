using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;

namespace CfSharp;

/// <summary>
/// Owns the process-scoped resources for one persistent Windows Cloud Files sync root.
/// </summary>
/// <remarks>
/// <para>
/// Build an instance with <see cref="CreateBuilder(string)"/>, then call <see cref="StartAsync"/> once
/// before performing file-system operations. Starting validates the local root again, opens the
/// configured durable state store, opens or registers the persistent sync root, and optionally
/// connects a content provider. The state-store factory is never called for invalid local-root or
/// builder configuration.
/// </para>
/// <para>
/// A successfully opened state store and provider session are owned exclusively by this instance.
/// Synchronous or asynchronous disposal first stops the provider session and then disposes the
/// store. Disposal never unregisters the persistent sync root; account removal remains an explicit
/// <see cref="CloudSyncRoot.Unregister"/> operation.
/// </para>
/// <para>
/// A local-change feed created after startup is also owned by this instance. It is stopped before
/// the state store is disposed, while its durable journal entries remain available for replay.
/// </para>
/// <para>
/// Public lifecycle members are safe for concurrent calls. Startup and disposal are serialized.
/// A failed startup can be retried only when all partially acquired resources were released. Item
/// operations admitted while started hold explicit resource-lifetime leases. Conflicting path
/// scopes are serialized while non-overlapping paths may proceed concurrently. Disposal rejects
/// new work, drains admitted work, and then releases resources. If resource disposal fails,
/// successfully released resources remain released and the instance stays in
/// <see cref="CloudFileSystemLifecycleState.Stopping"/> so disposal can be retried for the
/// remaining resources.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class CloudFileSystem : IDisposable, IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _disposalGate = new(1, 1);
    private readonly CloudItemOperationCoordinator _operationCoordinator = new();
    private readonly AsyncLocal<CloudFileSystemOperationLease?> _operationContext = new();
    private readonly ICloudStateStoreFactory _stateStoreFactory;
    private readonly SyncRootRegistrationOptions? _registration;
    private readonly ICloudFileContentProvider? _contentProvider;
    private readonly ICloudFileSystemRuntime _runtime;
    private readonly object _localChangeFeedGate = new();
    private ICloudStateStore? _stateStore;
    private ICloudFileSystemRuntimeSession? _runtimeSession;
    private CloudLocalChangeFeed? _localChangeFeed;
    private TaskCompletionSource? _operationsDrained;
    private int _activeOperations;
    private int _state = (int)CloudFileSystemLifecycleState.Created;

    private CloudFileSystem(
        string syncRootPath,
        ICloudStateStoreFactory stateStoreFactory,
        SyncRootRegistrationOptions? registration,
        ICloudFileContentProvider? contentProvider,
        ICloudFileSystemRuntime runtime)
    {
        SyncRootPath = syncRootPath;
        _stateStoreFactory = stateStoreFactory;
        _registration = registration;
        _contentProvider = contentProvider;
        _runtime = runtime;
    }

    /// <summary>Gets the normalized absolute path of the managed sync root.</summary>
    public string SyncRootPath { get; }

    /// <summary>Gets a thread-safe snapshot of the current process lifecycle state.</summary>
    public CloudFileSystemLifecycleState LifecycleState =>
        (CloudFileSystemLifecycleState)Volatile.Read(ref _state);

    /// <summary>Gets an immutable reference to the sync-root directory.</summary>
    /// <exception cref="InvalidOperationException">The file system has not finished starting.</exception>
    /// <exception cref="ObjectDisposedException">The file system is stopping or disposed.</exception>
    public CloudDirectory Root => GetDirectory(string.Empty);

    /// <summary>
    /// Creates or returns the explicit local-change feed owned by this started file system.
    /// </summary>
    /// <param name="options">Bounded watcher and delivery settings, or the defaults.</param>
    /// <returns>A feed that must be started explicitly with <see cref="CloudLocalChangeFeed.StartAsync"/>.</returns>
    /// <exception cref="InvalidOperationException">The file system has not started.</exception>
    /// <exception cref="ObjectDisposedException">The file system is stopping or disposed.</exception>
    /// <remarks>
    /// Only one feed is created for a file system. The file system stops it before disposing the
    /// durable state store. The feed does not perform remote synchronization or periodic full
    /// reconciliation; applications must acknowledge a rescan-required batch only after a full
    /// reconciliation of <see cref="SyncRootPath"/>.
    /// </remarks>
    public CloudLocalChangeFeed CreateLocalChangeFeed(
        CloudLocalChangeFeedOptions? options = null)
    {
        lock (_localChangeFeedGate)
        {
            EnsureStarted();
            return _localChangeFeed ??= new CloudLocalChangeFeed(
                SyncRootPath,
                _stateStore ?? throw new InvalidOperationException(
                    "The cloud file system has no open state store."),
                options ?? CloudLocalChangeFeedOptions.Default,
                new WindowsLocalChangeSource(
                    SyncRootPath,
                    (options ?? CloudLocalChangeFeedOptions.Default).BufferCapacity),
                disposedFeed =>
                {
                    lock (_localChangeFeedGate)
                    {
                        if (ReferenceEquals(_localChangeFeed, disposedFeed))
                        {
                            _localChangeFeed = null;
                        }
                    }
                },
                async (scopes, cancellationToken) => await AcquireOperationCoreAsync(
                    scopes, allowLocalChangeDrain: true, cancellationToken).ConfigureAwait(false));
        }
    }

    /// <summary>Creates a mutable builder for one local sync-root directory.</summary>
    /// <param name="syncRootPath">
    /// Absolute path of the existing directory that is or will become a Cloud Files sync root.
    /// </param>
    /// <returns>A builder that requires an explicit durable state-store factory.</returns>
    /// <exception cref="ArgumentException">
    /// The path is empty, relative, or cannot be normalized.
    /// </exception>
    public static Builder CreateBuilder(string syncRootPath) =>
        new(syncRootPath, WindowsCloudFileSystemRuntime.Instance);

    internal static Builder CreateBuilder(
        string syncRootPath,
        ICloudFileSystemRuntime runtime) =>
        new(syncRootPath, runtime);

    /// <summary>Creates an immutable path-bound file reference without opening the item.</summary>
    /// <param name="relativePath">Path relative to the sync root. It need not currently exist.</param>
    /// <returns>A file reference whose inspections read fresh state.</returns>
    /// <exception cref="ArgumentException">
    /// The path is rooted, identifies the sync root, escapes it lexically, or resolves outside it
    /// through an existing symbolic link or junction.
    /// </exception>
    /// <exception cref="InvalidOperationException">The file system has not finished starting.</exception>
    /// <exception cref="ObjectDisposedException">The file system is stopping or disposed.</exception>
    public CloudFile GetFile(string relativePath)
    {
        EnsureStarted();
        CloudItemPath path = CloudItemPathResolver.Resolve(
            SyncRootPath,
            relativePath,
            allowRoot: false);
        return new CloudFile(this, path.FullPath, path.RelativePath);
    }

    /// <summary>Creates an immutable path-bound directory reference without opening the item.</summary>
    /// <param name="relativePath">
    /// Path relative to the sync root, or an empty string for the root. It need not currently exist.
    /// </param>
    /// <returns>A directory reference whose inspections read fresh state.</returns>
    /// <exception cref="ArgumentException">
    /// The path is rooted, escapes the sync root lexically, or resolves outside it through an
    /// existing symbolic link or junction.
    /// </exception>
    /// <exception cref="InvalidOperationException">The file system has not finished starting.</exception>
    /// <exception cref="ObjectDisposedException">The file system is stopping or disposed.</exception>
    public CloudDirectory GetDirectory(string relativePath)
    {
        EnsureStarted();
        CloudItemPath path = CloudItemPathResolver.Resolve(
            SyncRootPath,
            relativePath,
            allowRoot: true);
        return new CloudDirectory(this, path.FullPath, path.RelativePath);
    }

    internal CloudItem ResolveExistingItem(string relativePath)
    {
        EnsureStarted();
        CloudItemPath path = CloudItemPathResolver.Resolve(
            SyncRootPath,
            relativePath,
            allowRoot: true);
        if (!File.Exists(path.FullPath) && !Directory.Exists(path.FullPath))
        {
            throw new FileNotFoundException(
                "The local cloud item does not exist.",
                path.FullPath);
        }

        FileAttributes attributes = File.GetAttributes(path.FullPath);
        return CreateItemReference(
            path.RelativePath,
            attributes.HasFlag(FileAttributes.Directory)
                ? CloudItemKind.Directory
                : CloudItemKind.File);
    }

    internal CloudItem CreateItemReference(string relativePath, CloudItemKind kind)
    {
        CloudItemPath path = CloudItemPathResolver.Resolve(
            SyncRootPath,
            relativePath,
            allowRoot: kind == CloudItemKind.Directory);
        return kind == CloudItemKind.Directory
            ? new CloudDirectory(this, path.FullPath, path.RelativePath)
            : new CloudFile(this, path.FullPath, path.RelativePath);
    }

    /// <summary>Opens all configured process resources and makes the file system ready.</summary>
    /// <param name="cancellationToken">
    /// Token that cancels waiting for lifecycle ownership or opening the state store. Native
    /// registration and connection calls are synchronous and cannot be interrupted once entered.
    /// </param>
    /// <returns>An operation that completes after the store and optional provider session open.</returns>
    /// <exception cref="DirectoryNotFoundException">
    /// The configured local root no longer exists when startup begins.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The instance is already starting or started.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The instance is stopping or disposed.</exception>
    /// <exception cref="OperationCanceledException">Startup was canceled before completion.</exception>
    /// <exception cref="CloudFilesException">
    /// Windows rejects sync-root registration, lookup, or provider connection.
    /// </exception>
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CloudFileSystemLifecycleState state = LifecycleState;
            ObjectDisposedException.ThrowIf(
                state is CloudFileSystemLifecycleState.Stopping or CloudFileSystemLifecycleState.Disposed,
                this);

            if (state is not CloudFileSystemLifecycleState.Created)
            {
                throw new InvalidOperationException("The cloud file system has already been started.");
            }

            if (!Directory.Exists(SyncRootPath))
            {
                throw new DirectoryNotFoundException(
                    $"The sync-root directory does not exist: '{SyncRootPath}'.");
            }

            Volatile.Write(ref _state, (int)CloudFileSystemLifecycleState.Starting);
            ICloudStateStore? openedStore = null;
            ICloudFileSystemRuntimeSession? openedRuntimeSession = null;
            try
            {
                openedStore = await _stateStoreFactory
                    .OpenAsync(new CloudStateStoreContext(SyncRootPath), cancellationToken)
                    .ConfigureAwait(false);
                if (openedStore is null)
                {
                    throw new InvalidOperationException(
                        "The state-store factory returned no store.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                openedRuntimeSession = _runtime.Start(
                    SyncRootPath,
                    _registration,
                    _contentProvider,
                    openedStore);
                if (openedRuntimeSession is null)
                {
                    throw new InvalidOperationException(
                        "The Cloud Files runtime returned no session.");
                }

                _stateStore = openedStore;
                _runtimeSession = openedRuntimeSession;
                Volatile.Write(ref _state, (int)CloudFileSystemLifecycleState.Started);
            }
            catch (Exception startFailure)
            {
                _runtimeSession = openedRuntimeSession;
                _stateStore = openedStore;
                IReadOnlyList<Exception> cleanupFailures = await DisposeOwnedResourcesAsync()
                    .ConfigureAwait(false);
                Volatile.Write(
                    ref _state,
                    cleanupFailures.Count == 0
                        ? (int)CloudFileSystemLifecycleState.Created
                        : (int)CloudFileSystemLifecycleState.Stopping);
                if (cleanupFailures.Count == 0)
                {
                    ExceptionDispatchInfo.Throw(startFailure);
                }

                throw new AggregateException(
                    "Cloud file-system startup failed and acquired resources could not be released cleanly.",
                    new[] { startFailure }.Concat(cleanupFailures));
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Synchronously releases process resources without unregistering the sync root.</summary>
    /// <remarks>
    /// This compatibility method blocks the calling thread until admitted operations and owned
    /// resources finish disposal. Applications with a UI or single-threaded synchronization
    /// context should call <see cref="DisposeAsync"/> instead and await it.
    /// </remarks>
    /// <exception cref="AggregateException">
    /// More than one owned resource failed during disposal. Resources are attempted in dependency
    /// order; an unfinished or failed feed drain keeps its dependent resources alive for a retry.
    /// Failed resources remain owned so disposal can be retried.
    /// </exception>
    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases process resources without unregistering the persistent sync root.</summary>
    /// <returns>
    /// An operation that completes after admitted item operations drain and the provider session
    /// and state store terminate.
    /// </returns>
    /// <remarks>
    /// Public operation admission stops immediately. Accepted local notifications retain internal
    /// path-scope admission until their feed drains, without holding the lifecycle admission lock.
    /// A failed feed drain retains dependent resources for a later disposal attempt.
    /// </remarks>
    /// <exception cref="AggregateException">
    /// More than one owned resource failed during disposal. Resources are attempted in dependency order.
    /// Failed resources remain owned so disposal can be retried.
    /// </exception>
    public async ValueTask DisposeAsync()
    {
        RejectProtectedLocalReentry();
        // Serialize disposal attempts independently of admission. The feed must acquire the
        // lifecycle gate while draining accepted renames, including after public admission stops.
        await _disposalGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (LifecycleState is CloudFileSystemLifecycleState.Disposed)
                {
                    return;
                }

                Volatile.Write(ref _state, (int)CloudFileSystemLifecycleState.Stopping);
                _contentConfirmationStopping.Cancel();
            }
            finally
            {
                _lifecycleGate.Release();
            }

            IReadOnlyList<Exception> failures = await DisposeOwnedResourcesAsync()
                .ConfigureAwait(false);
            await _lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (failures.Count == 0)
                {
                    _contentConfirmationStopping.Dispose();
                    Volatile.Write(ref _state, (int)CloudFileSystemLifecycleState.Disposed);
                    GC.SuppressFinalize(this);
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }

            if (failures.Count == 1)
            {
                ExceptionDispatchInfo.Throw(failures[0]);
            }

            if (failures.Count > 1)
            {
                throw new AggregateException(
                    "Cloud file-system resources could not be released cleanly.",
                    failures);
            }
        }
        finally
        {
            _disposalGate.Release();
        }
    }

    internal async ValueTask<CloudItemSnapshot> InspectAsync(
        CloudItem item,
        CancellationToken cancellationToken)
    {
        if (s_protectedLocalContext.Value is { } protectedContext)
        {
            return await protectedContext.InspectItemAsync(item, cancellationToken).ConfigureAwait(false);
        }
        using CloudFileSystemOperationLease operation = await AcquireOperationAsync(
            [CloudItemOperationScope.Exact(item.FullPath)],
            cancellationToken).ConfigureAwait(false);
        return await InspectCoreAsync(item, operation.StateStore, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async ValueTask<CloudFileSystemOperationLease> AcquireOperationAsync(
        IEnumerable<CloudItemOperationScope> scopes,
        CancellationToken cancellationToken = default) =>
        await AcquireOperationCoreAsync(
                scopes,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    private async ValueTask<CloudFileSystemOperationLease> AcquireOperationCoreAsync(
        IEnumerable<CloudItemOperationScope> scopes,
        bool allowLocalChangeDrain = false,
        CancellationToken cancellationToken = default)
    {
        RejectProtectedLocalReentry();
        ArgumentNullException.ThrowIfNull(scopes);
        IReadOnlyList<CloudItemOperationScope> requestedScopes = scopes.ToArray();
        ICloudStateStore stateStore;
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Only the owned feed receives this internal admission path. Public operations
            // remain rejected in Stopping, while accepted notifications retain path exclusion
            // and a store-lifetime lease until the feed has completely drained.
            if (!allowLocalChangeDrain || LifecycleState != CloudFileSystemLifecycleState.Stopping || _stateStore is null)
            {
                EnsureStarted();
            }
            CloudFileSystemOperationLease? currentContext = _operationContext.Value;
            if (currentContext is not null &&
                currentContext.Owner is not null &&
                currentContext.Owner == this &&
                currentContext.Covers(requestedScopes))
            {
                stateStore = currentContext.StateStore;
                Interlocked.Increment(ref _activeOperations);
                try
                {
                    return new CloudFileSystemOperationLease(
                        this,
                        stateStore,
                        pathLease: null,
                        pathHandleLease: CloudPathHandleLease.OpenParentChains(
                            SyncRootPath,
                            requestedScopes.Select(static scope => scope.Path)),
                        scopes: []);
                }
                catch
                {
                    ReleaseOperation();
                    throw;
                }
            }

            stateStore = _stateStore ??
                throw new InvalidOperationException("The cloud file system has no open state store.");
            if (Volatile.Read(ref _activeOperations) == 0)
            {
                Volatile.Write(
                    ref _operationsDrained,
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            Interlocked.Increment(ref _activeOperations);
        }
        finally
        {
            _lifecycleGate.Release();
        }

        try
        {
            CloudItemOperationCoordinator.CloudItemOperationPathLease pathLease =
                await _operationCoordinator.AcquireAsync(requestedScopes, cancellationToken)
                    .ConfigureAwait(false);
            CloudPathHandleLease pathHandleLease;
            try
            {
                pathHandleLease = CloudPathHandleLease.OpenParentChains(
                    SyncRootPath,
                    requestedScopes.Select(static scope => scope.Path));
            }
            catch
            {
                pathLease.Dispose();
                throw;
            }
            return new CloudFileSystemOperationLease(
                this,
                stateStore,
                pathLease,
                pathHandleLease,
                requestedScopes);
        }
        catch
        {
            ReleaseOperation();
            throw;
        }
    }

    internal static async ValueTask<CloudItemSnapshot> InspectCoreAsync(
        CloudItem item,
        ICloudStateStore stateStore,
        CancellationToken cancellationToken)
    {
        LocalCloudItemInspection local = CloudItemInspector.Inspect(item.FullPath, item.Kind, item.SyncRootPath);
        await using ICloudStateTransaction transaction = await stateStore
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        CloudItemState? durableState = await transaction.Items
            .GetByRelativePathAsync(item.RelativePath, cancellationToken)
            .ConfigureAwait(false);
        if (durableState is not null && durableState.Kind != item.Kind)
        {
            throw new InvalidOperationException(
                $"Durable state identifies '{item.RelativePath}' as a " +
                $"{durableState.Kind.ToString().ToLowerInvariant()}, not a " +
                $"{item.Kind.ToString().ToLowerInvariant()}.");
        }

        return CreateSnapshot(item, local, durableState);
    }

    internal static CloudItemSnapshot CreateSnapshot(CloudItem item, LocalCloudItemInspection local, CloudItemState? durableState) =>
        new(
            item.Kind,
            local.Exists,
            local.Attributes,
            local.Length,
            local.CreationTime,
            local.LastWriteTime,
            local.LastAccessTime,
            local.PlaceholderState,
            local.ContentAvailability,
            local.PinState,
            local.SynchronizationState,
            local.LocalFileId,
            local.SyncRootFileId,
            local.OnDiskDataSize,
            local.ValidatedDataSize,
            local.ModifiedDataSize,
            local.PropertyDataSize,
            local.PlaceholderIdentity,
            durableState,
            DateTimeOffset.UtcNow,
            local.LocalBinding);

    private void EnsureStarted()
    {
        CloudFileSystemLifecycleState state = LifecycleState;
        ObjectDisposedException.ThrowIf(
            state is CloudFileSystemLifecycleState.Stopping or CloudFileSystemLifecycleState.Disposed,
            this);
        if (state is not CloudFileSystemLifecycleState.Started)
        {
            throw new InvalidOperationException("The cloud file system has not been started.");
        }
    }

    private void ReleaseOperation()
    {
        int remaining = Interlocked.Decrement(ref _activeOperations);
        if (remaining < 0)
        {
            throw new InvalidOperationException("Cloud file-system operation accounting underflowed.");
        }

        if (remaining == 0)
        {
            Volatile.Read(ref _operationsDrained)?.TrySetResult();
        }
    }

    private async ValueTask<IReadOnlyList<Exception>> DisposeOwnedResourcesAsync()
    {
        List<Exception>? failures = null;
        CloudLocalChangeFeed? localChangeFeed;
        lock (_localChangeFeedGate)
        {
            localChangeFeed = _localChangeFeed;
            _localChangeFeed = null;
        }

        if (localChangeFeed is not null)
        {
            try
            {
                await localChangeFeed.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
                lock (_localChangeFeedGate)
                {
                    _localChangeFeed ??= localChangeFeed;
                }
                // Keep the store available for both deferred worker completion and a durable
                // loss-marker retry, even if the worker completed concurrently with the failure.
                return failures;
            }
        }

        // The stopped producer cannot admit more feed operations. Drain remaining facade
        // leases before disconnecting callbacks or disposing the store they still reference.
        Task operationsDrained = Volatile.Read(ref _activeOperations) == 0
            ? Task.CompletedTask
            : Volatile.Read(ref _operationsDrained)?.Task ?? Task.CompletedTask;
        await operationsDrained.ConfigureAwait(false);

        if (_runtimeSession is not null)
        {
            bool disposed = false;
            try
            {
                await _runtimeSession.DisposeAsync().ConfigureAwait(false);
                disposed = true;
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
                if (_runtimeSession is ICloudFileSystemRuntimeSessionDrain drain &&
                    !drain.DisposeCompletion.IsCompleted)
                {
                    // A provider handler may still be using the state store after the native
                    // connection has disconnected. Keep both resources owned until its deferred
                    // completion releases callback state.
                    return failures;
                }
            }

            if (_runtimeSession is ICloudFileSystemRuntimeSessionDrain completedDrain &&
                !completedDrain.DisposeCompletion.IsCompleted)
            {
                (failures ??= []).Add(new TimeoutException(
                    "The provider runtime is still draining callback work."));
                return failures;
            }

            if (disposed)
            {
                _runtimeSession = null;
            }
        }

        if (_stateStore is not null)
        {
            try
            {
                await _stateStore.DisposeAsync().ConfigureAwait(false);
                _stateStore = null;
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        return failures ?? [];
    }

    internal sealed class CloudFileSystemOperationLease : IDisposable
    {
        private CloudFileSystem? _owner;
        private CloudItemOperationCoordinator.CloudItemOperationPathLease? _pathLease;
        private CloudPathHandleLease? _pathHandleLease;
        private readonly IReadOnlyList<CloudItemOperationScope> _scopes;
        private CloudFileSystemOperationLease? _previousContext;
        private int _establishesContext;

        internal CloudFileSystemOperationLease(
            CloudFileSystem owner,
            ICloudStateStore stateStore,
            CloudItemOperationCoordinator.CloudItemOperationPathLease? pathLease,
            CloudPathHandleLease? pathHandleLease,
            IReadOnlyList<CloudItemOperationScope> scopes)
        {
            _owner = owner;
            StateStore = stateStore;
            _pathLease = pathLease;
            _pathHandleLease = pathHandleLease;
            _scopes = scopes;
        }

        internal CloudFileSystem? Owner => _owner;

        internal ICloudStateStore StateStore { get; }

        internal bool Covers(IReadOnlyList<CloudItemOperationScope> scopes) =>
            _pathLease is not null && scopes.All(scope =>
                _scopes.Any(outer => outer.Contains(scope)));

        /// <summary>
        /// Publishes this lease in the caller's execution context for nested operations.
        /// </summary>
        /// <remarks>
        /// The assignment intentionally happens after the asynchronous acquisition has returned.
        /// Assigning an <see cref="AsyncLocal{T}"/> from inside the acquisition method would write
        /// to that method's copied execution context after a suspension and would not be visible to
        /// the caller. This method is synchronous so the caller owns the context being updated.
        /// </remarks>
        internal void EstablishContext()
        {
            CloudFileSystem owner = _owner ??
                throw new ObjectDisposedException(nameof(CloudFileSystemOperationLease));
            if (Interlocked.CompareExchange(ref _establishesContext, 1, 0) == 0)
            {
                _previousContext = owner._operationContext.Value;
                owner._operationContext.Value = this;
            }
        }

        public void Dispose()
        {
            CloudFileSystem? owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
            {
                return;
            }

            if (Volatile.Read(ref _establishesContext) != 0)
            {
                owner._operationContext.Value = _previousContext;
            }

            Interlocked.Exchange(ref _pathLease, null)?.Dispose();
            Interlocked.Exchange(ref _pathHandleLease, null)?.Dispose();
            owner.ReleaseOperation();
        }
    }

    /// <summary>Builds immutable lifecycle configuration for one cloud file system.</summary>
    /// <remarks>
    /// A builder is mutable and not thread-safe. <see cref="Build"/> may be called repeatedly;
    /// each result has independent process ownership but uses the same configured dependencies.
    /// A state-store implementation may reject concurrently started instances.
    /// </remarks>
    public sealed class Builder
    {
        private readonly string _syncRootPath;
        private readonly ICloudFileSystemRuntime _runtime;
        private ICloudStateStoreFactory? _stateStoreFactory;
        private SyncRootRegistrationOptions? _registration;
        private ICloudFileContentProvider? _contentProvider;

        internal Builder(string syncRootPath, ICloudFileSystemRuntime runtime)
        {
            ArgumentNullException.ThrowIfNull(runtime);
            _syncRootPath = NormalizePath(syncRootPath);
            _runtime = runtime;
        }

        /// <summary>Sets the required factory for durable synchronization coordination state.</summary>
        /// <param name="stateStoreFactory">
        /// Factory opened during <see cref="StartAsync"/> after local validation succeeds.
        /// </param>
        /// <returns>This builder.</returns>
        public Builder WithStateStore(ICloudStateStoreFactory stateStoreFactory)
        {
            ArgumentNullException.ThrowIfNull(stateStoreFactory);
            _stateStoreFactory = stateStoreFactory;
            return this;
        }

        /// <summary>Configures persistent registration to apply during startup.</summary>
        /// <param name="registration">
        /// Immutable registration and policy values. Without this option, startup requires the
        /// local path to be registered already.
        /// </param>
        /// <returns>This builder.</returns>
        public Builder WithRegistration(SyncRootRegistrationOptions registration)
        {
            ArgumentNullException.ThrowIfNull(registration);
            _registration = registration;
            return this;
        }

        /// <summary>Configures the optional provider used for Windows hydration requests.</summary>
        /// <param name="contentProvider">
        /// Thread-safe provider whose callback lifetime is owned by the started file system.
        /// </param>
        /// <returns>This builder.</returns>
        public Builder WithContentProvider(ICloudFileContentProvider contentProvider)
        {
            ArgumentNullException.ThrowIfNull(contentProvider);
            _contentProvider = contentProvider;
            return this;
        }

        /// <summary>Validates configuration and creates an inactive file-system facade.</summary>
        /// <returns>
        /// A resource-free instance in the <see cref="CloudFileSystemLifecycleState.Created"/>
        /// state. Call <see cref="StartAsync"/> to acquire process resources.
        /// </returns>
        /// <exception cref="DirectoryNotFoundException">The local root does not exist.</exception>
        /// <exception cref="InvalidOperationException">No state-store factory was configured.</exception>
        public CloudFileSystem Build()
        {
            if (!Directory.Exists(_syncRootPath))
            {
                throw new DirectoryNotFoundException(
                    $"The sync-root directory does not exist: '{_syncRootPath}'.");
            }

            if (_stateStoreFactory is null)
            {
                throw new InvalidOperationException(
                    "A cloud state-store factory must be configured before building.");
            }

            return new CloudFileSystem(
                _syncRootPath,
                _stateStoreFactory,
                _registration,
                _contentProvider,
                _runtime);
        }

        private static string NormalizePath(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (!Path.IsPathFullyQualified(path))
            {
                throw new ArgumentException(
                    "The sync-root path must be fully qualified.",
                    nameof(path));
            }

            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
    }
}
