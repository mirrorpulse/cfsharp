using System.Security.Principal;

namespace CfSharp.IntegrationTests;

public sealed partial class RemoteDeleteRecoveryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task LocalDeleteReconciliationRetainsAnOriginalConflictWithoutNativeWork(bool directory, bool acknowledged)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        bool administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        string architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        _output.WriteLine($"Reconciliation producer: architecture={architecture}; OS={Environment.OSVersion.VersionString}; SID={identity.User?.Value}; administrator={administrator}; directory={directory}; acknowledged={acknowledged}.");
        if (Environment.GetEnvironmentVariable("CFSHARP_EXPECTED_TEST_SID") is string expectedSid)
        {
            Assert.Equal(expectedSid, identity.User?.Value);
            Assert.False(administrator);
            Assert.Equal(Environment.GetEnvironmentVariable("CFSHARP_EXPECTED_TEST_ARCHITECTURE"), architecture, ignoreCase: true);
        }

        await using DeleteRoot root = await DeleteRoot.OpenAsync(directory, alternateCasing: acknowledged, deferCreation: true);
        IReadOnlyList<CloudOperationJournalEntry> operations;
        CloudItemState tombstone;
        Guid originalDelete;
        string backendObject = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(root.Path)!, "backend-object");
        await using (CloudLocalChangeFeed feed = root.FileSystem.CreateLocalChangeFeed())
        {
            await feed.StartAsync();
            CloudRemoteChange create = new("birth", directory ? CloudRemoteChangeKind.DirectoryUpsert : CloudRemoteChangeKind.FileUpsert,
                root.Identity.RemoteId, "v1", directory ? CloudItemKind.Directory : CloudItemKind.File,
                root.Target, root.ItemId, length: directory ? null : 512,
                metadata: directory ? CloudPlaceholderMetadata.CreateDirectoryBuilder().Build() : CloudPlaceholderMetadata.CreateFileBuilder().Build());
            CloudRemoteApplyResult birth = await root.FileSystem.ApplyRemoteChangesAsync(new("birth", Array.Empty<byte>(), [create], new byte[] { 1 }));
            Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single(birth.Entries).Status);
            if (directory) { Directory.CreateDirectory(backendObject); }
            else { await File.WriteAllBytesAsync(backendObject, new byte[512]); }
            Assert.True(directory ? Directory.Exists(backendObject) : File.Exists(backendObject));
            if (directory) { await root.FileSystem.GetDirectory(root.Target).DeleteAsync(); }
            else { await root.FileSystem.GetFile(root.Target).DeleteAsync(); }
            Assert.False(File.Exists(System.IO.Path.Combine(root.Path, root.Target)) || Directory.Exists(System.IO.Path.Combine(root.Path, root.Target)));
            CloudLocalChange observed = await WaitForReconciliationChangeAsync(feed,
                change => change.Kind == CloudLocalChangeKind.Delete && string.Equals(change.RelativePath, root.Target, StringComparison.OrdinalIgnoreCase));
            originalDelete = observed.OperationId;
            Assert.Equal(directory, observed.IsDirectory);
            if (acknowledged)
            {
                if (directory) { Directory.Delete(backendObject); }
                else { File.Delete(backendObject); }
                Assert.False(File.Exists(backendObject) || Directory.Exists(backendObject));
                await feed.AcknowledgeAsync([originalDelete]);
            }
            Assert.Equal(!acknowledged, directory ? Directory.Exists(backendObject) : File.Exists(backendObject));
            _output.WriteLine($"Disposable filesystem backend: directory={directory}; deletionObservedBeforeAck={acknowledged}.");
            await File.WriteAllTextAsync(System.IO.Path.Combine(root.Path, "unrelated.txt"), "unrelated later local work");
            await WaitForReconciliationChangeAsync(feed,
                change => change.Kind == CloudLocalChangeKind.Create && change.RelativePath == "unrelated.txt");
        }
        await using (ICloudStateTransaction capture = await root.Factory.Store!.BeginTransactionAsync())
        {
            Assert.Equal(!acknowledged, await capture.Operations.GetAsync(originalDelete) is not null);
            operations = await capture.Operations.ListAsync(1000);
            Assert.NotEmpty(operations);
            tombstone = (await capture.Items.GetByItemIdAsync(root.ItemId))!;
            Assert.True(tombstone.IsTombstone);
            Assert.Equal("v1", tombstone.RemoteRevision);
            if (acknowledged)
            {
                Assert.StartsWith("Docs\\", tombstone.RelativePath, StringComparison.Ordinal);
                Assert.NotEqual(root.Target, tombstone.RelativePath);
            }
            _output.WriteLine($"Original path spellings: request={root.Target}; tombstone={tombstone.RelativePath}.");
            Assert.Empty(await capture.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix));
        }
        await root.RestartAsync();
        CloudRemoteApplyOptions options = new() { MaximumEntries = 1 };
        CloudRemoteChangeBatch batch = new(root.Batch.BatchId, new byte[] { 1 }, root.Batch.Changes, new byte[] { 2 });
        for (int attempt = 0; attempt < 2; attempt++)
        {
            CloudRemoteApplyResult strict = await root.FileSystem.ApplyRemoteChangesAsync(batch, options);
            Assert.True(strict.RequiresRetry);
            Assert.Equal(0, strict.AppliedEntryCount);
            Assert.Equal(new byte[] { 1 }, strict.SafeCursor.ToArray());
            Assert.IsType<CloudRemoteDeleteRecoveryException>(Assert.Single(strict.Entries).Error);
            Assert.Empty(strict.ConflictIds);
            await root.RestartAsync();
        }

        // A later unmanaged occupant is unrelated to the retired identity. The explicit
        // rejection must not inspect/adopt it, even when its kind differs from the tombstone.
        string occupant = System.IO.Path.Combine(root.Path, root.Target);
        if (directory) { await File.WriteAllTextAsync(occupant, "later occupant"); }
        else { Directory.CreateDirectory(occupant); }
        uint attributes = (uint)File.GetAttributes(occupant);
        CloudRemoteDeleteReconciliationResult result = await root.FileSystem.ReconcileRemoteDeleteAsync(batch, 0, options);
        Assert.Equal(CloudRemoteDeleteReconciliationStatus.RecordedConflict, result.Status);
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, result.Entry!.Status);
        Assert.Equal(CloudRemoteConflictReason.LocalDeleteReconciliationRequired, result.Entry.Conflict!.Reason);
        Assert.False(result.NativeMutationAttempted);
        Assert.Equal(1, result.AppliedEntryCount);
        Assert.Equal(new byte[] { 2 }, result.SafeCursor.ToArray());
        await root.RestartAsync();
        CloudRemoteDeleteReconciliationResult lookup = await root.FileSystem.GetRemoteDeleteReconciliationAsync(batch, 0);
        Assert.Equal(result.ConflictId, lookup.ConflictId);
        Assert.Equal(CloudRemoteDeleteReconciliationStatus.AlreadyRecorded, lookup.Status);
        Assert.Equal(result.ConflictId, (await root.FileSystem.ReconcileRemoteDeleteAsync(batch, 0, options)).ConflictId);
        Assert.Equal(CloudRemoteApplyEntryStatus.AlreadyApplied,
            Assert.Single((await root.FileSystem.ApplyRemoteChangesAsync(batch, options)).Entries).Status);
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, (await root.FileSystem.ResolveRemoteConflictAsync(result.ConflictId!.Value,
            new(CloudRemoteConflictDecision.KeepRemote))).Status);
        Assert.Equal(attributes, (uint)File.GetAttributes(occupant));
        if (directory) { Assert.Equal("later occupant", await File.ReadAllTextAsync(occupant)); }
        Assert.Equal("unrelated later local work", await File.ReadAllTextAsync(System.IO.Path.Combine(root.Path, "unrelated.txt")));
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        CloudItemState retained = (await verify.Items.GetByItemIdAsync(root.ItemId))!;
        Assert.True(retained.IsTombstone);
        Assert.Equal(tombstone.RelativePath, retained.RelativePath);
        Assert.Equal(tombstone.UpdatedAt, retained.UpdatedAt);
        Assert.Equal(tombstone.RemoteRevision, retained.RemoteRevision);
        Assert.Equal(tombstone.LocalFileId, retained.LocalFileId);
        AssertOriginalReconciliationOperations(operations, await verify.Operations.ListAsync(1000));
        CloudRemoteBatchState progress = (await verify.RemoteBatches.GetAsync(batch.BatchId))!;
        Assert.Equal(batch.Fingerprint.ToArray(), progress.Fingerprint.ToArray());
        Assert.Equal(1, progress.AppliedEntryCount);
        Assert.Equal(new byte[] { 2 }, progress.Cursor.ToArray());
        Assert.Single(await verify.Conflicts.ListAsync());
        Assert.Equal(!acknowledged, directory ? Directory.Exists(backendObject) : File.Exists(backendObject));
        Assert.Equal(0, root.Provider.Reads);
        _output.WriteLine($"Original batch={batch.BatchId}; fingerprint={Convert.ToHexString(batch.Fingerprint.Span)}; localDelete={originalDelete}; retainedOperations={operations.Count}; conflict={result.ConflictId}; prefix={progress.AppliedEntryCount}/{progress.TotalEntryCount}; cursor={Convert.ToHexString(progress.Cursor.Span)}; hydrationReads={root.Provider.Reads}.");
    }

    [Theory]
    [InlineData(false, "before")]
    [InlineData(true, "before")]
    [InlineData(false, "after")]
    [InlineData(true, "after")]
    public async Task ReconciliationSurvivesRealProcessTerminationAtTheOfficialCommit(bool directory, string boundary)
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(directory);
        if (directory) { await root.FileSystem.GetDirectory(root.Target).DeleteAsync(); }
        else { await root.FileSystem.GetFile(root.Target).DeleteAsync(); }
        await root.FileSystem.DisposeAsync();
        System.Diagnostics.ProcessStartInfo start = new("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        string configuration = typeof(RemoteDeleteRecoveryTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .Cast<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration;
        string harness = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory,
            "../../../../CfSharp.Storage.Sqlite.CrashHarness/bin", configuration, "net10.0-windows/CfSharp.Storage.Sqlite.CrashHarness.dll"));
        foreach (string argument in new[] { harness, root.Database, root.Path, directory ? "directory" : "file",
            root.ItemId.ToString(), "delete-reconciliation-" + boundary }) { start.ArgumentList.Add(argument); }
        using System.Diagnostics.Process child = System.Diagnostics.Process.Start(start)!;
        try
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
            while (!File.Exists(root.Database + ".delete-ready") && !child.HasExited) { await Task.Delay(25, deadline.Token); }
            Assert.False(child.HasExited);
            Assert.True(File.Exists(root.Database + ".delete-ready"));
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); }
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        await root.RestartAsync();
        CloudRemoteDeleteReconciliationResult previous = await root.FileSystem.GetRemoteDeleteReconciliationAsync(root.Batch, 0);
        Assert.Equal(boundary == "after" ? CloudRemoteDeleteReconciliationStatus.AlreadyRecorded : CloudRemoteDeleteReconciliationStatus.NotFound,
            previous.Status);
        await using (ICloudStateTransaction interrupted = await root.Factory.Store!.BeginTransactionAsync())
        {
            Assert.Equal(boundary == "after" ? 1 : 0, (await interrupted.RemoteBatches.GetAsync(root.Batch.BatchId))?.AppliedEntryCount ?? 0);
            Assert.Equal(boundary == "after" ? 1 : 0, (await interrupted.Conflicts.ListAsync()).Count);
        }
        CloudRemoteDeleteReconciliationResult retry = await root.FileSystem.ReconcileRemoteDeleteAsync(root.Batch, 0);
        Assert.Equal(boundary == "after" ? CloudRemoteDeleteReconciliationStatus.AlreadyRecorded : CloudRemoteDeleteReconciliationStatus.RecordedConflict,
            retry.Status);
        if (boundary == "after") { Assert.Equal(previous.ConflictId, retry.ConflictId); }
        Assert.Equal(CloudRemoteApplyEntryStatus.AlreadyApplied, Assert.Single((await root.ApplyProtectedAsync()).Entries).Status);
        Assert.Equal(0, root.Provider.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalMixedDeleteReconciliationAdvancesOnlyItsActualMiddleOrdinal(bool directory)
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(directory);
        if (directory) { await root.FileSystem.GetDirectory(root.Target).DeleteAsync(); }
        else { await root.FileSystem.GetFile(root.Target).DeleteAsync(); }
        CloudRemoteChange Missing(int index) => new("missing-" + index, CloudRemoteChangeKind.Delete,
            "missing-" + index, "v1", CloudItemKind.File, "missing-" + index, cursorAfter: new byte[] { (byte)(index + 2) });
        CloudRemoteChange original = root.Batch.Changes[0];
        CloudRemoteChange target = new(original.ChangeId, original.Kind, original.RemoteId, original.RemoteRevision,
            original.ItemKind, original.RelativePath, original.ItemId, original.PreviousRemoteRevision, cursorAfter: new byte[] { 5 });
        CloudRemoteChangeBatch batch = new("original-five", new byte[] { 1 }, [Missing(0), Missing(1), Missing(2), target,
            new("last", CloudRemoteChangeKind.Delete, "last", "v1", CloudItemKind.File, "last")], new byte[] { 9 });
        CloudRemoteApplyResult strict = await root.FileSystem.ApplyRemoteChangesAsync(batch);
        Assert.True(strict.RequiresRetry);
        Assert.Equal(3, strict.AppliedEntryCount);
        Assert.Equal(new byte[] { 4 }, strict.SafeCursor.ToArray());
        Assert.IsType<CloudRemoteDeleteRecoveryException>(strict.Entries[3].Error);
        await root.RestartAsync();
        CloudRemoteDeleteReconciliationResult rejection = await root.FileSystem.ReconcileRemoteDeleteAsync(batch, 3);
        Assert.Equal(4, rejection.AppliedEntryCount);
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, rejection.Entry!.Status);
        Assert.Equal(new byte[] { 5 }, rejection.SafeCursor.ToArray());
        await using (ICloudStateTransaction middle = await root.Factory.Store!.BeginTransactionAsync())
        {
            CloudRemoteBatchState progress = (await middle.RemoteBatches.GetAsync(batch.BatchId))!;
            Assert.Equal(4, progress.AppliedEntryCount);
            Assert.Equal(batch.Fingerprint.ToArray(), progress.Fingerprint.ToArray());
            Assert.Equal(target.ChangeId, progress.LastAppliedChangeId);
        }
        await root.RestartAsync();
        CloudRemoteApplyResult suffix = await root.FileSystem.ApplyRemoteChangesAsync(batch);
        Assert.Equal(5, suffix.AppliedEntryCount);
        Assert.False(suffix.RequiresRetry);
        Assert.Equal(CloudRemoteApplyEntryStatus.AlreadyApplied, suffix.Entries[3].Status);
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, suffix.Entries[4].Status);
        Assert.Equal(new byte[] { 9 }, suffix.SafeCursor.ToArray());
        Assert.Equal(rejection.ConflictId, (await root.FileSystem.GetRemoteDeleteReconciliationAsync(batch, 3)).ConflictId);
        Assert.Equal(0, root.Provider.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateDeleteReconciliationCannotAdoptALocallyMovedSurvivor(bool directory)
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(directory);
        string surviving = "Docs\\survivor";
        await using (CloudLocalChangeFeed feed = root.FileSystem.CreateLocalChangeFeed())
        {
            await feed.StartAsync();
            if (directory) { Directory.Move(System.IO.Path.Combine(root.Path, root.Target), System.IO.Path.Combine(root.Path, surviving)); }
            else { File.Move(System.IO.Path.Combine(root.Path, root.Target), System.IO.Path.Combine(root.Path, surviving)); }
            await WaitForReconciliationChangeAsync(feed, change => change.Kind == CloudLocalChangeKind.Move && change.RelativePath == surviving);
        }
        IReadOnlyList<CloudOperationJournalEntry> operations;
        await using (ICloudStateTransaction capture = await root.Factory.Store!.BeginTransactionAsync())
        {
            Assert.Equal(surviving, (await capture.Items.GetByItemIdAsync(root.ItemId))!.RelativePath);
            operations = await capture.Operations.ListAsync(1000);
        }
        string oldPath = System.IO.Path.Combine(root.Path, root.Target);
        await File.WriteAllTextAsync(oldPath, "new address generation");
        await root.RestartAsync();
        Assert.Equal(CloudRemoteDeleteReconciliationStatus.NotApplicable,
            (await root.FileSystem.ReconcileRemoteDeleteAsync(root.Batch, 0)).Status);
        Assert.Equal("new address generation", await File.ReadAllTextAsync(oldPath));
        Assert.True(directory ? Directory.Exists(System.IO.Path.Combine(root.Path, surviving)) : File.Exists(System.IO.Path.Combine(root.Path, surviving)));
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        CloudItemState row = (await verify.Items.GetByItemIdAsync(root.ItemId))!;
        Assert.Equal(surviving, row.RelativePath);
        Assert.False(row.IsTombstone);
        Assert.Null(await verify.RemoteBatches.GetAsync(root.Batch.BatchId));
        Assert.Empty(await verify.Conflicts.ListAsync());
        AssertOriginalReconciliationOperations(operations, await verify.Operations.ListAsync(1000));
        Assert.Equal(0, root.Provider.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataReconciliationWorksOnDisconnectedOwnerWithRemovedRegistration(bool directory)
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(directory);
        if (directory) { await root.FileSystem.GetDirectory(root.Target).DeleteAsync(); }
        else { await root.FileSystem.GetFile(root.Target).DeleteAsync(); }
        await root.RestartAsync(connectProvider: false);
        root.Unregister();
        string path = System.IO.Path.Combine(root.Path, root.Target);
        await File.WriteAllTextAsync(path, "new ordinary occupant after account disabled");
        CloudRemoteDeleteReconciliationResult result = await root.FileSystem.ReconcileRemoteDeleteAsync(root.Batch, 0);
        Assert.Equal(CloudRemoteDeleteReconciliationStatus.RecordedConflict, result.Status);
        Assert.False(result.NativeMutationAttempted);
        Assert.Equal(result.ConflictId, (await root.FileSystem.GetRemoteDeleteReconciliationAsync(root.Batch, 0)).ConflictId);
        Assert.Equal(result.ConflictId, Assert.Single(await root.FileSystem.ListRemoteConflictsAsync()).ConflictId);
        Assert.Equal(CloudRemoteConflictDismissalStatus.Dismissed, await root.FileSystem.DismissRemoteConflictAsync(result.ConflictId!.Value));
        Assert.Equal(result.ConflictId, (await root.FileSystem.GetRemoteDeleteReconciliationAsync(root.Batch, 0)).ConflictId);
        Assert.Equal("new ordinary occupant after account disabled", await File.ReadAllTextAsync(path));
        Assert.Equal(0, root.Provider.Reads);
    }

    private static async Task<CloudLocalChange> WaitForReconciliationChangeAsync(CloudLocalChangeFeed feed, Func<CloudLocalChange, bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        while (true)
        {
            CloudLocalChangeScan scan = await feed.BeginScanAsync(timeout.Token);
            long cursor = 0;
            CloudLocalChangePage page;
            do
            {
                page = await feed.ReadPageAsync(scan, cursor, 100, timeout.Token);
                Assert.False(page.RequiresFullRescan);
                CloudLocalChange? match = page.Changes.FirstOrDefault(predicate);
                if (match is not null) { return match; }
                cursor = page.LastScannedSequence;
            } while (page.HasMore);
            await Task.Delay(25, timeout.Token);
        }
    }

    private static void AssertOriginalReconciliationOperations(IReadOnlyList<CloudOperationJournalEntry> expected,
        IReadOnlyList<CloudOperationJournalEntry> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].OperationId, actual[index].OperationId);
            Assert.Equal(expected[index].Sequence, actual[index].Sequence);
            Assert.Equal(expected[index].Kind, actual[index].Kind);
            Assert.Equal(expected[index].ItemId, actual[index].ItemId);
            Assert.Equal(expected[index].Payload.ToArray(), actual[index].Payload.ToArray());
            Assert.Equal(expected[index].AttemptCount, actual[index].AttemptCount);
            Assert.Equal(expected[index].RetryAfter, actual[index].RetryAfter);
            Assert.Equal(expected[index].CreatedAt, actual[index].CreatedAt);
        }
    }
}
