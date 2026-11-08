using System.ComponentModel;

using CfSharp.Native;

using Microsoft.Win32.SafeHandles;

namespace CfSharp;

public sealed partial class CloudLocalChangeFeed
{
    internal const string SecurityValidationPrefix = "cfsharp/security-validation/v1/";

    private static string SecurityValidationName(Guid operationId) => SecurityValidationPrefix + operationId.ToString("N");

    private async ValueTask PublishValidatedSecurityObservationAsync(CloudOperationJournalEntry entry, CancellationToken cancellationToken)
    {
        await using ICloudStateTransaction transaction = await _stateStore.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        string receipt = SecurityValidationName(entry.OperationId);
        if (await transaction.Checkpoints.GetAsync(receipt, cancellationToken).ConfigureAwait(false) is not null)
        {
            await transaction.Operations.EnqueueAsync(entry, cancellationToken).ConfigureAwait(false);
            await transaction.Checkpoints.RemoveAsync(receipt, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            SignalAvailable();
        }
    }

    private static async ValueTask RetainUnboundSecurityObservationAsync(ICloudStateTransaction transaction, Guid operationId,
        ReadOnlyMemory<byte> payload, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        if (await transaction.Operations.GetAsync(operationId, cancellationToken).ConfigureAwait(false) is null)
        {
            await transaction.Operations.EnqueueAsync(new(operationId, CloudStateOperationKind.MetadataUpdate, null,
                payload.Span, observedAt), cancellationToken).ConfigureAwait(false);
        }

        await transaction.Checkpoints.RemoveAsync(SecurityValidationName(operationId), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> RecoverStagedSecurityObservationsAsync(ICloudStateTransaction transaction,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CloudStateCheckpoint> receipts = await transaction.Checkpoints.ListAsync(SecurityValidationPrefix,
            cancellationToken).ConfigureAwait(false);
        foreach (CloudStateCheckpoint receipt in receipts)
        {
            if (!Guid.TryParseExact(receipt.Name.AsSpan(SecurityValidationPrefix.Length), "N", out Guid operationId) || operationId == Guid.Empty)
            {
                throw new InvalidDataException("A staged security observation has an invalid operation identifier.");
            }

            LocalChangePayload payload = LocalChangePayload.Decode(receipt.Value);
            await RetainUnboundSecurityObservationAsync(transaction, operationId, receipt.Value, payload.ObservedAt,
                cancellationToken).ConfigureAwait(false);
            await transaction.Checkpoints.UpsertAsync(new(NamespaceObservationsPrefix + "/" + operationId.ToString("N"),
                receipt.Value.Span, payload.ObservedAt), cancellationToken).ConfigureAwait(false);
        }

        return receipts.Count != 0;
    }

    private sealed record StagedSecurityObservation(CloudOperationJournalEntry Entry, SecurityFeedProjection Projection) : IDisposable
    {
        public void Dispose() => Projection.Dispose();
    }

    private async ValueTask<IDisposable?> AcquireSecurityOperationAsync(CloudItemPath path,
        LocalChangeSourceEvent observation, CancellationToken cancellationToken)
    {
        bool isDirectory = observation.SecurityIdentity?.IsDirectory ?? false;
        bool contained;
        try
        {
            contained = CloudItemPathResolver.IsPhysicallyContained(_syncRootPath, path);
        }
        catch (Exception exception) when (exception is CloudPathReparsePointException or IOException or UnauthorizedAccessException)
        {
            throw new CloudNamespaceObservationUncertainException(isDirectory, exception);
        }

        if (!contained)
        {
            // Keep the lexically valid historical path without acquiring a lease on the target.
            throw new CloudNamespaceObservationUncertainException(isDirectory);
        }

        if (_acquireOperation is null) { return null; }
        try
        {
            return await _acquireOperation([CloudItemOperationScope.Exact(path.FullPath)], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is CloudPathReparsePointException or IOException { InnerException: Win32Exception })
        {
            // A replacement or sharing conflict can prevent the facade from guarding an
            // ancestor before any transaction begins. Keep the observation for reconciliation.
            throw new CloudNamespaceObservationUncertainException(isDirectory, exception);
        }
    }

    private SecurityFeedProjection? TryBindSecurityObservation(CloudItemPath path, CloudItemState? state,
        LocalSecurityChangeIdentity? identity)
    {
        // Official creation/conversion rows can omit LocalFileId. The kernel/native ID match
        // remains mandatory; the placeholder blob binds that object to the official identity.
        // A durable file ID, when available, is an additional constraint rather than a prerequisite.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299) || state is null || state.IsTombstone || identity is null ||
            identity.Value.FileId == 0 || identity.Value.VolumeSerialNumber == 0 || identity.Value.RootFileId == Guid.Empty ||
            (state.LocalFileId is long durableFileId && unchecked((ulong)durableFileId) != identity.Value.FileId) ||
            (state.Kind == CloudItemKind.Directory) != identity.Value.IsDirectory)
        { return null; }

        CloudPathHandleLease? parents = null;
        SafeFileHandle? guard = null;
        try
        {
            parents = CloudPathHandleLease.OpenParentChains(_syncRootPath, [path.FullPath]);
            guard = WindowsFileMetadata.OpenNamespaceGuard(path.FullPath);
            LocalCloudItemInspection observed = _securityInspection?.Invoke(guard, path.FullPath, state.Kind, _syncRootPath)
                ?? CloudItemInspector.Inspect(guard, path.FullPath, state.Kind, _syncRootPath);
            if (observed.LocalFileId is not long nativeFileId || unchecked((ulong)nativeFileId) != identity.Value.FileId ||
                observed.LocalBinding?.VolumeSerialNumber != identity.Value.VolumeSerialNumber ||
                observed.LocalBinding?.SyncRootFileId != identity.Value.RootFileId ||
                !CloudPlaceholderIdentity.TryDecode(observed.PlaceholderIdentity, out CloudPlaceholderIdentity? placeholder) ||
                placeholder.ItemId != state.ItemId || !string.Equals(placeholder.RemoteId, state.RemoteId, StringComparison.Ordinal))
            { return null; }
            SecurityFeedProjection result = new(_syncRootPath, path, state.Kind, observed, guard, parents, _securityInspection);
            guard = null;
            parents = null;
            return result;
        }
        catch (Exception exception) when (exception is CloudPathReparsePointException or CloudFilesException or Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Missing/replaced objects, denied metadata access and unavailable bindings are
            // observations to reconcile, not permission to create an item from a stale path.
            // CfGetPlaceholderInfo can fail after attributes were read if the object reverted.
            return null;
        }
        finally
        {
            guard?.Dispose();
            parents?.Dispose();
        }
    }

    private sealed class SecurityFeedProjection(string root, CloudItemPath path, CloudItemKind kind,
        LocalCloudItemInspection expected, SafeFileHandle guard, IDisposable parents, CloudSecurityInspection? inspection) : IDisposable
    {
        internal void Validate()
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299)) { throw new PlatformNotSupportedException(); }
            try
            {
                LocalCloudItemInspection current = inspection?.Invoke(guard, path.FullPath, kind, root)
                    ?? CloudItemInspector.Inspect(guard, path.FullPath, kind, root);
                if (current.LocalBinding != expected.LocalBinding || current.LocalFileId != expected.LocalFileId ||
                    !current.PlaceholderIdentity.AsSpan().SequenceEqual(expected.PlaceholderIdentity))
                { throw new CloudNamespaceObservationUncertainException(kind == CloudItemKind.Directory); }
            }
            catch (Exception exception) when (exception is CloudPathReparsePointException or CloudFilesException or Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
            { throw new CloudNamespaceObservationUncertainException(kind == CloudItemKind.Directory, exception); }
        }

        public void Dispose()
        {
            guard.Dispose();
            parents.Dispose();
        }
    }
}

internal delegate LocalCloudItemInspection CloudSecurityInspection(SafeFileHandle handle, string path, CloudItemKind kind, string root);
