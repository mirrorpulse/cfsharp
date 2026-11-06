namespace CfSharp;

internal interface ILocalChangeSource : IAsyncDisposable
{
    Task StartAsync(
        Func<LocalChangeSourceEvent, ValueTask> eventHandler,
        CancellationToken cancellationToken);
}

/// <summary>
/// Adapts the Windows <c>ReadDirectoryChangesW</c>-backed <see cref="FileSystemWatcher"/> to the
/// immutable local-change source contract.
/// </summary>
internal sealed class WindowsLocalChangeSource : ILocalChangeSource
{
    private readonly string _rootPath;
    private readonly int _bufferCapacity;
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _securityWatcher;
    private Func<LocalChangeSourceEvent, ValueTask>? _eventHandler;
    private int _disposed;

    internal WindowsLocalChangeSource(string rootPath, int bufferCapacity)
    {
        _rootPath = rootPath;
        _bufferCapacity = Math.Clamp(bufferCapacity, 4096, 64 * 1024);
    }

    public Task StartAsync(
        Func<LocalChangeSourceEvent, ValueTask> eventHandler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventHandler);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_watcher is not null)
            {
                throw new InvalidOperationException("The local-change source has already started.");
            }

            FileSystemWatcher watcher = new(_rootPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName |
                    NotifyFilters.DirectoryName |
                    NotifyFilters.Attributes |
                    NotifyFilters.Size |
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
            // FILE_ACTION_MODIFIED does not carry the triggering filter. Keep security on a
            // separate native subscription so a DACL write cannot masquerade as file data.
            // Never suppress an event by path, timestamp or protected-operation completion:
            // the data watcher must still deliver any simultaneous or later ordinary write.
            FileSystemWatcher security = new(_rootPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.Security,
                InternalBufferSize = _bufferCapacity,
                Filter = "*",
            };
            security.Changed += OnSecurityChanged;
            security.Error += OnError;
            _eventHandler = eventHandler;
            _watcher = watcher;
            _securityWatcher = security;
            try
            {
                watcher.EnableRaisingEvents = true;
                security.EnableRaisingEvents = true;
            }
            catch
            {
                _watcher = null;
                _securityWatcher = null;
                _eventHandler = null;
                security.Dispose();
                watcher.Dispose();
                throw;
            }
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        FileSystemWatcher? watcher;
        FileSystemWatcher? security;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
            security = _securityWatcher;
            _securityWatcher = null;
            _eventHandler = null;
        }

        try
        {
            security?.Dispose();
        }
        finally
        {
            watcher?.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private void OnCreated(object sender, FileSystemEventArgs args) =>
        Publish(new(LocalChangeSourceAction.Created, GetRelativePath(args.FullPath)));

    private void OnChanged(object sender, FileSystemEventArgs args) =>
        Publish(new(LocalChangeSourceAction.Modified, GetRelativePath(args.FullPath)));

    private void OnSecurityChanged(object sender, FileSystemEventArgs args) =>
        Publish(new(LocalChangeSourceAction.SecurityModified, GetRelativePath(args.FullPath)));

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

    private void Publish(LocalChangeSourceEvent sourceEvent)
    {
        lock (_gate)
        {
            PublishCore(sourceEvent);
        }
    }

    private void PublishCore(LocalChangeSourceEvent sourceEvent)
    {
        if (Volatile.Read(ref _disposed) != 0 || _eventHandler is null)
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
