using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

using Microsoft.Win32.SafeHandles;

namespace CfSharp.Storage.Sqlite.Tests;

public sealed partial class CloudLocalChangeFeedTests
{
    [Theory]
    [InlineData("modified")]
    [InlineData("directory")]
    [InlineData("created")]
    [InlineData("deleted")]
    [InlineData("rename-source")]
    [InlineData("rename-target")]
    [SupportedOSPlatform("windows10.0.16299")]
    public async Task PathPermissionFailureFencesReconciliationAndProcessesLaterNotifications(string scenario)
    {
        string denied = Path.Combine(_syncRootPath, "restricted");
        string inner = Path.Combine(denied, "inner");
        Directory.CreateDirectory(inner);
        const string restrictedPath = @"restricted\inner\item";
        bool directory = scenario == "directory";
        string originalPath = scenario == "rename-target" ? "source.bin" : restrictedPath;
        string targetPath = scenario == "rename-source" ? "renamed.bin" : restrictedPath;
        string physicalOriginal = Path.Combine(_syncRootPath, originalPath);
        if (directory) { Directory.CreateDirectory(physicalOriginal); }
        else { await File.WriteAllTextAsync(physicalOriginal, "retained native content"); }
        bool rename = scenario.StartsWith("rename-", StringComparison.Ordinal);
        CloudStateOperationKind echoKind = rename ? CloudStateOperationKind.Move : scenario switch
        {
            "directory" => CloudStateOperationKind.MetadataUpdate,
            "created" => CloudStateOperationKind.Create,
            "deleted" => CloudStateOperationKind.Delete,
            _ => CloudStateOperationKind.ContentUpdate,
        };
        if (rename) { File.Move(physicalOriginal, Path.Combine(_syncRootPath, targetPath)); }
        if (scenario == "deleted") { File.Delete(physicalOriginal); }
        await File.WriteAllTextAsync(Path.Combine(_syncRootPath, "healthy.bin"), "later healthy content");
        Guid itemId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        Guid echoId = Guid.NewGuid();
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        CloudItemState originalState = new(itemId, "retained-remote", originalPath,
            directory ? CloudItemKind.Directory : CloudItemKind.File, "v1", null, false, observedAt);
        CloudOperationJournalEntry originalEntry;

        await using (ICloudStateStore store = await OpenStoreAsync())
        {
            await using (ICloudStateTransaction seed = await store.BeginTransactionAsync())
            {
                await seed.Items.UpsertAsync(originalState);
                await seed.Operations.EnqueueAsync(new(operationId, CloudStateOperationKind.ContentUpdate, itemId,
                    new LocalChangePayload(originalPath, null, directory, observedAt).Encode(), observedAt));
                await seed.EchoSuppressions.UpsertAsync(new(echoId, itemId, echoKind,
                    targetPath, "retained-echo"u8, observedAt.AddMinutes(10), previousRelativePath: rename ? originalPath : null));
                await seed.CommitAsync();
            }
            await using (ICloudStateTransaction capture = await store.BeginTransactionAsync())
            {
                originalEntry = Assert.Single(await capture.Operations.ListAsync(10));
            }
            using (DaclRestoreScope restore = DenyPathMetadata(denied))
            {
                string inaccessible = scenario == "rename-source" ? originalPath : targetPath;
                UnauthorizedAccessException deniedRead = Assert.Throws<UnauthorizedAccessException>(() =>
                    CloudItemPathResolver.Resolve(_syncRootPath, inaccessible, allowRoot: false));
                Assert.Equal(unchecked((int)0x80070005), deniedRead.HResult);
                FakeSource source = new();
                await using CloudLocalChangeFeed feed = CreateFeed(store, source);
                await feed.StartAsync();
                if (rename)
                {
                    await source.EmitAsync(new(LocalChangeSourceAction.RenamedOldName, originalPath));
                    await source.EmitAsync(new(LocalChangeSourceAction.RenamedNewName, targetPath));
                }
                else
                {
                    LocalChangeSourceAction action = scenario switch
                    {
                        "created" => LocalChangeSourceAction.Created,
                        "deleted" => LocalChangeSourceAction.Deleted,
                        _ => LocalChangeSourceAction.Modified,
                    };
                    await source.EmitAsync(new(action, targetPath));
                }
                await source.EmitAsync(new(LocalChangeSourceAction.Created, "healthy.bin"));
                await source.EmitAsync(new(LocalChangeSourceAction.Modified, "healthy.bin"));
                // A full-rescan marker alone does not prove survival: failed workers also
                // persist it. Both later events must commit through this same live feed.
                await WaitForJournalStateAsync(store, expectedOperations: 3, expectedSuppressions: 1);
                CloudLocalChangeScan scan = await feed.BeginScanAsync();
                Assert.True(scan.RequiresFullRescan);
                Assert.Empty((await feed.ReadPageAsync(scan, 0, 100)).Changes);
                await feed.DisposeAsync();
                await using ICloudStateTransaction verify = await store.BeginTransactionAsync();
                Assert.Equivalent(originalState, await verify.Items.GetByItemIdAsync(itemId), strict: true);
                CloudOperationJournalEntry retained = (await verify.Operations.GetAsync(operationId))!;
                Assert.Equal(originalEntry.Sequence, retained.Sequence);
                Assert.Equal(originalEntry.Payload.ToArray(), retained.Payload.ToArray());
                Assert.Equal(2, (await verify.Items.ListSubtreeAsync(string.Empty)).Count);
                CloudEchoSuppressionState echo = Assert.Single(await verify.EchoSuppressions.ListActiveAsync(DateTimeOffset.UtcNow));
                Assert.Equal(echoId, echo.SuppressionId);
                Assert.Equal(echoKind, echo.Kind);
                Assert.Equal(rename ? originalPath : null, echo.PreviousRelativePath);
                Assert.Equal(1, echo.RemainingObservations);
                Assert.Equal("retained-echo"u8.ToArray(), echo.Payload.ToArray());
            }
        }

        await using ICloudStateStore reopenedStore = await OpenStoreAsync();
        await using CloudLocalChangeFeed reopened = CreateFeed(reopenedStore, new FakeSource());
        await reopened.StartAsync();
        Assert.True((await reopened.BeginScanAsync()).RequiresFullRescan);
        await AcknowledgePersistedRescanAsync(reopened);
        CloudLocalChangePage page = await reopened.ReadPageAsync(await reopened.BeginScanAsync(), 0, 100);
        Assert.False(page.RequiresFullRescan);
        Assert.Equal(3, page.Changes.Count);
        Assert.Contains(page.Changes, change => change.Kind == CloudLocalChangeKind.Create && change.RelativePath == "healthy.bin");
        Assert.Contains(page.Changes, change => change.Kind == CloudLocalChangeKind.ContentUpdate && change.RelativePath == "healthy.bin");
        Assert.Contains(page.Changes, change => change.OperationId == operationId && change.Sequence == originalEntry.Sequence);
    }

