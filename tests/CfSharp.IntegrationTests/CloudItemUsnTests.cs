using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

using CfSharp.Native;
using CfSharp.Storage.Sqlite;

using Microsoft.Win32.SafeHandles;

using Xunit.Abstractions;

namespace CfSharp.IntegrationTests;

public sealed class CloudItemUsnTests
{
    private readonly ITestOutputHelper _output;

    public CloudItemUsnTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData(CloudInSyncPolicy.TrackAll)]
    [InlineData(CloudInSyncPolicy.None)]
    [SupportedOSPlatform("windows10.0.16299")]
    public Task ReadUsnObservesOrdinaryAndPlaceholderItemsWithoutHydration(CloudInSyncPolicy policy) =>
        WithFileSystemAsync(policy, async (fileSystem, rootPath) =>
        {
            CloudFile file = fileSystem.GetFile("ordinary.bin");
            CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await file.InspectAsync()).LocalBinding);
            Assert.True(await file.ReadUsnAsync() > 0);
            Assert.True(await fileSystem.GetDirectory("Folder").ReadUsnAsync() > 0);
            await Assert.ThrowsAsync<InvalidOperationException>(() => fileSystem
                .GetDirectory("ordinary.bin").ReadUsnAsync().AsTask());
            using CancellationTokenSource canceled = new();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => file.ReadUsnAsync(canceled.Token).AsTask());
            CloudFilesException missing = await Assert.ThrowsAsync<CloudFilesException>(() => fileSystem
                .GetFile("missing.bin").ReadUsnAsync().AsTask());
            Assert.Equal("CloudItem.ReadUsn", missing.Operation);
            Assert.Equal(2, missing.Win32ErrorCode);
            Assert.Equal(unchecked((int)0x80070002), missing.HResult);
            Assert.Equal(Path.Combine(rootPath, "missing.bin"), missing.Path);

            CloudPlaceholderMutationResult converted = await file.ConvertToPlaceholderAsync(
                new CloudPlaceholderIdentity(Guid.NewGuid(), "remote-usn"));
            _output.WriteLine($"ConvertUsn={converted.OperationUsn}; ObservedUsn={await file.ReadUsnAsync()}");
            Assert.True(await file.ReadUsnAsync() > 0);
            Assert.Equal(OriginalContent, await File.ReadAllBytesAsync(file.FullPath));
            Assert.Equal(binding, (await file.InspectAsync()).LocalBinding);

            long beforeWriteUsn = await file.ReadUsnAsync();
            await ChangeContentAsync(file.FullPath);
            Assert.NotEqual(beforeWriteUsn, await file.ReadUsnAsync());
            CloudPlaceholderMutationResult patched = await file.UpdatePlaceholderAsync(
                CloudPlaceholderPatch.CreateBuilder()
                    .WithMetadata(CloudPlaceholderMetadata.CreateFileBuilder()
                        .WithLastWriteTime(DateTimeOffset.UtcNow.AddMinutes(-5)).Build())
                    .Build());
            _output.WriteLine($"MetadataPatchUsn={patched.OperationUsn}; ObservedUsn={await file.ReadUsnAsync()}");
            Assert.True(await file.ReadUsnAsync() > 0);

            CloudDirectory directory = fileSystem.GetDirectory("Folder");
            await directory.ConvertToPlaceholderAsync(new CloudPlaceholderIdentity(Guid.NewGuid(), "remote-folder"));
            Assert.True(await directory.ReadUsnAsync() > 0);
            await fileSystem.Root.CreatePlaceholderAsync(CloudFilePlaceholderSpec
                .CreateBuilder("online-only.bin", "remote-online-only", 4096).Build());
            CloudFile onlineOnly = fileSystem.GetFile("online-only.bin");
            Assert.True(await onlineOnly.ReadUsnAsync() > 0);
            Assert.Equal(CloudContentAvailability.OnlineOnly, (await onlineOnly.InspectAsync()).ContentAvailability);
            Assert.NotNull((await onlineOnly.InspectAsync()).LocalBinding);
            await file.RevertToRegularItemAsync();
            Assert.True(await file.ReadUsnAsync() > 0);
            await fileSystem.DisposeAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => file.ReadUsnAsync().AsTask());
        });

    [Theory]
    [InlineData(CloudInSyncPolicy.TrackAll)]
    [InlineData(CloudInSyncPolicy.None)]
    [SupportedOSPlatform("windows10.0.16299")]
    public Task ChangedContentRejectsThePreviouslyObservedUsn(CloudInSyncPolicy policy) =>
        WithFileSystemAsync(policy, async (fileSystem, _) =>
        {
            CloudFile file = fileSystem.GetFile("ordinary.bin");
            await file.ConvertToPlaceholderAsync(new CloudPlaceholderIdentity(Guid.NewGuid(), "remote-usn"));
            long staleUsn = await file.ReadUsnAsync();
            Assert.True(staleUsn > 0);
            await ChangeContentAsync(file.FullPath);
            Assert.NotEqual(staleUsn, await file.ReadUsnAsync());
            CloudStateChangeResult cleared = await file.SetInSyncAsync(false);
            _output.WriteLine($"ChangedClearUsn={cleared.OperationUsn}");
            CloudFilesException stale = await Assert.ThrowsAsync<CloudFilesException>(() => file
                .SetInSyncAsync(true, new CloudInSyncChangeOptions(staleUsn)).AsTask());
            Assert.Equal("CloudItem.SetInSync", stale.Operation);
            Assert.Equal(unchecked((int)0x80070179), stale.HResult);
            Assert.Equal(CloudSynchronizationState.NotInSync, (await file.InspectAsync()).SynchronizationState);
        });

    [Theory]
    [InlineData(CloudInSyncPolicy.TrackAll)]
    [InlineData(CloudInSyncPolicy.None)]
    [SupportedOSPlatform("windows10.0.16299")]
    public Task VerifiedConditionalInSyncPreservesTheNativeResult(CloudInSyncPolicy policy) =>
        WithFileSystemAsync(policy, async (fileSystem, _) =>
        {
            CloudFile file = fileSystem.GetFile("ordinary.bin");
            await file.ConvertToPlaceholderAsync(
                new CloudPlaceholderIdentity(Guid.NewGuid(), "remote-usn"));
            await file.SetInSyncAsync(false);

            // Some Windows builds reject every nonzero conditional token. Compare the native
            // entry point with the public API without treating that rejection as success or
            // substituting an unconditional mark. Provider acceptance remains a separate gate.
            long nativeToken = await file.ReadUsnAsync();
            Assert.True(nativeToken > 0);
            (int nativeHresult, long nativeUsn) = SetInSyncNative(file.FullPath, true, nativeToken);
            _output.WriteLine($"NativeConditionalHRESULT=0x{nativeHresult:X8}; InputUsn={nativeToken}; OutputUsn={nativeUsn}");
            Assert.True(nativeHresult == 0 || nativeHresult == unchecked((int)0x80070179));
            await file.SetInSyncAsync(false);

            long verifiedUsn = await file.ReadUsnAsync();
            Assert.True(verifiedUsn > 0);
            await using (FileStream stream = File.OpenRead(file.FullPath))
            {
                Assert.Equal(SHA256.HashData(OriginalContent), await SHA256.HashDataAsync(stream));
            }

            long afterHashUsn = await file.ReadUsnAsync();
            _output.WriteLine($"BeforeHashUsn={verifiedUsn}; AfterHashUsn={afterHashUsn}");
            Assert.Equal(verifiedUsn, afterHashUsn);
            if (nativeHresult == 0)
            {
                CloudStateChangeResult marked = await file.SetInSyncAsync(true, new CloudInSyncChangeOptions(verifiedUsn));
                Assert.Equal(CloudSynchronizationState.InSync, marked.Snapshot.SynchronizationState);
            }
            else
            {
                CloudFilesException rejected = await Assert.ThrowsAsync<CloudFilesException>(() => file
                    .SetInSyncAsync(true, new CloudInSyncChangeOptions(verifiedUsn)).AsTask());
                Assert.Equal(nativeHresult, rejected.HResult);
                Assert.Equal("CloudItem.SetInSync", rejected.Operation);
                Assert.Equal(CloudSynchronizationState.NotInSync, (await file.InspectAsync()).SynchronizationState);
            }
        });

    private static byte[] OriginalContent => "CF-002 ordinary complete file"u8.ToArray();

    private static async Task ChangeContentAsync(string path)
    {
        // Mutate the existing placeholder. CREATE_ALWAYS may replace its reparse identity;
        // that would test item replacement rather than rejection of a stale content token.
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        await stream.WriteAsync("X"u8.ToArray());
    }

    [SupportedOSPlatform("windows10.0.16299")]
    private async Task WithFileSystemAsync(
        CloudInSyncPolicy policy,
        Func<CloudFileSystem, string, Task> verify)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
        {
            return;
        }

        string testPath = Path.Combine(Path.GetTempPath(), "CfSharp-usn-tests", Guid.NewGuid().ToString("N"));
        string rootPath = Path.Combine(testPath, "root");
        Directory.CreateDirectory(rootPath);
        await File.WriteAllBytesAsync(Path.Combine(rootPath, "ordinary.bin"), OriginalContent);
        Directory.CreateDirectory(Path.Combine(rootPath, "Folder"));
        Guid providerId = Guid.NewGuid();
        SyncRootRegistrationOptions registration = SyncRootRegistrationOptions
            .CreateBuilder($"CfSharp USN Test {providerId:N}", "1.0.0-test")
            .WithProviderId(providerId)
            .WithHydrationPolicy(CloudHydrationPolicy.Progressive)
            .WithInSyncPolicy(policy)
            .Build();
        CloudFileSystem fileSystem = CloudFileSystem.CreateBuilder(rootPath)
            .WithStateStore(new SqliteCloudStateStoreFactory(Path.Combine(testPath, "state.db")))
            .WithRegistration(registration)
            .WithContentProvider(new UnexpectedContentProvider())
            .Build();
        bool started = false;
        try
        {
            await fileSystem.StartAsync();
            started = true;
            _output.WriteLine($"OS={Environment.OSVersion.Version}; Architecture={RuntimeInformation.ProcessArchitecture}; Policy={policy}");
            await verify(fileSystem, rootPath);
        }
        finally
        {
            await fileSystem.DisposeAsync();
            if (started)
            {
                CloudSyncRoot.Open(rootPath).Unregister();
            }

            Directory.Delete(testPath, recursive: true);
        }
    }

    [SupportedOSPlatform("windows10.0.16299")]
    private static unsafe (int HResult, long Usn) SetInSyncNative(string path, bool inSync, long expectedUsn)
    {
        using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        long usn = expectedUsn;
        int hresult = CfApi.CfSetInSyncState(handle.DangerousGetHandle(),
            inSync ? CfInSyncState.InSync : CfInSyncState.NotInSync, CfSetInSyncFlags.None, &usn);
        return (hresult, usn);
    }

    private sealed class UnexpectedContentProvider : ICloudFileContentProvider
    {
        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Reading the USN must not request content.");
    }
}
