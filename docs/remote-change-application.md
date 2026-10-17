# Remote change application

Remote transport, authentication, cursors, content bytes, and business conflict policy belong to
the host application. CfSharp applies an immutable batch to the local namespace and records durable
progress around it.

## Submit an immutable batch

```csharp
CloudRemoteChangeBatch batch = new(
    "provider-batch-42",
    initialCursor,
    changes,
    finalCursor);

CloudRemoteApplyResult result = await fileSystem.ApplyRemoteChangesAsync(
    batch,
    new CloudRemoteApplyOptions { ConflictResolver = conflictResolver },
    cancellationToken);
```

Each entry has a durable result. The cursor advances only after the corresponding work is committed.
If a process stops after a partial batch, submitting the same immutable batch resumes at the safe
checkpoint. A retry is expected behavior, not evidence that the provider should create duplicate
namespace entries.

## Conflicts and directories

Conflicts are durable records that the application can resolve explicitly. Directory metadata can
be queried without creating local placeholders; materialized synchronized-directory views are
separate and side-effect free. Neither view performs hidden remote network access.

## Content bytes

Remote file upserts carry metadata and length. A configured content provider hydrates bytes later in
response to Windows demand. Keep authentication tokens, transport cursors, and remote business
payloads in application-owned storage rather than the CfSharp state database.

### Preserving local descendants during remote directory deletion

The default preservation policy checks the materialized subtree, including ordinary files absent from the state store. An untracked or dirty descendant, a pending descendant operation, a link, or an identity mismatch produces a delete conflict before deletion starts. Parent in-sync state does not certify its descendants. Only preflighted entries are deleted, with a further check before each deletion; newly created children are never swept into a fresh recursive enumeration. Concurrent namespace changes can stop a partially completed deletion, whose durable tombstones are retained. This is not a filesystem-wide transaction. Explicitly disabling preservation retains the existing destructive policy.
`r`n### Cached content during file upserts

File upserts atomically invalidate hydrated ranges when replacing the placeholder identity and metadata. Subsequent reads obtain the new revision from the content provider. Metadata-only changes retain cached content. The default preservation policy also verifies in-sync state in the native update. If Windows rejects invalidation (for example, a pinned or always-full file), the entry fails with its native error and the durable revision does not advance; CfSharp does not silently change pin or root policies.
`r`n### Keeping local content and closing a conflict

`KeepLocal` and `Defer` continue to leave the conflict unresolved. To explicitly close an individual persistent conflict without changing local content, call `DismissRemoteConflictAsync(conflictId)`. `Dismissed` means the unresolved record and a durable receipt were atomically updated. A retry, including after restart, returns `AlreadyDismissed`. `NotFound` means no active record or dismissal receipt exists; it is not proof of successful resolution by another action.

Dismissal neither uploads content nor advances a batch cursor, acknowledges pending uploads, or modifies placeholders. New remote changes may conflict again. Receipts are private coordination checkpoints retained without expiry; applications must not delete or parse them. This additive API requires a transactional custom store and works with the official SQLite store. Storage errors are visible and can be retried. Older binaries cannot interpret dismissal receipts, so use a consistent library version for conflict-center actions.
`r`n### Recovering newly created remote placeholders

New file and directory upserts prepare a durable creation intent before touching the namespace. The native identity is retained for retries; item state and identity-scoped echo suppression commit together, with foreign keys enabled. Batch progress removes the completed intent in its own atomic outcome transaction. If native creation succeeds but persistence fails, replay the unchanged batch to reconcile the same placeholder rather than generating a new item identity.

The local feed retains observations made during an uncommitted creation separately and reports `RequiresFullRescan` before delivering upload candidates. Replay the remote operation first, then reconcile the root and acknowledge the rescan. A rescan acknowledgement cannot discard a still-pending creation. Ordinary subsequent local edits are not suppressed when native state is out of sync. Native creation and the database are not one atomic resource; failures retain recovery evidence and never advance the batch cursor prematurely.

The official SQLite store upgrades to schema 5 to prevent older versions from ignoring this recovery protocol. Do not downgrade an upgraded database. Custom transactional stores remain supported using existing repositories, but all accessing runtimes must use the new protocol version together. Creation transactions contain a synchronous placeholder creation, not hydration or application callbacks.

## Recovering remote file moves (CF-007)

File moves and case-only file renames submitted in an immutable batch now retain a versioned
preparation before namespace mutation. Replay the **same batch**, including its ID, entry order,
fingerprint, IDs, paths, opaque revisions, and cursor envelope. Keep the original
`PreserveUnsynchronizedLocalContent`, `SuppressLocalEcho`, echo lifetime, and observation count.
`MaximumEntries` may change; a resolver instance is not part of the persisted policy.

The preparation belongs to the original store and root. It captures the original official item
state, native placeholder identity separately from official revision, complete volume/root/file
binding, creation/write times and length, and source/destination ancestor bindings and identities.
The native rename uses the inspected object handle with replacement disabled. It requests no data
access and performs no content reads, identity rewrite, hydration, or in-sync transition. Revisions
are opaque: recovery also works when previous and incoming revisions are equal.
Preparation and recovery hold source/destination ancestors with directory-list/read-attribute
access and delete sharing disabled. These handles prevent replacement during native and durable
work without enumerating children. Insufficient access or file-ID/USN support is an explicit error.

