using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace CfSharp.Storage.Sqlite.Tests;

public sealed partial class SqliteCloudStateStoreTests
{
    [Trait("Category", "PhysicalOwnership")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HardLinkedMainDatabaseCannotReturnAnotherUsableOwner(bool differentRoot)
    {
        await SeedOwnershipAsync();
        string alias = Path.Combine(Path.GetDirectoryName(_databasePath)!, "alias.db");
        CreateHardLink(alias, _databasePath);
        Assert.Equal(ReadPhysicalIdentity(_databasePath), ReadPhysicalIdentity(alias));
        CloudStateStoreContext context = OwnershipContext(differentRoot);
        await RequireOpenFailureAsync(_databasePath, CreateContext(), SqliteCloudStateStoreError.InvalidPath);
        await RequireOpenFailureAsync(alias, context, SqliteCloudStateStoreError.InvalidPath);
        File.Delete(alias);
        await using ICloudStateStore reopened = await CreateFactory().OpenAsync(CreateContext());
        await AssertOwnershipStateAsync(reopened);
        RecordOwnershipRuntime();
    }

    [Trait("Category", "PhysicalOwnership")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SamePathAndIndependentDatabaseKeepTheirOwnershipSemantics(bool differentRoot)
    {
        await SeedOwnershipAsync();
        await using ICloudStateStore first = await CreateFactory().OpenAsync(CreateContext());
        await RequireOpenFailureAsync(_databasePath, OwnershipContext(differentRoot), SqliteCloudStateStoreError.AlreadyInUse);
        string independentPath = Path.Combine(Path.GetDirectoryName(_databasePath)!, "independent.db");
        await using ICloudStateStore independent = await new SqliteCloudStateStoreFactory(independentPath).OpenAsync(CreateContext());
        Assert.NotEqual(ReadPhysicalIdentity(_databasePath), ReadPhysicalIdentity(independentPath));
        await AssertOwnershipStateAsync(first);
    }

    [Trait("Category", "PhysicalOwnership")]
    [Theory]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData(".cfsharp.lock")]
    public async Task PreexistingHardLinkedSidecarOrLockIsRejectedWithoutChangingPublicState(string suffix)
    {
        await SeedOwnershipAsync();
        string path = _databasePath + suffix;
        // Clean shutdown may remove sidecars; an ordinary empty retained sidecar is valid.
        if (!File.Exists(path)) { await File.WriteAllBytesAsync(path, []); }
        string alias = Path.Combine(Path.GetDirectoryName(path)!, "linked-file");
        CreateHardLink(alias, path);
        Assert.Equal(ReadPhysicalIdentity(path), ReadPhysicalIdentity(alias));
        await RequireOpenFailureAsync(_databasePath, CreateContext(), SqliteCloudStateStoreError.InvalidPath);
        File.Delete(alias);
        await using ICloudStateStore reopened = await CreateFactory().OpenAsync(CreateContext());
        await AssertOwnershipStateAsync(reopened);
    }

    [Trait("Category", "PhysicalOwnership")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AliasAddedDuringOwnershipCannotAdmitASecondOwner(bool differentRoot)
    {
        await SeedOwnershipAsync();
        ICloudStateStore first = await CreateFactory().OpenAsync(CreateContext());
        string alias = Path.Combine(Path.GetDirectoryName(_databasePath)!, "late-alias.db");
        try
        {
            if (TryCreateLiveAlias(alias, _databasePath))
            {
                Assert.Equal(ReadPhysicalIdentity(_databasePath), ReadPhysicalIdentity(alias));
                await RequireOpenFailureAsync(alias, OwnershipContext(differentRoot), SqliteCloudStateStoreError.AlreadyInUse);
                // A rejected attempt cannot release the actual first owner's locks.
                await RequireOpenFailureAsync(alias, CreateContext(), SqliteCloudStateStoreError.AlreadyInUse);
            }
            await AssertOwnershipStateAsync(first);
        }
        finally { await first.DisposeAsync(); }
        if (File.Exists(alias)) { File.Delete(alias); }
        await using ICloudStateStore reopened = await CreateFactory().OpenAsync(CreateContext());
        await AssertOwnershipStateAsync(reopened);
    }

    [Trait("Category", "PhysicalOwnership")]
    [Theory]
    [InlineData("-wal", "-wal")]
    [InlineData("-shm", "-shm")]
    [InlineData("-wal", "-shm")]
    [InlineData(".cfsharp.lock", ".cfsharp.lock")]
    public async Task SharedSidecarOrLockCannotAdmitAnIndependentDatabase(string sourceSuffix, string targetSuffix)
    {
        await SeedOwnershipAsync();
        await using ICloudStateStore first = await CreateFactory().OpenAsync(CreateContext());
        string other = Path.Combine(Path.GetDirectoryName(_databasePath)!, "other.db");
        string target = other + targetSuffix;
        if (TryCreateLiveAlias(target, _databasePath + sourceSuffix))
        {
            Assert.Equal(ReadPhysicalIdentity(_databasePath + sourceSuffix), ReadPhysicalIdentity(target));
            await RequireOpenFailureAsync(other, CreateContext(), SqliteCloudStateStoreError.AlreadyInUse);
        }
        await AssertOwnershipStateAsync(first);
    }

    [Trait("Category", "PhysicalOwnership")]
    [Theory]
    [InlineData("main")]
    [InlineData("wal")]
    [InlineData("shm")]
    public async Task PhysicalOwnershipExcludesASecondOpenBeforeInitializationAndReleasesAfterFailure(string stage)
    {
        await SeedOwnershipAsync();
        string suffix = stage == "main" ? string.Empty : "-" + stage;
        string path = _databasePath + suffix;
        using (SafeFileHandle guard = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            SqliteFileOwnership.Acquire(guard, _databasePath, path);
            await RequireOpenFailureAsync(_databasePath, CreateContext(), SqliteCloudStateStoreError.AlreadyInUse);
            await RequireOpenFailureAsync(_databasePath, CreateContext(), SqliteCloudStateStoreError.AlreadyInUse);
        }
        await using ICloudStateStore reopened = await CreateFactory().OpenAsync(CreateContext());
        await AssertOwnershipStateAsync(reopened);
    }

    [Trait("Category", "PhysicalOwnership")]
    [Fact]
    public async Task FailedInitializationAndCancellationDoNotRetainPhysicalOwnership()
    {
        await SeedOwnershipAsync();
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await CreateFactory().OpenAsync(CreateContext(), canceled.Token));
        await RequireOpenFailureAsync(_databasePath, OwnershipContext(true), SqliteCloudStateStoreError.SyncRootMismatch);
        await using ICloudStateStore reopened = await CreateFactory().OpenAsync(CreateContext());
        await AssertOwnershipStateAsync(reopened);
    }

    [Trait("Category", "PhysicalOwnership")]
    [Theory]
    [InlineData("")]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData(".cfsharp.lock")]
    public async Task RetainedOwnershipFilesCannotBeRetargetedWhileStoreIsAlive(string suffix)
    {
        await SeedOwnershipAsync();
        await using ICloudStateStore first = await CreateFactory().OpenAsync(CreateContext());
        string path = _databasePath + suffix;
        PhysicalFileIdentity identity = ReadPhysicalIdentity(path);
        IOException failure = Assert.Throws<IOException>(() => File.Move(path, path + ".moved"));
        Assert.Equal(32, failure.HResult & 0xffff);
        Assert.False(File.Exists(path + ".moved"));
        Assert.Equal(identity, ReadPhysicalIdentity(path));
        await AssertOwnershipStateAsync(first);
    }

