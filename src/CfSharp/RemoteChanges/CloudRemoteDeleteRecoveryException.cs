namespace CfSharp;

/// <summary>Reports an original remote Delete whose evidence requires explicit reconciliation.</summary>
/// <remarks>
/// This Windows-only operation error is returned in a failed remote entry with RequiresRetry set.
/// It does not certify native completion, create a user conflict, or advance the safe batch prefix.
/// Retain the unchanged batch and durable store. Inspect the namespace and pending local work before
/// retrying; a legacy or unproven deletion may require application/operator reconciliation rather
/// than another automatic attempt. Properties are immutable and safe for concurrent reads. This
/// exception owns no native resources. Storage and native failures retain their original types.
/// </remarks>
public sealed class CloudRemoteDeleteRecoveryException : IOException
{
    internal CloudRemoteDeleteRecoveryException(string batchId, int entryIndex, string changeId, string stage)
        : base($"Remote batch '{batchId}' entry {entryIndex} requires Delete reconciliation at {stage}; " +
            "retain the original batch and evidence without advancing its cursor.")
    {
        BatchId = batchId;
        EntryIndex = entryIndex;
        ChangeId = changeId;
    }

    /// <summary>Gets the immutable original batch identifier, for durable correlation.</summary>
    public string BatchId { get; }

    /// <summary>Gets the zero-based original entry ordinal; earlier committed entries remain safe.</summary>
    public int EntryIndex { get; }

    /// <summary>Gets the original provider-stable change identifier without interpreting its value.</summary>
    public string ChangeId { get; }
}
