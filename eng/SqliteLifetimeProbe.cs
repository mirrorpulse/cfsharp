using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

using CfSharp;
using CfSharp.Storage.Sqlite;
using Microsoft.Data.Sqlite;
using SQLitePCL;

internal static class SqliteLifetimeProbe
{
    private const int SampleCount = 5;

    internal static async Task RunAsync()
    {
        DirectoryInfo area = Directory.CreateTempSubdirectory("CfSharp-sqlite-lifetime-");
        try
        {
            string root = Directory.CreateDirectory(Path.Combine(area.FullName, "sync")).FullName;
            CloudStateStoreContext context = new(root);
            Console.WriteLine($"runtime\t{RuntimeInformation.ProcessArchitecture}\t{RuntimeInformation.OSDescription}");
            foreach (Assembly assembly in new[] { typeof(SqliteCloudStateStoreFactory).Assembly, typeof(CloudStateStoreContext).Assembly, typeof(SqliteConnection).Assembly })
            {
                string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
                string name = assembly.GetName().Name!;
                Console.WriteLine($"managed\t{name}\t{version}\t{Sha256(Path.Combine(AppContext.BaseDirectory, name + ".dll"))}");
            }
            Console.WriteLine("sample\tscenario\titeration\topen_ms\ttransaction_ms\tdispose_ms\twal_bytes\tshm_bytes");
            Dictionary<string, double[]> results = new(StringComparer.Ordinal);
            foreach (string scenario in new[] { "fresh", "reopened", "migrated", "read-only", "committed" })
            {
                double[] closing = new double[SampleCount];
                for (int index = 0; index < closing.Length; index++)
                {
                    string name = scenario == "reopened" ? "fresh0" : scenario + index.ToString(CultureInfo.InvariantCulture);
                    string database = Path.Combine(area.FullName, name + ".db");
                    if (scenario == "migrated")
                    {
                        await using SqliteConnection legacy = new($"Data Source={database};Pooling=False");
                        await legacy.OpenAsync();
                        await ExecuteAsync(legacy, "CREATE TABLE cfsharp_schema(singleton INTEGER PRIMARY KEY, version INTEGER NOT NULL); INSERT INTO cfsharp_schema VALUES(1, 0);");
                    }

                    SqliteCloudStateStoreFactory factory = new(database);
                    Guid operation = Guid.NewGuid();
                    byte[] payload = [1, 42, 255];
                    Stopwatch timer = Stopwatch.StartNew();
                    double open;
                    double transactionElapsed;
                    await using (ICloudStateStore store = await factory.OpenAsync(context))
                    {
                        open = timer.Elapsed.TotalMilliseconds;
                        timer.Restart();
                        if (scenario is "read-only" or "committed")
                        {
                            await using ICloudStateTransaction transaction = await store.BeginTransactionAsync();
                            if (scenario == "committed")
                            {
                                await transaction.Operations.EnqueueAsync(new(operation, CloudStateOperationKind.Create, null, payload, DateTimeOffset.UtcNow));
                                await transaction.CommitAsync();
                            }
                            else if ((await transaction.Operations.ListAsync(1)).Count != 0)
                            {
                                throw new InvalidDataException("The empty fixture has a pending operation.");
                            }
                        }
                        transactionElapsed = timer.Elapsed.TotalMilliseconds;
                        timer.Restart();
                    }
                    closing[index] = timer.Elapsed.TotalMilliseconds;
                    Console.WriteLine(FormattableString.Invariant($"provider\t{scenario}\t{index}\t{open:F3}\t{transactionElapsed:F3}\t{closing[index]:F3}\t{Size(database + "-wal")}\t{Size(database + "-shm")}"));
                    if (scenario == "committed")
                    {
                        await using ICloudStateStore store = await factory.OpenAsync(context);
                        await using ICloudStateTransaction read = await store.BeginTransactionAsync();
                        if ((await read.Operations.GetAsync(operation)) is not { } row || !row.Payload.Span.SequenceEqual(payload))
                        {
                            throw new InvalidDataException("Reopening lost the original pending operation or its payload.");
                        }
                    }
                }
                results.Add(scenario, closing);
            }

            double[] guarded = new double[3];
            for (int index = 0; index < guarded.Length; index++)
            {
                _ = await ControlAsync(area.FullName, "none", index, null);
                guarded[index] = await ControlAsync(area.FullName, "deny-delete", index, FileShare.ReadWrite);
                _ = await ControlAsync(area.FullName, "share-delete", index, FileShare.ReadWrite | FileShare.Delete);
            }

            double reference = Median(guarded);
            foreach ((string scenario, double[] samples) in results)
            {
                double median = Median(samples);
                // Use the matched control to tolerate a slower hosted runner. If SQLite no longer
                // incurs the retry plateau in the control, retain the samples for inspection.
                if (reference >= 1000 && median > reference / 2)
                {
                    throw new InvalidOperationException(FormattableString.Invariant($"{scenario} disposal median {median:F1} ms still approaches the guarded SQLite control ({reference:F1} ms)."));
                }
            }
        }
        finally
        {
            DeleteFixture(area);
        }
    }

    private static void DeleteFixture(DirectoryInfo area)
    {
        if (!string.Equals(area.Parent?.FullName, Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
                StringComparison.OrdinalIgnoreCase) || !area.Name.StartsWith("CfSharp-sqlite-lifetime-", StringComparison.Ordinal) ||
            area.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException("The SQLite lifetime fixture is outside its temporary boundary.");
        }
        area.Delete(recursive: true);
    }

    private static async Task<double> ControlAsync(string folder, string name, int index, FileShare? share)
    {
        string database = Path.Combine(folder, $"control-{name}-{index}.db");
        await using SqliteConnection connection = new($"Data Source={database};Pooling=False");
        await connection.OpenAsync();
        await ExecuteAsync(connection, "PRAGMA journal_mode=WAL; CREATE TABLE proof(value TEXT); INSERT INTO proof VALUES('original');");
        if (name == "none" && index == 0)
        {
            using Process process = Process.GetCurrentProcess();
            ProcessModule module = process.Modules.Cast<ProcessModule>().Single(module => module.ModuleName.Equals("e_sqlite3.dll", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"sqlite\t{raw.sqlite3_sourceid().utf8_to_string()}\t{Sha256(module.FileName)}");
        }
        double elapsed;
        {
            using var main = OpenControlGuard(database, share);
            using var wal = OpenControlGuard(database + "-wal", share);
            using var shm = OpenControlGuard(database + "-shm", share);
            Stopwatch timer = Stopwatch.StartNew();
            await connection.CloseAsync();
            elapsed = timer.Elapsed.TotalMilliseconds;
            Console.WriteLine(FormattableString.Invariant($"control\t{name}\t{index}\t\t\t{elapsed:F3}\t{Size(database + "-wal")}\t{Size(database + "-shm")}"));
        }
        await connection.OpenAsync();
        await using SqliteCommand verify = connection.CreateCommand();
        verify.CommandText = "SELECT value FROM proof;";
        if ((string?)await verify.ExecuteScalarAsync() != "original")
        {
            throw new InvalidDataException("The SQLite control lost its original row.");
        }
        return elapsed;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : -1;

    private static Microsoft.Win32.SafeHandles.SafeFileHandle? OpenControlGuard(string path, FileShare? share) =>
        share.HasValue ? File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, share.Value) : null;

    private static string Sha256(string path)
    {
        using FileStream binary = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(binary));
    }

    private static double Median(double[] samples) => samples.Order().ElementAt(samples.Length / 2);
}
