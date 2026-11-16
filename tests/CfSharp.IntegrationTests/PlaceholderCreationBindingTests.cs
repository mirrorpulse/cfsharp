using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using CfSharp.Storage.Sqlite;
using Xunit.Abstractions;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed class PlaceholderCreationBindingTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, "returned")]
    [InlineData(true, "returned")]
    [InlineData(false, "lost")]
    [InlineData(true, "lost")]
    [InlineData(false, "pending")]
    [InlineData(true, "pending")]
    public async Task OfficialCreationBindingSurvivesOnlyCommittedProjection(bool directory, string boundary)
    {
        using WindowsIdentity user = WindowsIdentity.GetCurrent();
        bool administrator = new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator);
        string architecture = RuntimeInformation.ProcessArchitecture.ToString();
        if (Environment.GetEnvironmentVariable("CFSHARP_EXPECTED_TEST_ARCHITECTURE") is string expectedArchitecture)
        {
            Assert.Equal(expectedArchitecture, architecture, ignoreCase: true);
        }

        if (Environment.GetEnvironmentVariable("CFSHARP_EXPECTED_TEST_SID") is string sid)
        {
            Assert.Equal(sid, user.User!.Value);
            Assert.False(administrator);
        }

        await using CreationRoot root = await CreationRoot.OpenAsync();
        CloudPlaceholderCreationRequest request = MakeRequest(directory);
        root.Factory.Cut = boundary;
        CloudPlaceholderCreationReceipt? receipt = null;
        if (boundary == "returned")
        {
            CloudPlaceholderCreationRecoveryResult created = await root.CreateAsync(request);
            Assert.Equal(CloudPlaceholderCreationRecoveryStatus.Created, created.Status);
            receipt = Assert.IsType<CloudPlaceholderCreationReceipt>(created.Receipt);
        }
        else
        {
            Assert.Same(root.Factory.Failure, await Assert.ThrowsAsync<IOException>(() => root.CreateAsync(request)));
        }

        CloudItemSnapshot before = await root.Item(request).InspectAsync();
        Assert.True(before.Exists && before.IsPlaceholder);
        Assert.Equal(request.Specification.Identity.Encode(), before.PlaceholderIdentity.ToArray());
        Assert.NotNull(before.LocalBinding);
        await using (ICloudStateTransaction check = await root.Factory.Store!.BeginTransactionAsync())
        {
            CloudItemState? projected = await check.Items.GetByItemIdAsync(request.Specification.Identity.ItemId);
            Assert.Equal(boundary != "pending", projected is not null);
        }

        await root.RestartAsync();
        CloudPlaceholderCreationRecoveryResult recovered = await root.Owner.GetDirectory("DOCS").RecoverPlaceholderCreationAsync(request);
        CloudPlaceholderCreationRecoveryStatus expected = boundary == "pending"
            ? CloudPlaceholderCreationRecoveryStatus.Pending : CloudPlaceholderCreationRecoveryStatus.Recovered;
        Assert.Equal(expected, recovered.Status);
        Assert.Equal(expected, (await root.CreateAsync(request)).Status);
        CloudItemSnapshot after = await root.Item(request).InspectAsync();
        Assert.Equal(before.LocalBinding, after.LocalBinding);
        Assert.Equal(before.PinState, after.PinState);
        Assert.Equal(before.OnDiskDataSize, after.OnDiskDataSize);
        Assert.Equal(before.Length, after.Length);
        if (boundary == "pending")
        {
            Assert.Null(recovered.Receipt);
            await using ICloudStateTransaction check = await root.Factory.Store!.BeginTransactionAsync();
            Assert.Null(await check.Items.GetByItemIdAsync(request.Specification.Identity.ItemId));
        }
        else
        {
            CloudPlaceholderCreationReceipt replay = Assert.IsType<CloudPlaceholderCreationReceipt>(recovered.Receipt);
            Assert.Equal(before.LocalBinding, replay.Binding);
            Assert.Equal(after.PlaceholderIdentity.ToArray(), replay.PlaceholderIdentity.ToArray());
            Assert.Equal(request.OperationId, replay.OperationId);
            Assert.Equal("Docs\\target", replay.RelativePath);
            Assert.Equal(request.Specification.Kind, replay.Kind);
            Assert.True(replay.CreateUsn > 0);
            if (receipt is not null)
            {
                Assert.Equal(receipt.Binding, replay.Binding);
                Assert.Equal(receipt.CreateUsn, replay.CreateUsn);
            }
        }

        Assert.Equal(0, root.Provider.Reads);
        output.WriteLine($"Architecture={architecture}, SID={user.User!.Value}, administrator={administrator}, directory={directory}, boundary={boundary}, status={recovered.Status}, original={before.LocalBinding}, providerReads=0.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementWithSameIdentityCannotSupplyOriginalBinding(bool directory)
    {
        await using CreationRoot root = await CreationRoot.OpenAsync();
        CloudPlaceholderCreationRequest request = MakeRequest(directory);
        CloudPlaceholderCreationReceipt original = (await root.CreateAsync(request)).Receipt!;
        await root.Owner.DisposeAsync();
        string path = root.PathFor(request);
        if (directory) { Directory.Move(path, Path.Combine(root.Path, "old")); }
        else { File.Move(path, Path.Combine(root.Path, "old")); }
        await root.RestartAsync();
        Assert.Equal(CloudPlaceholderCreationRecoveryStatus.Missing,
            (await root.Parent.RecoverPlaceholderCreationAsync(request)).Status);

        string marker = directory ? Path.Combine(path, "marker") : path;
        byte[] payload = [1, 2, 3, 4];
        if (directory)
        {
            await root.Parent.CreatePlaceholderAsync(request.Specification);
            await File.WriteAllBytesAsync(marker, payload);
        }
        else
        {
            await File.WriteAllBytesAsync(marker, payload);
            await root.Parent.GetFile(request.Specification.Name).ConvertToPlaceholderAsync(request.Specification.Identity);
        }

        CloudItemSnapshot replacement = await root.Item(request).InspectAsync();
        Assert.NotEqual(original.Binding, replacement.LocalBinding);
        Assert.Equal(original.PlaceholderIdentity.ToArray(), replacement.PlaceholderIdentity.ToArray());
        CloudPlaceholderCreationRecoveryResult replay = await root.CreateAsync(request);
        Assert.Equal(CloudPlaceholderCreationRecoveryStatus.Replaced, replay.Status);
        Assert.Null(replay.Receipt);
        CloudItemSnapshot after = await root.Item(request).InspectAsync();
        Assert.Equal(replacement.LocalBinding, after.LocalBinding);
        Assert.Equal(replacement.Attributes, after.Attributes);
        Assert.Equal(replacement.OnDiskDataSize, after.OnDiskDataSize);
        Assert.Equal(payload, await File.ReadAllBytesAsync(marker));
        Assert.Equal(0, root.Provider.Reads);
    }

    [Fact]
    public async Task BindingsDoNotTransferToAnAlteredRequestParentOrStore()
    {
        await using CreationRoot root = await CreationRoot.OpenAsync();
        CloudPlaceholderCreationRequest request = MakeRequest(directory: true);
        Assert.Equal(CloudPlaceholderCreationRecoveryStatus.Created, (await root.CreateAsync(request)).Status);
        CloudPlaceholderIdentity identity = request.Specification.Identity;
        foreach (CloudPlaceholderSpec changed in new CloudPlaceholderSpec[]
        {
            CloudDirectoryPlaceholderSpec.CreateBuilder("other", identity).Build(),
            CloudDirectoryPlaceholderSpec.CreateBuilder("target", identity.RemoteId).Build(),
            CloudFilePlaceholderSpec.CreateBuilder("target", identity, 0).Build(),
            CloudDirectoryPlaceholderSpec.CreateBuilder("target", identity).WithInSyncState(false).Build(),
        })
        {
            Assert.Equal(CloudPlaceholderCreationRecoveryStatus.Conflict,
                (await root.Parent.RecoverPlaceholderCreationAsync(new(request.OperationId, changed))).Status);
        }

        Directory.CreateDirectory(Path.Combine(root.Path, "other"));
        Assert.Equal(CloudPlaceholderCreationRecoveryStatus.Conflict,
            (await root.Owner.GetDirectory("other").RecoverPlaceholderCreationAsync(request)).Status);

        // The official database still opens at its configured path after the physical root was
        // replaced. Only the original full root ID can authenticate the stored receipt.
        await root.ReplacePhysicalRootAsync();
        Assert.Equal(CloudPlaceholderCreationRecoveryStatus.WrongRoot,
            (await root.Parent.RecoverPlaceholderCreationAsync(request)).Status);
        Assert.False(Directory.Exists(root.PathFor(request)));
    }

    [Fact]
    public async Task ReplacementParentCannotAuthenticateTheOriginalCreationPath()
    {
        await using CreationRoot root = await CreationRoot.OpenAsync();
        CloudPlaceholderCreationRequest request = MakeRequest(directory: true);
        Assert.Equal(CloudPlaceholderCreationRecoveryStatus.Created, (await root.CreateAsync(request)).Status);
        Directory.Move(root.Parent.FullPath, Path.Combine(root.Path, "moved"));
        Assert.Equal(CloudPlaceholderCreationRecoveryStatus.Missing,
            (await root.Owner.GetDirectory("Docs").RecoverPlaceholderCreationAsync(request)).Status);
        Directory.CreateDirectory(Path.Combine(root.Path, "Docs"));
        Assert.Equal(CloudPlaceholderCreationRecoveryStatus.Replaced,
            (await root.Parent.RecoverPlaceholderCreationAsync(request)).Status);
        Assert.True(Directory.Exists(Path.Combine(root.Path, "moved", "target")));
        Assert.False(Directory.Exists(root.PathFor(request)));
    }

    [Fact]
    public async Task SuccessfulPartialBatchEntryRecoversIndependentlyOfCollisionAndUnprocessedEntries()
    {
        await using CreationRoot root = await CreationRoot.OpenAsync();
        CloudPlaceholderCreationRequest created = MakeRequest(directory: true, "created");
        CloudPlaceholderCreationRequest collision = MakeRequest(directory: false, "existing");
        CloudPlaceholderCreationRequest skipped = MakeRequest(directory: false, "skipped");
        Directory.CreateDirectory(root.PathFor(collision));
        IReadOnlyList<CloudPlaceholderCreationRecoveryResult> results = await root.Parent.CreateRecoverablePlaceholdersAsync(
            [created, collision, skipped], new(stopOnFirstFailure: true));
        Assert.Equal(new[] { CloudPlaceholderCreationRecoveryStatus.Created, CloudPlaceholderCreationRecoveryStatus.Conflict,
            CloudPlaceholderCreationRecoveryStatus.NotProcessed }, results.Select(result => result.Status));
        await root.RestartAsync();
        Assert.Equal(results[0].Receipt!.Binding, (await root.Parent.RecoverPlaceholderCreationAsync(created)).Receipt!.Binding);
        Assert.Equal(CloudPlaceholderCreationRecoveryStatus.NotFound, (await root.Parent.RecoverPlaceholderCreationAsync(collision)).Status);
        Assert.Equal(CloudPlaceholderCreationRecoveryStatus.NotFound, (await root.Parent.RecoverPlaceholderCreationAsync(skipped)).Status);
        Assert.False(File.Exists(root.PathFor(skipped)));
    }

    [Fact]
    public async Task OldItemProjectionAndLegacyIdentityMatchDoNotInventOriginalEvidence()
    {
        await using CreationRoot root = await CreationRoot.OpenAsync();
        CloudPlaceholderCreationRequest request = MakeRequest(directory: true);
        CloudPlaceholderBatchEntryResult legacy = await root.Parent.CreatePlaceholderAsync(request.Specification);
        Assert.True(legacy.DurableStatePersisted);
        await root.RestartAsync();
        Assert.Equal(CloudPlaceholderCreationRecoveryStatus.NotFound, (await root.Parent.RecoverPlaceholderCreationAsync(request)).Status);
        Assert.Equal(CloudPlaceholderCreationRecoveryStatus.Conflict, (await root.CreateAsync(request)).Status);
        CloudPlaceholderBatchEntryResult match = await root.Parent.CreatePlaceholderAsync(request.Specification);
        Assert.True(match.Progress.HasFlag(CloudPlaceholderCreationProgress.ExistingPlaceholderMatched));
        Assert.Null(match.CreateUsn);
        Assert.Equal(CloudPlaceholderCreationRecoveryStatus.NotFound, (await root.Parent.RecoverPlaceholderCreationAsync(request)).Status);
    }

    private static CloudPlaceholderCreationRequest MakeRequest(bool directory, string name = "target")
    {
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "remote-" + name, "revision");
        CloudPlaceholderSpec spec = directory
            ? CloudDirectoryPlaceholderSpec.CreateBuilder(name, identity).WithPopulationState(CloudDirectoryPopulationState.Complete).Build()
            : CloudFilePlaceholderSpec.CreateBuilder(name, identity, 512).Build();
        return new(Guid.NewGuid(), spec);
    }

    private sealed class CreationRoot(string area, string path, CloudFileSystem owner, FaultFactory factory, NoContent provider) : IAsyncDisposable
    {
        internal CloudFileSystem Owner { get; private set; } = owner;
        internal CloudDirectory Parent => Owner.GetDirectory("Docs");
        internal FaultFactory Factory => factory;
        internal NoContent Provider => provider;
        internal string Path => path;
        internal string PathFor(CloudPlaceholderCreationRequest request) => System.IO.Path.Combine(path, "Docs", request.Specification.Name);
        internal CloudItem Item(CloudPlaceholderCreationRequest request) => request.Specification.Kind == CloudItemKind.Directory
            ? Parent.GetDirectory(request.Specification.Name) : Parent.GetFile(request.Specification.Name);

        internal async Task<CloudPlaceholderCreationRecoveryResult> CreateAsync(CloudPlaceholderCreationRequest request) =>
            Assert.Single(await Parent.CreateRecoverablePlaceholdersAsync([request]));

        internal static async Task<CreationRoot> OpenAsync()
        {
            string area = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CfSharp-original-birth", Guid.NewGuid().ToString("N"));
            string path = System.IO.Path.Combine(area, "root");
            Directory.CreateDirectory(System.IO.Path.Combine(path, "Docs"));
            FaultFactory factory = new(new SqliteCloudStateStoreFactory(System.IO.Path.Combine(area, "state.db")));
            NoContent provider = new();
            CloudFileSystem owner = CloudFileSystem.CreateBuilder(path).WithStateStore(factory).WithContentProvider(provider)
                .WithRegistration(Registration()).Build();
            await owner.StartAsync();
            return new(area, path, owner, factory, provider);
        }

        internal async Task RestartAsync()
        {
            await Owner.DisposeAsync();
            Owner = CloudFileSystem.CreateBuilder(path).WithStateStore(factory).WithContentProvider(provider).Build();
            await Owner.StartAsync();
        }

        internal async Task ReplacePhysicalRootAsync()
        {
            await Owner.DisposeAsync();
            CloudSyncRoot.Open(path).Unregister();
            Directory.Move(path, System.IO.Path.Combine(area, "retired-root"));
            Directory.CreateDirectory(System.IO.Path.Combine(path, "Docs"));
            _ = CloudSyncRoot.Register(path, Registration());
            await RestartAsync();
        }

        private static SyncRootRegistrationOptions Registration() => SyncRootRegistrationOptions.CreateBuilder("CfSharp Original Birth Test", "1.0")
            .WithProviderId(Guid.NewGuid()).WithRootMarkedInSync().Build();

        public async ValueTask DisposeAsync()
        {
            await Owner.DisposeAsync();
            CloudSyncRoot.Open(path).Unregister();
            Directory.Delete(area, recursive: true);
        }
    }

    private sealed class NoContent : ICloudFileContentProvider
    {
        internal int Reads { get; private set; }
        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken cancellationToken)
        {
            Reads++;
            throw new InvalidOperationException("Original binding recovery must not hydrate files.");
        }
    }

    private sealed class FaultFactory(ICloudStateStoreFactory inner) : ICloudStateStoreFactory
    {
        internal ICloudStateStore? Store { get; private set; }
        internal string? Cut { get; set; }
        internal IOException Failure { get; } = new("Injected interruption at the official creation projection commit.");

        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context, CancellationToken cancellationToken = default) =>
            Store = new FaultStore(await inner.OpenAsync(context, cancellationToken), this);

        private sealed class FaultStore(ICloudStateStore inner, FaultFactory owner) : ICloudStateStore
        {
            public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) =>
                new FaultTransaction(await inner.BeginTransactionAsync(cancellationToken), owner);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }

        private sealed class FaultTransaction(ICloudStateTransaction inner, FaultFactory owner) : ICloudStateTransaction
        {
            public ICloudItemStateRepository Items => inner.Items;
            public ICloudCheckpointRepository Checkpoints => inner.Checkpoints;
            public ICloudOperationJournal Operations => inner.Operations;
            public ICloudConflictRepository Conflicts => inner.Conflicts;
            public ICloudRemoteBatchRepository RemoteBatches => inner.RemoteBatches;
            public ICloudEchoSuppressionRepository EchoSuppressions => inner.EchoSuppressions;

            public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
            {
                string? cut = null;
                if ((owner.Cut is "pending" or "lost") && (await Items.ListSubtreeAsync("Docs", cancellationToken)).Count > 0)
                {
                    cut = owner.Cut;
                    owner.Cut = null;
                }

                if (cut == "pending") { throw owner.Failure; }
                await inner.CommitAsync(cancellationToken);
                if (cut == "lost") { throw owner.Failure; }
            }

            public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => inner.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
