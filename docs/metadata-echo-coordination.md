# Metadata echo coordination

Independent Windows attribute notifications identify a native object, not its writer or the
particular mutation that caused an observation. Native USNs, in-sync state, attribute equality,
timestamps, paths and notification counts do not establish that an observation belongs to a
successful remote metadata apply. A real local edit can occur within the same echo window.

## Default: retain and reconcile

`CloudRemoteApplyOptions.AttributeEchoPolicy` defaults to
`CloudRemoteAttributeEchoPolicy.RequireReconciliation`. When `SuppressLocalEcho` is enabled,
remote `MetadataUpdate` retains a durable attribution preparation before native mutation.
Possible metadata echoes become unbound `MetadataUpdate` journal originals and set
`RequiresFullRescan`. They are not delivered as dispatchable, bound local edits.

Complete an application-level reconciliation before calling `AcknowledgeFullRescanAsync`.
That call clears the fence; it does not erase observations, clear their operation IDs, assign an
item, or acknowledge them. A new finite scan exposes the same IDs and sequences with `ItemId = null`.
The application must decide and individually acknowledge those observations after reconciliation.
An immediately following genuine native Hidden write is retained too; it cannot be silently
consumed by the remote operation's path/kind ticket.

Reconciliation retains one attribution marker per native generation and historical path. A late
attribute callback requires reconciliation again instead of becoming a purported new local edit.
`EchoSuppressionLifetime` limits discard authority, not uncertainty: a timeout cannot prove the
origin of a queued observation. Subsequent attributes for that generation therefore also require
reconciliation unless a fresh, valid caller-coordinated declaration covers them. A successor with
its own official and native identity does not inherit that generation's authority.

## Explicit caller-coordinated attribution

Select `CallerCoordinated` only when the application already controls every writer capable of
generating attribute observations for the affected objects. Account for notifications already
queued before application, deferred writes through existing handles, rename-related attribute
notifications and other processes. Maintain that guarantee from before the remote call until at
least `EchoSuppressionLifetime` after it returns successfully. CfSharp does not establish this
exclusion through file sharing, an oplock or a kernel driver.

```csharp
// Only after the application has established the complete writer-coordination guarantee:
var options = new CloudRemoteApplyOptions
{
    AttributeEchoPolicy = CloudRemoteAttributeEchoPolicy.CallerCoordinated,
};
CloudRemoteApplyResult result = await fileSystem.ApplyRemoteChangesAsync(batch, options, token);
// Keep application-owned coordination in force for the complete declared lifetime after return.
```

This is an explicit provenance declaration, not a heuristic about a quiet filesystem. Do not
enable it in a root with uncontrolled applications or native writers. A local attribute write
inside this declared ownership window violates the caller's precondition. Applications that
need to allow such writes must use the default reconciliation policy instead.

Only a successful complete public call activates authorization in the current file-system owner.
Each suppressed attribute observation must also match the original volume, sync-root file ID,
complete native generation and official item identity. Suppression follows identity validation
and durable observation staging. No attribute or ancestor handle is retained across journal waits.
Generic data/name notifications and independent DACL notifications never redeem this authorization.
The observation count option for other remote operation kinds does not limit this declared window.
Authority expires using a monotonic clock, so a wall-clock adjustment cannot extend the promise.
Once the window expires, the receipt requires reconciliation before the next protected write.
Entries confirmed in a call stopped by `MaximumEntries` also require reconciliation. Such a
partial call never activates coordinated authority, even for entries that were applied successfully.
A subsequent metadata operation or in-place upsert supersedes earlier permission for the affected
object before writing attributes. An upsert with attribution history under the default policy
retains uncertain observations for reconciliation. Default upserts without attribution history
continue to use their usual echo handling.

Setting `SuppressLocalEcho = false` installs an explicit passthrough policy for remote metadata
updates and in-place file and directory upserts. Provider-generated and genuine observations use
the ordinary bound metadata path after confirmation. A failed or incomplete operation still
retains uncertainty. Earlier unresolved observations still require reconciliation.

## Failure, restart and reconciliation

Preparation is durable before mutation. Completion shares the remote outcome transaction, and
confirmation is written separately once that transaction has returned successfully. An exception
from the outcome commit leaves the entry unconfirmed under every policy, even if the database
already contains the outcome. The retained preparation requires reconciliation and protects
genuine writes. A different native generation cannot inherit authorization at the same name.
Completed observations staged before a crash recover through the existing metadata-validation
receipt protocol with the same original operation IDs and sequences.
The running feed reads retained preparations directly. A failed preparation, an unreconciled
default-policy result, or an unreconciled coordinated receipt without current authorization
requires reconciliation before any corresponding watcher notification arrives. A protected file
or directory upsert or metadata update checks this same evidence for the affected native object
and reports a metadata conflict before writing. This applies with an empty journal and with
unbound observations. Reconciling the object and acknowledging the full rescan allows a later
protected update. The retained originals still need their individual acknowledgements.

Passthrough becomes effective for each confirmed entry, including entries applied in a partial
call limited by `MaximumEntries`. A later failure leaves those confirmed entries intact. If an
outcome commit throws after persisting, replay can report `AlreadyApplied`, while the feed still
requires reconciliation for that entry. If only the confirmation write fails, the known apply
outcome stays successful and any unconfirmed receipt remains conservative. Older receipts without
confirmation also require reconciliation.

A new file-system owner does not recover the former caller's promise about external writers.
Unreconciled attribution evidence fences the feed on restart, even if the original call used
`CallerCoordinated`. Reconcile explicitly; no journal original is automatically acknowledged.
Reconciliation acknowledgement is rejected while a matching remote call is still active.
Already-applied batch replay acknowledges earlier work; it does not renew the old authorization.

## Upgrade and custom stores

This changes default remote metadata behavior from bound local echoes or path-based discard to
explicit reconciliation. Existing journal rows remain queryable and individually acknowledgeable;
they are not rewritten or deleted during this upgrade. Legacy metadata path/kind suppression
tickets are ignored. Calling `SuppressProviderEchoAsync` with `MetadataUpdate` now reports
`NotSupportedException`; use the explicit remote attribute policy instead.

Custom stores continue to use the existing transactional checkpoint and journal capabilities.
They must preserve the new versioned metadata checkpoints exactly, atomically publish completion
with the remote outcome, and retain the existing journal association-update capability required
by metadata recovery. There is no new database schema, provider callback or native component.
The supported Windows and CPU architecture boundaries are unchanged.
