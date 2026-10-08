# Local change feed

The local change feed turns Windows directory notifications into an explicit, bounded application
queue. It is a coordination aid, not a lossless replacement for reconciliation.

Disposal stops notification production and drains accepted events while the state store remains
open, including queue-tail renames during owner shutdown. If draining times out or fails, the feed
persists a full-rescan marker before canceling its worker. Persisting that fence can wait for an
active store transaction beyond the worker's `ShutdownTimeout`. If fence persistence fails, disposal
reports the failure, leaves the worker uncanceled, and retains the owner's store for a disposal retry;
the retry must complete the drain and persist the fence before the store is closed.

## Start one feed per sync root

```csharp
CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed();
await feed.StartAsync(cancellationToken);

CloudLocalChangeBatch batch = await feed.ReadBatchAsync(cancellationToken);
if (batch.RequiresFullRescan)
{
    await ReconcileEntireSyncRootAsync(fileSystem, cancellationToken);
    await feed.AcknowledgeFullRescanAsync(cancellationToken);
}
else
{
    await UploadLocalChangesAsync(batch.Changes, cancellationToken);
    await feed.AcknowledgeAsync(
        batch.Changes.Select(change => change.OperationId),
        cancellationToken);
}
```

The feed normalizes paths relative to the sync root, pairs renames, and persists the ordered
journal with its watcher checkpoint in one transaction. Acknowledgement advances durable progress
only after the application has accepted the batch.

## Scan a finite backlog without acknowledging its head

Use typed scans when some pending changes cannot currently be dispatched. A scan captures the
highest pending sequence once; each page uses an exclusive lower bound and that inclusive upper
bound. Reads never ACK a deferred row, wait for a new event, or extend the scan with later writes.

```csharp
CloudLocalChangeScan scan = await feed.BeginScanAsync(cancellationToken);
long after = 0;
while (true)
{
    CloudLocalChangePage page = await feed.ReadPageAsync(scan, after, 4, cancellationToken);
    if (page.RequiresFullRescan)
    {
        // Stop dispatch. Replay pending remote creations, reconcile, explicitly acknowledge
        // the rescan, then begin a new scan from zero.
        break;
    }

    IReadOnlyList<Guid> acceptedIds = await DispatchEligibleChangesAsync(
        page.Changes, cancellationToken);
    // This application method returns only IDs whose meaning it durably accepted.
    await feed.AcknowledgeAsync(acceptedIds, cancellationToken);
    after = page.LastScannedSequence;
    if (!page.HasMore)
    {
        break;
    }
}
```

The page limit is 1 through 4096 and is independent of the legacy `BatchSize` (default 64).
Use `LastScannedSequence` directly, without adding one. ACKs between pages can leave sequence
gaps and can make the next page empty. Empty terminal pages return `ThroughSequence` immediately.
Restarting the feed or runtime invalidates old scans; create a new scan from zero against the same
durable store. `StoreScope` identifies the running feed lifetime, not a persisted database identity.
Scans and pages retain no transaction, reader, native handle, or file content.

Custom stores opt in by implementing `ICloudOperationJournalPaging` on `Operations`; unsupported
stores throw `NotSupportedException` for scans and retain the existing batch API. Invalid or foreign
journal payloads throw `CloudLocalChangeJournalException` with the original operation ID and
sequence, leaving the row pending. Consumers do not need private payload parsing or a copied queue.
Scheduling across roots and preserving dependencies between changes remain application concerns.
Keep a dependency view across pages before dispatching dependent moves/deletes. A deferred root
does not have to be ACKed to reach an independent later root. Completing this finite scan does not
accept deferred operations: begin the next cycle at zero, preserving those original IDs. Later writes
belong to the next captured boundary. Multiple consumers must coordinate dispatch and ACK ownership.

## Rescan is a normal state

A buffer overflow, watcher error, or ambiguous rename is reported as `RequiresFullRescan`. Stop
assuming that notifications are complete, enumerate the materialized local tree, reconcile it, and
acknowledge the rescan explicitly. Recursive operations never follow links or remote children.

Scans and legacy batches both withhold dispatchable changes while recovery is required, including
when a pending backlog already exists. Pending remote creation intents and their uncertain watcher
observations are checked in the same transaction as each page. Replay creation recovery before
acknowledging reconciliation. A loss received while the watcher worker waits for the store also
blocks pages immediately. Reading a page never clears recovery markers.

An overflow/error or a full-rescan acknowledgement invalidates previously captured scans even
after the durable marker is cleared. Begin a new scan after recovery. If a new loss races
`AcknowledgeFullRescanAsync`, it either rejects acknowledgement with `InvalidOperationException`
or remains observable as a new rescan condition; finish reconciliation before retrying.

## Directory rename projection

Directory renames use retained library-captured native bindings and historical membership to
validate the target. Paths of the complete known subtree, its immutable move receipt, the new
formal Move operation, and the feed checkpoint commit together. Current revisions, tombstones,
and pending operations keep their original values. An old operation's paths and observation time
describe its historical event; projecting today's paths does not rewrite or acknowledge it.

Missing evidence, an unrelated target, unknown membership, or a delayed rename whose object has
already moved again retains an unresolved formal Move with a nullable item association and requires
full reconciliation. The feed never deletes an unrelated destination row to make a rename fit.
Explicit full-rescan acknowledgement clears the reconciliation fence while leaving those original
operations pending. Dispatch and acknowledge them only after their meaning has been accepted.
Native changes racing the transaction can require reconciliation even after its projection committed;
the committed operation retains its original ID and sequence. Native notifications remain advisory.

For an existing partial projection, use the original source directory's `MoveToAsync` or the
prepared-proof recovery described in [placeholders](placeholders.md). A matching path or remote ID
alone cannot establish historical native ownership.

## Provider echoes

Provider-originated writes can be wrapped by `SuppressProviderEchoAsync` so they do not become
uploads. Hydration, pinning, and availability transitions are not local upload operations by
themselves.

### Acknowledging uploaded snapshots

Each observed change has its own durable operation identifier, including repeated notifications for the same path. Acknowledge only the identifiers whose snapshots have completed uploading. An acknowledgement does not cover subsequent observations. Consumers must tolerate redundant uploads rather than assume pending events are coalesced.
