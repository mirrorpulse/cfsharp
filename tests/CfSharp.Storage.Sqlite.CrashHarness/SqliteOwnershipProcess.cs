using CfSharp;
using CfSharp.Storage.Sqlite;

internal static class SqliteOwnershipProcess
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        try
        {
            if (arguments[2] == "ownership-provider-startup")
            {
                string parent = Path.GetDirectoryName(arguments[0])!;
                if (Directory.Exists(parent)) { throw new InvalidOperationException("The configuration-only fixture already exists."); }
                SqliteCloudStateStoreFactory factory = new(arguments[0]);
                string?[] loaded = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name).ToArray();
                if (Directory.Exists(parent) || !loaded.Contains("System.Runtime.InteropServices.RuntimeInformation", StringComparer.Ordinal))
                {
                    throw new InvalidOperationException("Provider dependencies must initialize before any database file access.");
                }
                await using (ICloudStateStore initial = await factory.OpenAsync(new(arguments[1])))
                {
                    await using ICloudStateTransaction write = await initial.BeginTransactionAsync();
                    await write.Checkpoints.UpsertAsync(new("ownership", new byte[] { 11, 22 }, DateTimeOffset.UtcNow));
                    await write.CommitAsync();
                }
                Console.WriteLine("ProviderInitializedBeforeStoreIO");
            }
            await using ICloudStateStore store = await new SqliteCloudStateStoreFactory(arguments[0])
                .OpenAsync(new CloudStateStoreContext(arguments[1]));
            await VerifyCheckpointAsync(store);
            Console.WriteLine("UsableOwner");
            if (arguments[2] == "ownership-owner" && arguments.Length == 5)
            {
                using EventWaitHandle ready = EventWaitHandle.OpenExisting(arguments[3]);
                using EventWaitHandle release = EventWaitHandle.OpenExisting(arguments[4]);
                ready.Set();
                if (!release.WaitOne(TimeSpan.FromSeconds(30))) { return 3; }
                await VerifyCheckpointAsync(store);
                Console.WriteLine("OriginalOwnerStillUsable");
            }
            return 0;
        }
        catch (SqliteCloudStateStoreException exception)
        {
            Console.WriteLine($"Rejected:{exception.Error}");
            return 10 + (int)exception.Error;
        }
    }

    private static async Task VerifyCheckpointAsync(ICloudStateStore store)
    {
        await using ICloudStateTransaction transaction = await store.BeginTransactionAsync();
        if ((await transaction.Checkpoints.GetAsync("ownership")) is not { } checkpoint ||
            !checkpoint.Value.Span.SequenceEqual(new byte[] { 11, 22 }))
        {
            throw new InvalidDataException("The original public checkpoint was not retained.");
        }
    }
}