    [SupportedOSPlatform("windows")]
    private static DaclRestoreScope DenyPathMetadata(string path)
    {
        DirectoryInfo directory = new(path);
        DirectorySecurity security = directory.GetAccessControl(AccessControlSections.Access);
        byte[] descriptor = security.GetSecurityDescriptorBinaryForm();
        int offset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(16)));
        byte[] dacl = descriptor.AsSpan(offset, BinaryPrimitives.ReadUInt16LittleEndian(descriptor.AsSpan(offset + 2))).ToArray();
        SafeFileHandle handle = OpenPathForDaclRestore(path, 0x00060000, 7, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new Win32Exception(error);
        }
        try
        {
            using WindowsIdentity user = WindowsIdentity.GetCurrent();
            security.AddAccessRule(new(user.User!, FileSystemRights.ReadAttributes | FileSystemRights.ListDirectory,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Deny));
            directory.SetAccessControl(security);
            return new(handle, dacl);
        }
        catch { handle.Dispose(); throw; }
    }

    private sealed class DaclRestoreScope(SafeFileHandle handle, byte[] dacl) : IDisposable
    {
        public void Dispose()
        {
            // Retain READ_CONTROL/WRITE_DAC before denying metadata. A path-based restore
            // would itself query the denied attributes and could mask the test outcome.
            uint error = RestorePathDacl(handle, 1, 4, 0, 0, dacl, 0);
            handle.Dispose();
            if (error != 0) { throw new Win32Exception(checked((int)error)); }
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenPathForDaclRestore(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [DllImport("advapi32.dll", EntryPoint = "SetSecurityInfo", ExactSpelling = true)]
    private static extern uint RestorePathDacl(SafeFileHandle handle, uint objectType, uint securityInformation,
        nint owner, nint group, byte[] dacl, nint sacl);
}