    [Trait("Category", "PhysicalOwnership")]
    [Fact]
    public void OwnershipInteropLayoutsMatchWindowsAbi()
    {
        Assert.Equal(24, Marshal.SizeOf<SqliteFileOwnership.FileStandardInfo>());
        Assert.Equal(16, Marshal.OffsetOf<SqliteFileOwnership.FileStandardInfo>("NumberOfLinks").ToInt32());
        Assert.Equal(20, Marshal.OffsetOf<SqliteFileOwnership.FileStandardInfo>("DeletePending").ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 32 : 20, Marshal.SizeOf<SqliteFileOwnership.FileOverlapped>());
        Assert.Equal(2 * IntPtr.Size, Marshal.OffsetOf<SqliteFileOwnership.FileOverlapped>("Offset").ToInt32());
    }

    [Trait("Category", "PhysicalOwnership")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeparateProcessRejectsPreexistingMainAliasesBeforeRootBinding(bool differentRoot)
    {
        await SeedOwnershipAsync();
        string alias = Path.Combine(Path.GetDirectoryName(_databasePath)!, "process-alias.db");
        CreateHardLink(alias, _databasePath);
        Assert.Equal(ReadPhysicalIdentity(alias), ReadPhysicalIdentity(_databasePath));
        foreach (string path in new[] { _databasePath, alias })
        {
            using OwnershipChild child = StartOwnershipChild(path, OwnershipContext(differentRoot).SyncRootPath);
            await child.RequireResultAsync(SqliteCloudStateStoreError.InvalidPath);
        }
        File.Delete(alias);
        await using ICloudStateStore reopened = await CreateFactory().OpenAsync(CreateContext());
        await AssertOwnershipStateAsync(reopened);
    }

    [Trait("Category", "PhysicalOwnership")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeparateProcessesKeepFirstOwnerAliveThroughDuplicateAndLateAliasAttempts(bool differentRoot)
    {
        await SeedOwnershipAsync();
        string independent = Path.Combine(Path.GetDirectoryName(_databasePath)!, "independent-process.db");
        File.Copy(_databasePath, independent);
        Assert.NotEqual(ReadPhysicalIdentity(_databasePath), ReadPhysicalIdentity(independent));
        string readyName = "CfSharp.SQLite.Ownership." + Guid.NewGuid().ToString("N");
        using EventWaitHandle ready = new(false, EventResetMode.ManualReset, readyName);
        using EventWaitHandle release = new(false, EventResetMode.ManualReset, readyName + ".release");
        using OwnershipChild first = StartOwnershipChild(_databasePath, _syncRootPath, readyName);
        try
        {
            Assert.True(await Task.Run(() => ready.WaitOne(TimeSpan.FromSeconds(15))));
            Assert.False(first.Process.HasExited);
            using (OwnershipChild separate = StartOwnershipChild(independent, _syncRootPath))
            {
                await separate.RequireResultAsync(null);
            }
            CloudStateStoreContext context = OwnershipContext(differentRoot);
            using (OwnershipChild duplicate = StartOwnershipChild(_databasePath, context.SyncRootPath))
            {
                await duplicate.RequireResultAsync(SqliteCloudStateStoreError.AlreadyInUse);
            }
            string alias = Path.Combine(Path.GetDirectoryName(_databasePath)!, "live-process-alias.db");
            if (TryCreateLiveAlias(alias, _databasePath))
            {
                Assert.Equal(ReadPhysicalIdentity(alias), ReadPhysicalIdentity(_databasePath));
                using OwnershipChild aliasAttempt = StartOwnershipChild(alias, context.SyncRootPath);
                await aliasAttempt.RequireResultAsync(SqliteCloudStateStoreError.AlreadyInUse);
            }
            Assert.False(first.Process.HasExited);
        }
        finally { release.Set(); }
        await first.RequireResultAsync(null);
        Assert.Contains("OriginalOwnerStillUsable", await first.Output);
        string lateAlias = Path.Combine(Path.GetDirectoryName(_databasePath)!, "live-process-alias.db");
        if (File.Exists(lateAlias)) { File.Delete(lateAlias); }
        await using ICloudStateStore reopened = await CreateFactory().OpenAsync(CreateContext());
        await AssertOwnershipStateAsync(reopened);
    }

    [Trait("Category", "PhysicalOwnership")]
    [Theory]
    [InlineData("main")]
    [InlineData("wal")]
    [InlineData("shm")]
    public async Task SeparateProcessCannotCrossPhysicalAcquisitionBoundary(string stage)
    {
        await SeedOwnershipAsync();
        string path = stage == "main" ? _databasePath : _databasePath + "-" + stage;
        using (SafeFileHandle guard = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            SqliteFileOwnership.Acquire(guard, _databasePath, path);
            using OwnershipChild second = StartOwnershipChild(_databasePath, _syncRootPath);
            await second.RequireResultAsync(SqliteCloudStateStoreError.AlreadyInUse);
        }
        await using ICloudStateStore reopened = await CreateFactory().OpenAsync(CreateContext());
        await AssertOwnershipStateAsync(reopened);
    }

    private static OwnershipChild StartOwnershipChild(string database, string root, string? ready = null)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CfSharp.sln"))) { directory = directory.Parent; }
        Assert.NotNull(directory);
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string harness = Path.Combine(directory.FullName, "tests", "CfSharp.Storage.Sqlite.CrashHarness", "bin",
            configuration, "net10.0-windows", "CfSharp.Storage.Sqlite.CrashHarness.dll");
        Assert.True(File.Exists(harness));
        ProcessStartInfo start = new("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[] { "exec", harness, database, root, ready is null ? "ownership-attempt" : "ownership-owner" })
        {
            start.ArgumentList.Add(argument);
        }
        if (ready is not null) { start.ArgumentList.Add(ready); start.ArgumentList.Add(ready + ".release"); }
        return new(Process.Start(start) ?? throw new InvalidOperationException("The ownership child did not start."));
    }

    private sealed class OwnershipChild(Process process) : IDisposable
    {
        internal Process Process { get; } = process;
        internal Task<string> Output { get; } = process.StandardOutput.ReadToEndAsync();
        private readonly Task<string> _error = process.StandardError.ReadToEndAsync();

        internal async Task RequireResultAsync(SqliteCloudStateStoreError? error)
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
            await Process.WaitForExitAsync(deadline.Token);
            Assert.Equal(string.Empty, await _error);
            Assert.Equal(error.HasValue ? 10 + (int)error.Value : 0, Process.ExitCode);
            Assert.Contains(error.HasValue ? "Rejected:" + error : "UsableOwner", await Output);
        }

        public void Dispose()
        {
            if (!Process.HasExited) { Process.Kill(entireProcessTree: true); Process.WaitForExit(); }
            Process.Dispose();
        }
    }

    private void RecordOwnershipRuntime()
    {
        output.WriteLine($"OS={Environment.OSVersion}; architecture={RuntimeInformation.ProcessArchitecture}; physical alias equality verified.");
        foreach (System.Reflection.Assembly assembly in new[] { typeof(CloudFileSystem).Assembly, typeof(SqliteCloudStateStoreFactory).Assembly })
        {
            using FileStream stream = File.OpenRead(assembly.Location);
            output.WriteLine($"Assembly={assembly.GetName().Name}; version={assembly.GetCustomAttributesData().Single(attribute => attribute.AttributeType == typeof(System.Reflection.AssemblyInformationalVersionAttribute)).ConstructorArguments[0].Value}; SHA256={Convert.ToHexString(SHA256.HashData(stream))}.");
        }
    }

    private bool TryCreateLiveAlias(string alias, string original)
    {
        if (CreateHardLinkW(alias, original, IntPtr.Zero)) { output.WriteLine("Live alias creation succeeded; physical exclusion is required."); return true; }
        int error = Marshal.GetLastWin32Error();
        Assert.Equal(32, error);
        Assert.False(File.Exists(alias));
        output.WriteLine("Live alias creation was denied by the retained Windows no-delete sharing handle (32).");
        return false;
    }

    private static async Task RequireOpenFailureAsync(string path, CloudStateStoreContext context, SqliteCloudStateStoreError expected)
    {
        ICloudStateStore? unexpected = null;
        try
        {
            SqliteCloudStateStoreException failure = await Assert.ThrowsAsync<SqliteCloudStateStoreException>(async () =>
                unexpected = await new SqliteCloudStateStoreFactory(path).OpenAsync(context));
            Assert.Equal(expected, failure.Error);
            if (expected == SqliteCloudStateStoreError.AlreadyInUse)
            {
                Assert.True(Assert.IsType<Win32Exception>(failure.InnerException).NativeErrorCode is 32 or 33);
            }
        }
        finally { if (unexpected is not null) { await unexpected.DisposeAsync(); } }
    }

    private async Task SeedOwnershipAsync()
    {
        await using ICloudStateStore store = await CreateFactory().OpenAsync(CreateContext());
        await using ICloudStateTransaction transaction = await store.BeginTransactionAsync();
        Guid id = Guid.Parse("7811b0dc-bff0-42b0-b376-d256878c2a5e");
        await transaction.Items.UpsertAsync(new(id, "ownership-item", "item.txt", CloudItemKind.File,
            "v1", null, false, DateTimeOffset.UtcNow));
        await transaction.Checkpoints.UpsertAsync(new("ownership", new byte[] { 11, 22 }, DateTimeOffset.UtcNow));
        await transaction.Operations.EnqueueAsync(new(Guid.Parse("ee3ca377-f49c-4279-86b9-552ce5c7f21c"),
            CloudStateOperationKind.MetadataUpdate, id, new byte[] { 33, 44 }, DateTimeOffset.UtcNow));
        await transaction.CommitAsync();
    }

    private static async Task AssertOwnershipStateAsync(ICloudStateStore store)
    {
        await using ICloudStateTransaction transaction = await store.BeginTransactionAsync();
        Assert.Equal(new byte[] { 11, 22 }, (await transaction.Checkpoints.GetAsync("ownership"))!.Value.ToArray());
        CloudOperationJournalEntry operation = (await transaction.Operations.GetAsync(
            Guid.Parse("ee3ca377-f49c-4279-86b9-552ce5c7f21c")))!;
        Assert.Equal(Guid.Parse("7811b0dc-bff0-42b0-b376-d256878c2a5e"), operation.ItemId);
        Assert.Equal(new byte[] { 33, 44 }, operation.Payload.ToArray());
    }

    private CloudStateStoreContext OwnershipContext(bool differentRoot)
    {
        string root = differentRoot ? Path.Combine(_temporaryDirectory, "other-root") : _syncRootPath;
        Directory.CreateDirectory(root);
        return new(root);
    }

    private static void CreateHardLink(string alias, string original)
    {
        if (!CreateHardLinkW(alias, original, IntPtr.Zero)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
    }

    private static PhysicalFileIdentity ReadPhysicalIdentity(string path)
    {
        // Attribute-only queries do not request data access or participate in the exclusive
        // data-sharing contract of the ownership-lock file. They still identify that object.
        using SafeFileHandle handle = OpenForPhysicalIdentity(path, 0x80, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        if (!GetPhysicalFileIdentity(handle, 18, out PhysicalFileIdentity identity, 24))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        return identity;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PhysicalFileIdentity(ulong Volume, ulong Low, ulong High);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenForPhysicalIdentity(string path, uint access, uint share,
        IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalFileIdentity(SafeFileHandle handle, int informationClass,
        out PhysicalFileIdentity information, uint size);
}
