using System.Buffers.Binary;
using System.ComponentModel;

using CfSharp.Native;

using Microsoft.Win32.SafeHandles;

namespace CfSharp;

public sealed partial class CloudLocalChangeFeed
{
    internal const string SecurityValidationPrefix = "cfsharp/security-validation/v1/";
    private long _validatedAttributeSequence;

    private static string SecurityValidationName(Guid operationId) => SecurityValidationPrefix + operationId.ToString("N");

    private async ValueTask PublishValidatedSecurityObservationAsync(CloudOperationJournalEntry entry,
        SecurityFeedProjection projection, CancellationToken cancellationToken)
    {
        string receipt = SecurityValidationName(entry.OperationId);
        CloudOperationJournalEntry? published = null;
        await using (ICloudStateTransaction transaction = await _stateStore.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await transaction.Checkpoints.GetAsync(receipt, cancellationToken).ConfigureAwait(false) is not null)
            {
                if (projection.AllowsRename) { projection.Validate(); }
                published = await transaction.Operations.EnqueueAsync(entry, cancellationToken).ConfigureAwait(false);
                // A snapshot can change during CommitAsync. Keep its receipt atomically with
                // the journal row: readers and ACK must withhold it until post-commit validation.
                if (!projection.AllowsRename) { await transaction.Checkpoints.RemoveAsync(receipt, cancellationToken).ConfigureAwait(false); }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        if (published is null) { return; }
        if (projection.AllowsRename)
        {
            projection.Validate();
            // The worker serializes publication, so one monotonic watermark covers all
            // successfully validated rows without retaining an unbounded per-operation cache.
            Volatile.Write(ref _validatedAttributeSequence, published.Sequence);
            SignalAvailable();
            // Eligibility is established above, without another publication commit window.
            // A crash before this bookkeeping completes leaves the receipt for safe recovery.
            await using ICloudStateTransaction completed = await _stateStore.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await completed.Checkpoints.RemoveAsync(receipt, cancellationToken).ConfigureAwait(false);
            await completed.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        else { SignalAvailable(); }
    }

    private async ValueTask<int> ValidatedMetadataPrefixLengthAsync(ICloudStateTransaction transaction,
        IReadOnlyList<CloudOperationJournalEntry> operations, CancellationToken cancellationToken)
    {
        if (operations.Count == 0) { return 0; }
        IReadOnlyList<CloudStateCheckpoint> receipts = await transaction.Checkpoints.ListAsync(SecurityValidationPrefix,
            cancellationToken).ConfigureAwait(false);
        if (receipts.Count == 0) { return operations.Count; }
        HashSet<string> pending = new(receipts.Select(receipt => receipt.Name), StringComparer.Ordinal);
        long validated = Volatile.Read(ref _validatedAttributeSequence);
        for (int index = 0; index < operations.Count; index++)
        {
            CloudOperationJournalEntry operation = operations[index];
            if (operation.Sequence > validated && pending.Contains(SecurityValidationName(operation.OperationId))) { return index; }
        }
        return operations.Count;
    }

    private static async ValueTask RetainUnboundSecurityObservationAsync(ICloudStateTransaction transaction, Guid operationId,
        ReadOnlyMemory<byte> payload, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        CloudOperationJournalEntry? existing = await transaction.Operations.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await transaction.Operations.EnqueueAsync(new(operationId, CloudStateOperationKind.MetadataUpdate, null,
                payload.Span, observedAt), cancellationToken).ConfigureAwait(false);
        }
        else if (existing.ItemId is not null)
        {
            if (existing.Kind != CloudStateOperationKind.MetadataUpdate) { throw new InvalidDataException("A metadata validation receipt references a different operation kind."); }
            // The row may already have committed while its validation receipt still withheld
            // delivery. Clear only its association, preserving ordering and retry metadata.
            await transaction.Operations.UpdateAsync(new(existing.OperationId, existing.Kind, null,
                existing.Payload.Span, existing.CreatedAt, existing.AttemptCount, existing.RetryAfter, existing.Sequence),
                cancellationToken).ConfigureAwait(false);
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

        Func<IReadOnlyList<CloudItemOperationScope>, CancellationToken, ValueTask<IDisposable>>? acquire =
            observation.Action == LocalChangeSourceAction.AttributesModified ? _acquireAttributeOperation : _acquireOperation;
        if (acquire is null) { return null; }
        try
        {
            return await acquire([CloudItemOperationScope.Exact(path.FullPath)], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is CloudPathReparsePointException or IOException { InnerException: Win32Exception })
        {
            // A replacement or sharing conflict can prevent the facade from guarding an
            // ancestor before any transaction begins. Keep the observation for reconciliation.
            throw new CloudNamespaceObservationUncertainException(isDirectory, exception);
        }
    }

    private SecurityFeedProjection? TryBindSecurityObservation(CloudItemPath path, CloudItemState? state,
        LocalSecurityChangeIdentity? identity, bool attributeObservation = false)
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
            if (!attributeObservation) { parents = CloudPathHandleLease.OpenParentChains(_syncRootPath, [path.FullPath]); }
            guard = attributeObservation ? WindowsFileMetadata.OpenMetadataReference(path.FullPath) : WindowsFileMetadata.OpenNamespaceGuard(path.FullPath);
            if (attributeObservation && !MatchesAttributePath(guard, path)) { return null; }
            LocalCloudItemInspection observed = _securityInspection?.Invoke(guard, path.FullPath, state.Kind, _syncRootPath)
                ?? CloudItemInspector.Inspect(guard, path.FullPath, state.Kind, _syncRootPath);
            long? fileId = observed.LocalFileId ?? NtfsFileId(observed.LocalBinding);
            bool placeholderMatches = CloudPlaceholderIdentity.TryDecode(observed.PlaceholderIdentity, out CloudPlaceholderIdentity? placeholder) &&
                placeholder.ItemId == state.ItemId && string.Equals(placeholder.RemoteId, state.RemoteId, StringComparison.Ordinal);
            bool ordinaryMatches = attributeObservation && observed.PlaceholderIdentity.Length == 0 &&
                !observed.PlaceholderState.HasFlag(CloudPlaceholderState.Placeholder) && state.LocalFileId == fileId;
            if (fileId is not long nativeFileId || unchecked((ulong)nativeFileId) != identity.Value.FileId ||
                observed.LocalBinding?.VolumeSerialNumber != identity.Value.VolumeSerialNumber ||
                observed.LocalBinding?.SyncRootFileId != identity.Value.RootFileId ||
                (!placeholderMatches && !ordinaryMatches))
            { return null; }
            // Even an all-sharing descendant reference can interfere with a parent rename
            // on Windows. Attribute snapshots retain identity data, not a live handle, while
            // a transaction waits; every subsequent check opens a fresh shared reference.
            if (attributeObservation) { guard.Dispose(); guard = null; }
            SecurityFeedProjection result = new(_syncRootPath, path, state.Kind, observed, guard, parents, _securityInspection, attributeObservation);
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

    private long? TryReadDiscoveredFileId(CloudItemPath path)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299)) { return null; }
        try
        {
            // Discovery is advisory too: observe a stable identity without excluding a user
            // rename. A changed path cannot establish the discovered row's native reference.
            using SafeFileHandle guard = WindowsFileMetadata.OpenMetadataReference(path.FullPath);
            if (!MatchesAttributePath(guard, path)) { return null; }
            CloudItemKind kind = (WindowsFileMetadata.ReadBasic(guard.DangerousGetHandle()).Attributes & 0x10) != 0
                ? CloudItemKind.Directory : CloudItemKind.File;
            LocalCloudItemInspection observed = CloudItemInspector.Inspect(guard, path.FullPath, kind, _syncRootPath);
            return MatchesAttributePath(guard, path) ? observed.LocalFileId ?? NtfsFileId(observed.LocalBinding) : null;
        }
        catch (Exception exception) when (exception is CloudPathReparsePointException or CloudFilesException or Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Discovery still records the ordinary notification. Without a native binding,
            // a later independent metadata record must remain unbound for reconciliation.
            return null;
        }
    }

