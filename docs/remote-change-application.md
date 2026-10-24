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

### Authorizing remote deletion

With the default `PreserveUnsynchronizedLocalContent = true`, a remote Delete retains the local
object and records a durable Delete conflict, including for clean files, empty directories and
fully synchronized trees. This changes the earlier automatic deletion behavior. A user-mode
library cannot exclude arbitrary external attribute writers between its final observation and
native deletion, so a clean snapshot alone is insufficient destructive authorization.

Applications can explicitly select `KeepRemote` through the conflict resolver or
`ResolveRemoteConflictAsync`, or submit the batch with `PreserveUnsynchronizedLocalContent = false`.
These choices authorize destructive deletion, including existing unsynchronized content and
ordinary descendants. They retain sealed inventory, object identity, attribute snapshots, local
operation bindings, native receipts and terminal validation; they do not promise isolation from
concurrent external metadata writes. Native APIs remain available for advanced callers.

A retained conflict is a processed batch entry under the existing cursor contract: its durable
record allows the safe prefix to advance, but the entry reports `Conflict`, not a successful
Delete. Replaying that processed batch does not delete the retained object or duplicate the
conflict. Resolve the conflict explicitly when the application authorizes deletion.

Authorized deletion uses a sealed children-first inventory with a further check before each
mutation. Newly created children are never swept into a fresh recursive enumeration. Concurrent
namespace changes can stop partial deletion, whose original receipts and tombstones remain
available for recovery. Native mutation and durable state are separate resources.
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

The official SQLite provider upgrades schemas 0–8 in place to **schema 9**, retaining existing
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

## Recovering original remote deletions (CF-008)

For an already-authorized Delete, submit the unchanged original batch again through
`ApplyRemoteChangesAsync`, including its initial/final cursors, change IDs, entry order, opaque
revisions and paths. Recovery also binds
the preservation and echo policies; only the per-call entry limit may change. No application
preparation token, private SQL, revision rewrite or automatic conflict decision is required.
Revisions remain opaque: an omitted previous revision or equal previous/incoming revisions do
not weaken identity or operation-ownership checks.

Before native work, CfSharp commits a versioned preparation containing the original item/native
identity, root/store scope, guarded parent bindings, local-operation fingerprints and the complete
authorized inventory. The exact original official path is stored separately from the native
path spelling, preserving durable state when Windows reports different path casing.
File and directory evidence includes native USNs so equal-length writes with restored timestamps
and later directory metadata changes cannot silently reuse old authorization. Native deletion
targets the inspected handle; it never reopens a mutable path for deletion. Handles exclude competing file writers/deleters,
parent guards prevent ancestor replacement, and metadata queries do not hydrate content.

Deletion requires exclusive data access. Existing readers, including readers that share deletion,
defer the operation before native mutation; close those handles and replay the unchanged batch.
This prevents a reader from leaving the object delete-pending after the SDK closes its handle.

After Windows accepts deletion on the authenticated native object, an acceptance receipt commits
before reopening the path. Attribute-only query handles can delay disappearance despite exclusive
data access. While such a handle remains open, the original entry and local-scan fence stay pending
with their durable receipt; close the handle and replay the unchanged batch, including after restart.
Only observed disappearance permits the exact item tombstone and its projection receipt to commit
atomically. Finally, the original batch outcome, safe prefix/cursor and receipt consumption commit
in one transaction. A failed or
ambiguous commit retains the original durable state for re-reading; an exception does not prove
rollback. Neither preparation nor a matching current tombstone alone proves native completion.

| Interruption | Unchanged original replay |
|---|---|
| Preparation not committed | Protected policy records a conflict; explicit authorization validates and prepares before mutation |
| Authorized preparation committed; original object still present | Revalidate the original object, parents, attributes, native USN and local work |
| Earlier protected preparation; native acceptance not committed | Retain original evidence and report unresolved without another disposition |
| Original absent; no committed native-completion receipt | Explicit unresolved entry; no success cursor |
| Native acceptance committed; object still delete-pending | Retain proof and retry absence verification without deleting again |
| Native receipt committed; tombstone transaction lost | Repair only the original projection after state and absence validation |
| Tombstone/receipt committed; batch outcome lost | Complete the original entry without deleting again or producing a stale conflict |
| Outcome committed; response lost | Return the durable prefix as `AlreadyApplied` without further deletion |

An unprovable or changed recovery is returned as `Failed` with `RequiresRetry`, retaining the
safe prefix and stopping the suffix. `CloudRemoteDeleteRecoveryException` identifies reconciliation
that cannot safely be inferred automatically and exposes the original batch ID, change ID and entry
ordinal. Other native/storage failures retain their error types and native details. If recording
the failure itself fails, both errors are preserved in an aggregate exception. Real pre-application
stale/local conflicts retain ordinary durable conflict semantics. Batch-level `Applied` can include
conflict entries; it does not certify successful deletion of every entry.

### Recursive authorization and replacements

