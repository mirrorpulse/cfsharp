using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

using CfSharp;
using CfSharp.Storage.Sqlite;

[SupportedOSPlatform("windows10.0.17134")]
internal static class OfflineReferenceObserver
{
    internal static async Task<int> RunAsync(string root, string manifestPath, string compatibilityMode)
    {
        sbyte previous = RtlSetProcessPlaceholderCompatibilityMode(1);
        if (previous < 0) { throw new InvalidOperationException($"Cannot select placeholder compatibility: {previous}."); }
        object[] observations;
        int? discoveryError = null;
        bool? discoveryExists = null;
        sbyte restoreResult = 0;
        try
        {
            using JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
            string database = manifest.RootElement.GetProperty("ObserverDatabase").GetString()!;
            await using (CloudFileSystem observer = CloudFileSystem.CreateBuilder(root)
                .WithStateStore(new SqliteCloudStateStoreFactory(database)).Build())
            {
                await observer.StartAsync();
                JsonElement[] entries = manifest.RootElement.GetProperty("Items").EnumerateArray().ToArray();
                CloudItem[] retained = entries.Select(Reference).ToArray();
                sbyte mode = sbyte.Parse(compatibilityMode, System.Globalization.CultureInfo.InvariantCulture);
                if (RtlSetProcessPlaceholderCompatibilityMode(mode) < 0) { throw new InvalidOperationException("Cannot select observation mode."); }
                CloudItem[] fresh = entries.Select(Reference).ToArray();
                // Reference construction is tested in both views. Every actual inspection
                // must expose native Cloud Files state, including the default-mode control.
                if (RtlSetProcessPlaceholderCompatibilityMode(2) < 0) { throw new InvalidOperationException("Cannot expose placeholders."); }
                List<object> results = [];
                for (int index = 0; index < entries.Length; index++)
                {
                    JsonElement entry = entries[index];
                    string relative = entry.GetProperty("Path").GetString()!;
                    CloudItemSnapshot oldSnapshot = await retained[index].InspectAsync();
                    CloudItemSnapshot freshSnapshot = await fresh[index].InspectAsync();
                    string? contentHash = null;
                    int? readError = null;
                    if (entry.GetProperty("ReadContent").GetBoolean())
                    {
                        try { contentHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(fresh[index].FullPath))); }
                        catch (IOException error) { readError = error.HResult; }
                    }
                    results.Add(new
                    {
                        Path = relative,
                        fresh[index].RelativePath,
                        Retained = Witness(oldSnapshot),
                        Fresh = Witness(freshSnapshot),
                        ContentHash = contentHash,
                        ReadError = readError,
                    });
                }
                observations = results.ToArray();
                // A name never populated beneath a partial directory is genuinely different
                // from querying an existing cloud object. Preserve its native provider error.
                try { discoveryExists = (await observer.GetFile(@"Partial\not-populated.bin").InspectAsync()).Exists; }
                catch (IOException error) { discoveryError = error.HResult; }

                CloudItem Reference(JsonElement entry) => entry.GetProperty("Directory").GetBoolean()
                    ? observer.GetDirectory(entry.GetProperty("Path").GetString()!)
                    : observer.GetFile(entry.GetProperty("Path").GetString()!);
            }
            await using ICloudStateStore store = await new SqliteCloudStateStoreFactory(database).OpenAsync(new(root));
            await using ICloudStateTransaction transaction = await store.BeginTransactionAsync();
            if ((await transaction.Operations.ListAsync(1000)).Count != 0) { throw new InvalidOperationException("Observation created journal work."); }
        }
        finally
        {
            restoreResult = RtlSetProcessPlaceholderCompatibilityMode(previous);
        }
        if (restoreResult < 0) { throw new InvalidOperationException("Cannot restore placeholder compatibility."); }
        Console.WriteLine(JsonSerializer.Serialize(new { Observations = observations, DiscoveryError = discoveryError, DiscoveryExists = discoveryExists, RestoredMode = RtlQueryProcessPlaceholderCompatibilityMode(), PreviousMode = previous }));
        return 0;
    }

    private static object Witness(CloudItemSnapshot value) => new
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

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern sbyte RtlSetProcessPlaceholderCompatibilityMode(sbyte mode);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern sbyte RtlQueryProcessPlaceholderCompatibilityMode();
}
