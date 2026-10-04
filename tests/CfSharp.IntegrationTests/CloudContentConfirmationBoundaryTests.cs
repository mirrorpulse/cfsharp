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
        await using (FileStream stream = new(fixture.File.FullPath, FileMode.CreateNew, FileAccess.Write))
        {
            stream.SetLength(length);
        }

        byte[] zeros = new byte[segment];
        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (long remaining = length; remaining > 0; remaining -= Math.Min(remaining, zeros.Length))
        {
            digest.AppendData(zeros, 0, (int)Math.Min(remaining, zeros.Length));
        }

        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudContentConfirmationRequest proof = new(binding, new CloudPlaceholderIdentity(Guid.NewGuid(), "large-object", "revision-1"),
            length, digest.GetHashAndReset(), CloudContentPreparation.ConvertRegularFile, segmentSize: segment,
            referenceBudget: TimeSpan.FromSeconds(2));
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
