using System.Security.Cryptography;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonePolicyConfirmsContentWithoutRewritingMetadata(bool hidden)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        byte[] content = "accepted content"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudPlaceholderIdentity accepted = new(Guid.NewGuid(), "content-only-object", "revision-2");
        await fixture.File.ConvertToPlaceholderAsync(accepted);
        CloudItemSnapshot uploaded = await fixture.File.InspectAsync();
        CloudContentConfirmationRequest proof = new(uploaded.LocalBinding!, accepted, content.Length, SHA256.HashData(content));
        if (hidden)
        {
            File.SetAttributes(fixture.File.FullPath, File.GetAttributes(fixture.File.FullPath) | FileAttributes.Hidden);
        }
        else
        {
            File.SetLastWriteTimeUtc(fixture.File.FullPath, uploaded.LastWriteTime!.Value.UtcDateTime.AddMinutes(5));
        }

        CloudItemSnapshot changed = await fixture.File.InspectAsync();
        CloudContentConfirmationResult confirmation = await fixture.File.ConfirmUploadedContentAsync(proof);
        Assert.Equal(CloudContentConfirmationOutcome.Confirmed, confirmation.Outcome);
        Assert.True(confirmation.NativeApplied);
        Assert.True(confirmation.NativeConfirmationVerified);
        Assert.True(confirmation.DurableProjectionCommitted);
        CloudItemSnapshot after = await fixture.File.InspectAsync();
        Assert.Equal(CloudSynchronizationState.InSync, after.SynchronizationState);
        Assert.Equal(changed.LastWriteTime, after.LastWriteTime);
        Assert.Equal(changed.Attributes, after.Attributes);
        Assert.Equal(CloudContentConfirmationOutcome.AlreadyConfirmed,
            (await fixture.File.ConfirmUploadedContentAsync(proof)).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContentProofCannotClearTrackedMetadataChanges(bool hidden)
    {
        await using Fixture fixture = await Fixture.StartAsync(inSyncPolicy: CloudInSyncPolicy.TrackAll);
        byte[] content = "accepted content"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudPlaceholderIdentity accepted = new(Guid.NewGuid(), "metadata-object", "revision-2");
        await fixture.File.ConvertToPlaceholderAsync(accepted);
        await fixture.File.SetInSyncAsync(true);
        CloudItemSnapshot uploaded = await fixture.File.InspectAsync();
        CloudContentConfirmationRequest proof = new(uploaded.LocalBinding!, accepted, content.Length, SHA256.HashData(content));
        if (hidden)
        {
            File.SetAttributes(fixture.File.FullPath, File.GetAttributes(fixture.File.FullPath) | FileAttributes.Hidden);
        }
        else
        {
            File.SetLastWriteTimeUtc(fixture.File.FullPath, uploaded.LastWriteTime!.Value.UtcDateTime.AddMinutes(5));
        }

        CloudItemSnapshot changed = await fixture.File.InspectAsync();
        Assert.Equal(CloudSynchronizationState.NotInSync, changed.SynchronizationState);
        CloudContentConfirmationResult rejected = await fixture.File.ConfirmUploadedContentAsync(proof);
        AssertPolicyRejected(rejected);
        CloudItemSnapshot after = await fixture.File.InspectAsync();
        Assert.Equal(CloudSynchronizationState.NotInSync, after.SynchronizationState);
        Assert.Equal(changed.LastWriteTime, after.LastWriteTime);
        Assert.Equal(changed.Attributes, after.Attributes);
        Assert.Equal(changed.PlaceholderIdentity.ToArray(), after.PlaceholderIdentity.ToArray());
        await fixture.RestartAsync();
        AssertPolicyRejected(await fixture.File.ConfirmUploadedContentAsync(proof));
        Assert.Equal(CloudSynchronizationState.NotInSync, (await fixture.File.InspectAsync()).SynchronizationState);
    }

    [Theory]
    [InlineData(CloudContentPreparation.None)]
    [InlineData(CloudContentPreparation.ConvertRegularFile)]
    [InlineData(CloudContentPreparation.ReplacePlaceholderIdentity)]
    public async Task TrackedRootRejectsBeforePreparationOrProjection(CloudContentPreparation preparation)
    {
        int commits = 0;
        FaultFactory? store = null;
        await using Fixture fixture = await Fixture.StartAsync(path => store = new FaultFactory(path), CloudInSyncPolicy.TrackAll);
        byte[] content = "accepted content"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudPlaceholderIdentity accepted = new(Guid.NewGuid(), "policy-object", "revision-2");
        byte[] previous = [];
        if (preparation != CloudContentPreparation.ConvertRegularFile)
        {
            CloudPlaceholderIdentity initial = preparation == CloudContentPreparation.None
                ? accepted : new(accepted.ItemId, accepted.RemoteId, "revision-1");
            await fixture.File.ConvertToPlaceholderAsync(initial);
            previous = preparation == CloudContentPreparation.ReplacePlaceholderIdentity ? initial.Encode() : [];
        }

        CloudItemSnapshot before = await fixture.File.InspectAsync();
        CloudContentConfirmationRequest proof = new(before.LocalBinding!, accepted, content.Length,
            SHA256.HashData(content), preparation, previous);
        store!.BeforeCommit = () => { commits++; return ValueTask.CompletedTask; };
        AssertPolicyRejected(await fixture.File.ConfirmUploadedContentAsync(proof));
        Assert.Equal(0, commits);
        store.BeforeCommit = null;
        CloudItemSnapshot after = await fixture.File.InspectAsync();
        Assert.Equal(before.IsPlaceholder, after.IsPlaceholder);
        Assert.Equal(before.PlaceholderIdentity.ToArray(), after.PlaceholderIdentity.ToArray());
        Assert.Equal(before.SynchronizationState, after.SynchronizationState);
        Assert.Equal(content, await File.ReadAllBytesAsync(fixture.File.FullPath));
    }

    [Fact]
    public async Task ReplaysUseActualPolicyInsteadOfTheOriginalRegistrationOptions()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        Assert.Equal(CloudInSyncPolicy.None, CloudSyncRoot.Open(fixture.Root).GetInfo().InSyncPolicy);
        byte[] content = "accepted content"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudLocalFileBinding binding = (await fixture.File.InspectAsync()).LocalBinding!;
        CloudContentConfirmationRequest proof = Proof(binding, content, CloudContentPreparation.ConvertRegularFile);
        Assert.Equal(CloudContentConfirmationOutcome.Confirmed, (await fixture.File.ConfirmUploadedContentAsync(proof)).Outcome);

        async Task UpdatePolicyAsync(CloudInSyncPolicy policy)
        {
            await fixture.System.DisposeAsync();
            CloudSyncRoot.Register(fixture.Root,
                SyncRootRegistrationOptions.CreateBuilder(fixture.Registration.ProviderName, fixture.Registration.ProviderVersion)
                    .WithProviderId(fixture.Registration.ProviderId).WithHydrationPolicy(CloudHydrationPolicy.Progressive)
                    .WithInSyncPolicy(policy).WithExistingRegistrationUpdate().Build());
            await fixture.RestartAsync();
            Assert.Equal(policy, CloudSyncRoot.Open(fixture.Root).GetInfo().InSyncPolicy);
        }

        // Disconnect and drain before re-registration, then reopen the existing registration.
        // The original options stay None, so they cannot authorize this retained proof.
        await UpdatePolicyAsync(CloudInSyncPolicy.TrackAll);
        Assert.Equal(CloudInSyncPolicy.None, fixture.Registration.InSyncPolicy);
        AssertPolicyRejected(await fixture.File.ConfirmUploadedContentAsync(proof));
        await fixture.RestartAsync();
        AssertPolicyRejected(await fixture.File.ConfirmUploadedContentAsync(proof));
        await UpdatePolicyAsync(CloudInSyncPolicy.None);
        Assert.Equal(CloudContentConfirmationOutcome.AlreadyConfirmed,
            (await fixture.File.ConfirmUploadedContentAsync(proof)).Outcome);
        await using (FileStream writer = new(fixture.File.FullPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
        {
            await writer.WriteAsync("X"u8.ToArray());
        }
        Assert.Equal(CloudSynchronizationState.NotInSync, (await fixture.File.InspectAsync()).SynchronizationState);
        Assert.Equal(CloudContentConfirmationOutcome.ContentMismatch, (await fixture.File.ConfirmUploadedContentAsync(proof)).Outcome);
        Assert.Equal(CloudSynchronizationState.NotInSync, (await fixture.File.InspectAsync()).SynchronizationState);
    }

    private static void AssertPolicyRejected(CloudContentConfirmationResult result)
    {
        Assert.Equal(CloudContentConfirmationOutcome.NotApplicable, result.Outcome);
        Assert.Equal(CloudContentConfirmationStage.Verify, result.Stage);
        Assert.False(result.NativeIdentityPrepared);
        Assert.False(result.NativeApplied);
        Assert.False(result.NativeConfirmationVerified);
        Assert.False(result.DurableProjectionCommitted);
        Assert.Null(result.PreparationHResult);
        Assert.Null(result.NativeMarkHResult);
        Assert.Equal(0, result.BytesVerified);
        Assert.Equal(0, result.SegmentsRead);
    }
}
