namespace CfSharp.Tests.Persistence;

public sealed class RemoteDeleteReconciliationReceiptTests
{
    [Fact]
    public void ReceiptRetainsExactDurableMetadataAndRejectsTruncationOrNoncanonicalEncoding()
    {
        RemoteDeleteReconciliationReceipt original = Create();
        byte[] encoded = original.Encode();
        RemoteDeleteReconciliationReceipt decoded = RemoteDeleteReconciliationReceipt.Decode(encoded);
        Assert.Equal("docs/Retired.bin", decoded.Tombstone.RelativePath);
        Assert.Equal(original.Tombstone.UpdatedAt, decoded.Tombstone.UpdatedAt);
        Assert.Equal(original.Tombstone.LocalFileId, decoded.Tombstone.LocalFileId);
        Assert.Equal(encoded, decoded.Encode());
        for (int length = 0; length < encoded.Length; length++)
        {
            Assert.Throws<InvalidDataException>(() => RemoteDeleteReconciliationReceipt.Decode(encoded.AsMemory(0, length)));
        }
        Assert.Throws<InvalidDataException>(() => RemoteDeleteReconciliationReceipt.Decode(encoded.Concat(new byte[] { 0 }).ToArray()));
        byte[] malformedBoolean = encoded.ToArray();
        // The final fixed policy fields precede the length-prefixed cursor.
        malformedBoolean[encoded.Length - original.Cursor.Length - 4 - 4 - 4 - 8 - 1] = 2;
        Assert.Throws<InvalidDataException>(() => RemoteDeleteReconciliationReceipt.Decode(malformedBoolean));
    }

    [Fact]
    public void MetadataBudgetIncludesAllEncodedFieldsBeforeAnyOutcomeCanCommit()
    {
        RemoteDeleteReconciliationReceipt original = Create();
        int overhead = original.Encode().Length - original.Cursor.Length;
        byte[] exact = (original with { Cursor = new byte[RemoteDeleteReconciliationReceipt.MaximumBytes - overhead] }).Encode();
        Assert.Equal(RemoteDeleteReconciliationReceipt.MaximumBytes, exact.Length);
        Assert.Equal(exact, RemoteDeleteReconciliationReceipt.Decode(exact).Encode());
        Assert.Throws<NotSupportedException>(() => (original with
        {
            Cursor = new byte[RemoteDeleteReconciliationReceipt.MaximumBytes - overhead + 1],
        }).Encode());
        Assert.Throws<NotSupportedException>(() => (original with { BatchId = new string('x', 32769) }).Encode());
        Assert.Throws<InvalidDataException>(() => RemoteDeleteReconciliationReceipt.Decode(new byte[RemoteDeleteReconciliationReceipt.MaximumBytes + 1]));
    }

    private static RemoteDeleteReconciliationReceipt Create() => new(Guid.NewGuid(), @"C:\OwnedRoot", "original", 3,
        new byte[32], Guid.NewGuid(), new(Guid.NewGuid(), "remote", "docs/Retired.bin", CloudItemKind.File,
            "v1", long.MaxValue, true, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow,
        true, TimeSpan.FromMinutes(2).Ticks, 4, 256, new byte[] { 5 });
}
