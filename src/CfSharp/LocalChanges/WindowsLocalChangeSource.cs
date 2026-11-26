using CfSharp.Native;

namespace CfSharp;

internal interface ILocalChangeSource : IAsyncDisposable
{
    /// <summary>Opens subscriptions, completing only after startup succeeds or fails.</summary>
    Task StartAsync(
        Func<LocalChangeSourceEvent, ValueTask> eventHandler,
        CancellationToken cancellationToken);
}

/// <summary>
/// Combines the Windows name and time watcher with identity-bearing size, attribute and security notifications
/// into the immutable local-change source contract.
/// </summary>
internal sealed class WindowsLocalChangeSource : ILocalChangeSource
{
    private readonly string _rootPath;
    private readonly int _bufferCapacity;
    private readonly Action? _beforeSecurityRearm;
    private readonly Action? _beforeTerminalSecurityDelivery;
    private readonly Action? _beforeAttributeStart;
    private readonly Action? _afterMetadataDisposal;
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private WindowsSecurityChangeSource? _securityWatcher;
    private WindowsSecurityChangeSource? _attributeWatcher;
    private WindowsSecurityChangeSource? _sizeWatcher;
    private Func<LocalChangeSourceEvent, ValueTask>? _eventHandler;
    private int _disposed;
    private Task? _disposal;

    internal WindowsLocalChangeSource(string rootPath, int bufferCapacity, Action? beforeSecurityRearm = null,
        Action? beforeTerminalSecurityDelivery = null, Action? beforeAttributeStart = null, Action? afterMetadataDisposal = null)
    {
        _rootPath = rootPath;
        _bufferCapacity = Math.Clamp(bufferCapacity, 4096, 64 * 1024);
        _beforeSecurityRearm = beforeSecurityRearm;
        _beforeTerminalSecurityDelivery = beforeTerminalSecurityDelivery;
        _beforeAttributeStart = beforeAttributeStart;
        _afterMetadataDisposal = afterMetadataDisposal;
    }

    public async Task StartAsync(
        Func<LocalChangeSourceEvent, ValueTask> eventHandler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventHandler);
        cancellationToken.ThrowIfCancellationRequested();
        bool initializing = false;
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (_watcher is not null)
                {
                    throw new InvalidOperationException("The local-change source has already started.");
                }

