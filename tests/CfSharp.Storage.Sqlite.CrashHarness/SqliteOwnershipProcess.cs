using CfSharp;
using CfSharp.Storage.Sqlite;

internal static class SqliteOwnershipProcess
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        try
        {
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
