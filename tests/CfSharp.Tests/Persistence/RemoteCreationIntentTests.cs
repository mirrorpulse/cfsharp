namespace CfSharp.Tests.Persistence;

public sealed class RemoteCreationIntentTests
{
    [Fact]
    public void VersionedIntentRetainsExplicitIdentityRootAncestryAndNativeReceipt()
    {
        RemoteCreationIntent intent = CreateIntent();
        RemoteCreationIntent recovered = RemoteCreationIntent.Decode(intent.Encode());
        Assert.Equal(intent.Encode(), recovered.Encode());
        Assert.Equal(intent.Identity.ItemId, recovered.Identity.ItemId);
        Assert.Equal(intent.RootPath, recovered.RootPath);
        Assert.Equal(intent.StoreScope, recovered.StoreScope);
        Assert.Equal(intent.CreatedObject!.Binding, recovered.CreatedObject!.Binding);
        Assert.Equal(intent.Parents![0].Binding, Assert.Single(recovered.Parents!).Binding);
        Assert.True(recovered.Committed);
    }

    [Fact]
    public void LegacyPayloadIsDecodedWithoutInventingMissingProvenance()
    {
        RemoteCreationIntent modern = CreateIntent();
        RemoteCreationIntent legacy = new(modern.RelativePath, modern.Kind, modern.Identity, modern.Fingerprint, false);
        RemoteCreationIntent decoded = RemoteCreationIntent.Decode(legacy.Encode());
        Assert.Equal(legacy.Encode(), decoded.Encode());
        Assert.Null(decoded.Parents);
        Assert.Null(decoded.CreatedObject);
        Assert.Null(decoded.RootPath);
        Assert.Equal(Guid.Empty, decoded.StoreScope);
        Assert.Equal(legacy.Identity.ItemId, decoded.Identity.ItemId);
    }

    [Fact]
    public void TruncationTrailingDataAndFutureVersionsAreRejected()
    {
        byte[] encoded = CreateIntent().Encode();
        for (int length = 0; length < encoded.Length; length++)
        {
            Assert.Throws<InvalidDataException>(() => RemoteCreationIntent.Decode(encoded.AsMemory(0, length)));
        }
        Assert.Throws<InvalidDataException>(() => RemoteCreationIntent.Decode((byte[])[.. encoded, 1]));
        BitConverter.GetBytes(4).CopyTo(encoded, 0);
        Assert.Throws<InvalidDataException>(() => RemoteCreationIntent.Decode(encoded));
    }

    [Fact]
    public void InvalidReceiptIdentityAndMissingAncestryCannotDecodeAsCommitted()
    {
        RemoteCreationIntent intent = CreateIntent();
        Assert.Throws<InvalidDataException>(() => RemoteCreationIntent.Decode((intent with { CreatedObject = null }).Encode()));
        Assert.Throws<InvalidDataException>(() => RemoteCreationIntent.Decode((intent with { Parents = [] }).Encode()));
        Assert.Throws<InvalidDataException>(() => RemoteCreationIntent.Decode((intent with { StoreScope = Guid.Empty }).Encode()));
        Assert.Throws<InvalidDataException>(() => RemoteCreationIntent.Decode((intent with { Resolution = CloudRemoteConflictDecision.KeepRemote }).Encode()));
        Assert.Throws<InvalidDataException>(() => RemoteCreationIntent.Decode((intent with
        {
            CreatedObject = intent.CreatedObject! with { Identity = new CloudPlaceholderIdentity(Guid.NewGuid(), "remote", "v3").Encode() },
        }).Encode()));
    }

    [Fact]
    public void AuthorizedCopyKeepsOriginalFingerprintAndChosenIdentityAcrossDecode()
    {
        RemoteCreationIntent original = CreateIntent();
        CloudRemoteChange change = new("birth", CloudRemoteChangeKind.FileUpsert, original.Identity.RemoteId, "v3",
            CloudItemKind.File, original.RelativePath, original.Identity.ItemId, previousRemoteRevision: "v2", length: 7,
            metadata: CloudPlaceholderMetadata.CreateFileBuilder().Build());
        CloudPlaceholderIdentity copy = new(Guid.NewGuid(), "opaque-copy", "v3");
        RemoteCreationIntent authorized = original with
        {
            RelativePath = "Copies\\item.bin",
            Identity = copy,
            Fingerprint = RemoteCreationIntent.GetFingerprint(change),
            PreserveContent = false,
            Resolution = CloudRemoteConflictDecision.KeepBoth,
            CreatedObject = original.CreatedObject! with { RelativePath = "Copies\\item.bin", Identity = copy.Encode() },
        };
        RemoteCreationIntent decoded = RemoteCreationIntent.Decode(authorized.Encode());
        CloudRemoteChange effective = decoded.EffectiveChange(change);
        Assert.Equal(copy.ItemId, effective.ItemId);
        Assert.Equal(copy.RemoteId, effective.RemoteId);
        Assert.Equal("Copies\\item.bin", effective.RelativePath);
        Assert.Equal(copy.ItemId, decoded.EffectiveChange(change).ItemId);
        Assert.Null(effective.PreviousRemoteRevision);
        Assert.Equal(RemoteCreationIntent.GetFingerprint(change), decoded.Fingerprint);
    }

    [Fact]
    public void PreparationReservesNativeReceiptGrowthWithinTheEncodingBudget()
    {
        RemoteCreationIntent intent = CreateIntent();
        string path = string.Empty;
        List<RemoteNamespaceObject> parents = [];
        for (int index = 0; index < 510; index++)
        {
            parents.Add(new(path, intent.Parents![0].Binding, [], 1, 2, 0));
            path = path.Length == 0 ? new string('a', 31) : path + "\\" + new string('a', 31);
        }
        RemoteCreationIntent large = intent with { Parents = parents };
        Assert.InRange(large.Encode().Length, 1, 4 * 1024 * 1024);
        Assert.Throws<InvalidDataException>(() => (large with { CreatedObject = null, Committed = false }).Encode());
    }

    private static RemoteCreationIntent CreateIntent()
    {
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "opaque-remote", "v3");
        CloudLocalFileBinding parent = new(1, Guid.NewGuid(), Guid.NewGuid());
        RemoteNamespaceObject ancestor = new(string.Empty, parent, [], 100, 200, 0);
        RemoteNamespaceObject item = new("Docs\\item.bin", new(1, parent.SyncRootFileId, Guid.NewGuid()), identity.Encode(), 300, 400, 7);
        CloudRemoteChange change = new("birth", CloudRemoteChangeKind.FileUpsert, identity.RemoteId, "v3",
            CloudItemKind.File, "Docs\\item.bin", identity.ItemId, length: 7, metadata: CloudPlaceholderMetadata.CreateFileBuilder().Build());
        return new(change.RelativePath, change.ItemKind, identity, RemoteCreationIntent.GetFingerprint(change), true,
            Guid.NewGuid(), Path.GetFullPath(Path.GetTempPath()), [ancestor], item);
    }
}
