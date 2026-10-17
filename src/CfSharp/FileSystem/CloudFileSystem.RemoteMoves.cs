using System.ComponentModel;

using CfSharp.Native;

using Microsoft.Win32.SafeHandles;

namespace CfSharp;

public sealed partial class CloudFileSystem
{
    private async ValueTask<RemoteMoveIntent?> ReadRemoteMoveIntentAsync(RemoteMoveRequest request,
        CancellationToken cancellationToken)
    {
        await using ICloudStateTransaction transaction = await _stateStore!.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await ReadRemoteMoveIntentCoreAsync(transaction, request, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RemoteMoveIntent?> ReadRemoteMoveIntentCoreAsync(ICloudStateTransaction transaction,
        RemoteMoveRequest request, CancellationToken cancellationToken)
    {
        CloudStateCheckpoint? checkpoint = await transaction.Checkpoints.GetAsync(
            RemoteMoveIntent.Name(request.Batch.BatchId, request.EntryIndex), cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
        {
            return null;
        }

        RemoteMoveIntent intent = RemoteMoveIntent.Decode(checkpoint.Value);
        CloudRemoteChange change = request.Batch.Changes[request.EntryIndex];
        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Decode(intent.Object.Identity);
        if (!intent.Matches(request.Batch, request.EntryIndex, request.Options) ||
            intent.Original.RemoteId != change.RemoteId || identity.RemoteId != change.RemoteId ||
            (change.ItemId.HasValue && change.ItemId != intent.Original.ItemId) ||
            !CloudDirectoryStateProjection.SamePath(intent.Original.RelativePath, change.PreviousRelativePath!) ||
            !CloudDirectoryStateProjection.SamePath(intent.Object.RelativePath, change.PreviousRelativePath!) ||
            !CloudDirectoryStateProjection.SamePath(intent.TargetRelativePath, change.RelativePath) ||
            Path.GetFileName(intent.TargetRelativePath) != Path.GetFileName(change.RelativePath) ||
            !string.Equals(intent.RootPath, SyncRootPath, StringComparison.OrdinalIgnoreCase) ||
            intent.StoreScope != await CloudDirectoryMoveEvidence.GetScopeAsync(transaction, false, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The remote move preparation does not match this batch, policy, store, or root.");
        }

        return intent;
    }

    private async ValueTask<RemoteMoveIntent> PrepareRemoteFileMoveAsync(CloudRemoteChange change,
        CloudItemState original, CloudRemoteApplyOptions effectiveOptions, RemoteMoveRequest request,
        CancellationToken cancellationToken)
    {
        string source = ToFullPath(change.PreviousRelativePath!);
        string target = ToFullPath(change.RelativePath);
        using CloudPathHandleLease chains = CloudPathHandleLease.OpenParentChains(SyncRootPath, [source, target]);
        using RemoteMoveParentLease parentLease = HoldRemoteMoveParents(source, target);
        using SafeFileHandle guard = WindowsFileMetadata.Open(source, preventDelete: true);
        RemoteMoveObject observed = ObserveRemoteMoveObject(guard, directory: false, effectiveOptions.PreserveUnsynchronizedLocalContent,
            preserveContent: effectiveOptions.PreserveUnsynchronizedLocalContent);
        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Decode(observed.Identity);
        if (original.IsTombstone || original.Kind != CloudItemKind.File || original.RemoteId != change.RemoteId ||
            (change.ItemId.HasValue && change.ItemId != original.ItemId) || identity.ItemId != original.ItemId ||
            identity.RemoteId != original.RemoteId || !CloudDirectoryStateProjection.SamePath(observed.RelativePath, original.RelativePath))
        {
            throw new IOException("The remote move source does not match its original durable identity.");
        }

        IReadOnlyList<RemoteMoveObject> parents = parentLease.Parents;
        string destinationParent = Path.GetDirectoryName(target)!;
        using SafeFileHandle parentHandle = WindowsFileMetadata.Open(destinationParent, preventDelete: true);
        string actualTarget = Path.Combine(WindowsFileMetadata.ReadFinalPath(parentHandle.DangerousGetHandle()), Path.GetFileName(target));
        await using ICloudStateTransaction transaction = await _stateStore!.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!SameRemoteMoveItem(await transaction.Items.GetByItemIdAsync(original.ItemId, cancellationToken).ConfigureAwait(false), original))
        {
            throw new IOException("The remote move source state changed during preparation.");
        }

        foreach (CloudStateCheckpoint checkpoint in await transaction.Checkpoints.ListAsync(RemoteMoveIntent.Prefix, cancellationToken).ConfigureAwait(false))
        {
            RemoteMoveIntent pending = RemoteMoveIntent.Decode(checkpoint.Value);
            if (pending.Original.ItemId == original.ItemId)
            {
                throw new IOException("Replay the original pending remote move before preparing another move for this item.");
            }
        }

        CloudRemoteApplyOptions policy = request.Options;
        IReadOnlyList<CloudOperationJournalEntry> localOperations = await transaction.Operations.ListByItemIdAsync(
            original.ItemId, int.MaxValue, cancellationToken).ConfigureAwait(false);
        if (effectiveOptions.PreserveUnsynchronizedLocalContent && localOperations.Count != 0)
        {
            throw new IOException("Local operations changed during remote move preparation.");
        }

        Guid scope = await CloudDirectoryMoveEvidence.GetScopeAsync(transaction, true, cancellationToken).ConfigureAwait(false);
        RemoteMoveIntent intent = new(scope, SyncRootPath, request.Batch.BatchId, request.EntryIndex,
            request.Batch.Fingerprint.ToArray(), Path.GetRelativePath(SyncRootPath, actualTarget),
            policy.PreserveUnsynchronizedLocalContent, policy.SuppressLocalEcho,
            policy.EchoSuppressionLifetime.Ticks, policy.EchoSuppressionObservationCount,
            original, observed, parents, null, effectiveOptions.PreserveUnsynchronizedLocalContent,
            RemoteMoveIntent.FingerprintOperations(localOperations), null);
        await transaction.Checkpoints.UpsertAsync(new(RemoteMoveIntent.Name(intent.BatchId, intent.EntryIndex),
            intent.Encode(), DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        ValidateRemoteMoveParents(intent, source, target);
        RequireSameRemoteMoveObject(observed, ObserveRemoteMoveObject(guard, false, intent.RequireInSync, intent.RequireInSync));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return intent;
    }

    private async ValueTask<RemoteEntryOutcome> CompleteRemoteFileMoveAsync(CloudRemoteChange change,
        RemoteMoveIntent intent, CancellationToken cancellationToken)
    {
        string source = ToFullPath(intent.Object.RelativePath);
        string target = ToFullPath(intent.TargetRelativePath);
        using CloudPathHandleLease chains = CloudPathHandleLease.OpenParentChains(SyncRootPath, [source, target]);
        using RemoteMoveParentLease parentLease = HoldRemoteMoveParents(source, target);
        ValidateRemoteMoveParents(intent, source, target);
        bool requireInSync = intent.RequireInSync && intent.ProjectedAtTicks is null;
        using SafeFileHandle? sourceHandle = TryOpenRemoteMoveObject(source);
        using SafeFileHandle? targetHandle = TryOpenRemoteMoveObject(target);
        RemoteMoveObject? atSource = sourceHandle is null ? null : ObserveRemoteMoveObject(sourceHandle, false, requireInSync, intent.RequireInSync);
        RemoteMoveObject? atTarget = targetHandle is null ? null : ObserveRemoteMoveObject(targetHandle, false, requireInSync, intent.RequireInSync);
        bool moved = atTarget is not null && SameRemoteMoveObject(intent.Object, atTarget) &&
            atTarget.RelativePath == intent.TargetRelativePath;
        bool notMoved = atSource is not null && SameRemoteMoveObject(intent.Object, atSource) &&
            atSource.RelativePath == intent.Object.RelativePath;
        bool samePath = CloudDirectoryStateProjection.SamePath(intent.Object.RelativePath, intent.TargetRelativePath);
        if ((!moved && !notMoved) || (!samePath && moved && atSource is not null) ||
            (!samePath && notMoved && atTarget is not null) || (intent.ProjectedAtTicks.HasValue && !moved))
        {
            throw RemoteMoveUnresolved(intent, "native observation");
        }

        if (intent.ProjectedUsn is long usn && WindowsFileUsn.Read(target, expectedDirectory: false) != usn)
        {
            throw RemoteMoveUnresolved(intent, "post-projection native USN validation");
        }

        // Holding one transaction excludes feed/custom-store writers across native rename and
        // projection. This synchronous metadata-only operation invokes no application callback.
        // A failed/ambiguous commit leaves the preparation, or its committed projected stage,
        // for the next call to reread; never assume rollback from the thrown exception alone.
        await using ICloudStateTransaction transaction = await _stateStore!.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        string name = RemoteMoveIntent.Name(intent.BatchId, intent.EntryIndex);
        CloudStateCheckpoint? retained = await transaction.Checkpoints.GetAsync(name, cancellationToken).ConfigureAwait(false);
        if (retained is null || !retained.Value.Span.SequenceEqual(intent.Encode()))
        {
            throw RemoteMoveUnresolved(intent, "preparation authentication");
        }

        CloudItemState? current = await transaction.Items.GetByItemIdAsync(intent.Original.ItemId, cancellationToken).ConfigureAwait(false);
        CloudItemState expected = intent.ProjectedAtTicks is long ticks
            ? new(intent.Original.ItemId, change.RemoteId, change.RelativePath, CloudItemKind.File,
                change.RemoteRevision, intent.Original.LocalFileId, false, new DateTimeOffset(ticks, TimeSpan.Zero))
            : intent.Original;
        CloudItemState? occupant = await transaction.Items.GetByRelativePathAsync(change.RelativePath, cancellationToken).ConfigureAwait(false);
        if (!SameRemoteMoveItem(current, expected) || (occupant is not null && occupant.ItemId != expected.ItemId) ||
            !RemoteMoveIntent.FingerprintOperations(await transaction.Operations.ListByItemIdAsync(expected.ItemId, int.MaxValue,
                cancellationToken).ConfigureAwait(false)).AsSpan().SequenceEqual(intent.LocalOperationsFingerprint))
        {
            throw RemoteMoveUnresolved(intent, "durable projection validation");
        }

        if (!moved)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using SafeFileHandle rename = WindowsFileMetadata.OpenForRename(source);
            RequireSameRemoteMoveObject(intent.Object, ObserveRemoteMoveObject(rename, false, intent.RequireInSync, intent.RequireInSync));
            if (Path.GetRelativePath(SyncRootPath, WindowsFileMetadata.ReadFinalPath(rename.DangerousGetHandle())) != intent.Object.RelativePath)
            {
                throw RemoteMoveUnresolved(intent, "source path validation");
            }

            try
            {
                WindowsFileMetadata.Rename(rename.DangerousGetHandle(), target);
            }
            catch (Win32Exception exception)
            {
                throw CloudFilesException.FromHResult("CloudFileSystem.ApplyRemoteMove", source,
                    unchecked((int)(0x80070000u | (uint)exception.NativeErrorCode)));
            }
        }

        // Once native work starts, finish its durable projection with the admitted operation's
        // lifetime, even if cancellation or disposal was requested. Outcome publication may be
        // canceled separately; its unchanged original preparation remains available for replay.
        // Close every handle that overlapped rename before recording its final USN. NTFS can
        // append the accumulated CLOSE reason when the last such file object is released.
        sourceHandle?.Dispose();
        targetHandle?.Dispose();
        using SafeFileHandle final = WindowsFileMetadata.Open(target, preventDelete: true);
        // TrackAll can clear in-sync solely because this rename changed the name. Do not mark
        // it in sync or guess that a later dirty object is ours. Retain the observed post-rename
        // USN with projection; replay rejects any subsequent native change, even an equal-size
        // content write whose timestamps were restored. Earlier gaps still require in-sync proof.
        RemoteMoveObject completed = ObserveRemoteMoveObject(final, false, requireInSync: false, preserveContent: intent.RequireInSync);
        RequireSameRemoteMoveObject(intent.Object, completed);
        long completedUsn = WindowsFileUsn.Read(target, expectedDirectory: false);
        if (intent.ProjectedUsn.HasValue && intent.ProjectedUsn != completedUsn)
        {
            throw RemoteMoveUnresolved(intent, "final native USN validation");
        }
        if (completed.RelativePath != intent.TargetRelativePath)
        {
            throw RemoteMoveUnresolved(intent, "exact target spelling");
        }

        ValidateRemoteMoveParents(intent, source, target);

        if (intent.ProjectedAtTicks is null)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            await transaction.Items.UpsertAsync(new(expected.ItemId, change.RemoteId, change.RelativePath,
                CloudItemKind.File, change.RemoteRevision, expected.LocalFileId, false, now), CancellationToken.None).ConfigureAwait(false);
            await CloudDirectoryProvenance.RelocateRetainedPathsAsync(transaction, [expected], expected.RelativePath,
                change.RelativePath, CancellationToken.None).ConfigureAwait(false);
            if (intent.SuppressEcho)
            {
                await transaction.EchoSuppressions.UpsertAsync(new(Guid.NewGuid(), expected.ItemId,
                    CloudStateOperationKind.Move, change.RelativePath, [], now + TimeSpan.FromTicks(intent.EchoLifetimeTicks),
                    change.PreviousRelativePath, intent.EchoObservations), CancellationToken.None).ConfigureAwait(false);
            }

            await transaction.Checkpoints.UpsertAsync(new(name, (intent with { ProjectedAtTicks = now.UtcTicks, ProjectedUsn = completedUsn }).Encode(), now),
                CancellationToken.None).ConfigureAwait(false);
            RequireSameRemoteMoveObject(intent.Object, ObserveRemoteMoveObject(final, false, requireInSync: false, preserveContent: intent.RequireInSync));
            if (WindowsFileUsn.Read(target, expectedDirectory: false) != completedUsn)
            {
                throw RemoteMoveUnresolved(intent, "post-rename native USN validation");
            }
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return RemoteEntryOutcome.AppliedResult;
    }

    private string[] GetRemoteMoveParentPaths(string source, string target)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase) { SyncRootPath };
        foreach (string path in new[] { source, target })
        {
            string? parent = Path.GetDirectoryName(path);
            while (parent is not null && !string.Equals(parent, SyncRootPath, StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(parent);
                parent = Path.GetDirectoryName(parent);
            }
        }

        return paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private RemoteMoveParentLease HoldRemoteMoveParents(string source, string target)
    {
        List<SafeFileHandle> handles = [];
        try
        {
            foreach (string path in GetRemoteMoveParentPaths(source, target))
            {
                handles.Add(WindowsFileMetadata.OpenDirectoryGuard(path));
            }

            return new(handles, handles.Select(handle => ObserveRemoteMoveObject(handle, directory: true, requireInSync: false)).ToArray());
        }
        catch
        {
            foreach (SafeFileHandle handle in handles)
            {
                handle.Dispose();
            }

            throw;
        }
    }

    private void ValidateRemoteMoveParents(RemoteMoveIntent intent, string source, string target)
    {
        using RemoteMoveParentLease lease = HoldRemoteMoveParents(source, target);
        RemoteMoveObject[] parents = lease.Parents;
        if (parents.Length != intent.Parents.Count || parents.Where((parent, index) =>
            parent.RelativePath != intent.Parents[index].RelativePath || parent.Binding != intent.Parents[index].Binding ||
            parent.CreationTime != intent.Parents[index].CreationTime ||
            !parent.Identity.AsSpan().SequenceEqual(intent.Parents[index].Identity)).Any())
        {
            throw RemoteMoveUnresolved(intent, "parent provenance validation");
        }
    }

    private RemoteMoveObject ObserveRemoteMoveObject(SafeFileHandle handle, bool directory, bool requireInSync, bool preserveContent = true)
    {
        NativeFileMetadata metadata = WindowsFileMetadata.Read(handle.DangerousGetHandle());
        bool placeholder = metadata.PlaceholderState != CfPlaceholderState.Invalid &&
            metadata.PlaceholderState.HasFlag(CfPlaceholderState.Placeholder);
        if (metadata.Directory != directory || metadata.DeletePending || (!directory && (!placeholder || metadata.Links != 1)) ||
            (metadata.ReparseTag != 0 && !placeholder) || (requireInSync && !metadata.InSync) ||
            (!directory && preserveContent && metadata.ModifiedDataSize != 0))
        {
            throw new IOException("Remote move recovery requires the unchanged original object and safe ancestry.");
        }

        string path = WindowsFileMetadata.ReadFinalPath(handle.DangerousGetHandle());
        if (!string.Equals(path, SyncRootPath, StringComparison.OrdinalIgnoreCase) && !IsStrictDescendant(path, SyncRootPath))
        {
            throw new IOException("The observed remote move object is outside its original root.");
        }

        WindowsFileMetadata.FileBasicInfo basic = WindowsFileMetadata.ReadBasic(handle.DangerousGetHandle());
        return new(string.Equals(path, SyncRootPath, StringComparison.OrdinalIgnoreCase) ? string.Empty : Path.GetRelativePath(SyncRootPath, path),
            CloudLocalFileBindingPlatform.Read(handle.DangerousGetHandle(), SyncRootPath), metadata.PlaceholderIdentity,
            basic.CreationTime, basic.LastWriteTime, metadata.Length);
    }

    private static SafeFileHandle? TryOpenRemoteMoveObject(string path)
    {
        try
        {
            return WindowsFileMetadata.Open(path);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return null;
        }
    }

    private static bool SameRemoteMoveObject(RemoteMoveObject expected, RemoteMoveObject current) =>
        expected.Binding == current.Binding && expected.Identity.AsSpan().SequenceEqual(current.Identity) &&
        expected.CreationTime == current.CreationTime && expected.LastWriteTime == current.LastWriteTime && expected.Length == current.Length;

    private static void RequireSameRemoteMoveObject(RemoteMoveObject expected, RemoteMoveObject current)
    {
        if (!SameRemoteMoveObject(expected, current))
        {
            throw new IOException("The native object no longer matches the retained remote move preparation.");
        }
    }

    private static bool SameRemoteMoveItem(CloudItemState? current, CloudItemState expected) =>
        current is not null && current.ItemId == expected.ItemId && current.RemoteId == expected.RemoteId &&
        current.RelativePath == expected.RelativePath && current.Kind == expected.Kind &&
        current.RemoteRevision == expected.RemoteRevision && current.LocalFileId == expected.LocalFileId &&
        current.IsTombstone == expected.IsTombstone && current.UpdatedAt == expected.UpdatedAt;

    private static IOException RemoteMoveUnresolved(RemoteMoveIntent intent, string stage) =>
        new($"Remote batch '{intent.BatchId}' entry {intent.EntryIndex} remains pending at {stage}; " +
            "retain the original batch and reconcile the namespace before retrying.");

    private sealed class RemoteMoveParentLease(List<SafeFileHandle> handles, RemoteMoveObject[] parents) : IDisposable
    {
        internal RemoteMoveObject[] Parents => parents;
        public void Dispose()
        {
            foreach (SafeFileHandle handle in handles)
            {
                handle.Dispose();
            }
        }
    }
}
