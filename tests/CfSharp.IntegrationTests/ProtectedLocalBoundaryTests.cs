using System.Diagnostics;
using System.Security.AccessControl;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtectedLocalBoundaryShutdownAfterNativePreparationCommitsBeforeStoreClosure(bool ownedFeed)
    {
        FaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new FaultFactory(path));
        await File.WriteAllTextAsync(fixture.File.FullPath, "unuploaded");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Create("local-shutdown-projection");
        CloudOperationJournalEntry original;
        await using (ICloudStateTransaction seed = await factory!.Store.BeginTransactionAsync())
        {
            await seed.Items.UpsertAsync(new(identity.ItemId, identity.RemoteId, fixture.File.RelativePath,
                CloudItemKind.File, null, null, false, DateTimeOffset.UtcNow));
            original = await seed.Operations.EnqueueAsync(new(Guid.NewGuid(), CloudStateOperationKind.ContentUpdate,
                identity.ItemId, [1, 2], DateTimeOffset.UtcNow));
            await seed.CommitAsync();
        }
        if (ownedFeed)
        {
            await fixture.System.CreateLocalChangeFeed().StartAsync();
        }
        Task? shutdown = null;
        factory!.BeforeCommit = async () =>
        {
            // No public facade reentry: inspect only the native metadata while the current
            // transaction commits. A feed notification can also reach this hook, but only
            // after the same native conversion; both owners must retain the admitted store.
            Assert.True(CloudItemInspector.Inspect(fixture.File.FullPath, CloudItemKind.File).PlaceholderState
                .HasFlag(CloudPlaceholderState.Placeholder));
            shutdown = Task.Run(async () => await fixture.System.DisposeAsync());
            long started = Stopwatch.GetTimestamp();
            while (fixture.System.LifecycleState != CloudFileSystemLifecycleState.Stopping)
            {
                Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10));
                await Task.Delay(10);
            }
            Assert.Throws<ObjectDisposedException>(() => fixture.System.GetFile("rejected.bin"));
        };
        CloudProtectedLocalOperationResult result = await fixture.File.RunProtectedLocalOperationAsync(
            CloudProtectedLocalOperationRequest.ForLocalConversion(binding, identity),
            (_, _) => throw new InvalidOperationException("Stopping must prevent permission callback entry."));
        Assert.Equal(CloudProtectedLocalOperationOutcome.Canceled, result.Outcome);
        Assert.True(result.NativeConverted);
        Assert.True(result.NativeIdentityPrepared);
        Assert.True(result.DurableProjectionCommitted);
        Assert.False(result.CallbackStarted);
        Assert.True(result.Drained);
        Assert.NotNull(shutdown);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(15));
        await fixture.RestartAsync();
        await fixture.RestartAsync();
        CloudItemSnapshot recovered = await fixture.File.InspectAsync();
        Assert.Equal(binding, recovered.LocalBinding);
        Assert.Equal(identity.ItemId, recovered.ItemId);
        Assert.Equal(identity.Encode(), recovered.PlaceholderIdentity.ToArray());
        Assert.NotEqual(CloudSynchronizationState.InSync, recovered.SynchronizationState);
        Assert.Null(recovered.RemoteRevision);
        Assert.Equal("unuploaded", await File.ReadAllTextAsync(fixture.File.FullPath));
        await using ICloudStateTransaction verify = await factory!.Store.BeginTransactionAsync();
        CloudOperationJournalEntry retained = Assert.IsType<CloudOperationJournalEntry>(await verify.Operations.GetAsync(original.OperationId));
        Assert.Equal(original.Sequence, retained.Sequence);
        Assert.Equal(original.Payload.ToArray(), retained.Payload.ToArray());
        Assert.Equal(original.CreatedAt, retained.CreatedAt);
        Assert.Equal(0, fixture.Provider.Fetches);
    }

    [Theory]
    [InlineData(CloudInSyncPolicy.None)]
    [InlineData(CloudInSyncPolicy.TrackAll)]
    public async Task ProtectedLocalBoundaryDoesNotIntroduceAcceptanceUnderEitherSyncPolicy(CloudInSyncPolicy policy)
    {
        await using Fixture fixture = await Fixture.StartAsync(inSyncPolicy: policy);
        await File.WriteAllTextAsync(fixture.File.FullPath, "unuploaded");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudProtectedLocalOperationResult result = await fixture.File.RunProtectedLocalOperationAsync(
            CloudProtectedLocalOperationRequest.ForLocalConversion(binding, CloudPlaceholderIdentity.Create("policy-local")),
            async (scope, stop) => await scope.ApplyAccessDescriptorAsync(await scope.ReadAccessDescriptorAsync(stop), stop));
        Assert.Equal(CloudProtectedLocalOperationOutcome.Completed, result.Outcome);
        Assert.Null(result.Snapshot!.RemoteRevision);
        Assert.Equal(CloudSynchronizationState.NotInSync, (await fixture.File.InspectAsync()).SynchronizationState);
        Assert.Equal(0, fixture.Provider.Fetches);
    }

    [Fact]
    public async Task ProtectedLocalBoundaryChecksActualHardlinkPolicyBeforeConversionOrPermissions()
    {
        await using Fixture fixture = await Fixture.StartAsync(allowHardLinks: true);
        await File.WriteAllTextAsync(fixture.File.FullPath, "ordinary");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudProtectedLocalOperationResult result = await fixture.File.RunProtectedLocalOperationAsync(
            CloudProtectedLocalOperationRequest.ForLocalConversion(binding, CloudPlaceholderIdentity.Create("disallowed-request")),
            (_, _) => throw new InvalidOperationException("The actual allowed-hardlink policy must prevent callback entry."));
        Assert.Equal(CloudProtectedLocalOperationOutcome.Unsupported, result.Outcome);
        Assert.False(result.NativeConverted);
        Assert.False(result.CallbackStarted);
        Assert.False((await fixture.File.InspectAsync()).IsPlaceholder);
        Assert.Equal("ordinary", await File.ReadAllTextAsync(fixture.File.FullPath));
    }

    [Fact]
    public async Task ProtectedLocalBoundaryPreservesTheFirstFailureWhenApplicationContinuesInspecting()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        await File.WriteAllTextAsync(fixture.File.FullPath, "ordinary");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        Exception? first = null;
        CloudProtectedLocalOperationResult result = await fixture.File.RunProtectedLocalOperationAsync(new(binding), async (scope, stop) =>
        {
            first = await Record.ExceptionAsync(() => scope.ApplyAccessDescriptorAsync(new FileSecurity(), stop).AsTask());
            Assert.NotNull(first);
            Assert.Equal(binding, (await scope.InspectAsync(stop)).LocalBinding);
        });
        Assert.Equal(CloudProtectedLocalOperationOutcome.Unsupported, result.Outcome);
        Assert.Equal(CloudProtectedLocalOperationStage.AccessDescriptor, result.Stage);
        Assert.True(result.CallbackCompleted);
        Assert.Same(first, result.Error);
        Assert.False(result.AccessDescriptorApplied);
        Assert.Equal("ordinary", await File.ReadAllTextAsync(fixture.File.FullPath));
    }

    [Fact]
    public async Task ProtectedLocalBoundaryRejectsForeignReparseDataAndWrongRootBinding()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        await File.WriteAllTextAsync(fixture.File.FullPath, "original");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudLocalFileBinding foreignRoot = new(binding.VolumeSerialNumber, Guid.NewGuid(), binding.LocalFileId);
        CloudProtectedLocalOperationResult wrongRoot = await fixture.File.RunProtectedLocalOperationAsync(new(foreignRoot),
            (_, _) => throw new InvalidOperationException("A wrong root binding cannot be admitted."));
        Assert.Equal(CloudProtectedLocalOperationOutcome.LocalObjectMismatch, wrongRoot.Outcome);
        string original = Path.Combine(fixture.Root, "original.bin");
        File.Move(fixture.File.FullPath, original);
        // The final reparse component is opened as reparse data, never as its target.
        File.CreateSymbolicLink(fixture.File.FullPath, original);
        CloudProtectedLocalOperationResult reparse = await fixture.File.RunProtectedLocalOperationAsync(new(binding),
            (_, _) => throw new InvalidOperationException("Foreign reparse data cannot enter a protected callback."));
        Assert.False(reparse.CallbackStarted);
        Assert.True(reparse.Outcome is CloudProtectedLocalOperationOutcome.LocalObjectMismatch or CloudProtectedLocalOperationOutcome.NotApplicable);
        Assert.False(reparse.AccessDescriptorApplied);
        Assert.Equal("original", await File.ReadAllTextAsync(original));
        File.Delete(fixture.File.FullPath);
    }

    [Fact]
    public async Task ProtectedLocalBoundaryPublicRenameAndReplacementCompetitorsCannotChangeTheOriginal()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        await File.WriteAllTextAsync(fixture.File.FullPath, "original");
        string replacement = Path.Combine(fixture.Root, "replacement.bin");
        await File.WriteAllTextAsync(replacement, "replacement");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudProtectedLocalOperationResult result = await fixture.File.RunProtectedLocalOperationAsync(new(binding), async (scope, stop) =>
        {
            foreach (string action in NamespaceProtectionCompetitors)
            {
                string target = action == "rename" ? Path.Combine(fixture.Root, "renamed.bin") : replacement;
                using LocalCompetitor competitor = StartLocalCompetitor(fixture.File.FullPath, target, action);
                await competitor.StartAsync();
                await competitor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10), stop);
                Assert.True(competitor.ExitCode == 32 || action == "replace" && competitor.ExitCode == 5,
                    $"Unexpected namespace competitor result: {action}={competitor.ExitCode}");
                Assert.Equal(binding, (await scope.InspectAsync(stop)).LocalBinding);
            }
        });
        Assert.True(result.Outcome == CloudProtectedLocalOperationOutcome.Completed, result.Error?.ToString());
        Assert.Equal("original", await File.ReadAllTextAsync(fixture.File.FullPath));
        Assert.Equal("replacement", await File.ReadAllTextAsync(replacement));
    }

    private static readonly string[] NamespaceProtectionCompetitors = ["rename", "replace"];

    [Fact]
    public async Task ProtectedLocalBoundaryRootMetadataNeedsNoPopulationOrSourceRead()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        CloudDirectory root = fixture.System.Root;
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await root.InspectAsync()).LocalBinding);
        CloudProtectedLocalOperationResult result = await root.RunProtectedLocalOperationAsync(
            new(binding, CloudProtectedLocalOperationMode.DirectoryMetadata), async (scope, stop) =>
            {
                await scope.ApplyAccessDescriptorAsync(await scope.ReadAccessDescriptorAsync(stop), stop);
                Assert.Equal(binding, (await root.InspectAsync(stop)).LocalBinding);
            });
        Assert.True(result.Outcome == CloudProtectedLocalOperationOutcome.Completed, result.Error?.ToString());
        Assert.True(result.AccessDescriptorApplied);
        Assert.Equal(0, fixture.Provider.Fetches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtectedLocalBoundaryRetainsTheParentNamespaceOfItsOriginalObject(bool directory)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        string parent = Path.Combine(fixture.Root, "Parent");
        Directory.CreateDirectory(parent);
        CloudItem item;
        if (directory)
        {
            Directory.CreateDirectory(Path.Combine(parent, "Child"));
            item = fixture.System.GetDirectory("Parent\\Child");
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(parent, "child.bin"), "original");
            item = fixture.System.GetFile("Parent\\child.bin");
        }
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await item.InspectAsync()).LocalBinding);
        CloudProtectedLocalOperationResult result = await item.RunProtectedLocalOperationAsync(
            new(binding, directory ? CloudProtectedLocalOperationMode.DirectoryMetadata : CloudProtectedLocalOperationMode.ExclusiveFile),
            async (scope, stop) =>
            {
                Assert.Throws<IOException>(() => Directory.Move(parent, parent + "-renamed"));
                Assert.Equal(binding, (await scope.InspectAsync(stop)).LocalBinding);
            });
        Assert.True(result.Outcome == CloudProtectedLocalOperationOutcome.Completed, result.Error?.ToString());
        Assert.Equal(0, fixture.Provider.Fetches);
    }
}
