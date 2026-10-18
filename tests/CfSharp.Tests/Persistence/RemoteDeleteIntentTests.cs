namespace CfSharp.Tests.Persistence;

public sealed class RemoteDeleteIntentTests
{
    [Fact]
    public void OriginalDurablePathRoundTripsIndependentlyOfNativeCasing()
    {
        (RemoteDeleteIntent intent, _) = Create();
        RemoteDeleteMember member = intent.Members[0];
        CloudItemState original = member.Original!;
        member = member with
        {
            Original = new(original.ItemId, original.RemoteId, "docs\\file.bin", original.Kind,
                original.RemoteRevision, original.LocalFileId, original.IsTombstone, original.UpdatedAt),
        };
        RemoteDeleteIntent decoded = RemoteDeleteIntent.Decode((intent with { Members = [member] }).Encode());
        Assert.Equal("docs\\file.bin", decoded.Members[0].Original!.RelativePath);
        Assert.Equal("Docs\\file.bin", decoded.Members[0].Object.RelativePath);
        Assert.Equal((intent with { Members = [member] }).Encode(), decoded.Encode());
    }

    [Fact]
    public void PreparationReservesBothCompletionTimestampsForEveryMember()
    {
        (RemoteDeleteIntent original, _) = Create();
        RemoteDeleteMember root = original.Members[0];
        CloudItemState state = root.Original!;
        const int count = 2048;
        RemoteDeleteMember[] members = Enumerable.Range(0, count).Select(index => root with
        {
            Object = root.Object with { RelativePath = $"Docs\\child-{index:D4}", Identity = [] },
            Original = null,
        }).ToArray();
        members[^1] = root with
        {
            Kind = CloudItemKind.Directory,
            Object = root.Object with { RelativePath = "Docs", Identity = [] },
            Original = new(state.ItemId, state.RemoteId, "Docs", CloudItemKind.Directory,
                state.RemoteRevision, state.LocalFileId, false, state.UpdatedAt),
        };
        RemoteDeleteIntent intent = original with { Members = members };
        int reserved = count * 2 * sizeof(long);
        int padding = RemoteDeleteIntent.MaximumBytes - reserved - intent.Encode().Length;
        for (int index = 0; index < members.Length; index++)
        {
            int length = Math.Min(padding, SyncRootRegistrationOptions.MaxFileIdentityLength);
            members[index] = members[index] with { Object = members[index].Object with { Identity = new byte[length] } };
            padding -= length;
        }

        Assert.Equal(0, padding);
        Assert.Equal(RemoteDeleteIntent.MaximumBytes - reserved, intent.Encode().Length);
        Assert.Equal(intent.Encode(), RemoteDeleteIntent.Decode(intent.Encode()).Encode());
        long now = DateTimeOffset.UtcNow.UtcTicks;
        RemoteDeleteIntent native = intent with { Members = members.Select(member => member with { DeletedAtTicks = now }).ToArray() };
        Assert.Equal(RemoteDeleteIntent.MaximumBytes - (count * sizeof(long)), native.Encode().Length);
        RemoteDeleteIntent completed = native with { Members = native.Members.Select(member => member with { ProjectedAtTicks = now }).ToArray() };
        Assert.Equal(RemoteDeleteIntent.MaximumBytes, completed.Encode().Length);
        Assert.Equal(completed.Encode(), RemoteDeleteIntent.Decode(completed.Encode()).Encode());

        // One extra byte is rejected in preparation, while there have been no native effects.
        RemoteDeleteMember[] oversized = members.ToArray();
        oversized[^1] = oversized[^1] with { Object = oversized[^1].Object with { Identity = new byte[oversized[^1].Object.Identity.Length + 1] } };
        Assert.Throws<IOException>(() => (intent with { Members = oversized }).Encode());
    }

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
        value[0] = 1;
        Assert.Throws<InvalidDataException>(() => RemoteDeleteIntent.Decode(value));
        value[0] = 3;
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
        CloudItemState original = member.Original!;
        RemoteDeleteMember mismatched = member with
        {
            Original = new(original.ItemId, original.RemoteId, "elsewhere\\file.bin", original.Kind,
                original.RemoteRevision, original.LocalFileId, false, original.UpdatedAt),
        };
        Assert.Throws<InvalidDataException>(() => RemoteDeleteIntent.Decode((intent with { Members = [mismatched] }).Encode()));
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
