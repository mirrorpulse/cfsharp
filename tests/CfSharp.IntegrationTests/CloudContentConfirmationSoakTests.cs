using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Fact]
    [Trait("Category", "LongSoak")]
    public async Task ConfirmationResourcesRemainBoundedDuringContinuousRun()
    {
        string? minutesValue = Environment.GetEnvironmentVariable("CFSHARP_SOAK_DURATION_MINUTES");
        TimeSpan duration = int.TryParse(minutesValue, out int minutes) && minutes > 0
            ? TimeSpan.FromMinutes(Math.Min(minutes, 1440)) : TimeSpan.FromSeconds(30);
        string artifact = Environment.GetEnvironmentVariable("CFSHARP_CONFIRMATION_SOAK_ARTIFACT")
            ?? Path.Combine(AppContext.BaseDirectory, "confirmation-soak.json");
        FaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new FaultFactory(path));
        byte[] content = new byte[16 * 1024 * 1024];
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudPlaceholderIdentity accepted = new(Guid.NewGuid(), "soak-object", "revision-1");
        await fixture.File.ConvertToPlaceholderAsync(accepted);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        byte[] digest = SHA256.HashData(content);
        CloudContentConfirmationRequest proof = new(binding, accepted, content.Length, digest,
            segmentSize: 64 * 1024, referenceBudget: TimeSpan.FromSeconds(2));
        CloudContentConfirmationRequest slow = new(binding, accepted, content.Length, digest,
            segmentSize: 128, referenceBudget: TimeSpan.FromSeconds(2));
        int[] outcomes = new int[5];
        int cycles = 0;
        List<ConfirmationResourceSample> samples = [];
        JsonSerializerOptions jsonOptions = new() { WriteIndented = true };
        using Process process = Process.GetCurrentProcess();
        Stopwatch watch = new();
        bool passed = false;
        try
        {
            // Warm native reads, SQLite rollback/retry, and the independent writer before
            // measuring retained resources. No forced GC occurs inside an active reference.
            await CycleAsync();
            Array.Clear(outcomes);
            watch.Start();
            Sample();
            while (watch.Elapsed < duration)
            {
                await CycleAsync();
                cycles++;
                if (watch.ElapsedMilliseconds - samples[^1].ElapsedMilliseconds >= 10_000)
                {
                    Sample();
                    WriteArtifact("running");
                }
            }

            Sample();
            Assert.True(cycles > 1);
            Assert.All(outcomes, count => Assert.Equal(cycles, count));
            Assert.Equal(0, fixture.Provider.Fetches);
            ConfirmationResourceSample baseline = samples[0];
            foreach (ConfirmationResourceSample sample in samples)
            {
                Assert.True(sample.Handles <= baseline.Handles + 128, $"Handle growth: {sample.Handles - baseline.Handles}");
                Assert.True(sample.PinnedObjects <= baseline.PinnedObjects + 16, $"Pinned-object growth: {sample.PinnedObjects - baseline.PinnedObjects}");
                Assert.True(sample.PrivateBytes <= baseline.PrivateBytes + 256L * 1024 * 1024, $"Private-byte growth: {sample.PrivateBytes - baseline.PrivateBytes}");
                Assert.True(sample.ManagedBytes <= baseline.ManagedBytes + 64L * 1024 * 1024, $"Managed-byte growth: {sample.ManagedBytes - baseline.ManagedBytes}");
            }

            passed = true;
        }
        finally
        {
            WriteArtifact(passed ? "passed" : "failed");
        }

        void Sample()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            process.Refresh();
            samples.Add(new(watch.ElapsedMilliseconds, process.HandleCount,
                GC.GetGCMemoryInfo().PinnedObjectsCount, process.PrivateMemorySize64, GC.GetTotalMemory(false)));
        }

        void WriteArtifact(string status)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(artifact))!);
            File.WriteAllText(artifact, JsonSerializer.Serialize(new
            {
                status,
                durationSeconds = watch.Elapsed.TotalSeconds,
                requestedDurationSeconds = duration.TotalSeconds,
                architecture = RuntimeInformation.OSArchitecture.ToString(),
                osVersion = Environment.OSVersion.Version.ToString(),
                cycles,
                confirmed = outcomes[0],
                canceledDuringRead = outcomes[1],
                nativeProtectionLost = outcomes[2],
                projectionFailed = outcomes[3],
                replayed = outcomes[4],
                samples,
            }, jsonOptions));
        }

        async Task CycleAsync()
        {
            await fixture.File.SetInSyncAsync(false);
            CloudContentConfirmationResult confirmed = await fixture.File.ConfirmUploadedContentAsync(proof);
            Assert.Equal(CloudContentConfirmationOutcome.Confirmed, confirmed.Outcome);
            Assert.True(confirmed.NativeApplied && confirmed.DurableProjectionCommitted);
            outcomes[0]++;

            await fixture.File.SetInSyncAsync(false);
            using (CancellationTokenSource cancellation = new())
            {
                Task<CloudContentConfirmationResult> reading = fixture.File.ConfirmUploadedContentAsync(slow, cancellation.Token).AsTask();
                await Task.Delay(100);
                Assert.False(reading.IsCompleted);
                cancellation.Cancel();
                CloudContentConfirmationResult canceled = await reading.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(CloudContentConfirmationOutcome.Canceled, canceled.Outcome);
                Assert.True(canceled.BytesVerified > 0);
                Assert.False(canceled.NativeApplied);
                outcomes[1]++;
            }

            using (Competitor writer = StartCompetitor(fixture.File.FullPath, "", "write"))
            {
                try
                {
                    await WaitForReadyAsync(writer);
                    Task<CloudContentConfirmationResult> reading = fixture.File.ConfirmUploadedContentAsync(slow).AsTask();
                    await Task.Delay(100);
                    Assert.False(reading.IsCompleted);
                    writer.Go.Set();
                    await writer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(0, writer.ExitCode);
                    CloudContentConfirmationResult broken = await reading.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(CloudContentConfirmationOutcome.ProtectionLost, broken.Outcome);
                    Assert.True(broken.BytesVerified > 0);
                    Assert.False(broken.NativeApplied);
                    outcomes[2]++;
                }
                finally
                {
                    if (!writer.HasExited)
                    {
                        writer.Kill(entireProcessTree: true);
                        await writer.WaitForExitAsync();
                    }
                }
            }

            using (FileStream restore = new(fixture.File.FullPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                restore.WriteByte(content[0]);
            }

            factory!.NextCommitFault = new ProjectionFault();
            CloudContentConfirmationResult pending = await fixture.File.ConfirmUploadedContentAsync(proof);
            Assert.Equal(CloudContentConfirmationOutcome.NativeAppliedProjectionPending, pending.Outcome);
            Assert.True(pending.NativeApplied);
            Assert.False(pending.DurableProjectionCommitted);
            outcomes[3]++;
            CloudContentConfirmationResult replayed = await fixture.File.ConfirmUploadedContentAsync(proof);
            Assert.Equal(CloudContentConfirmationOutcome.AlreadyConfirmed, replayed.Outcome);
            Assert.True(replayed.DurableProjectionCommitted);
            Assert.Equal(binding, (await fixture.File.InspectAsync()).LocalBinding);
            outcomes[4]++;
        }
    }

    private readonly record struct ConfirmationResourceSample(long ElapsedMilliseconds, int Handles,
        long PinnedObjects, long PrivateBytes, long ManagedBytes);
}
