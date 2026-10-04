using System.Diagnostics;
using System.Text.Json;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Fact]
    public async Task ProcessExitAfterNativeMarkReplaysTheRetainedProofAndRepairsSqlite()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        byte[] content = "upload"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudContentConfirmationRequest request = Proof(binding, content, CloudContentPreparation.ConvertRegularFile);
        string database = Path.Combine(Path.GetDirectoryName(fixture.Root)!, "state.db");
        // This retained application proof lives outside the root and the official state database.
        await File.WriteAllTextAsync(database + ".confirmation.json", JsonSerializer.Serialize(new
        {
            Volume = binding.VolumeSerialNumber,
            Root = binding.SyncRootFileId,
            File = binding.LocalFileId,
            Item = request.AcceptedIdentity.ItemId,
            Remote = request.AcceptedIdentity.RemoteId,
            Revision = request.AcceptedIdentity.RemoteRevision,
            Length = request.ExpectedLength,
            Hash = request.ExpectedSha256.ToArray(),
        }));
        await fixture.System.DisposeAsync();
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CfSharp.sln")))
        {
            repository = repository.Parent;
        }

        Assert.NotNull(repository);
        ProcessStartInfo start = new("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Path.Combine(repository!.FullName, "tests", "CfSharp.Storage.Sqlite.CrashHarness",
            "bin", "Release", "net10.0-windows", "CfSharp.Storage.Sqlite.CrashHarness.dll"));
        start.ArgumentList.Add(database);
        start.ArgumentList.Add(fixture.Root);
        start.ArgumentList.Add("protected-confirmation");
        using Process child = Process.Start(start)!;
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(91, child.ExitCode);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
        }

        await fixture.RestartAsync();
        CloudItemSnapshot beforeReplay = await fixture.File.InspectAsync();
        Assert.Equal(CloudSynchronizationState.InSync, beforeReplay.SynchronizationState);
        Assert.Null(beforeReplay.RemoteRevision);
        Assert.Equal(binding, beforeReplay.LocalBinding);
        CloudContentConfirmationResult repaired = await fixture.File.ConfirmUploadedContentAsync(request);
        Assert.Equal(CloudContentConfirmationOutcome.AlreadyConfirmed, repaired.Outcome);
        Assert.False(repaired.NativeApplied);
        Assert.True(repaired.NativeConfirmationVerified);
        Assert.True(repaired.DurableProjectionCommitted);
        Assert.Equal(content.Length, repaired.BytesVerified);
        Assert.Equal(request.AcceptedIdentity.RemoteRevision, (await fixture.File.InspectAsync()).RemoteRevision);
        CloudContentConfirmationResult replay = await fixture.File.ConfirmUploadedContentAsync(request);
        Assert.Equal(CloudContentConfirmationOutcome.AlreadyConfirmed, replay.Outcome);
        Assert.False(replay.NativeApplied);
    }
}
