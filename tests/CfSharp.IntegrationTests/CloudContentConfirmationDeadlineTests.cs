using System.Diagnostics;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Fact]
    public async Task DelayedTimerAndNativeSchedulingCannotMarkAfterThePublicEntryDeadline()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        await File.WriteAllBytesAsync(fixture.File.FullPath, []);
        CloudPlaceholderIdentity accepted = new(Guid.NewGuid(), "deadline-object", "revision-1");
        await fixture.File.ConvertToPlaceholderAsync(accepted);
        await fixture.File.SetInSyncAsync(false);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        await fixture.System.DisposeAsync();
        ProcessStartInfo start = new("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(GetHarnessPath());
        start.ArgumentList.Add(Path.Combine(Path.GetDirectoryName(fixture.Root)!, "state.db"));
        start.ArgumentList.Add(fixture.Root);
        start.ArgumentList.Add("protected-deadline");
        using Process child = Process.Start(start)!;
        try
        {
            Task<string> receipt = child.StandardOutput.ReadToEndAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            output.WriteLine(await receipt);
            Assert.Equal(0, child.ExitCode);
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
        CloudItemSnapshot after = await fixture.File.InspectAsync();
        Assert.Equal(binding, after.LocalBinding);
        Assert.Equal(accepted.Encode(), after.PlaceholderIdentity.ToArray());
        Assert.Equal(CloudSynchronizationState.NotInSync, after.SynchronizationState);
        Assert.Equal(0, fixture.Provider.Fetches);
    }
}
