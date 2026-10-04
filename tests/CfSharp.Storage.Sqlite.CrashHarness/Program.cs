using CfSharp;
using CfSharp.Storage.Sqlite;

if (args.Length == 3 && args[2] == "protected-confirmation" && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
{
    return await ProtectedConfirmationCrash.RunAsync(args[0], args[1]);
}

if (args.Length == 3 && args[2] == "protected-write")
{
    using EventWaitHandle ready = EventWaitHandle.OpenExisting(args[1]);
    using EventWaitHandle go = EventWaitHandle.OpenExisting(args[1] + "-go");
    ready.Set();
    if (!go.WaitOne(TimeSpan.FromSeconds(15)))
    {
        return 2;
    }

    try
    {
        using FileStream writer = new(args[0], FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writer.WriteByte(88);
        return 0;
    }
    catch (IOException exception)
    {
        return exception.HResult & 0xffff;
    }
}

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: <database-path> <sync-root-path> <before-commit|after-commit>");
    return 2;
}

string databasePath = Path.GetFullPath(args[0]);
string syncRootPath = Path.GetFullPath(args[1]);
string mode = args[2];
if (mode is not ("before-commit" or "after-commit"))
{
    Console.Error.WriteLine($"Unknown crash mode '{mode}'.");
    return 2;
}

Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
Directory.CreateDirectory(syncRootPath);
string markerPath = databasePath + "." + mode + ".started";
File.WriteAllText(markerPath, "started");
Guid itemId = Guid.Parse("11111111-1111-1111-1111-111111111111");

await using ICloudStateStore store = await new SqliteCloudStateStoreFactory(databasePath)
    .OpenAsync(new CloudStateStoreContext(syncRootPath));
await using ICloudStateTransaction transaction = await store.BeginTransactionAsync();
await transaction.Items.UpsertAsync(
    new CloudItemState(
        itemId,
        "crash-recovery-item",
        "crash-recovery.txt",
        CloudItemKind.File,
        "revision-1",
        null,
        false,
        DateTimeOffset.UtcNow));

if (mode == "before-commit")
{
    Environment.FailFast("Intentional crash before SQLite commit.");
}

await transaction.CommitAsync();
if (mode == "after-commit")
{
    Environment.FailFast("Intentional crash after SQLite commit.");
}

return 0;
