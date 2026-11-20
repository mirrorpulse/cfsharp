# Custom journal migration for metadata recovery

The local change feed now requires `ICloudOperationJournalItemAssociationUpdates` on each
transaction's `Operations` repository. This is an intentional breaking change for custom feed
stores. `StartAsync` checks the capability in its first state transaction, before replaying
validation receipts or opening any notification source. An unsupported repository receives
`NotSupportedException` without a new journal write, lost-event marker or consumed receipt.
Other use of the base journal interface does not acquire this requirement.

The old `ICloudOperationJournal.UpdateAsync` contract promises retry metadata and payload changes.
It never guaranteed that a supplied null `ItemId` would clear an existing association. An attribute
observation can commit before post-commit native validation; after a crash, recovery must be able
to clear that association while preserving the original operation ID, sequence and history.
An undeclared assumption about the old interface would defer incompatibility until recovery.

## Migrate before opening the new feed

1. Stop all old feed owners and retain the complete durable store. Do not delete journal rows,
   validation receipts, or reset sequence counters to make startup succeed.
2. Implement association replacement in the existing transactional `UpdateAsync`. Persist the
   supplied nullable `ItemId`, keep the original operation ID and sequence, and preserve every
   other field supplied by recovery. Reads in that same transaction must see the new association;
   rollback must restore the old row and its receipt together.
3. Only then implement `ICloudOperationJournalItemAssociationUpdates` on the concrete repository
   returned by every transaction. A wrapper must explicitly forward this capability as well as
   the update behavior. For example, a repository that already persists every supplied field can
   change its implemented interface from `ICloudOperationJournal` to the derived capability;
   it does not need a new method or a second journal.
4. Run the shared `JournalUpdateClearsItemAssociationAndPreservesOriginalSequence` store contract.
   Also interrupt an attribute publication before validation, restart, reconcile and acknowledge
   the recovered observation. Verify the same operation ID, sequence, payload, timestamp and
   retry information, with a null association. Cover rollback, repeated startup and paging wrappers.
5. Deploy the migrated store implementation before starting the new feed. Existing receipts are
   recovered transactionally into unbound observations requiring full reconciliation. After
   reconciliation, the original operations remain publicly readable and individually acknowledgeable.

For a SQL journal, updating `item_id = NULL` on the existing row addressed by both operation ID
and assigned sequence is sufficient for the association portion. Verify exactly one row was
updated; preserve the remaining recovery fields. A remove/re-enqueue implementation assigns a
new sequence and is not a valid migration. Stores with immutable association columns must first
migrate their own schema or provide equivalent transactional semantics.

The official SQLite provider already updates the nullable association in place. Its schema and
stored data need no migration; use the matching provider version with the new core library.
Custom stores cannot be upgraded merely by adding the declaration if their update still ignores
`ItemId`: recovery reads back the result, rejects that claim, and retains evidence on rollback.

There is no automatic deletion or acknowledgement of incompatible evidence. If a previous build
already wrote a validation receipt, upgrading the custom repository as above recovers it without
rewriting identifiers, ordering or accepted history. Until that migration is ready, keep the store
offline from this feed version instead of repeatedly attempting startup.