The inventory is sealed before the first mutation, with at most **4096 members** and **8 MiB** of
encoded evidence per operation, including reserved space for both completion timestamps of every
member. Enumeration stops at the member budget and performs no deletion
if sealing fails. Preparation is one bounded transaction; each member uses separate short native
receipt and projection transactions. Children precede their parent. Retries retain that same
inventory and the completed descendant tombstones; they never sweep newly created children into
the old authorization. A remaining/nonempty directory, dirty file, link, pending operation, identity
change or replaced parent stops incomplete work without representing it as an intact-tree conflict.
Directory last-write times can change as authorized children are removed; the directory's own USN
remains part of the sealed evidence. Completed children never exempt a later actual attribute
change or changed USN. An unfinished directory prepared by an older draft without its own USN
remains unresolved. Bindings, original local operations, completion receipts and the empty-directory
native check are also revalidated.

Preparation also stores every member's exact native file-attribute bits and compares them before
disposition, independently of USN and InSync. NTFS may combine repeated attribute changes into one
USN while a write-attribute handle stays open; a later Hidden/System change is still protected even
when that USN and the placeholder's InSync state remain unchanged. Evidence payload version 3
retains these snapshots. Older draft payloads without them remain fenced and unresolved rather
than inferring an attribute snapshot from the current object.

Final native validation follows transaction acquisition and all asynchronous official-state and
journal reads, immediately before native disposition. This detects metadata changes made while
waiting for the store. Windows exempts attribute-only writers from data-sharing exclusion, however;
the native queries and disposition are separate calls, without atomic compare-USN-and-delete
semantics. Default preservation therefore records a conflict before any disposition instead of
claiming that isolation. Explicit destructive authorization accepts this concurrency boundary.

An unfinished preparation from an earlier producer can retain an effective protected policy.
Replay never issues a new native disposition for such a preparation, even when the current native
snapshot matches. It returns `CloudRemoteDeleteRecoveryException` with the original preparation
and any completed descendants retained. Members with durable native-acceptance receipts may still
finish absence verification and projection, and a fully accepted preparation may finish its
original outcome without deleting again. Changing apply options, deleting checkpoints or toggling
private policy flags is not a supported reauthorization path; unresolved earlier preparations
require explicit reconciliation. Force-authorized preparations retain their original recovery.
An inline KeepRemote decision is sealed as effective destructive authorization independently
of the original requested preservation option. Replay of that preparation can complete without
invoking another conflict resolver, while retaining the original batch and apply-policy binding.

A new ordinary file or placeholder at a completed path is never deleted by replay. Its pending
scope remains unresolved until that replacement generation is reconciled, preventing delayed local
observations from reusing the tombstoned identity. A later official generation that prevents exact
projection validation also leaves the original entry unresolved.
Explicit `KeepRemote` resolution performs the same terminal validation inside the transaction
that removes its conflict and recovery evidence, retaining both when a replacement appears.
The explicit force policy can authorize existing ordinary descendants and existing pending local
work, including ordinary descendants already tracked by the local feed. Such descendants are bound
to their native object, exact official state and pending operations without a placeholder identity;
their original journal rows remain intact. Placeholders and the operation root still require
native/official identity agreement. Force still fixes the original inventory and rejects
replacement objects, later work and newly added children. Lower-level native capabilities remain
available; recovery never silently changes preservation policy.

A Delete identifies an existing item by its matched remote/item identity even after a locally
tracked rename. Default preservation still retains its pending move as a Delete conflict;
explicit `KeepRemote` binds and deletes that identity's current official/native path, retaining
the original local journal. Its preparation and terminal parent checks use the bound path; a new
occupant of the old remote spelling is never enlisted in this authorization.

Pending Delete scopes fence local-feed observations and scans until original remote recovery
finishes. Observations remain durable and require a full rescan; rescan acknowledgement cannot
discard a pending Delete. Replay, reconcile the actual namespace including replacement generations,
then acknowledge reconciliation. Echo suppression is separate: expiring echoes cannot remove
unfinished preparation, inventory or receipts. Terminal batch state retains idempotency after
completed evidence is consumed.
Later create/update observations at a tombstoned path also require reconciliation after terminal
completion, rather than resurrecting the historical item ID for the replacement generation.

### Store, lifetime and legacy limits

Use matching core and SQLite packages. Schema **8** atomically fences older runtimes without
rewriting existing items, operations, conflicts or batches. Custom stores must provide the same
atomic isolation across all public transactional repositories and preserve opaque checkpoints and
prefix listing. No stronger distributed lock is implied: one active owner must control a store/root.
Calls serialize under the remote-apply gate and root operation lease; disposal drains admitted work.
Cancellation before mutation can stop safely. After native effects, required receipt/projection
bookkeeping uses the admitted lifetime; cancellation may still interrupt outcome publication, whose
original evidence remains available after complete shutdown and reopening.

Preview.6 gaps have no authenticated Delete preparation. Their absent objects or existing
tombstones are reported as unresolved without advancing a new success cursor. Existing conflicts
are retained and not silently dismissed during migration. Preserve the unchanged batch and
application evidence for operator reconciliation. If original proof cannot be recovered, reconcile
the remote namespace and local work explicitly, then rebuild a new caller-selected store through
supported APIs when necessary. Never edit private checkpoints/tombstones, manufacture
`AlreadyApplied`, or advance the application's separate Adapter/catalog cursor to bypass this state.

