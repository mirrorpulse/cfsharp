using System.Diagnostics;
using System.Security.Cryptography;

using CfSharp.Native;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Theory]
    [InlineData(false, "oplock")]
    [InlineData(true, "oplock")]
    [InlineData(false, "guard")]
    [InlineData(true, "guard")]
    [InlineData(false, "metadata")]
    [InlineData(true, "metadata")]
    [InlineData(false, "read")]
    [InlineData(true, "read")]
    public async Task ShortReferencesRemainUsableWithoutACompetingMutation(bool placeholder, string operation)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        byte[] content = "upload"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        if (placeholder)
        {
            await fixture.File.ConvertToPlaceholderAsync(new CloudPlaceholderIdentity(Guid.NewGuid(), "reference-object", "revision-1"));
        }

        output.WriteLine($"OS={Environment.OSVersion.Version}; Placeholder={placeholder}; Operation={operation}");
        if (operation == "oplock")
        {
            using SafeCloudFilesProtectedHandle owner = SafeCloudFilesProtectedHandle.Open(fixture.File.FullPath,
                CfOpenFileFlags.Exclusive | CfOpenFileFlags.WriteAccess, "Confirmation.ReferenceContract");
            for (int index = 0; index < 3; index++)
            {
                output.WriteLine($"Reference={index}");
                using (SafeCloudFilesProtectedHandle.CloudFilesHandleReference reference = owner.AcquireReference())
                {
                    Assert.NotEqual(0, reference.Win32Handle);
                }
                await Task.Delay(20);
            }
        }
        else
        {
            using CloudProtectedContentSession owner = new(fixture.File.FullPath, fixture.Root);
            byte[] buffer = new byte[content.Length];
            for (int index = 0; index < 3; index++)
            {
                output.WriteLine($"Reference={index}");
                using (ICloudProtectedContentReference reference = owner.Reference())
                {
                    if (operation == "metadata")
                    {
                        Assert.Equal(content.Length, reference.Inspect().Length);
                    }
                    else if (operation == "read")
                    {
                        Assert.Equal(content.Length, reference.Read(buffer, buffer.Length, 0, TimeSpan.FromSeconds(2), CancellationToken.None));
                        Assert.Equal(content, buffer);
                    }
                }
                await Task.Delay(20);
            }
        }
    }

    [Fact]
    public async Task RecreatedRootCannotReuseTheProofEvenWhenTheFileIdIsPreserved()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        byte[] content = "upload"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudLocalFileBinding original = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudContentConfirmationRequest proof = Proof(original, content, CloudContentPreparation.ConvertRegularFile);
        await fixture.System.DisposeAsync();
        CloudSyncRoot.Open(fixture.Root).Unregister();
        string preserved = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "preserved.bin");
        File.Move(fixture.File.FullPath, preserved);
        Directory.Delete(fixture.Root);
        Directory.CreateDirectory(fixture.Root);
        File.Move(preserved, fixture.File.FullPath);
        await fixture.RestartAsync(register: true);
        CloudItemSnapshot recreated = await fixture.File.InspectAsync();
        CloudLocalFileBinding current = Assert.IsType<CloudLocalFileBinding>(recreated.LocalBinding);
        Assert.Equal(original.LocalFileId, current.LocalFileId);
        Assert.Equal(original.VolumeSerialNumber, current.VolumeSerialNumber);
        Assert.NotEqual(original.SyncRootFileId, current.SyncRootFileId);
        CloudContentConfirmationResult rejected = await fixture.File.ConfirmUploadedContentAsync(proof);
        Assert.Equal(CloudContentConfirmationOutcome.LocalObjectMismatch, rejected.Outcome);
        Assert.False(rejected.NativeIdentityPrepared);
        Assert.False(rejected.NativeApplied);
        Assert.False((await fixture.File.InspectAsync()).IsPlaceholder);
        Assert.Equal(content, await File.ReadAllBytesAsync(fixture.File.FullPath));
    }

    [Fact]
    public async Task ContentBeyondFourGiBUsesFullOffsetsAndBoundedMemory()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        const long length = (1L << 32) + 17;
        const int segment = 1024 * 1024;
        (long Offset, byte[] Bytes)[] markers =
        [
            (0, "low-offset"u8.ToArray()),
            ((1L << 31) + 31, "middle-offset"u8.ToArray()),
            ((1L << 32) + 3, "high-offset"u8.ToArray()),
        ];
        await using (FileStream stream = new(fixture.File.FullPath, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength(length);
            foreach ((long offset, byte[] bytes) in markers)
            {
                stream.Position = offset;
                await stream.WriteAsync(bytes);
            }
        }

        byte[] block = new byte[segment];
        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (long offset = 0; offset < length; offset += segment)
        {
            Array.Clear(block);
            int count = (int)Math.Min(length - offset, block.Length);
            // Compute the expected digest independently of native reads. Distinct markers
            // below and above 4 GiB expose offset truncation that all-zero content could hide.
            foreach ((long markerOffset, byte[] bytes) in markers)
            {
                if (markerOffset >= offset && markerOffset < offset + count)
                {
                    bytes.CopyTo(block.AsSpan((int)(markerOffset - offset)));
                }
            }

            digest.AppendData(block, 0, count);
        }

        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        // This case verifies full offsets and bounded memory. A synchronous conversion of
        // the 4 GiB fixture exceeded two seconds on an ARM64 CI runner; budget expiration
        // is tested independently with injected timing rather than large-file I/O latency.
        CloudContentConfirmationRequest proof = new(binding, new CloudPlaceholderIdentity(Guid.NewGuid(), "large-object", "revision-1"),
            length, digest.GetHashAndReset(), CloudContentPreparation.ConvertRegularFile, segmentSize: segment,
            referenceBudget: TimeSpan.FromSeconds(10));
        using Process process = Process.GetCurrentProcess();
        process.Refresh();
        long peakBefore = process.PeakWorkingSet64;
        CloudContentConfirmationResult result = await fixture.File.ConfirmUploadedContentAsync(proof);
        process.Refresh();
        long peakIncrease = Math.Max(0, process.PeakWorkingSet64 - peakBefore);
        output.WriteLine($"Length={length}; BytesRead={result.BytesVerified}; Segments={result.SegmentsRead}; Buffer={proof.SegmentSize}; PeakWorkingSetIncrease={peakIncrease}; ElapsedMs={result.Elapsed.TotalMilliseconds}; LongestReferenceMs={result.LongestReference.TotalMilliseconds}; Outcome={result.Outcome}; Stage={result.Stage}; HRESULT={result.Error?.HResult:X8}");
        Assert.Equal(CloudContentConfirmationOutcome.Confirmed, result.Outcome);
        Assert.Equal(length * 2, result.BytesVerified);
        Assert.True(result.SegmentsRead > 8192);
        Assert.True(peakIncrease < 256L * 1024 * 1024);
        Assert.Equal(binding, (await fixture.File.InspectAsync()).LocalBinding);
        Assert.Equal(length, (await fixture.File.InspectAsync()).Length);
    }

    [Fact]
    public async Task EmptyNativeIdentityIsLegalButOutsideProtectedReplacementScope()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        byte[] content = "upload"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        ConvertWithoutIdentity(fixture.File.FullPath);
        CloudItemSnapshot snapshot = await fixture.File.InspectAsync();
        Assert.True(snapshot.IsPlaceholder);
        Assert.Empty(snapshot.PlaceholderIdentity.ToArray());
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>(snapshot.LocalBinding);
        Assert.Throws<ArgumentException>(() => new CloudContentConfirmationRequest(binding,
            new(Guid.NewGuid(), "accepted", "revision-1"), content.Length, SHA256.HashData(content),
            CloudContentPreparation.ReplacePlaceholderIdentity, snapshot.PlaceholderIdentity.Span));
        Assert.Empty((await fixture.File.InspectAsync()).PlaceholderIdentity.ToArray());
        Assert.Equal(content, await File.ReadAllBytesAsync(fixture.File.FullPath));
        Assert.Equal(0, fixture.Provider.Fetches);
    }

    private static unsafe void ConvertWithoutIdentity(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        Assert.Equal(0, CfApi.CfConvertToPlaceholder(handle.DangerousGetHandle(), null, 0, CfConvertFlags.None, null, null));
    }

    [Fact]
    public async Task AValidReferenceWhoseParentBecomesALinkReturnsAnUncommittedRejection()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        string parent = Path.Combine(fixture.Root, "folder");
        string moved = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "moved-folder");
        Directory.CreateDirectory(parent);
        CloudFile file = fixture.System.GetFile(Path.Combine("folder", "content.bin"));
        byte[] content = "upload"u8.ToArray();
        await File.WriteAllBytesAsync(file.FullPath, content);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await file.InspectAsync()).LocalBinding);
        CloudContentConfirmationRequest proof = Proof(binding, content, CloudContentPreparation.ConvertRegularFile);
        Directory.Move(parent, moved);
        Directory.CreateSymbolicLink(parent, moved);
        try
        {
            CloudContentConfirmationResult result = await file.ConfirmUploadedContentAsync(proof);
            Assert.Equal(CloudContentConfirmationOutcome.NotApplicable, result.Outcome);
            Assert.Equal(CloudContentConfirmationStage.Open, result.Stage);
            Assert.False(result.NativeIdentityPrepared);
            Assert.False(result.NativeApplied);
            Assert.False(result.NativeConfirmationVerified);
            Assert.False(result.DurableProjectionCommitted);
            Assert.Null(result.PreparationHResult);
            Assert.Null(result.NativeMarkHResult);
            Assert.Equal(0, result.BytesVerified);
            Assert.False(File.GetAttributes(Path.Combine(moved, "content.bin")).HasFlag(FileAttributes.ReparsePoint));
            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(moved, "content.bin")));
            Assert.Equal(0, fixture.Provider.Fetches);
        }
        finally
        {
            // Remove only the link, before the fixture recursively cleans its owned directory.
            Directory.Delete(parent);
        }
    }

    [Fact]
    public async Task PartialContentAndReparseTargetsDoNotTriggerHydration()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        await fixture.System.Root.CreatePlaceholderAsync(CloudFilePlaceholderSpec.CreateBuilder("partial.bin", "partial-object", 8192).Build());
        CloudFile partial = fixture.System.GetFile("partial.bin");
        await using (CloudItemLease lease = await partial.AcquireLeaseAsync(new CloudItemLeaseOptions
        {
            Access = CloudItemLeaseAccess.Read | CloudItemLeaseAccess.Write,
            Foreground = true,
        }))
        await using (CloudTransfer transfer = await lease.BeginTransferAsync())
        {
            await transfer.TransferDataAsync(0, new byte[4096]);
        }

        CloudItemSnapshot snapshot = await partial.InspectAsync();
        Assert.Equal(CloudContentAvailability.PartiallyAvailable, snapshot.ContentAvailability);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>(snapshot.LocalBinding);
        CloudContentConfirmationResult result = await partial.ConfirmUploadedContentAsync(new(binding,
            CloudPlaceholderIdentity.Decode(snapshot.PlaceholderIdentity.Span), 8192, new byte[32]));
        Assert.Equal(CloudContentConfirmationOutcome.NotFullyLocal, result.Outcome);
        Assert.Equal(CloudContentAvailability.PartiallyAvailable, (await partial.InspectAsync()).ContentAvailability);
        string outside = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "outside");
        Directory.CreateDirectory(outside);
        string link = Path.Combine(fixture.Root, "junction");
        CloudFile linked = fixture.System.GetFile("junction");
        Directory.CreateSymbolicLink(link, outside);
        Assert.Throws<ArgumentException>(() => fixture.System.GetFile("junction"));
        result = await linked.ConfirmUploadedContentAsync(Proof(binding, []));
        Assert.Equal(CloudContentConfirmationOutcome.NotApplicable, result.Outcome);
        Assert.Equal(0, fixture.Provider.Fetches);
        Directory.Delete(link);
    }
}