                initializing = true;
                FileSystemWatcher watcher = new(_rootPath)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName |
                        NotifyFilters.DirectoryName |
                        NotifyFilters.LastWrite |
                        NotifyFilters.CreationTime,
                    InternalBufferSize = _bufferCapacity,
                    Filter = "*",
                };
                watcher.Created += OnCreated;
                watcher.Changed += OnChanged;
                watcher.Deleted += OnDeleted;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnError;
                // FILE_ACTION_MODIFIED does not carry the triggering filter. Keep attributes and
                // security on separate subscriptions so neither notification masquerades as data.
                // Never suppress an event by path, timestamp or protected-operation completion:
                // the data watcher must still deliver any simultaneous or later ordinary write.
                _eventHandler = eventHandler;
                _watcher = watcher;
                watcher.EnableRaisingEvents = true;
                _securityWatcher = new(_rootPath, _bufferCapacity, OnSecurityChanged, OnSecurityError, _beforeSecurityRearm);
                _beforeAttributeStart?.Invoke();
                _attributeWatcher = new(_rootPath, _bufferCapacity, OnAttributesChanged, OnAttributeError, notifyFilter: 0x4);
                // Hydration can raise Size without changing the logical file length.
                // Keep that observation separate from last-write notices, including
                // when both arrive for a real write that races hydration.
                _sizeWatcher = new(_rootPath, _bufferCapacity, OnSizeChanged, OnSizeError, notifyFilter: 0x8);
            }
        }
        catch (Exception startupFailure)
        {
            if (initializing)
            {
                try { await DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanupFailure)
                {
                    LocalChangeStartupFailure.Preserve(startupFailure, cleanupFailure, source: true);
                }
            }
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) { return new(_disposal ??= DisposeCoreAsync()); }
    }

    private async Task DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        FileSystemWatcher? watcher;
        WindowsSecurityChangeSource? security;
        WindowsSecurityChangeSource? attributes;
        WindowsSecurityChangeSource? size;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
            security = _securityWatcher;
            attributes = _attributeWatcher;
            size = _sizeWatcher;
        }

        try
        {
            try
            {
                try
                {
                    if (size is not null) { await size.DisposeAsync().ConfigureAwait(false); }
                }
                finally
                {
                    if (attributes is not null) { await attributes.DisposeAsync().ConfigureAwait(false); }
                }
            }
            finally
            {
                if (security is not null) { await security.DisposeAsync().ConfigureAwait(false); }
                _afterMetadataDisposal?.Invoke();
            }
        }
        finally
        {
            // The pump can already have copied a batch or decided to report terminal loss.
            // Keep its callbacks wired until the pump exits, before the feed closes its input.
            lock (_gate)
            {
                _securityWatcher = null;
                _attributeWatcher = null;
                _sizeWatcher = null;
                _eventHandler = null;
            }
            watcher?.Dispose();
        }

    }

    private void OnCreated(object sender, FileSystemEventArgs args) =>
        Publish(new(LocalChangeSourceAction.Created, GetRelativePath(args.FullPath)));

    private void OnChanged(object sender, FileSystemEventArgs args) =>
        Publish(new(LocalChangeSourceAction.Modified, GetRelativePath(args.FullPath)));

    private void OnSecurityChanged(WindowsSecurityChange change)
    {
        lock (_gate)
        {
            if (_securityWatcher is null) { return; }
            PublishCore(new(LocalChangeSourceAction.SecurityModified, change.RelativePath, SecurityIdentity:
                new(_securityWatcher.VolumeSerialNumber, _securityWatcher.RootFileId, change.FileId, change.ParentFileId, change.IsDirectory)),
                fromSecurityPump: true);
        }
    }

    private void OnAttributesChanged(WindowsSecurityChange change)
    {
        lock (_gate)
        {
            if (_attributeWatcher is null) { return; }
            PublishCore(new(LocalChangeSourceAction.AttributesModified, change.RelativePath, SecurityIdentity:
                new(_attributeWatcher.VolumeSerialNumber, _attributeWatcher.RootFileId, change.FileId, change.ParentFileId, change.IsDirectory)),
                fromSecurityPump: true);
        }
    }

    private void OnAttributeError(int error, bool terminal)
    {
        lock (_gate)
        {
            if (_attributeWatcher is not null)
            {
                // Loss of a metadata subscription invalidates the feed until rebuilt.
                PublishCore(new(terminal ? LocalChangeSourceAction.AttributeSubscriptionFailed :
                    error == 1022 ? LocalChangeSourceAction.Overflow : LocalChangeSourceAction.Error, string.Empty, error),
                    fromSecurityPump: true);
            }
        }
    }

    private void OnSizeChanged(WindowsSecurityChange change)
    {
        lock (_gate)
        {
            if (_sizeWatcher is null) { return; }
            LocalSecurityChangeIdentity identity = new(_sizeWatcher.VolumeSerialNumber, _sizeWatcher.RootFileId,
                change.FileId, change.ParentFileId, change.IsDirectory);
            CfPlaceholderState state = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299)
                ? CfApi.CfGetPlaceholderStateFromAttributeTag(change.FileAttributes, change.ReparsePointTag)
                : CfPlaceholderState.Invalid;
            PublishCore(new(LocalChangeSourceAction.SizeModified, change.RelativePath,
                SizeChange: new(identity, change.FileSize, state == CfPlaceholderState.Invalid
                    ? CloudPlaceholderState.None : (CloudPlaceholderState)state, change.LastWriteTime, change.ChangeTime)), fromSecurityPump: true);
        }
    }

    private void OnSizeError(int error, bool terminal)
    {
        lock (_gate)
        {
            if (_sizeWatcher is null) { return; }
            PublishCore(new(terminal ? LocalChangeSourceAction.SizeSubscriptionFailed :
                error == 1022 ? LocalChangeSourceAction.Overflow : LocalChangeSourceAction.Error, string.Empty, error),
                fromSecurityPump: true);
        }
    }

    private void OnDeleted(object sender, FileSystemEventArgs args) =>
        Publish(new(LocalChangeSourceAction.Deleted, GetRelativePath(args.FullPath)));

    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        // FileSystemWatcher receives the same paired old/new records produced by
        // ReadDirectoryChangesW. Publish them under one lock so the feed cannot interleave a
        // third path between the two halves of a rename.
        lock (_gate)
        {
            PublishCore(new(
                LocalChangeSourceAction.RenamedOldName,
                GetRelativePath(args.OldFullPath)));
            PublishCore(new(
                LocalChangeSourceAction.RenamedNewName,
                GetRelativePath(args.FullPath)));
        }
    }

    private void OnError(object sender, ErrorEventArgs args)
    {
        LocalChangeSourceAction action = args.GetException() is InternalBufferOverflowException
            ? LocalChangeSourceAction.Overflow
            : LocalChangeSourceAction.Error;
        Publish(new(action, string.Empty));
    }

    internal void OnSecurityError(int error, bool terminal)
    {
        if (terminal) { _beforeTerminalSecurityDelivery?.Invoke(); }
        LocalChangeSourceAction action = terminal ? LocalChangeSourceAction.SecuritySubscriptionFailed
            : error == 1022 ? LocalChangeSourceAction.Overflow : LocalChangeSourceAction.Error;
        lock (_gate)
        {
            if (_securityWatcher is not null)
            {
                PublishCore(new(action, string.Empty, error), fromSecurityPump: true);
            }
        }
    }

    private void Publish(LocalChangeSourceEvent sourceEvent)
    {
        lock (_gate)
        {
            PublishCore(sourceEvent);
        }
    }

    private void PublishCore(LocalChangeSourceEvent sourceEvent, bool fromSecurityPump = false)
    {
        if (_eventHandler is null || (!fromSecurityPump && Volatile.Read(ref _disposed) != 0))
        {
            return;
        }

        try
        {
            ValueTask delivery = _eventHandler(sourceEvent);
            if (!delivery.IsCompletedSuccessfully)
            {
                _ = ObserveDeliveryAsync(delivery);
            }
        }
        catch
        {
            // The feed owns the bounded channel and turns write failures into its terminal state;
            // an event callback must never escape into FileSystemWatcher.
        }
    }

    private static async Task ObserveDeliveryAsync(ValueTask delivery)
    {
        try
        {
            await delivery.ConfigureAwait(false);
        }
        catch
        {
            // The feed records processing failures independently; never leak an exception into
            // FileSystemWatcher callback dispatch.
        }
    }

    private string GetRelativePath(string fullPath)
    {
        string relativePath = Path.GetRelativePath(_rootPath, fullPath);
        return relativePath == "." ? string.Empty : relativePath;
    }
}
