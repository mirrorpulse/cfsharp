using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace CfSharp.Storage.Sqlite;

/// <summary>Creates official SQLite durable-state stores for CfSharp file systems.</summary>
/// <remarks>
/// <para>
/// The caller supplies an absolute database path. The path, its WAL and shared-memory sidecars,
/// and the CfSharp ownership lock must remain outside the managed sync root. Existing directory
/// junctions and symbolic links are resolved before this invariant is checked.
/// </para>
/// <para>
/// Opening acquires an exclusive cross-process ownership lock, creates or migrates the schema,
/// enables foreign keys and WAL mode, and applies the configured busy timeout. One database may
/// be owned by only one open store and is permanently bound to one sync-root path. Dispose the
/// returned store to release process ownership.
/// </para>
/// <para>
/// The factory is immutable and safe for concurrent calls, although concurrent opens for the same
/// database deterministically allow only one owner. The returned store serializes SQLite writes;
/// each transaction and its repositories remain single-consumer objects as required by the core
/// state-store contract. This provider supports Windows only.
/// </para>
/// </remarks>
public sealed class SqliteCloudStateStoreFactory : ICloudStateStoreFactory
{
    /// <summary>The default time SQLite waits for a conflicting lock.</summary>
    public static readonly TimeSpan DefaultBusyTimeout = TimeSpan.FromSeconds(5);

    private readonly int _busyTimeoutMilliseconds;

    /// <summary>Initializes a factory for an explicit SQLite database path.</summary>
    /// <param name="databasePath">
    /// Absolute path of the database. It must be outside every sync root that uses this factory.
    /// </param>
    /// <param name="busyTimeout">
    /// Optional positive lock-wait timeout. The default is five seconds.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The path is empty, relative, names a directory, or has no file name.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The timeout is not positive or exceeds the SQLite integer millisecond range.
    /// </exception>
    public SqliteCloudStateStoreFactory(string databasePath, TimeSpan? busyTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (!Path.IsPathFullyQualified(databasePath))
        {
            throw new ArgumentException("The SQLite database path must be fully qualified.", nameof(databasePath));
        }

        string normalizedPath = Path.GetFullPath(databasePath);
        if (string.IsNullOrEmpty(Path.GetFileName(normalizedPath)) ||
            Directory.Exists(normalizedPath))
        {
            throw new ArgumentException("The SQLite database path must name a file.", nameof(databasePath));
        }

        TimeSpan selectedTimeout = busyTimeout ?? DefaultBusyTimeout;
        if (selectedTimeout <= TimeSpan.Zero || selectedTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(busyTimeout),
                selectedTimeout,
                "The SQLite busy timeout must be positive and fit in integer milliseconds.");
        }

