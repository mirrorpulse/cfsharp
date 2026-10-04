using System.Diagnostics;
using System.Security.Cryptography;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
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
