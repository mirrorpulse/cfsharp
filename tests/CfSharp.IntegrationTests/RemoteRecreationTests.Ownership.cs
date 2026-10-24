using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace CfSharp.IntegrationTests;

public sealed partial class RemoteRecreationTests
{
    [Fact]
    public async Task HardLinkAliasCannotSatisfyOriginalCreationRecovery()
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(false, allowHardLinks: true);
        root.Factory.Boundary = "projection-before";
        Assert.True((await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch)).RequiresRetry);
        string alias = Path.Combine(root.FileSystem.SyncRootPath, "Docs", "alias");
        Assert.True(CreateHardLink(alias, Path.Combine(root.FileSystem.SyncRootPath, root.Target), 0));
        try
        {
            await root.RestartAsync();
            CloudRemoteApplyResult replay = await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch);
            Assert.True(replay.RequiresRetry);
            Assert.Equal(0, replay.AppliedEntryCount);
            Assert.IsType<IOException>(Assert.Single(replay.Entries).Error);
            Assert.True(File.Exists(alias));
            Assert.Equal(root.SuccessorId, (await root.InspectNativeIdentityAsync()).ItemId);
            await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
            Assert.True((await verify.Items.GetByItemIdAsync(root.OriginalId))!.IsTombstone);
            Assert.Null(await verify.Items.GetByItemIdAsync(root.SuccessorId));
            Assert.Single(await verify.Checkpoints.ListAsync(RemoteCreationIntent.Prefix));
            Assert.Equal(0, root.Reads);
        }
        finally { File.Delete(alias); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataOnlyBirthDoesNotRequireDeletePermissionOnItsNewObject(bool directory)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        DirectoryInfo parent = new(Path.Combine(root.FileSystem.SyncRootPath, "Docs"));
        DirectorySecurity originalAcl = parent.GetAccessControl();
        DirectorySecurity restricted = parent.GetAccessControl();
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        restricted.AddAccessRule(new FileSystemAccessRule(caller.User!, FileSystemRights.DeleteSubdirectoriesAndFiles,
            AccessControlType.Deny));
        restricted.AddAccessRule(new FileSystemAccessRule(caller.User!, FileSystemRights.Delete,
            InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit, PropagationFlags.InheritOnly, AccessControlType.Deny));
        parent.SetAccessControl(restricted);
        try
        {
            AssertApplied(await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch), root.SuccessorBatch);
            // Probe DELETE independently: the successful metadata birth must not have relied
            // on a permission the fixture explicitly denies. This open never mutates the object.
            using SafeFileHandle denied = OpenDeleteProbe(Path.Combine(root.FileSystem.SyncRootPath, root.Target),
                0x10080, 3, 0, 3, 0x02200000, 0);
            int error = Marshal.GetLastPInvokeError();
            Assert.True(denied.IsInvalid);
            Assert.Equal(5, error);
            await root.AssertGenerationsAsync();
        }
        finally
        {
            parent.SetAccessControl(originalAcl);
            string target = Path.Combine(parent.FullName, "recreated");
            // Restoring a parent DACL does not necessarily remove an inherited deny already
            // materialized on a cloud placeholder. Restore cleanup rights on this owned object.
            if (directory && Directory.Exists(target))
            {
                DirectorySecurity cleanup = new();
                cleanup.SetAccessRuleProtection(true, false);
                cleanup.AddAccessRule(new FileSystemAccessRule(caller.User!, FileSystemRights.FullControl, AccessControlType.Allow));
                new DirectoryInfo(target).SetAccessControl(cleanup);
            }
            else if (File.Exists(target))
            {
                FileSecurity cleanup = new();
                cleanup.SetAccessRuleProtection(true, false);
                cleanup.AddAccessRule(new FileSystemAccessRule(caller.User!, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(target).SetAccessControl(cleanup);
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ForeignTrackedAndUnmanagedOccupantsRemainProtected(bool directory, bool unmanaged)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        string path = Path.Combine(root.FileSystem.SyncRootPath, root.Target);
        CloudPlaceholderIdentity foreign = new(Guid.NewGuid(), "foreign", "f1");
        if (unmanaged)
        {
            if (directory) { Directory.CreateDirectory(path); }
            else { await File.WriteAllTextAsync(path, "retain ordinary bytes"); }
        }
        else
        {
            CloudPlaceholderSpec spec = directory ? CloudDirectoryPlaceholderSpec.CreateBuilder("recreated", foreign).WithInSyncState(true).Build()
                : CloudFilePlaceholderSpec.CreateBuilder("recreated", foreign, 7).WithInSyncState(true).Build();
            await root.FileSystem.GetDirectory("Docs").CreatePlaceholderAsync(spec);
        }
        CloudRemoteApplyResult result = await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch);
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, Assert.Single(result.Entries).Status);
        Assert.Single(result.ConflictIds);
        if (unmanaged && !directory) { Assert.Equal("retain ordinary bytes", await File.ReadAllTextAsync(path)); }
        if (!unmanaged) { Assert.Equal(foreign.ItemId, (await root.InspectNativeIdentityAsync()).ItemId); }
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        Assert.Null(await verify.Items.GetByItemIdAsync(root.SuccessorId));
        Assert.True((await verify.Items.GetByItemIdAsync(root.OriginalId))!.IsTombstone);
        Assert.Equal(0, root.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitIdentityCollisionIsDurableAndItsReplayCannotBecomeABirth(bool directory)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        CloudRemoteChangeBatch collision = root.Birth("collision", root.SuccessorId, "original-remote", "v3", 7, 2, 3);
        CloudRemoteApplyResult first = await root.FileSystem.ApplyRemoteChangesAsync(collision);
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, Assert.Single(first.Entries).Status);
        Assert.Single(first.ConflictIds);
        await root.RestartAsync();
        Assert.Equal(CloudRemoteApplyEntryStatus.AlreadyApplied, Assert.Single((await root.FileSystem.ApplyRemoteChangesAsync(collision)).Entries).Status);
        Assert.False((await root.Item.InspectAsync()).Exists);
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        Assert.True((await verify.Items.GetByItemIdAsync(root.OriginalId))!.IsTombstone);
        Assert.Null(await verify.Items.GetByItemIdAsync(root.SuccessorId));
        Assert.NotNull(await verify.Conflicts.GetAsync(first.ConflictIds[0]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SameIdentityResurrectionAndImplicitDistinctBirthHaveSeparateSemantics(bool directory, bool implicitBirth)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        CloudRemoteChangeBatch batch = root.Birth("compatible", implicitBirth ? null : root.OriginalId,
            implicitBirth ? "successor-remote" : "original-remote", "v3", 7, 2, 3);
        AssertApplied(await root.FileSystem.ApplyRemoteChangesAsync(batch), batch);
        Guid actual = (await root.InspectNativeIdentityAsync()).ItemId;
        if (implicitBirth) { Assert.NotEqual(root.OriginalId, actual); }
        else { Assert.Equal(root.OriginalId, actual); }
        await root.RestartAsync();
        Assert.False((await root.FileSystem.ApplyRemoteChangesAsync(batch)).RequiresRetry);
        Assert.Equal(actual, (await root.InspectNativeIdentityAsync()).ItemId);
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        Assert.Equal(implicitBirth, (await verify.Items.GetByItemIdAsync(root.OriginalId))!.IsTombstone);
        Assert.Equal(0, root.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultipleRetirementsAndCaseVariantsKeepOneLiveViewAndOriginalPaths(bool directory)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        CloudRemoteChangeBatch successor = root.Birth("successor", root.SuccessorId, "successor-remote", "v3", 7, 2, 3, "dOcS\\RECREATED");
        AssertApplied(await root.FileSystem.ApplyRemoteChangesAsync(successor), successor);
        CloudSynchronizedDirectoryPage view = await root.FileSystem.ReadSynchronizedDirectoryPageAsync(new("Docs", includeTombstones: true));
        Assert.Equal(root.SuccessorId, Assert.Single(view.Entries).DurableState!.ItemId);
        CloudRemoteChangeBatch retire = new("retire-successor", new byte[] { 3 }, [new("retire-successor", CloudRemoteChangeKind.Delete,
            "successor-remote", "v4", root.Kind, "dOcS\\RECREATED", root.SuccessorId, previousRemoteRevision: "v3")], new byte[] { 4 });
        AssertApplied(await root.FileSystem.ApplyRemoteChangesAsync(retire, AuthorizedDelete), retire);
        Guid thirdId = Guid.NewGuid();
        CloudRemoteChangeBatch third = root.Birth("third", thirdId, "third-remote", "v5", 9, 4, 5);
        AssertApplied(await root.FileSystem.ApplyRemoteChangesAsync(third), third);
        await root.RestartAsync();
        Assert.False((await root.FileSystem.ApplyRemoteChangesAsync(successor)).RequiresRetry);
        Assert.False((await root.FileSystem.ApplyRemoteChangesAsync(retire, AuthorizedDelete)).RequiresRetry);
        Assert.Equal(thirdId, (await root.InspectNativeIdentityAsync()).ItemId);
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        CloudItemState original = (await verify.Items.GetByItemIdAsync(root.OriginalId))!;
        CloudItemState second = (await verify.Items.GetByItemIdAsync(root.SuccessorId))!;
        Assert.True(original.IsTombstone);
        Assert.Equal(root.Target, original.RelativePath);
        Assert.Equal("v2", original.RemoteRevision);
        Assert.True(second.IsTombstone);
        Assert.Equal(successor.Changes[0].RelativePath, second.RelativePath);
        Assert.Equal("v4", second.RemoteRevision);
        Assert.Equal(thirdId, (await verify.Items.GetByRelativePathAsync("DOCS\\recreated"))!.ItemId);
        Assert.Equal(3, (await verify.Items.ListSubtreeAsync(root.Target)).Count);
        Assert.Equal(0, root.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompatibleLegacyPreparationRetainsItsOriginalFingerprintAndDoesNotCreate(bool directory)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        CloudRemoteChange change = root.SuccessorBatch.Changes[0];
        byte[] legacy = new RemoteCreationIntent(change.RelativePath, change.ItemKind,
            new(root.OriginalId, change.RemoteId, change.RemoteRevision), RemoteCreationIntent.GetFingerprint(change), false).Encode();
        await using (ICloudStateTransaction seed = await root.Factory.Store!.BeginTransactionAsync())
        {
            await seed.Checkpoints.UpsertAsync(new(RemoteCreationIntent.Name(change), legacy, DateTimeOffset.UtcNow));
            await seed.CommitAsync();
        }
        await root.RestartAsync();
        CloudRemoteApplyResult result = await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch);
        Assert.True(result.RequiresRetry);
        Assert.Equal(0, result.AppliedEntryCount);
        Assert.IsType<InvalidDataException>(Assert.Single(result.Entries).Error);
        Assert.False((await root.Item.InspectAsync()).Exists);
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        Assert.Equal(legacy, (await verify.Checkpoints.GetAsync(RemoteCreationIntent.Name(change)))!.Value.ToArray());
        Assert.True((await verify.Items.GetByItemIdAsync(root.OriginalId))!.IsTombstone);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AlreadyAcceptedWrongIdentityRemainsFencedAfterUpgradeWithoutFabricatedRepair(bool directory)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        CloudPlaceholderIdentity wrong = new(root.OriginalId, "successor-remote", "v3");
        CloudPlaceholderSpec spec = directory ? CloudDirectoryPlaceholderSpec.CreateBuilder("recreated", wrong).WithInSyncState(true).Build()
            : CloudFilePlaceholderSpec.CreateBuilder("recreated", wrong, 7).WithInSyncState(true).Build();
        await root.FileSystem.GetDirectory("Docs").CreatePlaceholderAsync(spec);
        DateTimeOffset acceptedAt = DateTimeOffset.UtcNow;
        await using (ICloudStateTransaction seed = await root.Factory.Store!.BeginTransactionAsync())
        {
            await seed.RemoteBatches.UpsertAsync(new("successor", new byte[] { 3 }, 1, 1, CloudRemoteBatchStatus.Applied,
                [9], acceptedAt, root.SuccessorBatch.Fingerprint, root.SuccessorBatch.Changes[0].ChangeId));
            await seed.CommitAsync();
        }
        await ReopenPreviewSevenLayoutAsync(root);
        await Assert.ThrowsAsync<InvalidDataException>(() => root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch).AsTask());
        Assert.Equal(root.OriginalId, (await root.InspectNativeIdentityAsync()).ItemId);
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        Assert.False((await verify.Items.GetByItemIdAsync(root.OriginalId))!.IsTombstone);
        Assert.Null(await verify.Items.GetByItemIdAsync(root.SuccessorId));
        CloudRemoteBatchState retained = (await verify.RemoteBatches.GetAsync("successor"))!;
        Assert.Equal(CloudRemoteBatchStatus.Applied, retained.Status);
        Assert.Equal(root.SuccessorBatch.Fingerprint.ToArray(), retained.Fingerprint.ToArray());
        Assert.Equal(acceptedAt, retained.UpdatedAt);
        Assert.Equal(new byte[] { 9 }, retained.Payload.ToArray());
        Assert.Equal(0, root.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameIdDifferentKindCannotRepurposeRetiredHistory(bool directory)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        CloudItemKind other = directory ? CloudItemKind.File : CloudItemKind.Directory;
        CloudRemoteChangeBatch batch = new("wrong-kind", new byte[] { 2 }, [new("wrong-kind",
            directory ? CloudRemoteChangeKind.FileUpsert : CloudRemoteChangeKind.DirectoryUpsert, "original-remote", "v3", other,
            root.Target, root.OriginalId, length: directory ? 7 : null, metadata: directory ?
                CloudPlaceholderMetadata.CreateFileBuilder().Build() : CloudPlaceholderMetadata.CreateDirectoryBuilder().Build())], new byte[] { 3 });
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, Assert.Single((await root.FileSystem.ApplyRemoteChangesAsync(batch)).Entries).Status);
        Assert.False((await root.Item.InspectAsync()).Exists);
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        CloudItemState original = (await verify.Items.GetByItemIdAsync(root.OriginalId))!;
        Assert.True(original.IsTombstone);
        Assert.Equal(root.Kind, original.Kind);
        Assert.Equal("v2", original.RemoteRevision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DistinctAbsentSuccessorMayHaveADifferentKindWithoutRewritingHistory(bool directory)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        CloudItemKind other = directory ? CloudItemKind.File : CloudItemKind.Directory;
        CloudRemoteChangeBatch batch = new("successor", new byte[] { 2 }, [new("successor",
            directory ? CloudRemoteChangeKind.FileUpsert : CloudRemoteChangeKind.DirectoryUpsert, "successor-remote", "v3", other,
            root.Target, root.SuccessorId, length: directory ? 7 : null, metadata: directory ?
                CloudPlaceholderMetadata.CreateFileBuilder().Build() : CloudPlaceholderMetadata.CreateDirectoryBuilder().Build())], new byte[] { 3 });
        AssertApplied(await root.FileSystem.ApplyRemoteChangesAsync(batch), batch);
        CloudItem successor = directory ? root.FileSystem.GetFile(root.Target) : root.FileSystem.GetDirectory(root.Target);
        Assert.Equal(root.SuccessorId, CloudPlaceholderIdentity.Decode((await successor.InspectAsync()).PlaceholderIdentity.Span).ItemId);
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        CloudItemState original = (await verify.Items.GetByItemIdAsync(root.OriginalId))!;
        Assert.True(original.IsTombstone);
        Assert.Equal(root.Kind, original.Kind);
        Assert.Equal("v2", original.RemoteRevision);
        Assert.Equal(other, (await verify.Items.GetByItemIdAsync(root.SuccessorId))!.Kind);
        Assert.Equal(0, root.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldPendingDeleteCannotActOnASuccessorAtItsRetiredPath(bool directory)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory, interruptDelete: true);
        AssertApplied(await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch), root.SuccessorBatch);
        await root.RestartAsync();
        CloudRemoteApplyResult replay = await root.FileSystem.ApplyRemoteChangesAsync(root.DeleteBatch, AuthorizedDelete);
        Assert.True(replay.RequiresRetry);
        Assert.Equal(0, replay.AppliedEntryCount);
        Assert.IsType<CloudRemoteDeleteRecoveryException>(Assert.Single(replay.Entries).Error);
        await root.AssertGenerationsAsync();
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        Assert.Single(await verify.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirtyOriginallyCreatedObjectIsPreservedDuringRecovery(bool directory)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        root.Factory.Boundary = "projection-before";
        Assert.True((await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch)).RequiresRetry);
        await root.Item.SetInSyncAsync(false);
        await root.RestartAsync();
        Assert.True((await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch)).RequiresRetry);
        Assert.Equal(CloudSynchronizationState.NotInSync, (await root.Item.InspectAsync()).SynchronizationState);
        Assert.Equal(root.SuccessorId, (await root.InspectNativeIdentityAsync()).ItemId);
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        Assert.Null(await verify.Items.GetByItemIdAsync(root.SuccessorId));
        Assert.True((await verify.Items.GetByItemIdAsync(root.OriginalId))!.IsTombstone);
        Assert.Equal(0, root.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalJournalAcknowledgementAndDelayedDeleteCannotRetargetSuccessor(bool directory)
    {
        await using RecreationRoot root = await RecreationRoot.OpenAsync(directory);
        Guid operationId = Guid.NewGuid();
        byte[] payload = new LocalChangePayload(root.Target, null, directory, DateTimeOffset.UtcNow).Encode();
        await using (ICloudStateTransaction seed = await root.Factory.Store!.BeginTransactionAsync())
        {
            await seed.Operations.EnqueueAsync(new(operationId, CloudStateOperationKind.Delete, root.OriginalId, payload, DateTimeOffset.UtcNow));
            await seed.CommitAsync();
        }
        AssertApplied(await root.FileSystem.ApplyRemoteChangesAsync(root.SuccessorBatch), root.SuccessorBatch);
        RecreationSource source = new();
        await using CloudLocalChangeFeed feed = CloudLocalChangeFeed.CreateForTesting(root.FileSystem.SyncRootPath,
            root.Factory.Store!, new(), source);
        await feed.StartAsync();
        await source.EmitAsync(new(LocalChangeSourceAction.Deleted, root.Target));
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        while (!(await feed.ReadBatchAsync(cancellationToken: deadline.Token)).RequiresFullRescan) { await Task.Delay(10, deadline.Token); }
        await root.AssertGenerationsAsync();
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        CloudOperationJournalEntry operation = (await verify.Operations.GetAsync(operationId))!;
        Assert.Equal(root.OriginalId, operation.ItemId);
        Assert.Equal(payload, operation.Payload.ToArray());
        Assert.Empty(await verify.Operations.ListByItemIdAsync(root.SuccessorId, 10));
        await verify.Operations.RemoveAsync(operationId);
        Assert.False((await verify.Items.GetByItemIdAsync(root.SuccessorId))!.IsTombstone);
        await verify.CommitAsync();
    }

    private sealed class RecreationSource : ILocalChangeSource
    {
        private Func<LocalChangeSourceEvent, ValueTask>? _handler;
        public Task StartAsync(Func<LocalChangeSourceEvent, ValueTask> handler, CancellationToken cancellationToken)
        {
            _handler = handler;
            return Task.CompletedTask;
        }
        internal ValueTask EmitAsync(LocalChangeSourceEvent value) => _handler!(value);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task ReopenPreviewSevenLayoutAsync(RecreationRoot root)
    {
        await root.FileSystem.DisposeAsync();
        await using (SqliteConnection connection = new(new SqliteConnectionStringBuilder { DataSource = root.Database, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            connection.CreateCollation("CFSHARP_UNICODE_NOCASE", (first, second) => string.Compare(first, second, StringComparison.OrdinalIgnoreCase));
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA foreign_keys = OFF;
                BEGIN IMMEDIATE;
                CREATE TABLE items_preview_seven (
                    item_id TEXT NOT NULL PRIMARY KEY,
                    remote_id TEXT NOT NULL UNIQUE,
                    relative_path TEXT NOT NULL COLLATE CFSHARP_UNICODE_NOCASE UNIQUE,
                    kind INTEGER NOT NULL,
                    remote_revision TEXT NULL,
                    local_file_id INTEGER NULL,
                    is_tombstone INTEGER NOT NULL,
                    updated_at_ticks INTEGER NOT NULL
                );
                INSERT INTO items_preview_seven SELECT * FROM items;
                DROP TABLE items;
                ALTER TABLE items_preview_seven RENAME TO items;
                CREATE UNIQUE INDEX ux_items_relative_path_unicode_nocase ON items(relative_path COLLATE CFSHARP_UNICODE_NOCASE);
                UPDATE cfsharp_schema SET version = 8;
                COMMIT;
                PRAGMA foreign_keys = ON;
                """;
            await command.ExecuteNonQueryAsync();
        }
        await root.RestartAsync();
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle OpenDeleteProbe(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string path, string existingPath, nint security);
}
