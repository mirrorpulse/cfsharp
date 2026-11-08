using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Trait("Category", "OfflineReference")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfflinePlaceholderReferencesPreserveNativeStateInIndependentProcess(bool exposeBeforeReference)
    {
        using WindowsIdentity user = WindowsIdentity.GetCurrent();
        string architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        if (Environment.GetEnvironmentVariable("CFSHARP_EXPECTED_TEST_ARCHITECTURE") is string expectedArchitecture)
        {
            Assert.Equal(expectedArchitecture, architecture, ignoreCase: true);
        }
        if (Environment.GetEnvironmentVariable("CFSHARP_EXPECTED_TEST_SID") is string expectedSid)
        {
            Assert.Equal(expectedSid, user.User?.Value);
            Assert.False(new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator));
        }
        output.WriteLine($"Offline producer: OS={Environment.OSVersion}; architecture={architecture}; SID={user.User?.Value}; exposed={exposeBeforeReference}.");
        string? database = null;
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => { database = path; return factory = new(path); });
        string area = Path.GetDirectoryName(database!)!;
        string ordinary = Path.Combine(fixture.Root, "Ordinary");
        string deep = Path.Combine(fixture.Root, "Docs", "Deep");
        string partial = Path.Combine(fixture.Root, "Partial");
        Directory.CreateDirectory(ordinary);
        Directory.CreateDirectory(deep);
        Directory.CreateDirectory(partial);
        string[] resident = ["content.bin", @"Ordinary\ordinary.bin", @"Ordinary\plain.bin", @"Docs\child.bin", @"Docs\Deep\child.bin", @"Partial\resident.bin"];
        byte[] content = "offline resident evidence"u8.ToArray();
        foreach (string path in resident) { await File.WriteAllBytesAsync(Path.Combine(fixture.Root, path), content); }
        foreach (string path in new[] { @"Docs\Deep", "Docs", "Partial" })
        {
            await fixture.System.GetDirectory(path).ConvertToPlaceholderAsync(new(Guid.NewGuid(), path, "v1"),
                CloudPlaceholderConversionOptions.CreateBuilder().WithPopulationState(path == "Partial"
                    ? CloudDirectoryPopulationState.Partial : CloudDirectoryPopulationState.Complete).Build());
        }
        foreach (string path in resident.Where(path => path != @"Ordinary\plain.bin"))
        {
            await fixture.System.GetFile(path).ConvertToPlaceholderAsync(new(Guid.NewGuid(), path, "v1"));
        }
        await fixture.System.Root.CreatePlaceholderAsync(CloudFilePlaceholderSpec.CreateBuilder("online-only.bin",
            new CloudPlaceholderIdentity(Guid.NewGuid(), "online-only", "v1"), 64).Build());
        Directory.CreateDirectory(Path.Combine(ordinary, "Deleted"));
        Directory.Delete(Path.Combine(ordinary, "Deleted"));

        string[] directories = [string.Empty, "Ordinary", "Docs", @"Docs\Deep", "Partial", @"Ordinary\Deleted\missing"];
        string[] files = [.. resident, "online-only.bin", @"Ordinary\missing.bin", @"Docs\missing.bin", @"Ordinary\Deleted\missing.bin"];
        var items = directories.Select(path => new { Path = path, Directory = true, ReadContent = false })
            .Concat(files.Select(path => new { Path = path, Directory = false, ReadContent = resident.Contains(path) || path == "online-only.bin" })).ToArray();
        Dictionary<string, JsonElement> expected = [];
        foreach (var item in items)
        {
            CloudItem reference = item.Directory ? fixture.System.GetDirectory(item.Path) : fixture.System.GetFile(item.Path);
            CloudItemSnapshot snapshot = await reference.InspectAsync();
            if (resident.Contains(item.Path) && item.Path != @"Ordinary\plain.bin")
            {
                Assert.True(snapshot.IsPlaceholder);
                Assert.NotEmpty(snapshot.PlaceholderIdentity.ToArray());
                Assert.NotNull(snapshot.LocalBinding);
                Assert.Equal(CloudContentAvailability.FullyAvailable, snapshot.ContentAvailability);
            }
            if (item.Path == "online-only.bin") { Assert.Equal(CloudContentAvailability.OnlineOnly, snapshot.ContentAvailability); }
            expected.Add(item.Path, JsonSerializer.SerializeToElement(OfflineWitness(snapshot)));
        }
        CloudItemSnapshot original = await fixture.File.InspectAsync();
        await using (ICloudStateTransaction transaction = await factory!.Store!.BeginTransactionAsync())
        {
            await transaction.Operations.EnqueueAsync(new(Guid.NewGuid(), CloudStateOperationKind.ContentUpdate,
                original.ItemId, [1, 3, 5, 7], DateTimeOffset.UtcNow));
            await transaction.CommitAsync();
        }
        IReadOnlyList<CloudOperationJournalEntry> journal = await AccessRowsAsync(factory!);
        Assert.NotEmpty(journal);
        await fixture.System.DisposeAsync();
        byte[] databaseHash = SHA256.HashData(await File.ReadAllBytesAsync(database!));
        string manifest = Path.Combine(area, "observer.json");
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new { ObserverDatabase = Path.Combine(area, "observer.db"), Items = items }));
        ProcessStartInfo start = new("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { GetHarnessPath(), "offline-reference", fixture.Root, manifest, exposeBeforeReference ? "2" : "0" })
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        string result = await stdout;
        string error = await stderr;
        output.WriteLine(result);
        Assert.True(process.ExitCode == 0, $"Observer exit={process.ExitCode}; error={error}; output={result}");
        using JsonDocument observation = JsonDocument.Parse(result);
        JsonElement discoveryError = observation.RootElement.GetProperty("DiscoveryError");
        if (discoveryError.ValueKind == JsonValueKind.Null)
        {
            // Windows may already know that a missing name is absent. If discovery is
            // provider-dependent, only its actual stopped-provider error is acceptable.
            Assert.False(observation.RootElement.GetProperty("DiscoveryExists").GetBoolean());
        }
        else { Assert.Equal(unchecked((int)0x8007016A), discoveryError.GetInt32()); }
        Assert.Equal(observation.RootElement.GetProperty("PreviousMode").GetSByte(), observation.RootElement.GetProperty("RestoredMode").GetSByte());
        JsonElement[] rows = observation.RootElement.GetProperty("Observations").EnumerateArray().ToArray();
        Assert.Equal(items.Length, rows.Length);
        foreach (JsonElement row in rows)
        {
            string path = row.GetProperty("Path").GetString()!;
            Assert.Equal(path, row.GetProperty("RelativePath").GetString());
            Assert.True(JsonElement.DeepEquals(expected[path], row.GetProperty("Fresh")), $"Fresh native state changed: {path}; {row}");
            Assert.True(JsonElement.DeepEquals(expected[path], row.GetProperty("Retained")), $"Retained native state changed: {path}; {row}");
            if (resident.Contains(path)) { Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), row.GetProperty("ContentHash").GetString()); }
            if (path == "online-only.bin") { Assert.Equal(unchecked((int)0x8007016A), row.GetProperty("ReadError").GetInt32()); }
        }
        Assert.Equal(databaseHash, SHA256.HashData(await File.ReadAllBytesAsync(database!)));
        await fixture.RestartAsync();
        AssertOriginalAccessRows(journal, await AccessRowsAsync(factory!));
        Assert.Equal(journal.Count, (await AccessRowsAsync(factory!)).Count);
        Assert.Equal(0, fixture.Provider.Fetches);
    }

    private static object OfflineWitness(CloudItemSnapshot value) => new
    {
        value.Exists,
        value.Kind,
        value.Attributes,
        value.Length,
        value.PlaceholderState,
        value.ContentAvailability,
        value.PinState,
        value.SynchronizationState,
        value.LocalFileId,
        value.SyncRootFileId,
        value.LocalBinding,
        value.OnDiskDataSize,
        value.ValidatedDataSize,
        value.ModifiedDataSize,
        value.PropertyDataSize,
        Identity = Convert.ToHexString(value.PlaceholderIdentity.Span),
    };
}
