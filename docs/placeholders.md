# Placeholders and hydration

## Prepare directory move evidence

Before authorizing an external rename of a managed directory, call
`await directory.PrepareMoveAsync(destination, name)` and persist the returned proof's `Encode()`
bytes. Preparation performs no native move and returns only after immutable evidence and the
known durable subtree membership commit. `CloudDirectoryMoveProof.Decode(bytes)` restores copied
metadata; it does not make caller-constructed data authoritative. Recovery must still validate the
library's original preparation in the same store and the actual complete native binding and identity.

Each preparation has its own ID and survives restart independently. Do not substitute a binding
observed at a renamed target for missing historical evidence. Ordinary directories may use normal
`MoveToAsync`, but cannot prepare external recovery without a managed placeholder identity and
complete native IDs. Metadata capture reads no content and holds no continuing lock after returning.

Directory creation, conversion, identity replacement, remote creation, and provider population
retain native provenance in the same transaction as their official item rows. Adding children
refreshes known membership of captured ancestors without enumerating native subtrees. Batch creation
and each provider population page capture new directory bindings first, then refresh each affected
directory's membership once from all item rows in the same transaction, including shared ancestors.
The existing recovery metadata limits still apply. Immutable
prepared proofs remain unchanged. Removing an identity or reverting a directory removes its live
provenance. Storage without complete IDs retains existing creation behavior but cannot establish
this recovery evidence; legacy directories can explicitly prepare while still at their source.

After the external move, call `await originalDirectory.ReconcileMoveAsync(proof)` on the original
source reference. Recovery never issues another native move. It validates the original stored
preparation, complete native root/directory binding and opaque identity, actual namespace spelling,
and known current membership. Source and destination paths and an immutable completion receipt
commit together. Current revisions, local IDs, tombstones, and pending journal IDs/sequences/payloads
are retained; a root already at the target is still a proven member. Unrelated target rows remain
untouched and cause a conflict. A historical receipt cannot regress later state.

Inspect `Outcome`, `Stage`, `NativeMoveObserved`, `DurableProjectionCommitted`, and
`RequiresFullRescan`. A store failure can leave a verified native move pending; retry its original
proof. `Error` preserves the underlying failure and `NativeHResult` is populated only for actual
native observation failures. Metadata guards stabilize the root object but do not freeze descendant
changes or identity writes. Validation around commit detects changed known descendants; local
ordinary children without native placeholder identity require a rescan before dispatch. A detected
post-commit race reports the committed fact and rescan requirement rather than claiming rollback.
An approved external Cloud Files rename can change the namespace while a metadata guard remains
open. Treat the result's native observation as a checked fact at that stage, not a continuing
namespace lock; honor a later conflict/rescan even when `DurableProjectionCommitted` is true.

`MoveToAsync` prepares supported managed directories before its native move. Retrying the original
source/destination after a crash uses the retained indexed preparation and performs only verified
projection. To select a specific externally prepared proof, pass `new CloudMoveOptions(proof)`;
it must name that exact target. Missing-source directory retries without evidence fail closed.
Normal ordinary-directory moves and the existing boolean file-replacement options remain available.
Directory move results and coordination exceptions expose `DirectoryReconciliation`, preserving a
post-commit race or rescan requirement without inventing a failed commit. The bounded intent index
may select a newer preparation but never replaces previous immutable proofs or receipts.
When no explicit preparation exists, recovery may promote the library's retained pre-rename
directory binding and known membership into an independent immutable preparation. It never
manufactures historical evidence from a current target. Legacy directories without either source
of provenance still require explicit preparation while at the original source.

### Retain and recover an external move

Keep the original source reference, exact destination spelling, and original encoded proof in
application-owned durable intent storage outside the sync root. Before an external consumer is
allowed to rename `Docs`:

```csharp
CloudDirectory original = fileSystem.GetDirectory("Docs");
CloudDirectoryMoveProof proof = await original.PrepareMoveAsync(
    fileSystem.Root, "Renamed", cancellationToken);
await File.WriteAllBytesAsync(applicationProofPath, proof.Encode(), cancellationToken);
// The application can now authorize its external consumer to perform that exact rename.
```

After the external consumer finishes, or after restarting against the same store:

```csharp
CloudDirectoryMoveProof retained = CloudDirectoryMoveProof.Decode(
    await File.ReadAllBytesAsync(applicationProofPath, cancellationToken));
CloudDirectoryMoveReconciliationResult result = await fileSystem.GetDirectory("Docs")
    .ReconcileMoveAsync(retained, cancellationToken);

if (result.RequiresFullRescan)
{
    await ReconcileEntireSyncRootAsync(fileSystem, cancellationToken);
}
// Inspect Outcome and native/committed facts before advancing the application intent.
// Reconciliation does not ACK local operations or accept a remote mutation.
```

