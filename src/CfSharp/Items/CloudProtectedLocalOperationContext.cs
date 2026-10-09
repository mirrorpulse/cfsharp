using System.Runtime.Versioning;
using CfSharp.Native;
using Microsoft.Win32.SafeHandles;

namespace CfSharp;

/// <summary>Provides coordinated same-object operations during one protected callback.</summary>
/// <remarks>
/// Owned by the library. It exposes no native handle and must not be disposed by the caller.
/// Scope methods may run concurrently; native and store phases are serialized on this object.
/// Admitted calls are drained on callback exit, including calls the application did not await.
/// New calls after exit throw ObjectDisposedException. Retained snapshots and descriptors are
/// independent copies, but do not retain protection. Use no network or unbounded work in the scope.
/// Windows 10 version 1709 or later is required. No method reads source content or hydrates data.
/// </remarks>
[SupportedOSPlatform("windows10.0.16299")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The library drains and closes this callback-owned scope; public disposal would permit premature native release.")]
public sealed partial class CloudProtectedLocalOperationContext
{
    private readonly object _admission = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CloudItem _item;
    private readonly CloudFileSystem _owner;
    private readonly ICloudStateStore _store;
    private readonly SafeFileHandle _handle;
    private readonly CloudProtectedLocalOperationRequest _request;
    private readonly CancellationToken _stop;
    private bool _accepting = true;
    private int _active;
    private int _stage;
    private Exception? _failure;
    private CloudProtectedLocalOperationStage? _failureStage;

    internal CloudProtectedLocalOperationContext(CloudFileSystem owner, CloudItem item, ICloudStateStore store,
        SafeFileHandle handle, CloudProtectedLocalOperationRequest request, CancellationToken stop)
    {
        _item = item;
        _owner = owner;
        _store = store;
        _handle = handle;
        _request = request;
        _stop = stop;
    }

    internal CloudItemSnapshot? LastSnapshot { get; private set; }
    internal Exception? Failure => _failure;
    internal CloudProtectedLocalOperationStage? FailureStage => _failureStage;
    internal CloudProtectedLocalOperationStage Stage => (CloudProtectedLocalOperationStage)Volatile.Read(ref _stage);
    internal void SetStage(CloudProtectedLocalOperationStage stage) => Volatile.Write(ref _stage, (int)stage);

    /// <summary>Inspects fresh metadata and official item state through the original protected file object.</summary>
    /// <param name="cancellationToken">Token observed before admitting and performing this scope step.</param>
    /// <returns>A handle-free snapshot owning its identity bytes; content is not read.</returns>
    /// <exception cref="ObjectDisposedException">The callback has exited and scope admission is closed.</exception>
    /// <exception cref="OperationCanceledException">Scope or step cancellation was requested.</exception>
    /// <exception cref="IOException">The protected object cannot be verified or queried.</exception>
    public ValueTask<CloudItemSnapshot> InspectAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(InspectCoreAsync, cancellationToken);

    internal ValueTask<CloudItemSnapshot> InspectItemAsync(CloudItem item, CancellationToken token)
    {
        if (!item.IsOwnedBy(_owner) || item.Kind != _item.Kind ||
            !string.Equals(item.FullPath, _item.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A protected callback can inspect only its original object.");
        }
        return InspectAsync(token);
    }

    internal NativeFileMetadata ValidateObject()
    {
        NativeFileMetadata facts = WindowsFileMetadata.Read(_handle.DangerousGetHandle());
        using SafeFileHandle root = WindowsFileMetadata.Open(_item.SyncRootPath);
        int actualResult = NativeSyncRoot.Query(_handle.DangerousGetHandle(), out NativeSyncRootInfo? actualRoot);
        int ownedResult = NativeSyncRoot.Query(root.DangerousGetHandle(), out NativeSyncRootInfo? ownedRoot);
        if (actualResult < 0 || ownedResult < 0)
        {
            throw new NativeFileException("CfGetSyncRootInfoByHandle", actualResult < 0 ? actualResult : ownedResult);
        }
        if (actualRoot is null || ownedRoot is null || actualRoot.FileId != ownedRoot.FileId)
        {
            throw new CloudProtectedLocalRejectedException(CloudProtectedLocalOperationOutcome.LocalObjectMismatch,
                "The native object belongs to a different Cloud Files root.");
        }
        CloudLocalFileBinding observed = CloudLocalFileBindingPlatform.Read(_handle.DangerousGetHandle(), _item.SyncRootPath);
        if (observed != _request.ExpectedBinding || !string.Equals(
            WindowsFileMetadata.ReadFinalPath(_handle.DangerousGetHandle()), _item.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new CloudProtectedLocalRejectedException(CloudProtectedLocalOperationOutcome.LocalObjectMismatch,
                "The original native object or its namespace no longer matches the request.");
        }
        if (facts.Directory != (_item.Kind == CloudItemKind.Directory) || facts.DeletePending ||
            !facts.Directory && facts.Links != 1 ||
            (facts.Attributes & (uint)FileAttributes.ReparsePoint) != 0 &&
            !facts.PlaceholderState.HasFlag(CfPlaceholderState.Placeholder))
        {
            throw new CloudProtectedLocalRejectedException(CloudProtectedLocalOperationOutcome.NotApplicable,
                "Protection requires a live supported object, without foreign reparse data or existing file aliases.");
        }
        return facts;
    }

    private async ValueTask<CloudItemSnapshot> InspectCoreAsync(CancellationToken token)
    {
        SetStage(CloudProtectedLocalOperationStage.Inspection);
        ValidateObject();
        LocalCloudItemInspection local = CloudItemInspector.Inspect(_handle, _item.FullPath, _item.Kind, _item.SyncRootPath);
        await using ICloudStateTransaction transaction = await _store.BeginTransactionAsync(token).ConfigureAwait(false);
        CloudItemState? durable = await transaction.Items.GetByRelativePathAsync(_item.RelativePath, token).ConfigureAwait(false);
        if (durable is not null && durable.Kind != _item.Kind)
        {
            throw new InvalidDataException("The official item row has a different object kind.");
        }
        LastSnapshot = CloudFileSystem.CreateSnapshot(_item, local, durable);
        return LastSnapshot;
    }

    private async ValueTask<T> ExecuteAsync<T>(Func<CancellationToken, ValueTask<T>> work, CancellationToken token)
    {
        lock (_admission)
        {
            ObjectDisposedException.ThrowIf(!_accepting, this);
            _stop.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            _active++;
        }
        bool entered = false;
        try
        {
            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(_stop, token);
            await _serial.WaitAsync(stop.Token).ConfigureAwait(false);
            entered = true;
            stop.Token.ThrowIfCancellationRequested();
            return await work(stop.Token).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Exception translated = CloudFileSystem.TranslateProtectedLocalError(error, _item.FullPath, Stage);
            lock (_admission)
            {
                if (_failure is null)
                {
                    _failure = translated;
                    _failureStage = Stage;
                }
            }
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(translated);
            throw;
        }
        finally
        {
            if (entered)
            {
                _serial.Release();
            }
            lock (_admission)
            {
                _active--;
                if (!_accepting && _active == 0)
                {
                    _drained.TrySetResult();
                }
            }
        }
    }

    internal async ValueTask CloseAsync()
    {
        lock (_admission)
        {
            _accepting = false;
            if (_active == 0)
            {
                _drained.TrySetResult();
            }
        }
        await _drained.Task.ConfigureAwait(false);
        _serial.Dispose();
    }
}

internal sealed class CloudProtectedLocalRejectedException(CloudProtectedLocalOperationOutcome outcome, string message)
    : IOException(message)
{
    internal CloudProtectedLocalOperationOutcome Outcome { get; } = outcome;
}
