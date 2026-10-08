using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    private static string GetHarnessPath()
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CfSharp.sln")))
        {
            repository = repository.Parent;
        }

        Assert.NotNull(repository);
        string configuration = typeof(CloudContentConfirmationTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .Cast<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration;
        string harness = Path.Combine(repository!.FullName, "tests", "CfSharp.Storage.Sqlite.CrashHarness",
            "bin", configuration, "net10.0-windows", "CfSharp.Storage.Sqlite.CrashHarness.dll");
        Assert.True(File.Exists(harness), $"The {configuration} harness build output is missing: {harness}");
        return harness;
    }

    private static Competitor StartCompetitor(string path, string target, string action)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        string name = "Local\\CfSharp-confirmation-" + Guid.NewGuid().ToString("N");
        EventWaitHandle started = new(false, EventResetMode.ManualReset, name);
        EventWaitHandle go = new(false, EventResetMode.ManualReset, name + "-go");
        ProcessStartInfo start = new("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        if (action == "write")
        {
            start.ArgumentList.Add(GetHarnessPath());
            start.ArgumentList.Add(path);
            start.ArgumentList.Add(name);
            start.ArgumentList.Add("protected-write");
        }
        else
        {
            string mutation = action == "rename"
                ? $"[IO.File]::Move({Quote(path)}, {Quote(target)})"
                : $"[IO.File]::Move({Quote(target)}, {Quote(path)}, $true)";
            string script = $"$ErrorActionPreference='Stop'; $ready=[Threading.EventWaitHandle]::OpenExisting({Quote(name)}); $go=[Threading.EventWaitHandle]::OpenExisting({Quote(name + "-go")}); $ready.Set() | Out-Null; if (!$go.WaitOne(15000)) {{ exit 2 }}; try {{ {mutation}; exit 0 }} catch {{ $failure=$_.Exception; while ($failure.InnerException) {{ $failure=$failure.InnerException }}; exit ($failure.HResult -band 65535) }}";
            start.FileName = "pwsh";
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        }

        // Use kernel events so signaling the competitor creates no extra file-system activity.
        return new Competitor(Process.Start(start)!, started, go);
    }

    private static async Task WaitForReadyAsync(Competitor competitor)
    {
        long started = Stopwatch.GetTimestamp();
        while (!competitor.Ready.WaitOne(0) && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
        {
            Assert.False(competitor.HasExited);
            await Task.Delay(10);
        }

        Assert.True(competitor.Ready.WaitOne(0));
    }

    private sealed class Competitor(Process process, EventWaitHandle ready, EventWaitHandle go) : IDisposable
    {
        internal EventWaitHandle Ready => ready;
        internal EventWaitHandle Go => go;
        internal bool HasExited => process.HasExited;
        internal int ExitCode => process.ExitCode;
        internal Task WaitForExitAsync() => process.WaitForExitAsync();
        internal void Kill(bool entireProcessTree) => process.Kill(entireProcessTree);
        public void Dispose()
        {
            process.Dispose();
            ready.Dispose();
            go.Dispose();
        }
    }
    [Theory]
    [InlineData("write")]
    [InlineData("rename")]
    [InlineData("replace")]
    public async Task IndependentProcessCannotMutateUntilReferenceRelease(string action)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        byte[] content = "upload"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudContentConfirmationRequest proof = Proof(binding, content, CloudContentPreparation.ConvertRegularFile);
        Assert.Equal(CloudContentConfirmationOutcome.Confirmed, (await fixture.File.ConfirmUploadedContentAsync(proof)).Outcome);
        string target = Path.Combine(fixture.Root, "renamed.bin");
        if (action == "replace")
        {
            await File.WriteAllBytesAsync(target, content);
        }
        string ready = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "ready");
        await using CloudItemLease lease = await fixture.File.AcquireLeaseAsync(CloudItemLeaseOptions.ExclusiveWrite);
        await using CloudTransfer reference = await lease.BeginTransferAsync();
        using Competitor competitor = StartCompetitor(fixture.File.FullPath, target, action);
        try
        {
            await WaitForReadyAsync(competitor);
            competitor.Go.Set();
            await Task.Delay(100);
            // Some Cloud Files states reject the write open with ERROR_SHARING_VIOLATION
            // instead of waiting for an oplock break. Both outcomes prevent mutation. Retry
            // only an observed pre-release sharing conflict, while the original lease lives.
            bool sharingConflict = action == "write" && competitor.HasExited && competitor.ExitCode == 32;
            Assert.True(sharingConflict || !competitor.HasExited,
                $"Competitor completed while the reference was held: action={action}; exit={(competitor.HasExited ? competitor.ExitCode : null)}");
            long released = Stopwatch.GetTimestamp();
            await reference.DisposeAsync();
            if (sharingConflict)
            {
                using Competitor retry = StartCompetitor(fixture.File.FullPath, target, action);
                try
                {
                    await WaitForReadyAsync(retry);
                    retry.Go.Set();
                    await retry.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.Equal(0, retry.ExitCode);
                }
                finally
                {
                    if (!retry.HasExited)
                    {
                        retry.Kill(entireProcessTree: true);
                        await retry.WaitForExitAsync();
                    }
                }
            }
            else
            {
                await competitor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(competitor.ExitCode == 0, $"Competitor exit={competitor.ExitCode}; Native=0x{competitor.ExitCode:X8}");
            }

            output.WriteLine($"Competitor={action}; PreReleaseSharingConflict={sharingConflict}; WaitAfterReleaseMs={Stopwatch.GetElapsedTime(released).TotalMilliseconds}");
            await Assert.ThrowsAsync<InvalidOperationException>(() => lease.BeginTransferAsync().AsTask());
            await lease.DisposeAsync();
            if (action == "write")
            {
                Assert.Equal(CloudSynchronizationState.NotInSync, (await fixture.File.InspectAsync()).SynchronizationState);
                Assert.Equal(CloudContentConfirmationOutcome.ContentMismatch, (await fixture.File.ConfirmUploadedContentAsync(proof)).Outcome);
            }
            else if (action == "replace")
            {
                CloudItemSnapshot replacement = await fixture.File.InspectAsync();
                Assert.False(replacement.IsPlaceholder);
                Assert.NotEqual(binding, replacement.LocalBinding);
                Assert.Equal(CloudContentConfirmationOutcome.LocalObjectMismatch, (await fixture.File.ConfirmUploadedContentAsync(proof)).Outcome);
            }
            else
            {
                Assert.False((await fixture.File.InspectAsync()).Exists);
                Assert.Equal(binding, (await fixture.System.GetFile("renamed.bin").InspectAsync()).LocalBinding);
            }
        }
        finally
        {
            if (!competitor.HasExited)
            {
                competitor.Kill(entireProcessTree: true);
                await competitor.WaitForExitAsync();
            }
        }
    }

    [Fact]
    public async Task WritableMappingIsBusyUntilTheMappingIsReleased()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        byte[] content = "upload"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudContentConfirmationRequest proof = Proof(binding, content, CloudContentPreparation.ConvertRegularFile);
        using (MemoryMappedFile mapping = MemoryMappedFile.CreateFromFile(fixture.File.FullPath,
                   FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite))
        using (MemoryMappedViewAccessor view = mapping.CreateViewAccessor())
        {
            CloudContentConfirmationResult result = await fixture.File.ConfirmUploadedContentAsync(proof);
            Assert.Equal(CloudContentConfirmationOutcome.Busy, result.Outcome);
            Assert.False(result.NativeApplied);
            Assert.False(result.NativeIdentityPrepared);
        }

        Assert.Equal(CloudContentConfirmationOutcome.Confirmed, (await fixture.File.ConfirmUploadedContentAsync(proof)).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SegmentCompetitionAndSharingConflictsReleaseTheProtectedOwner(bool materialized)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        const int length = 64 * 1024 * 1024;
        await using (FileStream stream = new(fixture.File.FullPath, FileMode.CreateNew, FileAccess.Write))
        {
            byte[] block = new byte[1024 * 1024];
            for (int index = 0; index < (materialized ? 64 : 0); index++)
            {
                await stream.WriteAsync(block);
            }
            stream.SetLength(length);
        }

        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "large-object", "revision-1");
        await fixture.File.ConvertToPlaceholderAsync(identity);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] zeros = new byte[1024 * 1024];
        for (int index = 0; index < 64; index++)
        {
            hash.AppendData(zeros);
        }

        CloudContentConfirmationRequest proof = new(binding, identity, length, hash.GetHashAndReset(), segmentSize: 1024);
        string ready = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "ready");
        using CancellationTokenSource cancellation = new();
        Task<CloudContentConfirmationResult> confirmation = fixture.File.ConfirmUploadedContentAsync(proof, cancellation.Token).AsTask();
        await Task.Delay(100);
        Assert.False(confirmation.IsCompleted);
        using Competitor competitor = StartCompetitor(fixture.File.FullPath, "", "write");
        await WaitForReadyAsync(competitor);
        long competing = Stopwatch.GetTimestamp();
        competitor.Go.Set();
        try
        {
            await competitor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            // Zero-extended files can reject the native write open with ERROR_SHARING_VIOLATION
            // instead of queuing a break. The writer must succeed after the owner closes.
            bool sharingConflict = !materialized && competitor.ExitCode == 32;
            if (sharingConflict)
            {
                cancellation.Cancel();
            }
            else
            {
                Assert.Equal(0, competitor.ExitCode);
            }

            CloudContentConfirmationResult result = await confirmation.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(sharingConflict ? CloudContentConfirmationOutcome.Canceled : CloudContentConfirmationOutcome.ProtectionLost,
                result.Outcome);
            Assert.True(result.BytesVerified > 0);
            Assert.False(result.NativeApplied);
            output.WriteLine($"Materialized={materialized}; WriterWin32={competitor.ExitCode}; WaitMs={Stopwatch.GetElapsedTime(competing).TotalMilliseconds}; BytesRead={result.BytesVerified}; LongestReferenceMs={result.LongestReference.TotalMilliseconds}; Outcome={result.Outcome}; Stage={result.Stage}; HRESULT={result.Error?.HResult:X8}");
            if (sharingConflict)
            {
                using Competitor retry = StartCompetitor(fixture.File.FullPath, "", "write");
                await WaitForReadyAsync(retry);
                retry.Go.Set();
                await retry.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0, retry.ExitCode);
            }

            Assert.Equal(CloudSynchronizationState.NotInSync, (await fixture.File.InspectAsync()).SynchronizationState);
        }
        finally
        {
            if (!competitor.HasExited)
            {
                competitor.Kill(entireProcessTree: true);
                await competitor.WaitForExitAsync();
            }
        }
    }
}
