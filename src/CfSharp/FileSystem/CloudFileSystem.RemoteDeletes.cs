using System.ComponentModel;

using CfSharp.Native;

using Microsoft.Win32.SafeHandles;

namespace CfSharp;

public sealed partial class CloudFileSystem
{
    private async ValueTask<RemoteDeleteIntent?> ReadRemoteDeleteIntentAsync(RemoteChangeRequest request,
        CancellationToken cancellationToken)
    {
        await using ICloudStateTransaction transaction = await _stateStore!.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadRemoteDeleteIntentCoreAsync(transaction, request, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RemoteDeleteIntent?> ReadRemoteDeleteIntentCoreAsync(ICloudStateTransaction transaction,
        RemoteChangeRequest request, CancellationToken cancellationToken)
    {
        CloudStateCheckpoint? checkpoint = await transaction.Checkpoints.GetAsync(
            RemoteDeleteIntent.Name(request.Batch.BatchId, request.EntryIndex), cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
        {
            return null;
        }

        RemoteDeleteIntent intent = RemoteDeleteIntent.Decode(checkpoint.Value);
        CloudRemoteChange change = request.Batch.Changes[request.EntryIndex];
        CloudItemState original = intent.Members[^1].Original!;
        if (!intent.Matches(request.Batch, request.EntryIndex, request.Options) ||
            original.RemoteId != change.RemoteId || original.Kind != change.ItemKind ||
            (change.ItemId.HasValue && change.ItemId != original.ItemId) ||
            !string.Equals(intent.RootPath, SyncRootPath, StringComparison.OrdinalIgnoreCase) ||
            intent.StoreScope != await CloudDirectoryMoveEvidence.GetScopeAsync(transaction, false, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("The remote Delete preparation does not match this batch, policy, store, or root.");
        }

        return intent;
    }

    private async ValueTask<RemoteEntryOutcome> ApplyRemoteDeleteAsync(CloudRemoteChange change, RemoteEntryContext context,
        CloudRemoteApplyOptions options, RemoteChangeRequest request, CancellationToken cancellationToken)
    {
        CloudItemState? original = context.LocalState;
        if (original is null || original.RemoteId != change.RemoteId || original.Kind != change.ItemKind ||
            (change.ItemId.HasValue && change.ItemId != original.ItemId))
        {
            return RemoteEntryOutcome.ConflictResult(CreateConflict(change, original, CloudRemoteConflictReason.MissingItem));
        }

        // Legacy gaps have no original-operation witness. Even an equal revision and absent
        // path cannot authorize a fabricated completion, nor may they consume a safe cursor.
        if (original.IsTombstone || (!File.Exists(ToFullPath(original.RelativePath)) && !Directory.Exists(ToFullPath(original.RelativePath))))
        {
            throw DeleteUnresolved(request, "legacy or unproven native outcome");
        }

        if (change.PreviousRemoteRevision is not null && original.RemoteRevision != change.PreviousRemoteRevision)
        {
            return RemoteEntryOutcome.ConflictResult(CreateConflict(change, original, CloudRemoteConflictReason.StaleRemoteRevision));
        }

        if (options.PreserveUnsynchronizedLocalContent && context.LocalOperations.Count != 0)
        {
            return RemoteEntryOutcome.ConflictResult(CreateConflict(change, original, CloudRemoteConflictReason.Delete));
        }

        CloudItemSnapshot snapshot = await CreateItemReference(original.RelativePath, original.Kind).InspectAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.IsPlaceholder || (options.PreserveUnsynchronizedLocalContent && snapshot.SynchronizationState != CloudSynchronizationState.InSync))
        {
            return RemoteEntryOutcome.ConflictResult(CreateConflict(change, original, CloudRemoteConflictReason.Delete));
        }

        List<RecursiveItemEntry> inventory = MaterializeRemoteDeleteInventory(original);
        if (original.Kind == CloudItemKind.Directory && options.PreserveUnsynchronizedLocalContent &&
            !await CanDeleteRemoteTreeAsync(inventory, original.RelativePath, cancellationToken).ConfigureAwait(false))
        {
            return RemoteEntryOutcome.ConflictResult(CreateConflict(change, original, CloudRemoteConflictReason.Delete));
        }

        RemoteDeleteIntent intent = await PrepareRemoteDeleteAsync(original, inventory, request, options, cancellationToken).ConfigureAwait(false);
        return await CompleteRemoteDeleteAsync(request, intent, cancellationToken).ConfigureAwait(false);
    }

    private List<RecursiveItemEntry> MaterializeRemoteDeleteInventory(CloudItemState original)
    {
        List<RecursiveItemEntry> entries = [];
        Stack<(string Path, CloudItemKind Kind)> pending = new();
        pending.Push((original.RelativePath, original.Kind));
        while (pending.TryPop(out var next))
        {
            string fullPath = ToFullPath(next.Path);
            FileAttributes attributes = File.GetAttributes(fullPath);
            bool link = IsFileSystemLink(fullPath, next.Kind, attributes);
            if (link)
            {
                throw new IOException("A remote Delete inventory cannot traverse a filesystem link.");
            }

            entries.Add(new(fullPath, next.Path, next.Kind, false));
            if (entries.Count + pending.Count > RemoteDeleteIntent.MaximumMembers)
            {
                throw new IOException("The remote Delete inventory exceeds the 4096 member budget before mutation.");
            }

            if (next.Kind == CloudItemKind.Directory)
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(fullPath))
                {
                    FileAttributes childAttributes = File.GetAttributes(child);
                    pending.Push((Path.GetRelativePath(SyncRootPath, child), childAttributes.HasFlag(FileAttributes.Directory)
                        ? CloudItemKind.Directory : CloudItemKind.File));
                    if (entries.Count + pending.Count > RemoteDeleteIntent.MaximumMembers)
                    {
                        throw new IOException("The remote Delete inventory exceeds the 4096 member budget before mutation.");
                    }
                }
            }
        }

        entries.Reverse(); // Every descendant follows its parent during discovery.
        return entries;
    }

    private async ValueTask<RemoteDeleteIntent> PrepareRemoteDeleteAsync(CloudItemState original,
        IReadOnlyList<RecursiveItemEntry> inventory, RemoteChangeRequest request, CloudRemoteApplyOptions effectiveOptions,
        CancellationToken cancellationToken)
    {
        string root = ToFullPath(original.RelativePath);
        using CloudPathHandleLease chains = CloudPathHandleLease.OpenParentChains(SyncRootPath, [root]);
        using RemoteNamespaceParentLease parents = HoldRemoteNamespaceParents(root, root);
        await using ICloudStateTransaction transaction = await _stateStore!.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (CloudStateCheckpoint checkpoint in await transaction.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix, cancellationToken).ConfigureAwait(false))
        {
            RemoteDeleteIntent pending = RemoteDeleteIntent.Decode(checkpoint.Value);
            if (pending.Contains(original.RelativePath) || CloudDirectoryStateProjection.SamePath(pending.RelativePath, original.RelativePath) ||
                (original.Kind == CloudItemKind.Directory && pending.RelativePath.StartsWith(original.RelativePath + "\\", StringComparison.OrdinalIgnoreCase)))
            {
                throw DeleteUnresolved(request, "another pending Delete in the original scope");
            }
        }

        List<RemoteDeleteMember> members = [];
        foreach (RecursiveItemEntry entry in inventory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using SafeFileHandle guard = WindowsFileMetadata.OpenForDelete(entry.FullPath);
            RemoteNamespaceObject observed = ObserveRemoteDeleteObject(guard, entry.Kind, effectiveOptions.PreserveUnsynchronizedLocalContent, request,
                out bool placeholder, out uint attributes);
            CloudItemState? state = await transaction.Items.GetByRelativePathAsync(entry.RelativePath, cancellationToken).ConfigureAwait(false);
            if (state is not null && (state.IsTombstone || state.Kind != entry.Kind))
            {
                throw DeleteUnresolved(request, "changed original projection during preparation");
            }

            // Explicit force also authorizes ordinary descendants discovered by the local feed.
            // Their official rows have no placeholder identity; bind the inspected native object,
            // exact official state and original journal instead. The root and every placeholder
            // still require native/official identity agreement, including under force.
            bool ordinaryDescendant = !placeholder && !effectiveOptions.PreserveUnsynchronizedLocalContent &&
                !CloudDirectoryStateProjection.SamePath(entry.RelativePath, original.RelativePath);
            if (state is not null && !ordinaryDescendant && (!CloudPlaceholderIdentity.TryDecode(observed.Identity, out CloudPlaceholderIdentity? identity) ||
                identity.ItemId != state.ItemId || identity.RemoteId != state.RemoteId))
            {
                throw DeleteUnresolved(request, "original native/official identity agreement");
            }

            IReadOnlyList<CloudOperationJournalEntry> operations = state is null ? [] :
                await transaction.Operations.ListByItemIdAsync(state.ItemId, int.MaxValue, cancellationToken).ConfigureAwait(false);
            if (effectiveOptions.PreserveUnsynchronizedLocalContent && (state is null || operations.Count != 0))
            {
                throw DeleteUnresolved(request, "local work changed during preparation");
            }

            members.Add(new(entry.Kind, observed, state, RemoteNamespaceEvidence.FingerprintOperations(operations), null, null,
                WindowsFileUsn.Read(entry.FullPath, expectedDirectory: entry.Kind == CloudItemKind.Directory), attributes));
        }

        if (!SameRemoteItemState(members[^1].Original, original))
        {
            throw DeleteUnresolved(request, "changed root projection during preparation");
        }

        Guid scope = await CloudDirectoryMoveEvidence.GetScopeAsync(transaction, true, cancellationToken).ConfigureAwait(false);
        CloudRemoteApplyOptions policy = request.Options;
        RemoteDeleteIntent intent = new(scope, SyncRootPath, request.Batch.BatchId, request.EntryIndex, request.Batch.Fingerprint.ToArray(),
            policy.PreserveUnsynchronizedLocalContent, effectiveOptions.PreserveUnsynchronizedLocalContent,
            policy.SuppressLocalEcho, policy.EchoSuppressionLifetime.Ticks, policy.EchoSuppressionObservationCount, parents.Parents, members.AsReadOnly());
        await transaction.Checkpoints.UpsertAsync(new(RemoteDeleteIntent.Name(intent.BatchId, intent.EntryIndex), intent.Encode(), DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return intent;
    }

    private async ValueTask<RemoteEntryOutcome> CompleteRemoteDeleteAsync(RemoteChangeRequest request,
        RemoteDeleteIntent intent, CancellationToken cancellationToken)
    {
        // The top-level ancestry survives every authorized member deletion. Keep it bound
        // even when all members are already projected, so receipts cannot cross a replaced
        // root/parent at the same spelling while repairing the original batch outcome.
        string scopePath = ToFullPath(intent.RelativePath);
        using CloudPathHandleLease scopeChains = CloudPathHandleLease.OpenParentChains(SyncRootPath, [scopePath]);
        using RemoteNamespaceParentLease scopeParents = HoldRemoteNamespaceParents(scopePath, scopePath);
        ValidateRemoteDeleteParents(request, intent, scopeParents.Parents);
        CloudRemoteChange change = request.Batch.Changes[request.EntryIndex];
        for (int index = 0; index < intent.Members.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemoteDeleteMember member = intent.Members[index];
            string fullPath = ToFullPath(member.Object.RelativePath);
            // Completed children have vanished; do not try to open their former parents after
            // those parents have also completed. Their exact receipts/projections authenticate
            // completion. A new occupant is never touched or enlisted in this operation.
            if (member.ProjectedAtTicks.HasValue)
            {
                await using ICloudStateTransaction verify = await _stateStore!.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await ValidateRemoteDeleteStateAsync(verify, request, intent, member, index, cancellationToken).ConfigureAwait(false);
                using SafeFileHandle? replacement = TryOpenRemoteNamespaceObject(fullPath);
                if (replacement is not null)
                {
                    // Keep the pending scope until a replacement generation has been reconciled.
                    // Consuming it now could let a delayed watcher event reuse the tombstoned ID.
                    throw DeleteUnresolved(request, "replacement at a completed original path");
                }
                continue;
            }

            using CloudPathHandleLease chains = CloudPathHandleLease.OpenParentChains(SyncRootPath, [fullPath]);
            using RemoteNamespaceParentLease parents = HoldRemoteNamespaceParents(fullPath, fullPath);
            ValidateRemoteDeleteParents(request, intent, parents.Parents);
            if (!member.DeletedAtTicks.HasValue)
            {
                // Enumeration needs its own LIST_DIRECTORY handle, which the exclusive delete
                // handle would reject. The native disposition still rejects any new child
                // created after this check, and the opened object's binding is verified below.
                if (member.Kind == CloudItemKind.Directory)
                {
                    try
                    {
                        if (Directory.EnumerateFileSystemEntries(fullPath).Any())
                        {
                            throw DeleteUnresolved(request, "new or unresolved children outside completed original inventory");
                        }
                    }
                    catch (DirectoryNotFoundException)
                    {
                        throw DeleteUnresolved(request, "unproven native deletion before completion receipt");
                    }
                }

                using SafeFileHandle? handle = TryOpenRemoteDeleteObject(fullPath);
                if (handle is null)
                {
                    throw DeleteUnresolved(request, "unproven native deletion before completion receipt");
                }

                // Completed children do not authorize later metadata changes on their parent.
                // The directory's own USN and in-sync state remain part of preservation even
                // though its last-write time can change as our children disappear.
                if (member.Kind == CloudItemKind.Directory && !member.Usn.HasValue)
                {
                    throw DeleteUnresolved(request, "missing original directory change evidence");
                }
                // Exclude transactional projections across the same-object native mutation.
                // This is not a cross-filesystem/store atomic transaction: the prepared-but-
                // absent crash window deliberately remains unresolved without a completion.
                await using ICloudStateTransaction native = await _stateStore!.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await ValidateRemoteDeleteStateAsync(native, request, intent, member, index, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                // Attribute-only writers bypass data sharing and can change this object
                // during any awaited store operation. Observe native state only after all
                // transaction/journal waits, with no further await before disposition.
                // These queries are not an atomic compare-and-delete or an attribute lock.
                RemoteNamespaceObject observed = ObserveRemoteDeleteObject(handle, member.Kind, intent.RequirePreservation, request, out _, out uint attributes);
                if (!SameDeleteObject(member, observed) || member.Attributes != attributes || (member.Usn.HasValue &&
                    WindowsFileUsn.Read(fullPath, expectedDirectory: member.Kind == CloudItemKind.Directory) != member.Usn))
                {
                    throw DeleteUnresolved(request, "changed original native object");
                }

                try
                {
                    WindowsFileMetadata.Delete(handle.DangerousGetHandle());
                }
                catch (Win32Exception exception)
                {
                    throw CloudFilesException.FromHResult("CloudFileSystem.ApplyRemoteDelete", fullPath,
                        exception.NativeErrorCode <= 0 ? exception.NativeErrorCode : unchecked((int)(0x80070000u | (uint)exception.NativeErrorCode)));
                }

                handle.Dispose();
                // Windows accepted disposition on this authenticated object. Attribute-only
                // handles are exempt from data sharing and can delay disappearance even after
                // our exclusive handle closes. Persist acceptance before any path reopen: an
                // access error must not discard this proof and force an unprovable second delete.
                member = member with { DeletedAtTicks = DateTimeOffset.UtcNow.UtcTicks };
                intent = ReplaceDeleteMember(intent, index, member);
                await SaveRemoteDeleteIntentAsync(native, intent, CancellationToken.None).ConfigureAwait(false);
                await native.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            // Acceptance is not proof of disappearance. A delayed delete, inaccessible path or
            // replacement leaves this receipt intact, without repeating the native mutation.
            // Only observed absence permits the tombstone and its projection receipt below.
            // Projection and its completed-stage receipt still commit together. Neither stage
            // advances the original batch, and a lost commit result is reread on the next call.
            await using ICloudStateTransaction transaction = await _stateStore!.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
            await ValidateRemoteDeleteStateAsync(transaction, request, intent, member, index, CancellationToken.None).ConfigureAwait(false);
            using SafeFileHandle? occupant = TryOpenRemoteNamespaceObject(fullPath);
            if (occupant is not null)
            {
                throw DeleteUnresolved(request, "replacement before original tombstone projection");
            }
            DateTimeOffset now = DateTimeOffset.UtcNow;
            CloudItemState? original = member.Original;
            if (original is not null)
            {
                await transaction.Items.UpsertAsync(ProjectDeletedMember(original, change, index == intent.Members.Count - 1, now), CancellationToken.None).ConfigureAwait(false);
                if (intent.SuppressEcho)
                {
                    await transaction.EchoSuppressions.UpsertAsync(new(Guid.NewGuid(), original.ItemId, CloudStateOperationKind.Delete,
                        original.RelativePath, [], now + TimeSpan.FromTicks(intent.EchoLifetimeTicks), remainingObservations: intent.EchoObservations),
                        CancellationToken.None).ConfigureAwait(false);
                }
            }

            member = member with { ProjectedAtTicks = now.UtcTicks };
            intent = ReplaceDeleteMember(intent, index, member);
            await SaveRemoteDeleteIntentAsync(transaction, intent, CancellationToken.None).ConfigureAwait(false);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return RemoteEntryOutcome.AppliedResult;
    }

    private static async ValueTask ValidateRemoteDeleteStateAsync(ICloudStateTransaction transaction, RemoteChangeRequest request,
        RemoteDeleteIntent intent, RemoteDeleteMember member, int index, CancellationToken cancellationToken)
    {
        CloudStateCheckpoint? retained = await transaction.Checkpoints.GetAsync(RemoteDeleteIntent.Name(intent.BatchId, intent.EntryIndex), cancellationToken).ConfigureAwait(false);
        if (retained is null || !retained.Value.Span.SequenceEqual(intent.Encode()))
        {
            throw DeleteUnresolved(request, "preparation authentication");
        }

        CloudItemState? expected = member.Original;
        CloudItemState? current = expected is null ? await transaction.Items.GetByRelativePathAsync(member.Object.RelativePath, cancellationToken).ConfigureAwait(false) :
            await transaction.Items.GetByItemIdAsync(expected.ItemId, cancellationToken).ConfigureAwait(false);
        if (expected is not null && member.ProjectedAtTicks is long projected)
        {
            expected = ProjectDeletedMember(expected, request.Batch.Changes[request.EntryIndex], index == intent.Members.Count - 1,
                new DateTimeOffset(projected, TimeSpan.Zero));
        }

        if ((expected is null && current is not null) || (expected is not null && !SameRemoteItemState(current, expected)))
        {
            throw DeleteUnresolved(request, "changed original official projection");
        }

        IReadOnlyList<CloudOperationJournalEntry> operations = expected is null ? [] :
            await transaction.Operations.ListByItemIdAsync(expected.ItemId, int.MaxValue, cancellationToken).ConfigureAwait(false);
        if (!member.OperationsFingerprint.AsSpan().SequenceEqual(RemoteNamespaceEvidence.FingerprintOperations(operations)))
        {
            throw DeleteUnresolved(request, "changed original local operations");
        }
    }

    private async ValueTask ValidateRemoteDeleteOutcomeAsync(ICloudStateTransaction transaction, CloudRemoteChangeBatch batch,
        int entryIndex, IReadOnlyList<RemoteNamespaceObject> parents, CancellationToken cancellationToken)
    {
        CloudStateCheckpoint? checkpoint = await transaction.Checkpoints.GetAsync(RemoteDeleteIntent.Name(batch.BatchId, entryIndex), cancellationToken)
            .ConfigureAwait(false);
        if (checkpoint is null)
        {
            throw new InvalidDataException("A successful remote Delete outcome requires its original completion receipts.");
        }
        RemoteDeleteIntent intent = RemoteDeleteIntent.Decode(checkpoint.Value);
        RemoteChangeRequest request = new(batch, entryIndex, CloudRemoteApplyOptions.Default);
        ValidateRemoteDeleteParents(request, intent, parents);
        for (int index = 0; index < intent.Members.Count; index++)
        {
            RemoteDeleteMember member = intent.Members[index];
            if (!member.ProjectedAtTicks.HasValue)
            {
                throw DeleteUnresolved(request, "incomplete member at terminal outcome publication");
            }
            await ValidateRemoteDeleteStateAsync(transaction, request, intent, member, index, cancellationToken).ConfigureAwait(false);
            using SafeFileHandle? occupant = TryOpenRemoteNamespaceObject(ToFullPath(member.Object.RelativePath));
            if (occupant is not null)
            {
                throw DeleteUnresolved(request, "replacement before terminal outcome publication");
            }
        }
    }

    private async ValueTask<RemoteNamespaceParentLease> HoldRemoteDeleteOutcomeParentsAsync(ICloudStateTransaction transaction,
        CloudRemoteChangeBatch batch, int entryIndex, CancellationToken cancellationToken)
    {
        CloudStateCheckpoint checkpoint = await transaction.Checkpoints.GetAsync(RemoteDeleteIntent.Name(batch.BatchId, entryIndex), cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("A successful remote Delete outcome requires its original completion receipts.");
        string path = ToFullPath(RemoteDeleteIntent.Decode(checkpoint.Value).RelativePath);
        return HoldRemoteNamespaceParents(path, path);
    }

    private static CloudItemState ProjectDeletedMember(CloudItemState original, CloudRemoteChange change, bool root, DateTimeOffset time) =>
        new(original.ItemId, original.RemoteId, original.RelativePath, original.Kind, root ? change.RemoteRevision : original.RemoteRevision,
            original.LocalFileId, true, time);

    private static RemoteDeleteIntent ReplaceDeleteMember(RemoteDeleteIntent intent, int index, RemoteDeleteMember member)
    {
        RemoteDeleteMember[] members = intent.Members.ToArray();
        members[index] = member;
        return intent with { Members = members };
    }

    private static ValueTask SaveRemoteDeleteIntentAsync(ICloudStateTransaction transaction, RemoteDeleteIntent intent, CancellationToken cancellationToken) =>
        transaction.Checkpoints.UpsertAsync(new(RemoteDeleteIntent.Name(intent.BatchId, intent.EntryIndex), intent.Encode(), DateTimeOffset.UtcNow), cancellationToken);

    private RemoteNamespaceObject ObserveRemoteDeleteObject(SafeFileHandle handle, CloudItemKind kind, bool preserve, RemoteChangeRequest request,
        out bool placeholder, out uint attributes)
    {
        NativeFileMetadata metadata = WindowsFileMetadata.Read(handle.DangerousGetHandle());
        placeholder = metadata.PlaceholderState != CfPlaceholderState.Invalid && metadata.PlaceholderState.HasFlag(CfPlaceholderState.Placeholder);
        if (metadata.Directory != (kind == CloudItemKind.Directory) || metadata.DeletePending || metadata.Links != 1 ||
            (metadata.ReparseTag != 0 && !placeholder) || (preserve && (!placeholder || !metadata.InSync)))
        {
            throw DeleteUnresolved(request, "native kind, links, or protected local state");
        }

        string path = WindowsFileMetadata.ReadFinalPath(handle.DangerousGetHandle());
        if (!IsStrictDescendant(path, SyncRootPath))
        {
            throw DeleteUnresolved(request, "native object outside the original sync root");
        }

        WindowsFileMetadata.FileBasicInfo basic = WindowsFileMetadata.ReadBasic(handle.DangerousGetHandle());
        // NTFS can merge repeated BASIC_INFO_CHANGE reasons while a writer remains open.
        // InSync and USN can therefore stay unchanged across different actual attributes.
        attributes = basic.Attributes;
        return new(Path.GetRelativePath(SyncRootPath, path), CloudLocalFileBindingPlatform.Read(handle.DangerousGetHandle(), SyncRootPath),
            metadata.PlaceholderIdentity, basic.CreationTime, basic.LastWriteTime, metadata.Length);
    }

    private static bool SameDeleteObject(RemoteDeleteMember expected, RemoteNamespaceObject current) =>
        expected.Object.RelativePath == current.RelativePath && expected.Object.Binding == current.Binding &&
        expected.Object.CreationTime == current.CreationTime && expected.Object.Identity.AsSpan().SequenceEqual(current.Identity) &&
        (expected.Kind == CloudItemKind.Directory || (expected.Object.LastWriteTime == current.LastWriteTime && expected.Object.Length == current.Length));

    private static SafeFileHandle? TryOpenRemoteDeleteObject(string path)
    {
        try
        {
            return WindowsFileMetadata.OpenForDelete(path);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return null;
        }
    }

    private static void ValidateRemoteDeleteParents(RemoteChangeRequest request, RemoteDeleteIntent intent, IReadOnlyList<RemoteNamespaceObject> parents)
    {
        foreach (RemoteNamespaceObject parent in parents)
        {
            RemoteNamespaceObject? expected = intent.Parents.FirstOrDefault(value => CloudDirectoryStateProjection.SamePath(value.RelativePath, parent.RelativePath)) ??
                intent.Members.FirstOrDefault(value => value.Kind == CloudItemKind.Directory &&
                    CloudDirectoryStateProjection.SamePath(value.Object.RelativePath, parent.RelativePath))?.Object;
            if (expected is null || expected.Binding != parent.Binding || expected.CreationTime != parent.CreationTime ||
                !expected.Identity.AsSpan().SequenceEqual(parent.Identity))
            {
                throw DeleteUnresolved(request, "changed original parent binding");
            }
        }
    }

    private static CloudRemoteDeleteRecoveryException DeleteUnresolved(RemoteChangeRequest request, string stage) =>
        new(request.Batch.BatchId, request.EntryIndex, request.Batch.Changes[request.EntryIndex].ChangeId, stage);
}