    private static bool MatchesAttributePath(SafeFileHandle reference, CloudItemPath path)
    {
        // ReadFinalPath alone can still name a deleted object's former path. Reopen with
        // delete sharing and compare full native identities to reject a replacement there.
        // Check physical names before calling CFAPI on a path redirected by a junction.
        if (!string.Equals(WindowsFileMetadata.ReadFinalPath(reference.DangerousGetHandle()), path.FullPath, StringComparison.OrdinalIgnoreCase)) { return false; }
        using SafeFileHandle current = WindowsFileMetadata.OpenMetadataReference(path.FullPath);
        if (!string.Equals(WindowsFileMetadata.ReadFinalPath(current.DangerousGetHandle()), path.FullPath, StringComparison.OrdinalIgnoreCase)) { return false; }
        WindowsFileMetadata.FileIdentity expected = WindowsFileMetadata.ReadIdentity(reference.DangerousGetHandle());
        WindowsFileMetadata.FileIdentity actual = WindowsFileMetadata.ReadIdentity(current.DangerousGetHandle());
        return expected.VolumeSerialNumber == actual.VolumeSerialNumber && expected.FileId == actual.FileId;
    }

    private static long? NtfsFileId(CloudLocalFileBinding? binding)
    {
        if (binding is null) { return null; }
        Span<byte> id = stackalloc byte[16];
        binding.LocalFileId.TryWriteBytes(id);
        // NTFS extended notifications carry the 64-bit file reference. Use the full
        // generation-bearing value only when the native 128-bit ID fits exactly.
        return BinaryPrimitives.ReadUInt64LittleEndian(id[8..]) == 0
            ? BinaryPrimitives.ReadInt64LittleEndian(id) : null;
    }

