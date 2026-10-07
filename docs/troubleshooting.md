# Troubleshooting

Start with the operation, Windows build, process architecture, and original native error. CfSharp
preserves native failure details when translating them into managed exceptions.

## The API reports unsupported

Check the Windows build and Cloud Files integration number against [platform support](platform-support.md).
Optional features are capability-gated; an unsupported result is expected on older systems and must
not be replaced with an unbounded retry.

## The provider starts but content does not hydrate

Confirm that:

1. the sync root is registered and the process owns the matching state database;
2. the content provider is connected before demand work is admitted;
3. the source content and state database are outside the sync root;
4. the provider handles cancellation and retries without reusing an expired native callback;
5. the original exception and post-failure snapshot are recorded.

## A local change batch requires a full rescan

Treat `RequiresFullRescan` as a normal recovery path. Reconcile the materialized local tree,
acknowledge the rescan, and only then resume incremental uploads. Do not assume that a watcher
buffer overflow can be repaired by reading the next notification.

## A restart appears to replay work

Replay is safe when the same immutable batch or callback is submitted again. Verify the durable
checkpoint and transaction commit rather than relying on an in-memory token. Duplicate attempts
should converge to one namespace result; do not delete the state database to suppress a retry.

## A deferred journal head hides later changes

Use `BeginScanAsync` and `ReadPageAsync` with the returned finite boundary. Keep deferred IDs
pending, carry dependencies across pages, and ACK only accepted IDs. `NotSupportedException`
means the custom journal lacks optional paging; add that capability rather than guessing an
unbounded read or rotating the queue. A `CloudLocalChangeJournalException` identifies a row whose
payload is not an official local change. Leave it pending and resolve it through its originating
component; consumers should not decode private payloads to bypass this error.

## A directory moved but durable child paths are still old

Retry the original source/destination through `MoveToAsync`, or decode and reconcile the original
prepared proof against the same store. Matching remote IDs, paths, or copied placeholder identity
do not prove that the target is the original native directory. Preserve the target and pending
journal on `Conflict`/`NotApplicable`; complete ownership reconciliation before resuming dispatch.

Report Outcome, Stage, NativeMoveObserved, DurableProjectionCommitted, RequiresFullRescan, proof ID,
and the original Error. A null NativeHResult is expected for logical/store failures. A conflict after
commit can accurately report `DurableProjectionCommitted = true`: the native namespace changed
again after projection. Preserve the receipt and original proof rather than claiming rollback.
Full-rescan acknowledgement clears the fence but does not ACK the unresolved formal move.

## Database locking or corruption concerns

Keep one active owner per sync-root database, allow the bounded lock wait to expire, and preserve
the database plus WAL files for diagnosis. Run the repository's crash/WAL recovery harness and
SQLite integrity checks before attempting manual repair. Never remove a WAL file while its database
may still be open.
