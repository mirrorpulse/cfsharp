namespace CfSharp.Tests.Persistence;

public sealed class RemoteMoveIntentTests
{
    [Fact]
    public void PreparationRoundTripsOriginalIdentityAndProjectedBoundary()
    {
        (RemoteMoveIntent intent, CloudRemoteChangeBatch batch) = CreateIntent();
        RemoteMoveIntent recovered = RemoteMoveIntent.Decode(intent.Encode());
        Assert.Equal(intent.Encode(), recovered.Encode());
        Assert.True(recovered.Matches(batch, 0, CloudRemoteApplyOptions.Default));
        Assert.Equal("v1", recovered.Original.RemoteRevision);
        Assert.Equal("native-v0", CloudPlaceholderIdentity.Decode(recovered.Object.Identity).RemoteRevision);
        Assert.Equal(intent.Object.Binding, recovered.Object.Binding);
        RemoteMoveIntent projected = recovered with { ProjectedAtTicks = DateTimeOffset.UtcNow.UtcTicks, ProjectedUsn = 456 };
        Assert.Equal(projected.Encode(), RemoteMoveIntent.Decode(projected.Encode()).Encode());
    }

    [Fact]
    public void PreparationBindsBatchEntryAndPolicyButAllowsDifferentPerCallLimit()
    {
        (RemoteMoveIntent intent, CloudRemoteChangeBatch batch) = CreateIntent();
        Assert.False(intent.Matches(batch, 1, CloudRemoteApplyOptions.Default));
        Assert.False(intent.Matches(new("another-batch", batch.InitialCursor, batch.Changes, batch.FinalCursor), 0, CloudRemoteApplyOptions.Default));
        Assert.False(intent.Matches(batch, 0, new() { SuppressLocalEcho = false }));
        Assert.False(intent.Matches(batch, 0, new() { PreserveUnsynchronizedLocalContent = false }));
        Assert.False(intent.Matches(batch, 0, new() { EchoSuppressionLifetime = TimeSpan.FromMinutes(1) }));
        Assert.False(intent.Matches(batch, 0, new() { EchoSuppressionObservationCount = 2 }));
        Assert.True(intent.Matches(batch, 0, new() { MaximumEntries = 1 }));
    }

    [Fact]
    public void TruncatedFutureAndTrailingPreparationDataAreRejected()
    {
        (RemoteMoveIntent intent, _) = CreateIntent();
        byte[] value = intent.Encode();
        for (int length = 0; length < value.Length; length++)
        {
            Assert.Throws<InvalidDataException>(() => RemoteMoveIntent.Decode(value.AsMemory(0, length)));
        }

        byte[] trailing = [.. value, 0];
        Assert.Throws<InvalidDataException>(() => RemoteMoveIntent.Decode(trailing));
        value[0] = 2;
        Assert.Throws<InvalidDataException>(() => RemoteMoveIntent.Decode(value));
    }

    [Fact]
    public void LocalOperationFingerprintPreservesAcknowledgementIdentityAndDetectsChangedWork()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid item = Guid.NewGuid();
        CloudOperationJournalEntry first = new(Guid.NewGuid(), CloudStateOperationKind.ContentUpdate, item, [1], now, sequence: 1);
        CloudOperationJournalEntry second = new(Guid.NewGuid(), CloudStateOperationKind.MetadataUpdate, item, [2], now, sequence: 2);
        Assert.Equal(RemoteNamespaceEvidence.FingerprintOperations([first, second]), RemoteNamespaceEvidence.FingerprintOperations([second, first]));
        CloudOperationJournalEntry edited = new(first.OperationId, first.Kind, item, [3], now, sequence: 1);
        Assert.NotEqual(RemoteNamespaceEvidence.FingerprintOperations([first]), RemoteNamespaceEvidence.FingerprintOperations([edited]));
        Assert.NotEqual(RemoteNamespaceEvidence.FingerprintOperations([first]), RemoteNamespaceEvidence.FingerprintOperations([]));
    }

    private static (RemoteMoveIntent, CloudRemoteChangeBatch) CreateIntent()
    {
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "remote", "native-v0");
        CloudRemoteChange move = new("move", CloudRemoteChangeKind.Move, "remote", "v2", CloudItemKind.File,
            "right/file.bin", identity.ItemId, previousRemoteRevision: "v1", previousRelativePath: "left/file.bin");
        CloudRemoteChangeBatch batch = new("batch", ReadOnlyMemory<byte>.Empty, [move], new byte[] { 1 });
        CloudLocalFileBinding binding = new(123, Guid.NewGuid(), Guid.NewGuid());
        CloudItemState original = new(identity.ItemId, "remote", move.PreviousRelativePath!, CloudItemKind.File, "v1", 23, false, DateTimeOffset.UtcNow);
        RemoteNamespaceObject file = new(original.RelativePath, binding, identity.Encode(), 100, 200, 512);
        RemoteNamespaceObject root = new(string.Empty, binding, [], 50, 60, 0);
        return (new(Guid.NewGuid(), "C:\\root", batch.BatchId, 0, batch.Fingerprint.ToArray(), move.RelativePath,
            true, true, TimeSpan.FromMinutes(2).Ticks, 4, original, file, [root], null, true,
            RemoteNamespaceEvidence.FingerprintOperations([]), null), batch);
    }
}