Native deletion and SQLite are not a single atomic resource. In particular, hard termination after
deletion but before a durable native receipt can remain unresolved. Native file/empty-directory
tests distinguish injected transaction failures from actual subprocess termination at preparation,
native receipt, projection and outcome boundaries. A new preview and downstream product settlement
require their own exact package/source, architecture and execution-role acceptance evidence; passing
these library tests alone does not establish installed/default-Host product acceptance.
The x64 and ARM64 CI jobs additionally execute the fourteen file/empty-directory receipt cases
under a newly created ordinary local user, retaining the original TRX and verified SID/role/OS
evidence. The account-creation harness runs only on disposable GitHub Actions runners.




## Distinct births at retired paths (CF-009)

A completed retirement does not give its path authority over a new remote identity. For a file or
empty-directory upsert with a distinct explicit `ItemId` and distinct opaque `RemoteId`, CfSharp
creates the requested successor, retaining the predecessor's original ID, remote ID, retired
revision, path and tombstone. The new native placeholder and live public row use the successor;
canonical path lookup returns that live row. Replaying the original completed birth or Delete
acknowledges its original batch without restoring or deleting a later generation.

An explicit ID incompatible with an existing remote identity, item identity or kind produces a
durable conflict. A live foreign placeholder or unmanaged occupant is preserved; a native object
that cannot be bound to original evidence remains retryable and unresolved. Processed conflicts
retain the existing safe-prefix contract: their cursor acknowledges a durable conflict, not a
successful birth. A retained conflict receipt distinguishes those outcomes during later replay.
A same-ID, same-remote-ID resurrection remains an intentional reuse of that identity. An upsert
without an ID reuses a matching remote identity; otherwise it allocates a new identity, including
when only a different remote identity's tombstone is known at that path.

Before native creation, a versioned intent retains the complete original change fingerprint,
requested identity, root/store scope, parent object bindings and preservation policy.
The same record seals an inline KeepRemote/KeepBoth decision and its effective identity/path
against the original immutable request. Reopen reuses that authorization without rerunning the
resolver or allocating another copy identity. Preparation reserves the later native receipt's
encoding capacity before creating an object. Parent namespace guards cover creation and
projection. After native success, a separate durable native
receipt binds the actual file object, identity, kind, times and logical length before item
projection. Retrying across owner reopen uses that original receipt, never a new path-derived ID.
A matching identity blob on another file object, a hard-link alias, changed parents or dirty
original object cannot substitute for that proof. Final outcome publication checks the original
native object and public projection inside the transaction before consuming the intent and
advancing progress. These operations do not read provider content.
Namespace read guards do not require DELETE permission on a newly created file or directory.

Cancellation before native mutation leaves no successful prefix. After native success, receipt
and projection commits ignore cancellation; the caller may need to replay outcome publication.
Native creation and the state store are separate resources. A hard kill or lost native-receipt
commit can leave a placeholder without durable original-object proof. That gap remains unresolved,
even if its identity matches, instead of adopting a possibly substituted object or creating again.
The guards authenticate namespace objects; they do not claim atomic exclusion of arbitrary future
external metadata writes. Late path-only Delete notifications with a present successor require
reconciliation instead of tombstoning the successor. Old journal and ACK identities remain bound
to their original item IDs.

### Upgrade and already affected preview.7 stores

Schema 9 preserves valid old tombstones and allows their first distinct successor without changing
history. Legacy version-1 pending creation intents lack root/native provenance, and some contain
an identity inconsistent with their explicit change. They remain intact and fail with an
`InvalidDataException` in the failed entry; they are not silently upgraded into new proof.

A preview.7 store may already have acknowledged B's batch while A is live with B's remote metadata
and B's public row is absent. Migration does not undo that accepted history. Replaying an accepted
explicit upsert whose remote projection identifies another item throws `InvalidDataException`
before changing the stored fingerprint, cursor or projection. Legacy records do not reliably
separate that corruption from every historical conflict; incompatible legacy success remains
fenced and requires the caller to inspect its original conflict and application evidence.
This is a managed identity/provenance failure, not an invented Win32 error for a successful native call.

Keep the protected product namespace fenced. Retain the original immutable batches, native
placeholder identities/bindings, official identity-based rows, journal/ACK and application ownership
proof. Compare the requested ID with both the actual native identity and the public remote/item
projections. `AlreadyApplied` alone is not evidence of repair. Reconcile the true original and
successor ownership explicitly using supported SDK/application operations; do not erase tombstones,
rewrite accepted fingerprints, reinterpret private checkpoint payloads or adopt the predecessor ID
as a workaround. If original ownership cannot be restored safely, reconcile application state
first, then rebuild into a new caller-selected official store/root through supported APIs. This
may require replaying earlier work and does not by itself advance product cursors or prove Host
acceptance. Package publication and downstream locked-restore validation are separate release gates.
