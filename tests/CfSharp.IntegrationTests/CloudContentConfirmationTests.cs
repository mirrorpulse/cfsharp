using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

using CfSharp.Storage.Sqlite;

using Xunit.Abstractions;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class CloudContentConfirmationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(128 * 1024)]
    public async Task GuardedOrdinaryConversionAndConfirmationPreserveObjectAndContent(int length)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        byte[] content = new byte[length];
        Random.Shared.NextBytes(content);
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudContentConfirmationRequest request = Proof(binding, content, CloudContentPreparation.ConvertRegularFile);
        CloudContentConfirmationResult result = await fixture.File.ConfirmUploadedContentAsync(request);
        output.WriteLine($"OS={Environment.OSVersion.Version}; Architecture={RuntimeInformation.ProcessArchitecture}; Outcome={result.Outcome}; Stage={result.Stage}; HRESULT={result.Error?.HResult:X8}; Bytes={result.BytesVerified}; Segments={result.SegmentsRead}; LongestReferenceMs={result.LongestReference.TotalMilliseconds}");
        Assert.Equal(CloudContentConfirmationOutcome.Confirmed, result.Outcome);
        Assert.True(result.NativeIdentityPrepared);
        Assert.True(result.NativeApplied);
        Assert.True(result.DurableProjectionCommitted);
        Assert.Equal(length * 2, result.BytesVerified);
        CloudItemSnapshot snapshot = await fixture.File.InspectAsync();
        Assert.Equal(binding, snapshot.LocalBinding);
        Assert.Equal(request.AcceptedIdentity.Encode(), snapshot.PlaceholderIdentity.ToArray());
        Assert.Equal(request.AcceptedIdentity.RemoteRevision, snapshot.RemoteRevision);
        Assert.Equal(CloudSynchronizationState.InSync, snapshot.SynchronizationState);
        Assert.Equal(content, await File.ReadAllBytesAsync(fixture.File.FullPath));
        CloudContentConfirmationResult replay = await fixture.File.ConfirmUploadedContentAsync(request);
        Assert.Equal(CloudContentConfirmationOutcome.AlreadyConfirmed, replay.Outcome);
        Assert.False(replay.NativeApplied);
        Assert.True(replay.DurableProjectionCommitted);
        Assert.Equal(length, replay.BytesVerified);
        Assert.Equal(0, fixture.Provider.Fetches);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementBeforePreparationCannotInheritTheAcceptedIdentity(bool sameContent)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        byte[] content = "upload"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        File.Move(fixture.File.FullPath, Path.Combine(fixture.Root, "original.bin"));
        await File.WriteAllBytesAsync(fixture.File.FullPath, sameContent ? content : "other!"u8.ToArray());
        CloudContentConfirmationResult result = await fixture.File.ConfirmUploadedContentAsync(
            Proof(binding, content, CloudContentPreparation.ConvertRegularFile));
        Assert.Equal(CloudContentConfirmationOutcome.LocalObjectMismatch, result.Outcome);
        Assert.False(result.NativeApplied);
        Assert.False(result.NativeIdentityPrepared);
        Assert.False((await fixture.File.InspectAsync()).IsPlaceholder);
    }

    [Fact]
    public async Task MismatchedContentIdentityAndBindingNeverMark()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        byte[] content = "upload"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudPlaceholderIdentity accepted = new(Guid.NewGuid(), "remote-object", "revision-2");
        await fixture.File.ConvertToPlaceholderAsync(accepted);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudContentConfirmationRequest request = new(binding, accepted, content.Length, SHA256.HashData(content));
        await fixture.File.SetInSyncAsync(false);
        CloudContentConfirmationResult mismatch = await fixture.File.ConfirmUploadedContentAsync(
            new(binding, accepted, content.Length, SHA256.HashData("bad!!!"u8)));
        Assert.Equal(CloudContentConfirmationOutcome.ContentMismatch, mismatch.Outcome);
        mismatch = await fixture.File.ConfirmUploadedContentAsync(new(binding, accepted, content.Length + 1, SHA256.HashData(content)));
        Assert.Equal(CloudContentConfirmationOutcome.ContentMismatch, mismatch.Outcome);
        mismatch = await fixture.File.ConfirmUploadedContentAsync(Proof(binding, content));
        Assert.Equal(CloudContentConfirmationOutcome.IdentityMismatch, mismatch.Outcome);
        foreach (CloudLocalFileBinding wrong in new[]
        {
            new CloudLocalFileBinding(binding.VolumeSerialNumber + 1, binding.SyncRootFileId, binding.LocalFileId),
            new CloudLocalFileBinding(binding.VolumeSerialNumber, Guid.NewGuid(), binding.LocalFileId),
            new CloudLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, Guid.NewGuid()),
        })
        {
            mismatch = await fixture.File.ConfirmUploadedContentAsync(new(wrong, accepted, content.Length, SHA256.HashData(content)));
            Assert.Equal(CloudContentConfirmationOutcome.LocalObjectMismatch, mismatch.Outcome);
        }

        Assert.Equal(CloudSynchronizationState.NotInSync, (await fixture.File.InspectAsync()).SynchronizationState);
        Assert.Equal(CloudContentConfirmationOutcome.Confirmed, (await fixture.File.ConfirmUploadedContentAsync(request)).Outcome);
        await using (FileStream writer = new(fixture.File.FullPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            await writer.WriteAsync("change"u8.ToArray());
        }

        Assert.Equal(CloudSynchronizationState.NotInSync, (await fixture.File.InspectAsync()).SynchronizationState);
        Assert.Equal(CloudContentConfirmationOutcome.ContentMismatch, (await fixture.File.ConfirmUploadedContentAsync(request)).Outcome);
    }

    [Fact]
    public async Task GuardedIdentityReplacementVerifiesAcceptedContentAgain()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        byte[] content = "upload"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudPlaceholderIdentity previous = new(Guid.NewGuid(), "remote-object", "revision-1");
        await fixture.File.ConvertToPlaceholderAsync(previous);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudPlaceholderIdentity accepted = new(previous.ItemId, previous.RemoteId, "revision-2");
        CloudContentConfirmationRequest request = new(binding, accepted, content.Length, SHA256.HashData(content),
            CloudContentPreparation.ReplacePlaceholderIdentity, previous.Encode(), segmentSize: 4);
        CloudContentConfirmationResult result = await fixture.File.ConfirmUploadedContentAsync(request);
        Assert.Equal(CloudContentConfirmationOutcome.Confirmed, result.Outcome);
        Assert.True(result.NativeIdentityPrepared);
        Assert.Equal(content.Length * 2, result.BytesVerified);
        Assert.Equal(accepted.Encode(), (await fixture.File.InspectAsync()).PlaceholderIdentity.ToArray());
    }

    [Fact]
    public async Task ExistingWritableHandleReturnsBusyAndRetryWorksAfterRelease()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        byte[] content = "upload"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudContentConfirmationRequest request = Proof(binding, content, CloudContentPreparation.ConvertRegularFile);
        using (FileStream writer = new(fixture.File.FullPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            CloudContentConfirmationResult busy = await fixture.File.ConfirmUploadedContentAsync(request);
            Assert.Equal(CloudContentConfirmationOutcome.Busy, busy.Outcome);
            Assert.False(busy.NativeIdentityPrepared);
            Assert.False(busy.NativeApplied);
            Assert.Equal(32, Assert.IsType<CloudFilesException>(busy.Error).Win32ErrorCode);
        }

        Assert.Equal(CloudContentConfirmationOutcome.Confirmed, (await fixture.File.ConfirmUploadedContentAsync(request)).Outcome);
    }

    [Fact]
    public async Task OnlineOnlyAndDirectoryAreRejectedWithoutProviderReads()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        await fixture.System.Root.CreatePlaceholderAsync(CloudFilePlaceholderSpec.CreateBuilder("online.bin", "remote-online", 4096).Build());
        CloudFile online = fixture.System.GetFile("online.bin");
        CloudItemSnapshot snapshot = await online.InspectAsync();
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>(snapshot.LocalBinding);
        CloudContentConfirmationResult result = await online.ConfirmUploadedContentAsync(new(binding,
            CloudPlaceholderIdentity.Decode(snapshot.PlaceholderIdentity.Span), 4096, new byte[32]));
        Assert.Equal(CloudContentConfirmationOutcome.NotFullyLocal, result.Outcome);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "directory"));
        result = await fixture.System.GetFile("directory").ConfirmUploadedContentAsync(Proof(binding, []));
        Assert.Equal(CloudContentConfirmationOutcome.NotApplicable, result.Outcome);
        Assert.Equal(0, fixture.Provider.Fetches);
    }

    private static CloudContentConfirmationRequest Proof(CloudLocalFileBinding binding, byte[] content,
        CloudContentPreparation preparation = CloudContentPreparation.None) =>
        new(binding, new CloudPlaceholderIdentity(Guid.NewGuid(), "remote-object", "revision-2"),
            content.Length, SHA256.HashData(content), preparation, segmentSize: 32 * 1024);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory;
        private Fixture(string directory, string root, CloudFileSystem system, UnexpectedProvider provider,
            SyncRootRegistrationOptions registration)
        {
            _directory = directory;
            Root = root;
            System = system;
            File = system.GetFile("content.bin");
            Provider = provider;
            Registration = registration;
        }

        internal string Root { get; }
        internal CloudFileSystem System { get; private set; }
        internal CloudFile File { get; private set; }
        internal UnexpectedProvider Provider { get; }
        internal SyncRootRegistrationOptions Registration { get; }
        private ICloudStateStoreFactory? _restartFactory;

        internal static async Task<Fixture> StartAsync(Func<string, ICloudStateStoreFactory>? createStore = null,
            CloudInSyncPolicy inSyncPolicy = CloudInSyncPolicy.None)
        {
            // This acceptance fixture fails on unavailable Windows capabilities; it never
            // silently returns success when native confirmation has not actually executed.
            Assert.True(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299));
            string directory = Path.Combine(Path.GetTempPath(), "CfSharp-confirmation", Guid.NewGuid().ToString("N"));
            string root = Path.Combine(directory, "root");
            Directory.CreateDirectory(root);
            UnexpectedProvider provider = new();
            string databasePath = Path.Combine(directory, "state.db");
            ICloudStateStoreFactory factory = createStore?.Invoke(databasePath) ?? new SqliteCloudStateStoreFactory(databasePath);
            SyncRootRegistrationOptions registration = SyncRootRegistrationOptions.CreateBuilder("CfSharp Confirmation", "1.0.0-test")
                .WithProviderId(Guid.NewGuid()).WithHydrationPolicy(CloudHydrationPolicy.Progressive)
                .WithInSyncPolicy(inSyncPolicy).Build();
            CloudFileSystem system = CloudFileSystem.CreateBuilder(root)
                .WithStateStore(factory)
                .WithRegistration(registration)
                .WithContentProvider(provider).Build();
            try
            {
                await system.StartAsync();
                return new Fixture(directory, root, system, provider, registration) { _restartFactory = factory };
            }
            catch
            {
                await system.DisposeAsync();
                Directory.Delete(directory, recursive: true);
                throw;
            }
        }

        internal async Task RestartAsync(bool register = false)
        {
            await System.DisposeAsync();
            CloudFileSystem.Builder builder = CloudFileSystem.CreateBuilder(Root).WithStateStore(_restartFactory!)
                .WithContentProvider(Provider);
            if (register)
            {
                builder.WithRegistration(Registration);
            }

            System = builder.Build();
            await System.StartAsync();
            File = System.GetFile("content.bin");
        }

        public async ValueTask DisposeAsync()
        {
            await System.DisposeAsync();
            CloudSyncRoot.Open(Root).Unregister();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class UnexpectedProvider : ICloudDemandProvider
    {
        internal int Fetches { get; private set; }
        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken cancellationToken)
        {
            Fetches++;
            throw new InvalidOperationException("Confirmation must not hydrate missing content.");
        }

        // This provider is connected only to its isolated owned fixture root. Permit its
        // external rename/delete tests so a provider policy denial cannot mimic an oplock failure.
        public ValueTask<CloudProviderPolicyDecision> ApproveRenameAsync(CloudProviderRenameRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(CloudProviderPolicyDecision.Allow);
        public ValueTask<CloudProviderPolicyDecision> ApproveDeleteAsync(CloudProviderDeleteRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(CloudProviderPolicyDecision.Allow);
        public ValueTask<CloudProviderPolicyDecision> ApproveDehydrateAsync(CloudProviderDehydrateRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(CloudProviderPolicyDecision.Allow);
    }
}