    private sealed class SecurityFeedProjection(string root, CloudItemPath path, CloudItemKind kind,
        LocalCloudItemInspection expected, SafeFileHandle? guard, IDisposable? parents, CloudSecurityInspection? inspection,
        bool allowRename) : IDisposable
    {
        internal bool AllowsRename => allowRename;
        internal void Validate()
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299)) { throw new PlatformNotSupportedException(); }
            try
            {
                using SafeFileHandle? reference = allowRename ? WindowsFileMetadata.OpenMetadataReference(path.FullPath) : null;
                SafeFileHandle currentHandle = reference ?? guard!;
                if (allowRename && !MatchesAttributePath(currentHandle, path)) { throw new CloudNamespaceObservationUncertainException(kind == CloudItemKind.Directory); }
                LocalCloudItemInspection current = inspection?.Invoke(currentHandle, path.FullPath, kind, root)
                    ?? CloudItemInspector.Inspect(currentHandle, path.FullPath, kind, root);
                if (current.LocalBinding != expected.LocalBinding || current.LocalFileId != expected.LocalFileId ||
                    !current.PlaceholderIdentity.AsSpan().SequenceEqual(expected.PlaceholderIdentity))
                { throw new CloudNamespaceObservationUncertainException(kind == CloudItemKind.Directory); }
            }
            catch (Exception exception) when (exception is CloudPathReparsePointException or CloudFilesException or Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
            { throw new CloudNamespaceObservationUncertainException(kind == CloudItemKind.Directory, exception); }
        }

        public void Dispose()
        {
            guard?.Dispose();
            parents?.Dispose();
        }
    }
}

internal delegate LocalCloudItemInspection CloudSecurityInspection(SafeFileHandle handle, string path, CloudItemKind kind, string root);
