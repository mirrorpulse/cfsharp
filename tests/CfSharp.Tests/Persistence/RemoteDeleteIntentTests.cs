namespace CfSharp.Tests.Persistence;

public sealed class RemoteDeleteIntentTests
{
    [Fact]
    public void SealedInventoryRoundTripsOriginalAndIndependentCompletionStages()
    {
        (RemoteDeleteIntent intent, CloudRemoteChangeBatch batch) = Create();
        RemoteDeleteIntent decoded = RemoteDeleteIntent.Decode(intent.Encode());
        Assert.Equal(intent.Encode(), decoded.Encode());
        Assert.True(decoded.Matches(batch, 0, CloudRemoteApplyOptions.Default));
        Assert.Equal("v1", decoded.Members[0].Original!.RemoteRevision);
        Assert.Equal("native-v0", CloudPlaceholderIdentity.Decode(decoded.Members[0].Object.Identity).RemoteRevision);
        RemoteDeleteMember member = intent.Members[0] with { DeletedAtTicks = DateTimeOffset.UtcNow.UtcTicks };
        RemoteDeleteIntent native = intent with { Members = [member] };
        Assert.Equal(native.Encode(), RemoteDeleteIntent.Decode(native.Encode()).Encode());
        RemoteDeleteIntent projected = intent with { Members = [member with { ProjectedAtTicks = DateTimeOffset.UtcNow.UtcTicks }] };
        Assert.Equal(projected.Encode(), RemoteDeleteIntent.Decode(projected.Encode()).Encode());
    }

    [Fact]
    public void OriginalOperationPolicyAndCursorAreBoundWithoutBindingPerCallLimit()
    {
        (RemoteDeleteIntent intent, CloudRemoteChangeBatch batch) = Create();
        Assert.False(intent.Matches(batch, 1, CloudRemoteApplyOptions.Default));
        Assert.False(intent.Matches(new("another", batch.InitialCursor, batch.Changes, batch.FinalCursor), 0, CloudRemoteApplyOptions.Default));
        Assert.False(intent.Matches(new(batch.BatchId, new byte[] { 5 }, batch.Changes, batch.FinalCursor), 0, CloudRemoteApplyOptions.Default));
        Assert.False(intent.Matches(batch, 0, new() { SuppressLocalEcho = false }));
        Assert.False(intent.Matches(batch, 0, new() { PreserveUnsynchronizedLocalContent = false }));
        Assert.False(intent.Matches(batch, 0, new() { EchoSuppressionObservationCount = 2 }));
        Assert.False(intent.Matches(batch, 0, new() { EchoSuppressionLifetime = TimeSpan.FromMinutes(1) }));
        Assert.True(intent.Matches(batch, 0, new() { MaximumEntries = 1 }));
    }

    [Fact]
    public void TruncatedFutureOrTrailingEvidenceIsRejectedWithoutInterpretingAnOutcome()
    {
        (RemoteDeleteIntent intent, _) = Create();
        byte[] value = intent.Encode();
        for (int length = 0; length < value.Length; length++)
        {
            Assert.Throws<InvalidDataException>(() => RemoteDeleteIntent.Decode(value.AsMemory(0, length)));
        }

        Assert.Throws<InvalidDataException>(() => RemoteDeleteIntent.Decode(new byte[RemoteDeleteIntent.MaximumBytes + 1]));
        Assert.Throws<InvalidDataException>(() => RemoteDeleteIntent.Decode(new byte[] { 0 }));
        Assert.Throws<InvalidDataException>(() => RemoteDeleteIntent.Decode(new byte[] { 1 }.Concat(value).ToArray()));
        Assert.Throws<InvalidDataException>(() => RemoteDeleteIntent.Decode(value.Concat(new byte[] { 0 }).ToArray()));
        value[0] = 2;
        Assert.Throws<InvalidDataException>(() => RemoteDeleteIntent.Decode(value));
    }

    [Fact]
    public void ProjectionWithoutNativeReceiptAndInventoryOutsideOriginalScopeAreRejected()
    {
        (RemoteDeleteIntent intent, _) = Create();
        RemoteDeleteMember member = intent.Members[0];
        Assert.Throws<InvalidDataException>(() => RemoteDeleteIntent.Decode((intent with
        {
            Members = [member with { ProjectedAtTicks = DateTimeOffset.UtcNow.UtcTicks }],
        }).Encode()));
        RemoteDeleteMember outside = member with { Object = member.Object with { RelativePath = "outside.bin" } };
        Assert.Throws<InvalidDataException>(() => RemoteDeleteIntent.Decode((intent with { Members = [outside, member] }).Encode()));
    }

    private static (RemoteDeleteIntent, CloudRemoteChangeBatch) Create()
    {
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "remote", "native-v0");
        CloudRemoteChange change = new("delete", CloudRemoteChangeKind.Delete, "remote", "v2", CloudItemKind.File,
            "Docs/file.bin", identity.ItemId, previousRemoteRevision: "v1");
        CloudRemoteChangeBatch batch = new("batch", ReadOnlyMemory<byte>.Empty, [change], new byte[] { 1 });
        CloudLocalFileBinding binding = new(123, Guid.NewGuid(), Guid.NewGuid());
        CloudItemState state = new(identity.ItemId, "remote", change.RelativePath, CloudItemKind.File, "v1", 123, false, DateTimeOffset.UtcNow);
        RemoteNamespaceObject item = new(change.RelativePath, binding, identity.Encode(), 100, 200, 512);
        RemoteNamespaceObject root = new(string.Empty, binding, [], 50, 60, 0);
        return (new(Guid.NewGuid(), "C:\\root", batch.BatchId, 0, batch.Fingerprint.ToArray(), true, true,
            true, CloudRemoteApplyOptions.Default.EchoSuppressionLifetime.Ticks, CloudRemoteApplyOptions.Default.EchoSuppressionObservationCount,
            [root], [new(CloudItemKind.File, item, state, RemoteNamespaceEvidence.FingerprintOperations([]), null, null)]), batch);
    }
}
