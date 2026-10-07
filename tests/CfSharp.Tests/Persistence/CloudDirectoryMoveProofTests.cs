using System.Buffers.Binary;

namespace CfSharp.Tests.Persistence;

public sealed class CloudDirectoryMoveProofTests
{
    [Fact]
    public void SerializationOwnsCompleteBindingAndIdentityAndRejectsMalformedLengths()
    {
        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Create("remote", "revision");
        byte[] bytes = identity.Encode();
        CloudLocalFileBinding binding = new(0x123456789abcdef0, Guid.NewGuid(), Guid.NewGuid());
        CloudDirectoryMoveProof proof = new(Guid.NewGuid(), Guid.NewGuid(), "Docs/Deep", "Target/Deep", identity.ItemId, binding, bytes);
        bytes[0] = 0;
        byte[] encoded = proof.Encode();
        CloudDirectoryMoveProof decoded = CloudDirectoryMoveProof.Decode(encoded);
        Assert.Equal(encoded, decoded.Encode());
        Assert.Equal(binding, decoded.ExpectedBinding);
        Assert.Equal(identity.Encode(), decoded.ExpectedPlaceholderIdentity.ToArray());
        Assert.Equal("Docs\\Deep", decoded.SourceRelativePath);
        Assert.Equal(proof.ProofId, decoded.ProofId);
        Assert.Equal(proof.StoreScope, decoded.StoreScope);
        Assert.Equal(proof.RootItemId, decoded.RootItemId);
        encoded[0] = 0;
        Assert.Equal(proof.Encode(), decoded.Encode());
        Assert.Throws<InvalidDataException>(() => CloudDirectoryMoveProof.Decode(encoded));
        Assert.Throws<InvalidDataException>(() => CloudDirectoryMoveProof.Decode(new byte[131073]));
        Assert.Throws<InvalidDataException>(() => CloudDirectoryMoveProof.Decode(proof.Encode().AsSpan()[..^1]));
        Assert.Throws<InvalidDataException>(() => CloudDirectoryMoveProof.Decode([.. proof.Encode(), 0]));
        byte[] corrupt = proof.Encode();
        BinaryPrimitives.WriteInt32LittleEndian(corrupt.AsSpan(96, 4), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => CloudDirectoryMoveProof.Decode(corrupt));
        byte[] future = proof.Encode();
        BinaryPrimitives.WriteInt32LittleEndian(future.AsSpan(4, 4), 2);
        Assert.Throws<NotSupportedException>(() => CloudDirectoryMoveProof.Decode(future));
    }

    [Theory]
    [InlineData("Docs", "Docs")]
    [InlineData("Docs", "Docs\\Deep")]
    [InlineData("..\\Docs", "Target")]
    [InlineData("Docs", "C:\\Target")]
    public void ConstructionRejectsUnsafePathShapes(string source, string destination)
    {
        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Create("remote");
        Assert.Throws<ArgumentException>(() => new CloudDirectoryMoveProof(Guid.NewGuid(), Guid.NewGuid(),
            source, destination, identity.ItemId, new CloudLocalFileBinding(1, Guid.NewGuid(), Guid.NewGuid()), identity.Encode()));
    }

    [Fact]
    public async Task PreparationIsImmutableIndependentAndRollsBackWithCallerTransaction()
    {
        await using ICloudStateStore store = await InMemoryCloudStateStoreContractTests.CreateFactoryForTesting()
            .OpenAsync(new CloudStateStoreContext(Path.GetTempPath()));
        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Create("remote");
        CloudItemState root = new(identity.ItemId, "remote", "Docs", CloudItemKind.Directory, "v1", 7, false, DateTimeOffset.UtcNow);
        CloudItemState tombstone = new(Guid.NewGuid(), "gone", "Docs\\gone", CloudItemKind.File, "v2", 8, true, DateTimeOffset.UtcNow);
        CloudDirectoryMoveProof first;
        await using (ICloudStateTransaction transaction = await store.BeginTransactionAsync())
        {
            Guid scope = await CloudDirectoryMoveEvidence.GetScopeAsync(transaction, true, default);
            first = new(Guid.NewGuid(), scope, "Docs", "Moved", root.ItemId,
                new CloudLocalFileBinding(1, Guid.NewGuid(), Guid.NewGuid()), identity.Encode());
            await CloudDirectoryMoveEvidence.PrepareAsync(transaction, first, [root, tombstone], default);
            await transaction.CommitAsync();
        }

        CloudDirectoryMoveProof second = new(Guid.NewGuid(), first.StoreScope, "Docs", "Other", root.ItemId, first.ExpectedBinding, identity.Encode());
        await using (ICloudStateTransaction rollback = await store.BeginTransactionAsync())
        {
            await CloudDirectoryMoveEvidence.PrepareAsync(rollback, second, [root, tombstone], default);
        }

        await using ICloudStateTransaction verify = await store.BeginTransactionAsync();
        Assert.Equal(first.StoreScope, await CloudDirectoryMoveEvidence.GetScopeAsync(verify, false, default));
        Assert.Equal(first.Encode(), (await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ProofName(first.ProofId)))!.Value.ToArray());
        Assert.Null(await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ProofName(second.ProofId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CloudDirectoryMoveEvidence.PrepareAsync(verify, first, [root, tombstone], default).AsTask());
    }
}