`applicationProofPath` is an example application intent file, not a CfSharp state-store setting.
Persist it before allowing the native move; do not regenerate a missing original proof from the
destination's present identity. An existing partial projection can also use the original source's
`MoveToAsync` when CfSharp retained sufficient pre-rename provenance. Otherwise perform ownership
reconciliation without relaxing target guards.

| Result | Application action |
| --- | --- |
| `NotMoved` | The proven source remains. Inspect the rescan flag before authorizing the intended move. |
| `Projected`, `AlreadyProjected` | Native movement and durable paths were validated; honor any remaining rescan requirement. |
| `NativeObservedProjectionPending` | Preserve the proof and retry after the original store failure is resolved. |
| `Conflict`, `NotApplicable` | Stop this intent and reconcile ownership; do not overwrite the target or invent provenance. |
| `Busy`, `Canceled`, `Failed` | Inspect Stage, Error, and both native/committed facts before retrying the same intent. |

A post-commit race can have `DurableProjectionCommitted = true` with a conflict/rescan outcome.
`NativeHResult` is nullable because a logical conflict or store exception is not a failed native
call. Log bounded identifiers, stage, outcome, and original error details as appropriate; avoid
dumping complete opaque identities or arbitrary user paths into routine diagnostics.
Proof version 1 bounds encoded paths to 32 KiB of UTF-8, identities to the native 4 KiB limit,
and known-member manifests to 64 MiB. Unknown protocol versions fail closed. Preparation is
metadata-only and does not freeze child content, identity writes, or later namespace moves.
These evidence bounds do not reduce existing directory conversion or ordinary `MoveToAsync`
availability. If a valid native path or known subtree exceeds the recovery format's capacity,
normal source-present operations retain their existing behavior and omit unusable live provenance;
explicit preparation reports `NotSupportedException`. Immutable earlier proofs, receipts, official
item rows, and pending operations remain intact.

Placeholder operations use immutable, kind-specific specifications rather than exposing native
unions and flag combinations directly to application code.

## Observe native object bindings

`CloudItemSnapshot.LocalBinding` exposes the complete volume serial number, sync-root directory
ID, and local object ID for files and directories on capable Windows storage. IDs retain all native
bits and are opaque comparison values. Inspection reads metadata without reading or hydrating
content and retains no handle or protection lifetime. Unsupported reparse targets, missing objects,
and storage without complete IDs return null.

A rename preserves the binding; a replacement can have a different binding even when its path,
remote ID, or placeholder identity matches. A current target observation alone cannot prove that
it belonged to an earlier source. File content confirmation still requires its accepted upload proof.

## Create a placeholder

```csharp
CloudFilePlaceholderSpec report = CloudFilePlaceholderSpec
    .CreateBuilder("report.pdf", "remote-report-42", length: 128_000)
    .WithRemoteRevision("etag-7")
    .WithInitialAvailability(CloudAvailabilityTarget.OnlineOnly)
    .Build();

CloudPlaceholderBatchResult result =
    await fileSystem.Root.CreatePlaceholdersAsync([report], cancellationToken);
result.ThrowIfAnyFailed();
```

The identity envelope is versioned, deterministic, and bounded by the native identity limit. Do not
put credentials or secrets in remote identifiers, revisions, or placeholder metadata.

## Availability is explicit

`OnlineOnly`, `LocallyAvailable`, and `AlwaysAvailable` describe different pin and content states.
CfSharp hydrates before applying the final pin intent when necessary and returns partial-failure
information if a transition cannot complete. A native failure remains available through the managed
exception and the post-failure snapshot.

```csharp
CloudAvailabilityChangeResult transition = await file.SetAvailabilityAsync(
    CloudAvailabilityTarget.LocallyAvailable,
    cancellationToken);

await file.SetInSyncAsync(true, cancellationToken);
```

## Ranges and reversion

Range results are normalized and kept separate for on-disk, provider-validated, and locally modified
content. Placeholder patches can condition a change on the observed USN. Move and delete operations
keep immutable path references and update durable descendants only after the file-system operation
succeeds.

## Conditional in-sync confirmation

`CloudItemSnapshot.LocalBinding` exposes the complete native volume, actual sync-root directory,
and 128-bit file object IDs for ordinary files and placeholders. Inspection reads metadata without
hydrating content. Capture the binding before uploading and retain it with the accepted proof;
re-observing the same path after replacement cannot establish that it is the uploaded object.
Bindings are immutable comparison values, not locks or conditional USN tokens. The existing
nullable 64-bit snapshot IDs retain their previous meaning and availability.

