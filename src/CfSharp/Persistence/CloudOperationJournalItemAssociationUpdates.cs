namespace CfSharp;

/// <summary>Declares that a journal can persist item-association updates without replacing an operation's sequence.</summary>
/// <remarks>
/// <para>
/// Implement this capability on each transaction's <see cref="ICloudStateTransaction.Operations"/>
/// repository only when <see cref="ICloudOperationJournal.UpdateAsync"/> persists the supplied
/// <see cref="CloudOperationJournalEntry.ItemId"/>, including <see langword="null"/>, and subsequent
/// reads in that transaction observe the update. Commit and rollback follow the owning transaction.
/// Clearing the association must preserve the original operation identifier and assigned sequence.
/// Other supplied fields retain the normal update contract; do not remove and re-enqueue the row.
/// </para>
/// <para>
/// The local change feed requires this capability before opening any Windows notification source
/// or replaying metadata validation receipts. Its absence produces <see cref="NotSupportedException"/>
/// before either phase. This is an explicit breaking requirement for custom feed stores; the base
/// journal interface alone continues to guarantee only retry metadata and payload updates.
/// The official SQLite provider implements the capability without a database schema migration.
/// </para>
/// <para>
/// This declaration owns no resources and adds no thread-safety guarantee. The repository remains
/// bound to its single-consumer transaction lifetime. All platform, cancellation and failure behavior
/// of the base repository still applies. Recovery reads back updates before consuming its evidence;
/// an implementation that declares the capability but does not honor it cannot complete recovery.
/// </para>
/// </remarks>
public interface ICloudOperationJournalItemAssociationUpdates : ICloudOperationJournal
{
}
