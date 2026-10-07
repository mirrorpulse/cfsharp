# SQLite state store

`CfSharp.Storage.Sqlite` is the official durable implementation of the core state contracts. The
application supplies its absolute path and owns its backup and retention policy.

## Choose a safe location

The database must be outside the managed sync root and must not contain file content, credentials,
or provider-specific business data. A per-account location under a protected application-data
directory is a practical default:

```text
C:\ProgramData\ExampleProvider\Accounts\account-42\cfsharp.db
```

One database is bound to one sync root and one active CfSharp owner. Do not open the same database
from two unrelated provider sessions.

## Transaction semantics

The provider uses transactions, foreign keys, WAL mode, bounded lock waits, and durable checkpoints.
Dispose an uncommitted transaction to roll back. Windows namespace operations and SQLite transactions
cannot be one physical transaction; if the namespace succeeds and the store fails, the managed
exception identifies the coordination boundary so the application can reconcile.

## Backup and recovery

Treat the database as coordination state that must be backed up according to the account's policy.
After a crash, reopen the same path and let durable checkpoints, tombstones, and journal state drive
replay. Do not repair the file by deleting the WAL or by replacing the database with an empty file.

## Custom stores

Applications that need another database can implement `ICloudStateStoreFactory`,
`ICloudStateStore`, and `ICloudStateTransaction`. Preserve the same commit, rollback, ownership,
checkpoint, and retry semantics; the high-level API does not require SQLite.

### Optional bounded journal paging

SQLite implements `ICloudOperationJournalPaging` on each transaction's `Operations` repository.
Capture `GetHighWaterSequenceAsync()` once, then call `ReadPageAsync(after, through, limit)` with
an exclusive lower bound, the captured inclusive upper bound, and a limit from 1 through 4096.
Use `LastScannedSequence` directly as the next lower bound. The sequence index bounds both the
query and its memory use; one extra row determines `HasMore` without advancing past that row.
Later enqueues stay outside the scan, and concurrent acknowledgements may leave sequence gaps or
an empty next page. A terminal empty page returns the upper bound and never waits.

Pages own no reader or transaction and do not acknowledge operations or update item revisions.
The legacy `ICloudOperationJournal` contract is unchanged: custom stores may opt into paging on
their repository, preserving monotonic enqueue sequences and transactional read semantics.

### Schema 6 recovery fence

Schema 6 preserves the existing table layout and data while fencing directory object provenance,
move preparation, and completion-receipt recovery. It includes schema 5's remote placeholder
creation and observation-reconciliation fence. Versions 0 through 5 upgrade in place, retaining
item IDs, revisions, tombstones, journal sequences and payloads, suppressions, and checkpoints.
Older SQLite packages reject version 6 before accessing its recovery metadata. Do not downgrade
the schema number manually. Back up state before upgrading and use matching core and SQLite packages.

Custom stores must enforce the same protocol boundary when opening persistent state with older
runtimes. Preserve CfSharp-owned checkpoint names and values transactionally, including immutable
preparations and receipts; never treat them as disposable caches or infer native ownership from an
item path. The schema fence changes protocol compatibility, not the replaceable core interfaces.