        DatabasePath = normalizedPath;
        BusyTimeout = selectedTimeout;
        _busyTimeoutMilliseconds = checked((int)Math.Ceiling(selectedTimeout.TotalMilliseconds));
    }

    /// <summary>Gets the normalized absolute database path.</summary>
    public string DatabasePath { get; }

    /// <summary>Gets the configured SQLite lock-wait timeout.</summary>
    public TimeSpan BusyTimeout { get; }

    /// <summary>Opens, creates, or migrates the configured SQLite state database.</summary>
    /// <param name="context">The validated sync root that permanently owns the database.</param>
    /// <param name="cancellationToken">Token that cancels opening before ownership transfers.</param>
    /// <returns>
    /// An exclusively owned transactional store. The caller must dispose it after every active
    /// transaction has terminated.
    /// </returns>
    /// <exception cref="OperationCanceledException">Opening was canceled.</exception>
    /// <exception cref="SqliteCloudStateStoreException">
    /// The path is unsafe, the database is already owned or bound to another root, its schema is
    /// incompatible, or SQLite could not initialize it. SQLite result codes are retained when
    /// available.
    /// </exception>
    public async ValueTask<ICloudStateStore> OpenAsync(
        CloudStateStoreContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        ValidateSafeLocation(context.SyncRootPath);
        string? parentPath = Path.GetDirectoryName(DatabasePath);
        if (string.IsNullOrEmpty(parentPath))
        {
            throw CreateException(
                SqliteCloudStateStoreError.InvalidPath,
                "The SQLite database must have a parent directory.");
        }

        try
        {
            Directory.CreateDirectory(parentPath);
            ValidateSafeLocation(context.SyncRootPath);
        }
        catch (SqliteCloudStateStoreException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw CreateException(
                SqliteCloudStateStoreError.InvalidPath,
                "The SQLite database directory could not be prepared.",
                exception);
        }

        FileStream ownerLock = AcquireOwnerLock();
        SqlitePathHandleLease? databaseLease = null;
        string connectionString = CreateConnectionString();
        try
        {
            // Hold the verified parent chain and database object while SQLite opens its own
            // handles. The handles deny delete sharing, so a writable peer cannot replace the
            // checked path components between validation and SQLite's path-based open.
            databaseLease = SqlitePathHandleLease.Open(DatabasePath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            await ownerLock.DisposeAsync().ConfigureAwait(false);
            throw CreateException(
                SqliteCloudStateStoreError.InvalidPath,
                "The SQLite database path could not be bound to verified handles.",
                exception);
        }

        try
        {
            ValidateSafeLocation(context.SyncRootPath);
            await SqliteSchema.InitializeAsync(
                connectionString,
                _busyTimeoutMilliseconds,
                DatabasePath,
                context.SyncRootPath,
                cancellationToken).ConfigureAwait(false);
            return new SqliteCloudStateStore(
                connectionString,
                _busyTimeoutMilliseconds,
                DatabasePath,
                ownerLock,
                databaseLease);
        }
        catch
        {
            databaseLease?.Dispose();
            try
            {
                SqliteConnectionPool.Clear(connectionString);
            }
            finally
            {
                await ownerLock.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    private void ValidateSafeLocation(string syncRootPath)
    {
        try
        {
            string physicalRoot = SqlitePathSafety.ResolvePhysicalPath(syncRootPath, isDirectory: true);
            string physicalDatabase = SqlitePathSafety.ResolvePhysicalPath(
                DatabasePath,
                isDirectory: false);
            if (SqlitePathSafety.IsSameOrChild(physicalRoot, physicalDatabase))
            {
                throw CreateException(
                    SqliteCloudStateStoreError.PathInsideSyncRoot,
                    "The SQLite state database must be outside the managed sync root.");
            }

            if (File.Exists(DatabasePath) &&
                (File.GetAttributes(DatabasePath) & FileAttributes.ReparsePoint) != 0)
            {
                throw CreateException(
                    SqliteCloudStateStoreError.InvalidPath,
                    "The SQLite database file cannot be a reparse point.");
            }

            foreach (string sidecarPath in new[] { DatabasePath + "-wal", DatabasePath + "-shm" })
            {
                if ((File.Exists(sidecarPath) || Directory.Exists(sidecarPath)) &&
                    (File.GetAttributes(sidecarPath) & FileAttributes.ReparsePoint) != 0)
                {
                    throw CreateException(
                        SqliteCloudStateStoreError.InvalidPath,
                        $"The SQLite sidecar '{sidecarPath}' cannot be a reparse point.");
                }
            }

            string lockPath = DatabasePath + ".cfsharp.lock";
            if (File.Exists(lockPath) || Directory.Exists(lockPath))
            {
                FileAttributes attributes = File.GetAttributes(lockPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw CreateException(
                        SqliteCloudStateStoreError.InvalidPath,
                        "The SQLite ownership lock cannot be a reparse point.");
                }
            }
        }
        catch (SqliteCloudStateStoreException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw CreateException(
                SqliteCloudStateStoreError.InvalidPath,
                "The SQLite database location could not be validated.",
                exception);
        }
    }

    private FileStream AcquireOwnerLock()
    {
        string lockPath = DatabasePath + ".cfsharp.lock";
        try
        {
            FileStream ownerLock = OpenOwnerLock(lockPath);
            try
            {
                FileAttributes attributes = File.GetAttributes(lockPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw CreateException(
                        SqliteCloudStateStoreError.InvalidPath,
                        "The SQLite ownership lock cannot be a reparse point.");
                }

                return ownerLock;
            }
            catch
            {
                ownerLock.Dispose();
                throw;
            }
        }
        catch (IOException exception)
        {
            throw CreateException(
                SqliteCloudStateStoreError.AlreadyInUse,
                "The SQLite state database is already owned by another CfSharp instance.",
                exception);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or NotSupportedException)
        {
            throw CreateException(
                SqliteCloudStateStoreError.InvalidPath,
                "The SQLite ownership lock could not be opened.",
                exception);
        }
    }

    private FileStream OpenOwnerLock(string lockPath)
    {
        SafeFileHandle handle = CreateFileW(
            lockPath,
            GenericRead | GenericWrite,
            FileShareNone,
            IntPtr.Zero,
            OpenAlways,
            FileFlagOpenReparsePoint | FileFlagWriteThrough,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is ErrorSharingViolation or ErrorLockViolation)
            {
                throw CreateException(
                    SqliteCloudStateStoreError.AlreadyInUse,
                    "The SQLite state database is already owned by another CfSharp instance.",
                    new Win32Exception(error));
            }

            throw CreateException(
                SqliteCloudStateStoreError.InvalidPath,
                "The SQLite ownership lock could not be opened.",
                new Win32Exception(error));
        }

        try
        {
            SqlitePathHandleLease.EnsureNotReparsePoint(handle, lockPath);
        }
        catch (IOException exception)
        {
            handle.Dispose();
            throw CreateException(
                SqliteCloudStateStoreError.InvalidPath,
                "The SQLite ownership lock cannot be a reparse point.",
                exception);
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        return new FileStream(handle, FileAccess.ReadWrite, bufferSize: 1, isAsync: false);
    }

    private string CreateConnectionString()
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            ForeignKeys = true,
            // SQLite's busy_timeout controls lock waits. Do not reuse it as Microsoft.Data.Sqlite's
            // command timeout: schema checks and large subtree reads must not be interrupted merely
            // because they outlive the lock-wait budget.
            DefaultTimeout = 0,
        };
        return builder.ToString();
    }

    private SqliteCloudStateStoreException CreateException(
        SqliteCloudStateStoreError error,
        string message,
        Exception? innerException = null) =>
        new(error, DatabasePath, message, innerException);

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareNone = 0;
    private const uint OpenAlways = 4;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}

internal sealed class SqliteCloudStateStore : ICloudStateStore
{
    private readonly object _lifecycleGate = new();
    private readonly string _connectionString;
    private readonly int _busyTimeoutMilliseconds;
    private readonly string _databasePath;
    private readonly FileStream _ownerLock;
    private readonly SqlitePathHandleLease _databaseLease;
    private readonly SemaphoreSlim _transactionGate = new(1, 1);
    private int _activeTransactions;
    private bool _disposed;

    internal SqliteCloudStateStore(
        string connectionString,
        int busyTimeoutMilliseconds,
        string databasePath,
        FileStream ownerLock,
        SqlitePathHandleLease databaseLease)
    {
        _connectionString = connectionString;
        _busyTimeoutMilliseconds = busyTimeoutMilliseconds;
        _databasePath = databasePath;
        _ownerLock = ownerLock;
        _databaseLease = databaseLease;
    }

    public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _transactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool transactionGateOwned = true;
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                _transactionGate.Release();
                transactionGateOwned = false;
                ObjectDisposedException.ThrowIf(true, this);
            }

            _activeTransactions++;
        }

        SqliteConnection? connection = null;
        try
        {
            connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await SqliteSchema.ConfigureConnectionAsync(
                connection,
                _busyTimeoutMilliseconds,
                cancellationToken).ConfigureAwait(false);
            SqliteTransaction transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            return new SqliteCloudStateTransaction(
                connection,
                transaction,
                _databasePath,
                OnTransactionCompleted);
        }
        catch (Exception exception)
        {
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }

            if (transactionGateOwned)
            {
                OnTransactionCompleted();
            }
            if (exception is OperationCanceledException or SqliteCloudStateStoreException)
            {
                throw;
            }

            throw SqliteSchema.TranslateFailure(_databasePath, "A SQLite transaction could not be started.", exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            if (_activeTransactions != 0)
            {
                throw new InvalidOperationException(
                    "The SQLite state store cannot be disposed while transactions are active.");
            }

            _disposed = true;
        }

        try
        {
            SqliteConnectionPool.Clear(_connectionString);
        }
        finally
        {
            _databaseLease.Dispose();
            await _ownerLock.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnTransactionCompleted()
    {
        lock (_lifecycleGate)
        {
            _activeTransactions--;
        }

        _transactionGate.Release();
    }
}

