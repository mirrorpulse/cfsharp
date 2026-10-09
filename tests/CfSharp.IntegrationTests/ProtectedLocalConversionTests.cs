using System.Security.AccessControl;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    private static readonly string[] LocalProtectionCompetitors = ["overwrite", "link"];

    [Fact]
    public async Task ProtectedLocalConversionDoesNotApplyDaclAfterHandledProjectionFailure()
    {
        FaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new FaultFactory(path));
        await File.WriteAllTextAsync(fixture.File.FullPath, "original");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        byte[] original = new FileInfo(fixture.File.FullPath).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
        ProjectionFault fault = new();
        CloudProtectedLocalOperationResult result = await fixture.File.RunProtectedLocalOperationAsync(new(binding), async (scope, stop) =>
        {
            FileSystemSecurity descriptor = await scope.ReadAccessDescriptorAsync(stop);
            factory!.NextCommitFault = fault;
            Assert.Same(fault, await Record.ExceptionAsync(() => scope.ConvertToPlaceholderAsync(
                CloudPlaceholderIdentity.Create("local-uncommitted"), stop).AsTask()));
            Assert.NotNull(await Record.ExceptionAsync(() => scope.ApplyAccessDescriptorAsync(descriptor, stop).AsTask()));
        });
        Assert.Equal(CloudProtectedLocalOperationOutcome.NativeAppliedProjectionPending, result.Outcome);
        Assert.Equal(CloudProtectedLocalOperationStage.Projection, result.Stage);
        Assert.Same(fault, result.Error);
        Assert.True(result.CallbackCompleted);
        Assert.True(result.NativeConverted);
        Assert.False(result.DurableProjectionCommitted);
        Assert.False(result.AccessDescriptorApplied);
        Assert.False(result.AccessDescriptorReadBack);
        Assert.Equal(original, new FileInfo(fixture.File.FullPath).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm());
        Assert.Equal("original", await File.ReadAllTextAsync(fixture.File.FullPath));
    }
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 19)]
    [InlineData(true, 0)]
    [InlineData(true, 19)]
    public async Task ProtectedLocalConversionPreservesBytesBindingAndDacl(bool prepareBeforeCallback, int length)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        byte[] content = Enumerable.Range(0, length).Select(index => (byte)index).ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        byte[] originalAccess = new FileInfo(fixture.File.FullPath).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm();
        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Create("local-unuploaded");
        CloudProtectedLocalOperationRequest request = prepareBeforeCallback
            ? CloudProtectedLocalOperationRequest.ForLocalConversion(binding, identity) : new(binding);
        CloudProtectedLocalOperationResult result = await fixture.File.RunProtectedLocalOperationAsync(request, async (scope, token) =>
        {
            if (!prepareBeforeCallback)
            {
                // Existing public conversion is dispatched to the active same-object scope.
                await fixture.File.ConvertToPlaceholderAsync(identity, cancellationToken: token);
            }
            CloudItemSnapshot snapshot = await scope.InspectAsync(token);
            Assert.True(snapshot.IsPlaceholder);
            Assert.Equal(identity.Encode(), snapshot.PlaceholderIdentity.ToArray());
            Assert.Equal(binding, snapshot.LocalBinding);
            Assert.NotEqual(CloudSynchronizationState.InSync, snapshot.SynchronizationState);
            Assert.Null(snapshot.RemoteRevision);
            foreach (string action in LocalProtectionCompetitors)
            {
                using LocalCompetitor competitor = StartLocalCompetitor(fixture.File.FullPath, Path.Combine(fixture.Root, "alias.bin"), action);
                await competitor.StartAsync();
                await competitor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10), token);
                Assert.Equal(action == "link" ? 396 : 32, competitor.ExitCode);
            }
            await Task.Yield();
            Assert.True((await fixture.File.InspectAsync(token)).IsPlaceholder);
        });
        Assert.Equal(CloudProtectedLocalOperationOutcome.Completed, result.Outcome);
        Assert.True(result.NativeIdentityPrepared);
        Assert.True(result.NativeConverted);
        Assert.True(result.DurableProjectionCommitted);
        Assert.Equal(0, result.PreparationHResult);
        Assert.NotNull(result.PreparationUsn);
        Assert.Equal(content, await File.ReadAllBytesAsync(fixture.File.FullPath));
        Assert.Equal(originalAccess, new FileInfo(fixture.File.FullPath).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorBinaryForm());
        Assert.Equal(0, fixture.Provider.Fetches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtectedLocalConversionRecoversOnlyTheOriginalObjectAfterTwoStoreRestarts(bool replace)
    {
        FaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new FaultFactory(path));
        await File.WriteAllTextAsync(fixture.File.FullPath, "unuploaded");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Create("local-retry");
        CloudProtectedLocalOperationRequest request = CloudProtectedLocalOperationRequest.ForLocalConversion(binding, identity);
        ProjectionFault fault = new();
        factory!.NextCommitFault = fault;
        CloudProtectedLocalOperationResult pending = await fixture.File.RunProtectedLocalOperationAsync(request,
            (_, _) => throw new InvalidOperationException("Failed preparation must not invoke application work."));
        Assert.Equal(CloudProtectedLocalOperationOutcome.NativeAppliedProjectionPending, pending.Outcome);
        Assert.Equal(CloudProtectedLocalOperationStage.Projection, pending.Stage);
        Assert.Same(fault, pending.Error);
        Assert.True(pending.NativeConverted);
        Assert.True(pending.NativeIdentityPrepared);
        Assert.False(pending.DurableProjectionCommitted);
        Assert.False(pending.CallbackStarted);
        Assert.True(pending.Snapshot!.IsPlaceholder);
        Assert.Null((await fixture.File.InspectAsync()).ItemId);
        await fixture.RestartAsync();
        await fixture.RestartAsync();
        if (replace)
        {
            File.Move(fixture.File.FullPath, Path.Combine(fixture.Root, "original.bin"));
            await File.WriteAllTextAsync(fixture.File.FullPath, "replacement");
        }
        CloudProtectedLocalOperationResult retried = await fixture.File.RunProtectedLocalOperationAsync(request,
            (_, _) => ValueTask.CompletedTask);
        Assert.Equal(replace ? CloudProtectedLocalOperationOutcome.LocalObjectMismatch : CloudProtectedLocalOperationOutcome.Completed, retried.Outcome);
        Assert.False(retried.NativeConverted);
        Assert.Equal(!replace, retried.DurableProjectionCommitted);
        CloudItemSnapshot after = await fixture.File.InspectAsync();
        Assert.Equal(replace ? null : identity.ItemId, after.ItemId);
        Assert.Null(after.RemoteRevision);
        Assert.NotEqual(CloudSynchronizationState.InSync, after.SynchronizationState);
        Assert.Equal(replace ? "replacement" : "unuploaded", await File.ReadAllTextAsync(fixture.File.FullPath));
        Assert.Equal(0, fixture.Provider.Fetches);
    }

    [Fact]
    public async Task ProtectedLocalConversionReportsARealCommitEvenWhenTransactionDisposalFails()
    {
        FaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new FaultFactory(path));
        await File.WriteAllTextAsync(fixture.File.FullPath, "original");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Create("local-committed");
        ProjectionFault fault = new();
        factory!.BeforeCommit = () =>
        {
            factory.NextDisposeFault = fault;
            return ValueTask.CompletedTask;
        };
        CloudProtectedLocalOperationResult result = await fixture.File.RunProtectedLocalOperationAsync(
            CloudProtectedLocalOperationRequest.ForLocalConversion(binding, identity), (_, _) => ValueTask.CompletedTask);
        Assert.Equal(CloudProtectedLocalOperationOutcome.Failed, result.Outcome);
        Assert.True(result.NativeConverted);
        Assert.True(result.DurableProjectionCommitted);
        Assert.False(result.CallbackStarted);
        Assert.Same(fault, result.Error);
        await fixture.RestartAsync();
        Assert.Equal(identity.ItemId, (await fixture.File.InspectAsync()).ItemId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtectedLocalConversionCancellationRespectsTheNativeBoundary(bool afterNative)
    {
        FaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new FaultFactory(path));
        await File.WriteAllTextAsync(fixture.File.FullPath, "original");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Create("local-cancel");
        using CancellationTokenSource cancellation = new();
        if (afterNative)
        {
            factory!.BeforeCommit = () =>
            {
                cancellation.Cancel();
                return ValueTask.CompletedTask;
            };
        }
        else
        {
            factory!.BeforeTransaction = () =>
            {
                factory.BeforeTransaction = () =>
                {
                    factory.AfterTransactionDispose = cancellation.Cancel;
                    return ValueTask.CompletedTask;
                };
                return ValueTask.CompletedTask;
            };
        }
        CloudProtectedLocalOperationResult result = await fixture.File.RunProtectedLocalOperationAsync(
            CloudProtectedLocalOperationRequest.ForLocalConversion(binding, identity), (_, _) => ValueTask.CompletedTask, cancellation.Token);
        Assert.Equal(CloudProtectedLocalOperationOutcome.Canceled, result.Outcome);
        Assert.Equal(afterNative, result.NativeConverted);
        Assert.Equal(afterNative, result.DurableProjectionCommitted);
        Assert.False(result.CallbackStarted);
        Assert.Equal(afterNative, (await fixture.File.InspectAsync()).IsPlaceholder);
        Assert.Equal("original", await File.ReadAllTextAsync(fixture.File.FullPath));
    }

    [Fact]
    public async Task ProtectedLocalConversionPreservesKnownStateAndPendingWorkAndRejectsRekeying()
    {
        FaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new FaultFactory(path));
        await File.WriteAllTextAsync(fixture.File.FullPath, "pending-local");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Create("known-local");
        DateTimeOffset stamp = DateTimeOffset.UtcNow;
        CloudOperationJournalEntry original;
        await using (ICloudStateTransaction seed = await factory!.Store.BeginTransactionAsync())
        {
            await seed.Items.UpsertAsync(new(identity.ItemId, identity.RemoteId, fixture.File.RelativePath,
                CloudItemKind.File, "previous-acknowledgement", 321, false, stamp));
            original = await seed.Operations.EnqueueAsync(new(Guid.NewGuid(), CloudStateOperationKind.ContentUpdate,
                identity.ItemId, [1, 2, 3], stamp, 3, stamp.AddMinutes(1)));
            await seed.Checkpoints.UpsertAsync(new("local-feed/rescan", [4, 5], stamp));
            await seed.CommitAsync();
        }
        CloudProtectedLocalOperationResult rejected = await fixture.File.RunProtectedLocalOperationAsync(
            CloudProtectedLocalOperationRequest.ForLocalConversion(binding, CloudPlaceholderIdentity.Create("different-local")),
            (_, _) => ValueTask.CompletedTask);
        Assert.Equal(CloudProtectedLocalOperationOutcome.NotApplicable, rejected.Outcome);
        Assert.False(rejected.NativeConverted);
        Assert.False(rejected.CallbackStarted);
        CloudProtectedLocalOperationResult result = await fixture.File.RunProtectedLocalOperationAsync(
            CloudProtectedLocalOperationRequest.ForLocalConversion(binding, identity), (_, _) => ValueTask.CompletedTask);
        Assert.Equal(CloudProtectedLocalOperationOutcome.Completed, result.Outcome);
        await fixture.RestartAsync();
        await using ICloudStateTransaction verify = await factory!.Store.BeginTransactionAsync();
        CloudItemState item = Assert.IsType<CloudItemState>(await verify.Items.GetByItemIdAsync(identity.ItemId));
        Assert.Equal("previous-acknowledgement", item.RemoteRevision);
        Assert.Equal(321, item.LocalFileId);
        CloudOperationJournalEntry retained = Assert.IsType<CloudOperationJournalEntry>(await verify.Operations.GetAsync(original.OperationId));
        Assert.Equal(original.Sequence, retained.Sequence);
        Assert.Equal(original.Payload.ToArray(), retained.Payload.ToArray());
        Assert.Equal(original.CreatedAt, retained.CreatedAt);
        Assert.Equal(original.AttemptCount, retained.AttemptCount);
        Assert.Equal(original.RetryAfter, retained.RetryAfter);
        Assert.Equal(new byte[] { 4, 5 }, (await verify.Checkpoints.GetAsync("local-feed/rescan"))!.Value.ToArray());
        Assert.Equal(0, fixture.Provider.Fetches);
    }
}
