using System.Runtime.Versioning;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class CloudDirectoryMoveTests
{
    [Fact]
    public async Task CreationAndIdentityMutationRetainBindingsWithoutRewritingPreparedProofs()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return;
        }

        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudDirectory directory = root.FileSystem.GetDirectory("Docs");
        CloudDirectoryMoveProof proof = await directory.PrepareMoveAsync(root.FileSystem.Root, "Moved");
        CloudDirectoryProvenance before;
        await using (ICloudStateTransaction transaction = await root.Store.BeginTransactionAsync())
        {
            before = CloudDirectoryProvenance.Decode((await transaction.Checkpoints.GetAsync(
                CloudDirectoryProvenance.BindingName(proof.RootItemId)))!.Value);
            Assert.Equal(proof.ExpectedBinding, before.Binding);
            Assert.Equal(proof.ExpectedPlaceholderIdentity.ToArray(), before.Identity);
            Assert.Equal(proof.StoreScope, before.StoreScope);
            using BinaryReader reader = new(new MemoryStream((await transaction.Checkpoints.GetAsync(
                CloudDirectoryProvenance.MembersName(proof.RootItemId)))!.Value.ToArray()));
            Assert.Equal(1, reader.ReadInt32());
            Assert.Equal(before.EvidenceId, CloudDirectoryEvidenceCodec.ReadGuid(reader));
            Assert.Equal(2, reader.ReadInt32());
        }

        CloudPlaceholderIdentity replacement = new(root.DirectoryIdentity.ItemId, "directory", "new-revision");
        await directory.UpdatePlaceholderAsync(CloudPlaceholderPatch.CreateBuilder().WithIdentity(replacement).Build());
        await using (ICloudStateTransaction transaction = await root.Store.BeginTransactionAsync())
        {
            CloudDirectoryProvenance after = CloudDirectoryProvenance.Decode((await transaction.Checkpoints.GetAsync(
                CloudDirectoryProvenance.BindingName(proof.RootItemId)))!.Value);
            Assert.Equal(before.Binding, after.Binding);
            Assert.Equal(replacement.Encode(), after.Identity);
            Assert.NotEqual(before.EvidenceId, after.EvidenceId);
            Assert.Equal(proof.Encode(), (await transaction.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ProofName(proof.ProofId)))!.Value.ToArray());
        }

        await directory.UpdatePlaceholderAsync(CloudPlaceholderPatch.CreateBuilder().WithIdentityRemoval().Build());
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        Assert.Null(await verify.Checkpoints.GetAsync(CloudDirectoryProvenance.BindingName(proof.RootItemId)));
        Assert.NotNull(await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ProofName(proof.ProofId)));
    }

    [Fact]
    public async Task DirectoryConversionAndRevertRetainOnlyLiveProvenance()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return;
        }

        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudDirectory directory = root.FileSystem.GetDirectory("Ordinary");
        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Create("converted");
        await directory.ConvertToPlaceholderAsync(identity);
        CloudItemSnapshot snapshot = await directory.InspectAsync();
        await using (ICloudStateTransaction transaction = await root.Store.BeginTransactionAsync())
        {
            CloudDirectoryProvenance provenance = CloudDirectoryProvenance.Decode((await transaction.Checkpoints.GetAsync(
                CloudDirectoryProvenance.BindingName(identity.ItemId)))!.Value);
            Assert.Equal(snapshot.LocalBinding, provenance.Binding);
            Assert.Equal(identity.Encode(), provenance.Identity);
        }

        await directory.RevertToRegularItemAsync();
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        Assert.Null(await verify.Checkpoints.GetAsync(CloudDirectoryProvenance.BindingName(identity.ItemId)));
        Assert.Null(await verify.Checkpoints.GetAsync(CloudDirectoryProvenance.MembersName(identity.ItemId)));
    }
}