/// <summary>Retains no-delete handles for the SQLite parent chain and database file.</summary>
internal sealed class SqlitePathHandleLease : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const int FileAttributeTagInformation = 9;
    private const uint FileAttributeReparsePoint = 0x00000400;

    private readonly List<SafeFileHandle> _handles;

    private SqlitePathHandleLease(List<SafeFileHandle> handles)
    {
        _handles = handles;
    }

    internal static SqlitePathHandleLease Open(string databasePath)
    {
        string fullDatabasePath = Path.GetFullPath(databasePath);
        string parentPath = Path.GetDirectoryName(fullDatabasePath) ??
            throw new IOException("The SQLite database has no parent directory.");
        List<SafeFileHandle> handles = [];
        try
        {
            OpenDirectoryChain(parentPath, handles);
            SafeFileHandle database = CreateFileW(
                fullDatabasePath,
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenAlways,
                FileFlagOpenReparsePoint | FileFlagWriteThrough,
                IntPtr.Zero);
            if (database.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                database.Dispose();
                throw new IOException(
                    $"The SQLite database '{fullDatabasePath}' could not be opened without delete sharing.",
                    new Win32Exception(error));
            }

            try
            {
                EnsureNotReparsePoint(database, fullDatabasePath);
                handles.Add(database);
                foreach (string sidecarPath in new[] { fullDatabasePath + "-wal", fullDatabasePath + "-shm" })
                {
                    SafeFileHandle sidecar = CreateFileW(
                        sidecarPath,
                        GenericRead | GenericWrite,
                        FileShareRead | FileShareWrite,
                        IntPtr.Zero,
                        OpenAlways,
                        FileFlagOpenReparsePoint | FileFlagWriteThrough,
                        IntPtr.Zero);
                    if (sidecar.IsInvalid)
                    {
                        int error = Marshal.GetLastWin32Error();
                        sidecar.Dispose();
                        throw new IOException(
                            $"The SQLite sidecar '{sidecarPath}' could not be opened without delete sharing.",
                            new Win32Exception(error));
                    }

                    try
                    {
                        EnsureNotReparsePoint(sidecar, sidecarPath);
                        handles.Add(sidecar);
                    }
                    catch
                    {
                        sidecar.Dispose();
                        throw;
                    }
                }
            }
            catch
            {
                database.Dispose();
                throw;
            }

            return new SqlitePathHandleLease(handles);
        }
        catch
        {
            foreach (SafeFileHandle handle in handles)
            {
                handle.Dispose();
            }

            throw;
        }
    }

    internal static void EnsureNotReparsePoint(SafeFileHandle handle, string path)
    {
        int size = Marshal.SizeOf<FileAttributeTagInfo>();
        nint buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileAttributeTagInformation,
                    buffer,
                    (uint)size))
            {
                throw new IOException(
                    $"The SQLite path component '{path}' attributes could not be read.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            FileAttributeTagInfo info = Marshal.PtrToStructure<FileAttributeTagInfo>(buffer);
            if ((info.FileAttributes & FileAttributeReparsePoint) != 0 || info.ReparseTag != 0)
            {
                throw new IOException($"The SQLite path component '{path}' cannot be a reparse point.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Resolves a directory through a kernel handle so aliases such as 8.3 short names are
    /// expanded before the sync-root containment check.
    /// </summary>
    internal static string ResolveFinalDirectoryPath(string directoryPath)
    {
        SafeFileHandle handle = CreateFileW(
            directoryPath,
            FileReadAttributes,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                $"The SQLite directory '{directoryPath}' could not be resolved through a handle.",
                new Win32Exception(error));
        }

        using (handle)
        {
            char[] buffer = new char[260];
            while (true)
            {
                uint length = GetFinalPathNameByHandleW(
                    handle,
                    buffer,
                    checked((uint)buffer.Length),
                    volumeNameDos: 0);
                if (length == 0)
                {
                    throw new IOException(
                        $"The SQLite directory '{directoryPath}' could not be canonicalized.",
                        new Win32Exception(Marshal.GetLastWin32Error()));
                }

                if (length < buffer.Length - 1)
                {
                    string finalPath = new(buffer, 0, checked((int)length));
                    if (finalPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                    {
                        finalPath = @"\\" + finalPath[8..];
                    }
                    else if (finalPath.StartsWith(@"\\?\", StringComparison.Ordinal))
                    {
                        finalPath = finalPath[4..];
                    }

                    return Path.TrimEndingDirectorySeparator(Path.GetFullPath(finalPath));
                }

                Array.Resize(ref buffer, checked((int)length + 1));
            }
        }
    }

    public void Dispose()
    {
        for (int index = _handles.Count - 1; index >= 0; index--)
        {
            _handles[index].Dispose();
        }

        _handles.Clear();
    }

    private static void OpenDirectoryChain(string directoryPath, List<SafeFileHandle> handles)
    {
        string volumeRoot = Path.GetPathRoot(directoryPath)!;
        string current = volumeRoot;
        OpenDirectory(current, handles);
        string remainder = directoryPath[volumeRoot.Length..];
        foreach (string segment in remainder.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            OpenDirectory(current, handles);
        }
    }

    private static void OpenDirectory(string path, List<SafeFileHandle> handles)
    {
        SafeFileHandle handle = CreateFileW(
            path,
            FileReadAttributes,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                $"The SQLite parent directory '{path}' could not be opened without delete sharing.",
                new Win32Exception(error));
        }

        try
        {
            EnsureNotReparsePoint(handle, path);
            handles.Add(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        nint fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        [Out] char[] filePath,
        uint filePathLength,
        uint volumeNameDos);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        internal uint FileAttributes;
        internal uint ReparseTag;
    }
}

internal static class SqliteSchema
{
    internal const int CurrentVersion = 6;
    internal const string PathCollationName = "CFSHARP_UNICODE_NOCASE";

    internal static async Task InitializeAsync(
        string connectionString,
        int busyTimeoutMilliseconds,
        string databasePath,
        string syncRootPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection = new(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ConfigureConnectionAsync(
                connection,
                busyTimeoutMilliseconds,
                cancellationToken).ConfigureAwait(false);
            await EnableWalAsync(connection, databasePath, cancellationToken).ConfigureAwait(false);

            bool schemaTableExists = await TableExistsAsync(
                connection,
                "cfsharp_schema",
                cancellationToken).ConfigureAwait(false);
            if (!schemaTableExists)
            {
                long applicationTableCount = await CountApplicationTablesAsync(
                    connection,
                    cancellationToken).ConfigureAwait(false);
                if (applicationTableCount != 0)
                {
                    throw new SqliteCloudStateStoreException(
                        SqliteCloudStateStoreError.InvalidSchema,
                        databasePath,
                        "The SQLite file is not an empty or recognized CfSharp state database.");
                }

                await CreateVersionOneAsync(
                    connection,
                    syncRootPath,
                    addSyncRootColumn: false,
                    schemaVersion: CurrentVersion,
                    cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            int version = await ReadVersionAsync(connection, databasePath, cancellationToken)
                .ConfigureAwait(false);
            if (version > CurrentVersion)
            {
                throw new SqliteCloudStateStoreException(
                    SqliteCloudStateStoreError.UnsupportedSchema,
                    databasePath,
                    $"The SQLite state schema version {version.ToString(CultureInfo.InvariantCulture)} " +
                    $"is newer than supported version {CurrentVersion.ToString(CultureInfo.InvariantCulture)}.");
            }

            if (version < 1)
            {
                await MigrateVersionZeroAsync(connection, syncRootPath, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (version < 2)
            {
                await MigrateVersionTwoAsync(connection, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (version < 3)
            {
                await MigrateVersionThreeAsync(connection, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (version < 4)
            {
                await MigrateVersionFourAsync(connection, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (version < 5)
            {
                // Version 5 fences the durable creation/reconciliation protocol even though
                // its records use existing tables. Older runtimes must not ignore them.
                await using SqliteCommand upgrade = connection.CreateCommand();
                upgrade.CommandText = "UPDATE cfsharp_schema SET version = 5 WHERE singleton = 1;";
                await upgrade.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (version < 6)
            {
                // Directory object provenance, immutable move preparations, and completion
                // receipts use checkpoints, but change the recovery protocol. The version fence
                // prevents older runtimes from silently performing unverified namespace replay.
                // No table, existing checkpoint, item field, or journal sequence is rewritten.
                await using SqliteCommand upgrade = connection.CreateCommand();
                upgrade.CommandText = "UPDATE cfsharp_schema SET version = 6 WHERE singleton = 1;";
                await upgrade.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await ValidateCurrentVersionAsync(
                connection,
                databasePath,
                syncRootPath,
                cancellationToken).ConfigureAwait(false);
            await EnsureOperationItemIndexAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (exception is OperationCanceledException or SqliteCloudStateStoreException)
            {
                throw;
            }

            throw TranslateFailure(databasePath, "The SQLite state database could not be initialized.", exception);
        }
    }

    internal static async Task ConfigureConnectionAsync(
        SqliteConnection connection,
        int busyTimeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        connection.CreateCollation(
            PathCollationName,
            static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left, right));
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = {busyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)};
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static SqliteCloudStateStoreException TranslateFailure(
        string databasePath,
        string message,
        Exception exception)
    {
        SqliteCloudStateStoreError error =
            exception is SqliteException { SqliteErrorCode: 11 or 26 }
                ? SqliteCloudStateStoreError.CorruptDatabase
                : SqliteCloudStateStoreError.DatabaseFailure;
        return new SqliteCloudStateStoreException(error, databasePath, message, exception);
    }

    private static async Task CreateVersionOneAsync(
        SqliteConnection connection,
        string syncRootPath,
        bool addSyncRootColumn,
        int schemaVersion,
        CancellationToken cancellationToken)
    {
        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (addSyncRootColumn)
        {
            await using SqliteCommand alterCommand = connection.CreateCommand();
            alterCommand.Transaction = transaction;
            alterCommand.CommandText =
                "ALTER TABLE cfsharp_schema ADD COLUMN sync_root_path TEXT NULL;";
            await alterCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS cfsharp_schema (
                singleton INTEGER NOT NULL PRIMARY KEY CHECK (singleton = 1),
                version INTEGER NOT NULL,
                sync_root_path TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS items (
                item_id TEXT NOT NULL PRIMARY KEY,
                remote_id TEXT NOT NULL UNIQUE,
                relative_path TEXT NOT NULL COLLATE CFSHARP_UNICODE_NOCASE UNIQUE,
                kind INTEGER NOT NULL,
                remote_revision TEXT NULL,
                local_file_id INTEGER NULL,
                is_tombstone INTEGER NOT NULL,
                updated_at_ticks INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS checkpoints (
                name TEXT NOT NULL COLLATE BINARY PRIMARY KEY,
                value BLOB NOT NULL,
                updated_at_ticks INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS operations (
                sequence INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                operation_id TEXT NOT NULL UNIQUE,
                kind INTEGER NOT NULL,
                item_id TEXT NULL,
                payload BLOB NOT NULL,
                created_at_ticks INTEGER NOT NULL,
                attempt_count INTEGER NOT NULL,
                retry_after_ticks INTEGER NULL,
                FOREIGN KEY (item_id) REFERENCES items(item_id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS conflicts (
                conflict_id TEXT NOT NULL PRIMARY KEY,
                item_id TEXT NULL,
                kind INTEGER NOT NULL,
                payload BLOB NOT NULL,
                created_at_ticks INTEGER NOT NULL,
                FOREIGN KEY (item_id) REFERENCES items(item_id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS remote_batches (
                batch_id TEXT NOT NULL COLLATE BINARY PRIMARY KEY,
                cursor BLOB NOT NULL,
                applied_entry_count INTEGER NOT NULL,
                total_entry_count INTEGER NOT NULL,
                status INTEGER NOT NULL,
                payload BLOB NOT NULL,
                fingerprint BLOB NOT NULL DEFAULT X'',
                last_change_id TEXT NULL,
                updated_at_ticks INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS echo_suppressions (
                suppression_id TEXT NOT NULL PRIMARY KEY,
                item_id TEXT NULL,
                kind INTEGER NOT NULL,
                relative_path TEXT NOT NULL COLLATE CFSHARP_UNICODE_NOCASE,
                previous_relative_path TEXT NULL COLLATE CFSHARP_UNICODE_NOCASE,
                payload BLOB NOT NULL,
                expires_at_ticks INTEGER NOT NULL,
                remaining_observations INTEGER NOT NULL DEFAULT 1,
                FOREIGN KEY (item_id) REFERENCES items(item_id) ON DELETE SET NULL
            );

            CREATE INDEX IF NOT EXISTS ix_operations_sequence ON operations(sequence);
            CREATE INDEX IF NOT EXISTS ix_operations_item_sequence ON operations(item_id, sequence);
            CREATE INDEX IF NOT EXISTS ix_conflicts_created ON conflicts(created_at_ticks, conflict_id);
            CREATE INDEX IF NOT EXISTS ix_echo_expiration ON echo_suppressions(expires_at_ticks);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_items_relative_path_unicode_nocase
                ON items(relative_path COLLATE CFSHARP_UNICODE_NOCASE);

            INSERT INTO cfsharp_schema(singleton, version, sync_root_path)
            VALUES (1, $version, $sync_root_path)
            ON CONFLICT(singleton) DO UPDATE SET
                version = excluded.version,
                sync_root_path = excluded.sync_root_path;
            """;
        command.Parameters.AddWithValue("$sync_root_path", syncRootPath);
        command.Parameters.AddWithValue("$version", schemaVersion);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateVersionZeroAsync(
        SqliteConnection connection,
        string syncRootPath,
        CancellationToken cancellationToken)
    {
        bool syncRootColumnExists = await ColumnExistsAsync(
            connection,
            "cfsharp_schema",
            "sync_root_path",
            cancellationToken).ConfigureAwait(false);
        await CreateVersionOneAsync(
            connection,
            syncRootPath,
            addSyncRootColumn: !syncRootColumnExists,
            schemaVersion: 1,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureOperationItemIndexAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "CREATE INDEX IF NOT EXISTS ix_operations_item_sequence " +
            "ON operations(item_id, sequence);";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateVersionTwoAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "remote_batches", cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await ColumnExistsAsync(connection, "remote_batches", "fingerprint", cancellationToken)
                .ConfigureAwait(false))
        {
            await using SqliteCommand addFingerprint = connection.CreateCommand();
            addFingerprint.Transaction = transaction;
            addFingerprint.CommandText =
                "ALTER TABLE remote_batches ADD COLUMN fingerprint BLOB NOT NULL DEFAULT X'';";
            await addFingerprint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!await ColumnExistsAsync(connection, "remote_batches", "last_change_id", cancellationToken)
                .ConfigureAwait(false))
        {
            await using SqliteCommand addLastChange = connection.CreateCommand();
            addLastChange.Transaction = transaction;
            addLastChange.CommandText =
                "ALTER TABLE remote_batches ADD COLUMN last_change_id TEXT NULL;";
            await addLastChange.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using SqliteCommand updateVersion = connection.CreateCommand();
        updateVersion.Transaction = transaction;
        updateVersion.CommandText =
            "UPDATE cfsharp_schema SET version = $version WHERE singleton = 1;";
        updateVersion.Parameters.AddWithValue("$version", 2);
        await updateVersion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateVersionThreeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "echo_suppressions", cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await ColumnExistsAsync(
                connection,
                "echo_suppressions",
                "previous_relative_path",
                cancellationToken).ConfigureAwait(false))
        {
            await using SqliteCommand addPreviousPath = connection.CreateCommand();
            addPreviousPath.Transaction = transaction;
            addPreviousPath.CommandText =
                "ALTER TABLE echo_suppressions ADD COLUMN previous_relative_path TEXT NULL COLLATE CFSHARP_UNICODE_NOCASE;";
            await addPreviousPath.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!await ColumnExistsAsync(
                connection,
                "echo_suppressions",
                "remaining_observations",
                cancellationToken).ConfigureAwait(false))
        {
            await using SqliteCommand addRemaining = connection.CreateCommand();
            addRemaining.Transaction = transaction;
            addRemaining.CommandText =
                "ALTER TABLE echo_suppressions ADD COLUMN remaining_observations INTEGER NOT NULL DEFAULT 1;";
            await addRemaining.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using SqliteCommand updateVersion = connection.CreateCommand();
        updateVersion.Transaction = transaction;
        updateVersion.CommandText =
            "UPDATE cfsharp_schema SET version = $version WHERE singleton = 1;";
        updateVersion.Parameters.AddWithValue("$version", 3);
        await updateVersion.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MigrateVersionFourAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "items", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand createIndex = connection.CreateCommand();
        createIndex.Transaction = transaction;
        createIndex.CommandText = """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_items_relative_path_unicode_nocase
                ON items(relative_path COLLATE CFSHARP_UNICODE_NOCASE);
            UPDATE cfsharp_schema SET version = 4 WHERE singleton = 1;
            """;
        await createIndex.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnableWalAsync(
        SqliteConnection connection,
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(
                Convert.ToString(result, CultureInfo.InvariantCulture),
                "wal",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new SqliteCloudStateStoreException(
                SqliteCloudStateStoreError.DatabaseFailure,
                databasePath,
                "The SQLite state database could not enable WAL journal mode.");
        }
    }

    private static async Task ValidateCurrentVersionAsync(
        SqliteConnection connection,
        string databasePath,
        string syncRootPath,
        CancellationToken cancellationToken)
    {
        string[] requiredTables =
        [
            "items",
            "checkpoints",
            "operations",
            "conflicts",
            "remote_batches",
            "echo_suppressions",
        ];
        foreach (string table in requiredTables)
        {
            if (!await TableExistsAsync(connection, table, cancellationToken).ConfigureAwait(false))
            {
                throw new SqliteCloudStateStoreException(
                    SqliteCloudStateStoreError.InvalidSchema,
                    databasePath,
                    $"The CfSharp state database is missing the required '{table}' table.");
            }
        }

        if (!await ColumnExistsAsync(
                connection,
                "remote_batches",
                "fingerprint",
                cancellationToken).ConfigureAwait(false) ||
            !await ColumnExistsAsync(
                connection,
                "remote_batches",
                "last_change_id",
                cancellationToken).ConfigureAwait(false))
        {
            throw new SqliteCloudStateStoreException(
                SqliteCloudStateStoreError.InvalidSchema,
                databasePath,
                "The remote_batches table is missing Phase 8 replay columns.");
        }

        if (!await ColumnExistsAsync(
                connection,
                "echo_suppressions",
                "previous_relative_path",
                cancellationToken).ConfigureAwait(false) ||
            !await ColumnExistsAsync(
                connection,
                "echo_suppressions",
                "remaining_observations",
                cancellationToken).ConfigureAwait(false))
        {
            throw new SqliteCloudStateStoreException(
                SqliteCloudStateStoreError.InvalidSchema,
                databasePath,
                "The echo_suppressions table is missing Phase 8 observation columns.");
        }

        if (!await IndexExistsAsync(
                connection,
                "ux_items_relative_path_unicode_nocase",
                cancellationToken).ConfigureAwait(false))
        {
            throw new SqliteCloudStateStoreException(
                SqliteCloudStateStoreError.InvalidSchema,
                databasePath,
                "The items table is missing the Unicode path uniqueness index.");
        }

        await using (SqliteCommand foreignKeyCommand = connection.CreateCommand())
        {
            foreignKeyCommand.CommandText = "PRAGMA foreign_key_check;";
            await using SqliteDataReader reader = await foreignKeyCommand
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new SqliteCloudStateStoreException(
                    SqliteCloudStateStoreError.InvalidSchema,
                    databasePath,
                    "The CfSharp state database contains invalid foreign-key references.");
            }
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT sync_root_path FROM cfsharp_schema WHERE singleton = 1;";
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        string? storedSyncRoot = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (string.IsNullOrWhiteSpace(storedSyncRoot))
        {
            throw new SqliteCloudStateStoreException(
                SqliteCloudStateStoreError.InvalidSchema,
                databasePath,
                "The CfSharp state database has no sync-root binding.");
        }

        string normalizedStoredSyncRoot;
        try
        {
            if (!Path.IsPathFullyQualified(storedSyncRoot))
            {
                throw new ArgumentException("The stored sync-root path is not fully qualified.");
            }

            normalizedStoredSyncRoot = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(storedSyncRoot));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new SqliteCloudStateStoreException(
                SqliteCloudStateStoreError.InvalidSchema,
                databasePath,
                "The CfSharp state database contains an invalid sync-root binding.",
                exception);
        }

        if (!string.Equals(
                normalizedStoredSyncRoot,
                syncRootPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new SqliteCloudStateStoreException(
                SqliteCloudStateStoreError.SyncRootMismatch,
                databasePath,
                "The SQLite state database belongs to a different sync root.");
        }
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name
            );
            """;
        command.Parameters.AddWithValue("$name", tableName);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{tableName.Replace("'", "''", StringComparison.Ordinal)}');";
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<bool> IndexExistsAsync(
        SqliteConnection connection,
        string indexName,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = $name
            );
            """;
        command.Parameters.AddWithValue("$name", indexName);
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<long> CountApplicationTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%';
            """;
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<int> ReadVersionAsync(
        SqliteConnection connection,
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM cfsharp_schema WHERE singleton = 1;";
        object? value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null)
        {
            throw new SqliteCloudStateStoreException(
                SqliteCloudStateStoreError.InvalidSchema,
                databasePath,
                "The CfSharp schema metadata row is missing.");
        }

        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }
}

internal static class SqliteConnectionPool
{
    internal static void Clear(string connectionString)
    {
        using SqliteConnection connection = new(connectionString);
        SqliteConnection.ClearPool(connection);
    }
}

internal static class SqlitePathSafety
{
    internal static string ResolvePhysicalPath(string path, bool isDirectory)
    {
        string fullPath = Path.GetFullPath(path);
        string targetPath = isDirectory ? fullPath : Path.GetDirectoryName(fullPath)!;
        string root = Path.GetPathRoot(targetPath)!;
        string current = root;
        string remainder = targetPath[root.Length..];

        foreach (string segment in remainder.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(current, segment);
            if (Directory.Exists(next))
            {
                DirectoryInfo directory = new(next);
                FileSystemInfo? resolved = directory.ResolveLinkTarget(returnFinalTarget: true);
                current = resolved?.FullName ?? next;
            }
            else
            {
                current = next;
            }
        }

        string resolvedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
        if (Directory.Exists(resolvedDirectory))
        {
            // Path.GetFullPath preserves an 8.3 alias. A final path obtained from a directory
            // handle is the kernel's canonical spelling and therefore closes that gap.
            resolvedDirectory = SqlitePathHandleLease.ResolveFinalDirectoryPath(resolvedDirectory);
        }

        return isDirectory
            ? resolvedDirectory
            : Path.Combine(resolvedDirectory, Path.GetFileName(fullPath));
    }

    internal static bool IsSameOrChild(string parentPath, string candidatePath)
    {
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath));
        string candidate = Path.GetFullPath(candidatePath);
        if (string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = parent + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