Replay authenticates the preparation **before** applying ordinary stale-revision checks:

| Durable/native boundary | Replay behavior |
| --- | --- |
| Preparation committed, original object still at source | Revalidate original conditions and issue the original rename. |
| Original object at exact target, item projection not committed | Verify original binding, identity, metadata, ancestry, and local journal; commit path/revision together without another rename. |
| Item projection committed, original batch outcome missing | Revalidate the same object and projected state; commit the missing original outcome without another rename. |
| Outcome committed, reply lost | Read the original committed prefix and return `AlreadyApplied`. |
| Object, ancestry, state, or local work changed | Return `Failed` with `Error`, `RequiresRetry=true`, and the unchanged safe prefix. Retain evidence for reconciliation; do not manufacture a stale conflict. |

Mismatched policy, store/root, or corrupted preparation is rejected with `InvalidDataException`
before normalizing progress. A changed batch fingerprint is rejected with `ArgumentException`.

Recovered entries return `Applied` only after their outcome commits. `AppliedEntryCount` counts
committed outcomes, **including legitimate conflicts**. A fully processed batch has `Status=Applied`
and `RequiresRetry=false`; callers still inspect `Entries` and `ConflictIds`. An unchanged completed
prefix returns `AlreadyApplied` on replay, using the existing batch semantics; this status certifies
the recorded outcome, not that every historical entry was conflict-free. `SafeCursor` advances only
with that durable prefix. CfSharp never publishes an application's separate Adapter cursor.

The projected preparation also captures the post-rename native USN after closing rename-overlapping
handles, so NTFS's accumulated close record is included. Replay rejects subsequent native changes.
`TrackAll` can clear in-sync because of the rename itself; recovery preserves that state rather than
marking the object clean. Before a projected USN exists, recovery requires the original in-sync
condition under the conservative policy. Thus an early interrupted rename that clears in-sync
remains explicitly unresolved; roots using `PreserveForSyncEngine` can preserve that proof boundary.
The conservative policy rejects nonzero modified content ranges; an explicit force policy retains
its original dirty content and pending journal without acknowledging or marking them clean.

The path/revision projection and echo suppression commit together. Preparation cleanup and original
outcome/progress commit together. Any pre-commit fault leaves evidence; a late exception after commit
is resolved by rereading durable state on the next call. Entry storage failures retain a failed,
retryable prefix and their original exception in `Error`. Outcome storage failures propagate, so a
caller must replay after reopening rather than assume either rollback or success. Storage errors
do not acquire native HRESULTs. Native rename errors preserve their actual HRESULT.

Cancellation before native mutation leaves an unchanged object and any committed preparation.
After native success, item projection finishes without cancellation under the admitted owner
lifetime; cancellation may leave batch outcome publication for replay. One owner's calls serialize,
and disposal waits for admitted work, including recovery transactions. Use one active SDK owner
per root/store. Reopening that pair is supported; simultaneous owners are not a distributed lock.
No conflict resolver or content-provider callback runs inside the rename/projection transaction.

Local feed observations at either pending path are durably retained and require a full rescan.
Replay first, reconcile the root, then acknowledge the rescan. Acknowledgement cannot discard a
pending move. Existing journal rows are never acknowledged by recovery; a changed journal or dirty
object leaves the operation unresolved. An explicit force policy may retain its original pending
operations while moving, but recovery rejects later operations. This fix conservatively refuses
to settle a move after later edits instead of clearing or overwriting them.

### Compatibility and limits

The official SQLite provider upgrades schemas 0–6 in place to **schema 7**, retaining existing
repositories and enforcing foreign keys. Older providers reject that version before ignoring
pending file-move evidence. Use matching core/provider packages; never downgrade the schema marker.
Custom stores use the existing public repositories and must atomically isolate transactions across
all repositories, preserve opaque checkpoints and their ordinal prefix listing, and fence older
runtimes themselves. Stores lacking those contracts cannot support this protocol. Only metadata
queries and synchronous rename occur inside the native/projection transaction.

This recovery protocol covers **files** submitted through `ApplyRemoteChangesAsync`. Directory
movement keeps its existing subtree/proof APIs; this fix does not claim automatic recovery of a
directory's missing remote-batch outcome. It does not add a public preparation token or infer an
outcome from matching present-day paths/revisions. Direct lower-level file moves and explicit
standalone conflict resolutions keep their existing contracts.

Preview.5 file gaps without original preparations remain conservative conflicts. Existing durable
conflicts are preserved for review, not silently reclassified during upgrade. Preserve the original
batch, native object, local work, and application evidence for operator reconciliation. Supported
conflict decisions remain explicit policy, not proof that a historical move succeeded. If proof
cannot be restored, retire that store only after application reconciliation and rebuild state in a
new caller-selected store through supported APIs; do not edit private checkpoints or mark a batch
complete manually. This can require reprocessing earlier work and does not complete product cursors.

Native regression tests cover ordinary/case-only moves with SQLite reopen, equal revisions,
preparation/projection/outcome interruptions, ambiguous commits, repeated recovery faults,
replacement/parent changes, dirty state, and pending edits. In-process exceptions followed by
normal disposal are distinct from power loss or hard process termination. Architecture/ordinary-user
results and remaining acceptance-matrix cases must be reported with their actual executed evidence.