`OperationUsn` preserves the value returned by the Cloud Files mutation. Windows can successfully
convert, update, or set the in-sync state while returning zero. Zero is not a conditional token:
[`CfSetInSyncState`](https://learn.microsoft.com/windows/win32/api/cfapi/nf-cfapi-cfsetinsyncstate)
treats an input USN of zero as an unconditional operation. CfSharp rejects zero in
`CloudInSyncChangeOptions` and `CloudPlaceholderPatch.Builder.WithExpectedUsn`.

Use `CloudItem.ReadUsnAsync` to query the last file-system USN through
[`FSCTL_READ_FILE_USN_DATA`](https://learn.microsoft.com/windows/win32/api/winioctl/ni-winioctl-fsctl_read_file_usn_data).
This attribute-only query supports ordinary files, directories, and Cloud Files placeholders on
NTFS or ReFS. It closes its handle before returning and does not hydrate content, create a journal,
or modify durable state. Native failures preserve their Win32 error as a `CloudFilesException`
HRESULT; a successful zero result remains unusable for a conditional mark.

After remote acceptance, obtain a positive token **before** independently verifying that the local
content still matches the accepted upload. Close verification streams, then pass the same token to
the conditional mark. Reading a fresh token only after hashing could confirm a later, unverified
change. For example, with `acceptedHash` from the uploaded content:

```csharp
long observedUsn = await file.ReadUsnAsync(cancellationToken);
if (observedUsn <= 0)
{
    throw new InvalidOperationException("A positive USN is required for conditional confirmation.");
}

byte[] currentHash;
await using (FileStream stream = File.OpenRead(file.FullPath))
{
    currentHash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken);
}

if (!currentHash.AsSpan().SequenceEqual(acceptedHash))
{
    throw new InvalidOperationException("The local content no longer matches the accepted upload.");
}

await file.SetInSyncAsync(
    true,
    new CloudInSyncChangeOptions(observedUsn),
    cancellationToken);
```

On verification failure, unavailable tokens, or a rejected USN condition, retain pending work and
reconcile. Do not retry with an unconditional mark. A USN is an observation of the same existing
item within the current volume journal; it is not a content hash or a durable identity across
deletion, path replacement, or journal recreation. Providers must coordinate those lifecycle
events separately. The operation lease serializes CfSharp calls, not external writers.

Reading a positive file-system USN does not guarantee that the installed Cloud Files platform will
accept it. Some Windows builds reject even an unchanged, verified token with `0x80070179`
(`ERROR_CLOUD_FILE_NOT_IN_SYNC`). CfSharp preserves this native failure. Verify successful
conditional confirmation on the target platform before relying on it in a provider's acceptance
tests; the read API alone does not establish that capability.

## Protected uploaded-content confirmation

Use `CloudFile.ConfirmUploadedContentAsync` when a remote service has accepted a complete content
proof, the root's actual registered `InSyncPolicy` is exactly `None`, and the installed platform
does not provide usable conditional in-sync tokens. Capture
`LocalBinding` before uploading, compute the digest of the uploaded bytes, authenticate the remote
acceptance, and durably retain that proof in the application. Close upload streams and mappings
before requesting protection.

This API proves content and identity, not remotely accepted timestamps or attributes. CFAPI
exclusive protection does not exclude every metadata writer. A root using `TrackAll`, any
individual tracking flag, or `PreserveForSyncEngine` returns `NotApplicable` at `Verify` before
reading, preparation, marking, or durable projection. Each verification queries the current
registration through the same protected file handle; configured or previously observed policy
values cannot authorize confirmation. Failed registration queries retain their native errors.
The registration builder still defaults to `TrackAll`; this operation never changes a root's policy.

A provider whose product contract permits content-only native sync state can explicitly choose
`.WithInSyncPolicy(CloudInSyncPolicy.None)` when registering, then verify
`CloudSyncRoot.Open(rootPath).GetInfo().InSyncPolicy`. It must align any Shell registration and
handle existing roots through its own coordinated migration. Do not silently downgrade a root
whose product contract includes tracked metadata. With `None`, data writes still clear native
in-sync; detecting and synchronizing metadata changes remains the application's responsibility.
Quiesce confirmations before registering, updating, or unregistering the root. Alternatively,
hold an application-owned root gate through each confirmation and every registration change;
policy observations alone cannot make concurrent re-registration atomic with a native mark.

```csharp
CloudLocalFileBinding uploadedObject = uploadSnapshot.LocalBinding
    ?? throw new InvalidOperationException("A complete upload-time object binding is required.");
CloudPlaceholderIdentity acceptedIdentity = new(itemId, acceptedRemoteId, acceptedRevision);
CloudContentConfirmationRequest proof = new(
    uploadedObject,
    acceptedIdentity,
    uploadedLength,
    uploadedSha256,
    preparation: CloudContentPreparation.ConvertRegularFile);

CloudContentConfirmationResult confirmation =
    await file.ConfirmUploadedContentAsync(proof, cancellationToken);
if (confirmation.Outcome is CloudContentConfirmationOutcome.Confirmed or
    CloudContentConfirmationOutcome.AlreadyConfirmed)
{
    // Advance only the application intent covered by this retained upload proof.
}
```

The default preparation is `None`: the complete opaque placeholder identity must already match.
`ConvertRegularFile` explicitly permits conversion of the verified ordinary file. For revision
replacement, select `ReplacePlaceholderIdentity` and provide the complete previously observed
identity bytes. Both modes also permit replay when the accepted identity is already present.
Replacement currently requires a nonempty previous identity, including when replacement is
explicitly selected. Empty native identities are legal in Windows, but those placeholders are
outside this operation's protected replacement scope. Empty is never interpreted as a wildcard.
The previous nonempty identity can be opaque data from another provider; it need not decode as
a CfSharp identity. The accepted replacement must use `CloudPlaceholderIdentity`.
Preparation checks binding, full length, and SHA-256 before changing identity, then verifies the
whole content again before marking. It never writes file content or changes pin intent.

The implementation uses one exclusive protected owner, without `Foreground`. Every bounded read
holds a reference until native I/O has completed or cancellation has drained. References are
released between segments and reacquired on the same opaque handle. A break discards the attempt;
the library never reopens by path to continue its digest. The final identity, length, object
checks and native mark share one reference. The mark passes a **null USN pointer**, which is
native-unconditional. Its safety relies on the actual `None` policy, exclusive content protection,
and the complete proof. Existing positive-USN conditional APIs keep their contract. This bounded
content-only capability does not resolve platforms rejecting conditional marks; provider acceptance
and recovery tests must still establish the application's complete behavior on the target platform.
CFAPI metadata and mutation calls retain the opaque protected owner; general Win32 metadata
queries and content reads use its borrowed Win32 handle while that reference is held.

Ordinary files and fully local placeholders are supported. Directories, hard links, arbitrary
reparse targets, and partial or online-only content are rejected without hydration. Default reads
use a 1 MiB buffer, a 250 ms reference budget, and a ten-minute total deadline; immutable request
options can adjust finite budgets.
The total deadline includes waiting for the library's item lease. Cancellation or expiry while
waiting returns an uncommitted receipt at the `Open` stage.
If a valid reference's parent later becomes an unsupported reparse point, admission returns
`NotApplicable` at `Open` without preparation, marking, or projection. Expected admission I/O
failures return `Failed` or `Busy` with native error details. Invalid request arguments and calls
on an owner that has not started or is already disposed still throw their documented exceptions.
Pending cancellation is requested with `CancelIoEx` and drained before buffers, events, or
references are freed. Driver cancellation drain and synchronous native
calls may exceed a budget; expired budgets stop subsequent work and marking.
Budgets are checked after synchronous read completion and after each reference. A preparation
that succeeds after its budget retains its mutation receipt and stops further verification;
a final mark that has already succeeded retains its commit fact even if that call ran late.

Competing native opens can wait for a reference to be released or fail with a sharing violation,
depending on Windows and the file's current state. A reference budget is not a promise that every
external open will queue successfully. Integration coverage includes fully written files whose
queued writers break the attempt, and zero-extended files whose rejected writers retry after the
protected owner closes. Neither path continues an old digest after a break.

| Outcome | Caller action |
| --- | --- |
| `Confirmed`, `AlreadyConfirmed` | Complete only the retained accepted-content intent. |
| `ContentMismatch`, `IdentityMismatch`, `LocalObjectMismatch` | Retain intent and reconcile; replacements do not inherit it. |
| `NotFullyLocal`, `NotApplicable` | Choose an explicit application workflow; confirmation does not download content. |
| `Busy`, `ProtectionLost` | Retry the entire proof after the competing operation finishes. |
| `Canceled`, `DeadlineExceeded`, `Failed` | Inspect stage, error, and preparation facts; do not assume identity preparation was rolled back. |
| `NativeAppliedProjectionPending` | Retain the same proof and replay to repair durable identity projection. |
| `ProjectionConflict` | Reconcile the observed complete native identity and durable row; preserve the historical receipt and inspect whether the database committed. |

The receipt owns its proof and distinguishes `NativeIdentityPrepared`, `NativeApplied`,
`NativeConfirmationVerified`, and `DurableProjectionCommitted`. Actual preparation and mark
HRESULTs are retained when those calls succeed; errors preserve operation and HRESULT.
`NativeStage` and `NativeError` preserve the native phase when `ProjectionError` reports another
failure. `Error` contains both in an `AggregateException` if both phases fail. The copied
`ObservedPlaceholderIdentity` records the complete last projection observation, independently of
the accepted proof and the durable row. A zero
`PreparationUsn` remains an observation. Already-in-sync files still undergo complete proof
verification, and an idempotent replay need not issue another mark.

The total deadline uses one monotonic origin from API entry through lease admission, scheduling,
opening, reads, preparation, and the final pre-mark check. Delayed cancellation timer callbacks
do not extend it. A synchronous native call may return late; an already successful preparation
or mark retains its historical receipt, while expired budgets prevent starting further work.

Protected work starts asynchronously and yields between segments without capturing the caller's
synchronization context or task scheduler. Synchronous owner disposal can cancel and drain a
confirmation without pumping the UI queue. Prefer asynchronous disposal to keep the UI responsive.

Native mutation and SQLite are separate commits. Protection is released before opening the
projection transaction. A metadata-only no-delete guard verifies that the durable identity is
projected onto the same object. It allows native identity updates through other writable handles.
The complete identity and object binding are checked before the transaction, immediately before
commit, and after commit. A detected change returns `ProjectionConflict` with an error, including
when the row already committed. These checks do not eliminate every race between native and SQLite
operations, including an uncoordinated update after the final observation.
If preparation succeeds but later verification or marking fails,
its receipt remains explicit and projection is attempted; do not acknowledge content merely
because identity preparation or its projection succeeded. After native confirmation, caller
cancellation cannot erase the commit fact or automatically mark the object not-in-sync.

Retain the proof across process crashes and replay it after reopening the file system. Replays
recheck object, identity, and complete content before repairing the accepted revision. The store
contains existing identity coordination metadata, not content hashes, upload receipts, or remote
business data. Confirmation does not acknowledge journal operations, local-change events, rescan
generations, or unrelated echo entries. Later writes remain native not-in-sync; the receipt's
`ObservedSynchronizationState` is a later observation and never forces the old state back onto
the file. Same-path library operations serialize through existing leases; other paths can progress.

Use `file.UpdatePlaceholderAsync` for identity updates through the same `CloudFileSystem` owner;
its existing item coordinator serializes with confirmation through the projection transaction.
Identity writers using raw CFAPI or another owner must join an application-owned per-item gate.
Hold that gate from before confirmation until it returns, and acquire the same gate for each
external identity mutation and its durable reconciliation. Content-only writers do not need this
identity gate. For example, with a shared `SemaphoreSlim identityGate` for this file:

```csharp
await identityGate.WaitAsync(cancellationToken);
try
{
    confirmation = await file.ConfirmUploadedContentAsync(proof, cancellationToken);
}
finally
{
    identityGate.Release();
}

// Every identity writer must use that same gate. Prefer the coordinated public patch:
await identityGate.WaitAsync(cancellationToken);
try
{
    await file.UpdatePlaceholderAsync(CloudPlaceholderPatch.CreateBuilder()
        .WithIdentity(nextIdentity).WithInSyncState(false).Build(), cancellationToken);
}
finally
{
    identityGate.Release();
}
```

The gate belongs to the application and must cover all of its identity-writing paths; it is not
an operating-system identity lock. Do not hold a `CloudItemLease` while invoking confirmation or
another item operation on the same path, because those operations acquire the same item lease.

The manual `eng/run-soak.ps1 -DurationMinutes 30` gate runs the existing feed/dispatcher soak
alongside a separate native confirmation soak for the same duration. Confirmation cycles include
success, cancellation during segmented reads, an independent writer causing real protection
loss, SQLite projection failure, and complete-proof retry. Its `confirmation-soak.json` samples
process handles, GC-reported pinned objects, private bytes, and retained managed bytes after
draining work and collecting garbage. Normal CI and the 24-round regression are separate from
this sustained resource evidence; use the manual x64/ARM64 jobs before claiming a soak passed.

## Provider responsibility

CfSharp coordinates the Windows demand callback and durable state; the application supplies content
bytes and remote transport. A provider must be prepared for retries, cancellation, and a callback
that is replayed after a process restart.
