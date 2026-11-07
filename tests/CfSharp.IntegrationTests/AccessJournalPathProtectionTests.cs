using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;

using Microsoft.Win32.SafeHandles;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InSyncSecurityNotificationDoesNotConsumeRemoteEcho(bool directory, bool metadataEcho)
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        string path = Path.Combine(fixture.Root, "before");
        if (directory) { Directory.CreateDirectory(path); }
        else { await File.WriteAllTextAsync(path, "resident-original"); }
        CloudItem item = directory ? fixture.System.GetDirectory("before") : fixture.System.GetFile("before");
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "security-creation-echo", "v1");
        await item.ConvertToPlaceholderAsync(identity);
        await item.SetInSyncAsync(true);
        if (metadataEcho)
        {
            CloudPlaceholderMetadata metadata = (directory ? CloudPlaceholderMetadata.CreateDirectoryBuilder()
                : CloudPlaceholderMetadata.CreateFileBuilder()).WithAttributes(directory
                    ? FileAttributes.Directory | FileAttributes.Hidden : FileAttributes.Hidden).Build();
            CloudRemoteChange change = new("remote-metadata", CloudRemoteChangeKind.MetadataUpdate, identity.RemoteId, "v2",
                item.Kind, item.RelativePath, identity.ItemId, previousRemoteRevision: "v1", metadata: metadata);
            CloudRemoteApplyResult applied = await fixture.System.ApplyRemoteChangesAsync(new("echo", Array.Empty<byte>(), [change], new byte[] { 1 }));
            Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single(applied.Entries).Status);
        }
        CloudItemSnapshot before = await item.InspectAsync();
        CloudEchoSuppressionState expected;
        await using (ICloudStateTransaction seed = await factory!.Store!.BeginTransactionAsync())
        {
            if (!metadataEcho)
            {
                await seed.EchoSuppressions.UpsertAsync(new(Guid.NewGuid(), identity.ItemId, CloudStateOperationKind.Create,
                    "before", RemoteCreationIntent.EchoPayload(identity), DateTimeOffset.UtcNow.AddMinutes(10), remainingObservations: 2));
            }
            expected = Assert.Single(await seed.EchoSuppressions.ListActiveAsync(DateTimeOffset.UtcNow));
            Assert.Equal(metadataEcho ? CloudStateOperationKind.MetadataUpdate : CloudStateOperationKind.Create, expected.Kind);
            Assert.Equal(identity.ItemId, expected.ItemId);
            await seed.CommitAsync();
        }
        DelayedSecuritySource source = new(fixture.Root, line => output.WriteLine(line));
        await using CloudLocalChangeFeed feed = new(fixture.Root, factory!.Store!, new(), source,
            acquireOperation: async (scopes, stop) => await fixture.System.AcquireOperationAsync(scopes, stop));
        await feed.StartAsync();
        await WriteSecurityObservationAsync(item, before);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        LocalChangeSourceEvent security = await source.Security.Task.WaitAsync(deadline.Token);
        CloudItemSnapshot after = await item.InspectAsync(deadline.Token);
        Assert.Equal(CloudSynchronizationState.InSync, after.SynchronizationState);
        Assert.Equal(before.PlaceholderIdentity.ToArray(), after.PlaceholderIdentity.ToArray());
        Assert.Equal(unchecked((ulong)after.LocalFileId!.Value), security.SecurityIdentity!.Value.FileId);
        await source.DeliverAsync(security);
        await feed.DisposeAsync();
        await using ICloudStateTransaction verify = await factory.Store!.BeginTransactionAsync();
        CloudEchoSuppressionState echo = Assert.Single(await verify.EchoSuppressions.ListActiveAsync(DateTimeOffset.UtcNow));
        Assert.Equal(expected.SuppressionId, echo.SuppressionId);
        Assert.Equal(expected.RemainingObservations, echo.RemainingObservations);
        Assert.Equal(expected.Payload.ToArray(), echo.Payload.ToArray());
        CloudOperationJournalEntry entry = Assert.Single(await verify.Operations.ListAsync(100));
        Assert.Equal(CloudStateOperationKind.MetadataUpdate, entry.Kind);
        Assert.Equal(identity.ItemId, entry.ItemId);
        LocalChangePayload payload = LocalChangePayload.Decode(entry.Payload);
        Assert.Equal("before", payload.RelativePath);
        Assert.Equal(directory, payload.IsDirectory);
        Assert.False(LocalChangeCheckpoint.Decode((await verify.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName))!.Value).RequiresFullRescan);
        Assert.Equal(0, fixture.Provider.Fetches);
        output.WriteLine($"In-sync security: directory={directory}; echoKind={echo.Kind}; nativeFile={after.LocalFileId}; bound={entry.ItemId}; echoRemaining={echo.RemainingObservations}.");
    }

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(false, SecurityAncestorFailure.JunctionAtAcquisition, false)]
    [InlineData(true, SecurityAncestorFailure.JunctionAtAcquisition, false)]
    [InlineData(false, SecurityAncestorFailure.JunctionAtBinding, false)]
    [InlineData(true, SecurityAncestorFailure.JunctionAtBinding, false)]
    [InlineData(false, SecurityAncestorFailure.JunctionAtAcquisition, true)]
    [InlineData(true, SecurityAncestorFailure.JunctionAtAcquisition, true)]
    [InlineData(false, SecurityAncestorFailure.JunctionAtBinding, true)]
    [InlineData(true, SecurityAncestorFailure.JunctionAtBinding, true)]
    [InlineData(false, SecurityAncestorFailure.JunctionBeforeDelivery, false)]
    [InlineData(true, SecurityAncestorFailure.JunctionBeforeDelivery, false)]
    [InlineData(false, SecurityAncestorFailure.JunctionBeforeDelivery, true)]
    [InlineData(true, SecurityAncestorFailure.JunctionBeforeDelivery, true)]
    [InlineData(false, SecurityAncestorFailure.SharingViolation, true)]
    [InlineData(true, SecurityAncestorFailure.SharingViolation, true)]
    public async Task SecurityAncestorFailureRetainsObservationAndProcessesLaterEvents(bool directory, SecurityAncestorFailure failure, bool creationEcho)
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        string parent = Path.Combine(fixture.Root, "parent");
        string retired = Path.Combine(fixture.Root, "retired-parent");
        string outside = Path.Combine(Directory.GetParent(fixture.Root)!.FullName, "junction-target");
        string staging = Path.Combine(Directory.GetParent(fixture.Root)!.FullName, "junction-staging");
        Directory.CreateDirectory(parent);
        Directory.CreateDirectory(outside);
        string relativePath = Path.Combine("parent", "before");
        string path = Path.Combine(fixture.Root, relativePath);
        string target = Path.Combine(outside, "before");
        if (directory) { Directory.CreateDirectory(path); }
        else { await File.WriteAllTextAsync(path, "resident-original"); }
        // A mismatched external target makes any unsafe echo inspection fail in the
        // real inspector. The rejected namespace must never reach that probe.
        bool targetDirectory = directory != creationEcho;
        if (targetDirectory) { Directory.CreateDirectory(target); }
        string sentinel = targetDirectory ? Path.Combine(target, "sentinel.bin") : target;
        await File.WriteAllTextAsync(sentinel, "outside-unchanged");
        CloudItem item = directory ? fixture.System.GetDirectory(relativePath) : fixture.System.GetFile(relativePath);
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "security-path-protection", "v1");
        await item.ConvertToPlaceholderAsync(identity);
        CloudItemSnapshot before = await item.InspectAsync();
        Guid retainedId = Guid.NewGuid();
        Guid echoId = Guid.NewGuid();
        CloudItemState originalState;
        await using (ICloudStateTransaction seed = await factory!.Store!.BeginTransactionAsync())
        {
            originalState = (await seed.Items.GetByItemIdAsync(identity.ItemId))!;
            await seed.Operations.EnqueueAsync(new(retainedId, CloudStateOperationKind.MetadataUpdate, identity.ItemId,
                new LocalChangePayload(relativePath, null, directory, DateTimeOffset.UtcNow).Encode(), DateTimeOffset.UtcNow));
            if (creationEcho)
            {
                await seed.EchoSuppressions.UpsertAsync(new(echoId, identity.ItemId, CloudStateOperationKind.Create,
                    relativePath, RemoteCreationIntent.EchoPayload(identity), DateTimeOffset.UtcNow.AddMinutes(10)));
            }
            await seed.CommitAsync();
        }
        IReadOnlyList<CloudOperationJournalEntry> originals = await AccessRowsAsync(factory!);
        int acquisitions = 0;
        int inspections = 0;
        int? guardError = null;
        bool junctionCreated = false;
        Guid observationId;
        long observationSequence;
        DelayedSecuritySource source = new(fixture.Root, line => output.WriteLine(line), relativePath);
        try
        {
            {
                await using CloudLocalChangeFeed feed = new(fixture.Root, factory!.Store!, new(), source,
                    acquireOperation: async (scopes, stop) =>
                    {
                        Assert.Equal(Path.Combine(fixture.Root, relativePath), Assert.Single(scopes).Path);
                        Assert.Equal(1, ++acquisitions);
                        if (failure == SecurityAncestorFailure.SharingViolation)
                        {
                            const uint delete = 0x00010000;
                            const uint listDirectoryAndReadAttributes = 0x81;
                            const uint shareReadWriteDelete = 7;
                            const uint shareReadWrite = 3;
                            const uint openExisting = 3;
                            const uint backupSemanticsAndOpenReparsePoint = 0x02200000;
                            using SafeFileHandle competing = OpenSecurityTestDirectory(parent, delete, shareReadWriteDelete,
                                0, openExisting, backupSemanticsAndOpenReparsePoint, 0);
                            if (competing.IsInvalid) { throw new Win32Exception(Marshal.GetLastPInvokeError()); }
                            // A metadata-only open can be exempt from NTFS sharing checks. Request
                            // directory-list access here to reliably exercise the native failure
                            // at the lease boundary without changing the production guard's access.
                            using SafeFileHandle guard = OpenSecurityTestDirectory(parent, listDirectoryAndReadAttributes, shareReadWrite,
                                0, openExisting, backupSemanticsAndOpenReparsePoint, 0);
                            guardError = Marshal.GetLastPInvokeError();
                            Assert.True(guard.IsInvalid);
                            Assert.Equal(32, guardError);
                            output.WriteLine($"Native parent open rejected the competing handle: {guardError}.");
                            throw new IOException("The parent directory guard could not be opened without delete sharing.",
                                new Win32Exception(guardError.Value));
                        }

                        Assert.NotEqual(SecurityAncestorFailure.JunctionBeforeDelivery, failure);
                        await ReplaceAncestorWithJunctionAsync(stop);
                        // Hold only the root for the binding case so TryBind must guard parent.
                        return await fixture.System.AcquireOperationAsync(failure == SecurityAncestorFailure.JunctionAtBinding
                            ? [CloudItemOperationScope.Exact(fixture.Root)] : scopes, stop);
                    }, securityInspection: (handle, nativePath, kind, root) =>
                    {
                        inspections++;
                        return CloudItemInspector.Inspect(handle, nativePath, kind, root);
                    });
                await feed.StartAsync();
                await WriteSecurityObservationAsync(item, before);
                using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
                LocalChangeSourceEvent security = await source.Security.Task.WaitAsync(deadline.Token);
                if (failure == SecurityAncestorFailure.JunctionBeforeDelivery) { await ReplaceAncestorWithJunctionAsync(deadline.Token); }
                await source.DeliverAsync(security);
                while (!(await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan) { await Task.Delay(20, deadline.Token); }
                await File.WriteAllTextAsync(Path.Combine(fixture.Root, "later.bin"), "genuine-later-bytes", deadline.Token);
                LocalChangeSourceEvent? created = null;
                while (created is null)
                {
                    created = source.Events.Where(change => change.Action == LocalChangeSourceAction.Created && change.RelativePath == "later.bin")
                        .Select(change => (LocalChangeSourceEvent?)change).FirstOrDefault();
                    if (created is null) { await Task.Delay(20, deadline.Token); }
                }
                await source.DeliverAsync(created.Value);
                await WaitForAccessRowsAsync(factory!, rows => rows.Any(row => row.Kind == CloudStateOperationKind.Create));
                await feed.DisposeAsync();
                Assert.Equal(failure == SecurityAncestorFailure.JunctionBeforeDelivery ? 0 : 1, acquisitions);
                Assert.Equal(failure == SecurityAncestorFailure.SharingViolation ? 32 : (int?)null, guardError);
                Assert.Equal(0, inspections);
                Assert.Equal("outside-unchanged", await File.ReadAllTextAsync(sentinel));
                await using ICloudStateTransaction verify = await factory.Store!.BeginTransactionAsync();
                IReadOnlyList<CloudOperationJournalEntry> rows = await verify.Operations.ListAsync(100);
                AssertOriginalAccessRows(originals, rows);
                CloudOperationJournalEntry observation = Assert.Single(rows.Where(row => row.Kind == CloudStateOperationKind.MetadataUpdate && row.OperationId != retainedId));
                Assert.Null(observation.ItemId);
                LocalChangePayload payload = LocalChangePayload.Decode(observation.Payload);
                Assert.Equal(relativePath, payload.RelativePath);
                Assert.Equal(directory, payload.IsDirectory);
                observationId = observation.OperationId;
                observationSequence = observation.Sequence;
                Assert.Single(await verify.Checkpoints.ListAsync(CloudLocalChangeFeed.NamespaceObservationsPrefix));
                Assert.True(LocalChangeCheckpoint.Decode((await verify.Checkpoints.GetAsync(CloudLocalChangeFeed.CheckpointName))!.Value).RequiresFullRescan);
                Assert.Equivalent(originalState, await verify.Items.GetByItemIdAsync(identity.ItemId), strict: true);
                Assert.Equal(2, (await verify.Items.ListSubtreeAsync(string.Empty)).Count);
                if (creationEcho)
                {
                    CloudEchoSuppressionState echo = Assert.Single(await verify.EchoSuppressions.ListActiveAsync(DateTimeOffset.UtcNow));
                    Assert.Equal(echoId, echo.SuppressionId);
                    Assert.Equal(1, echo.RemainingObservations);
                    Assert.Equal(RemoteCreationIntent.EchoPayload(identity), echo.Payload.ToArray());
                }
                output.WriteLine($"Security ancestor: directory={directory}; failure={failure}; guardError={guardError}; creationEcho={creationEcho}; nativeFile={before.LocalFileId}; unbound={observationId}; continued=True; outsideUnchanged=True.");
            }

            // Reconciliation may leave the junction in place. The historical observation
            // must still be readable and acknowledgeable without following that path.
            await fixture.RestartAsync();
            await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed();
            await reopened.StartAsync();
            CloudLocalChangeScan scan = await reopened.BeginScanAsync();
            Assert.True(scan.RequiresFullRescan);
            Assert.Empty((await reopened.ReadPageAsync(scan, 0, 100)).Changes);
            await reopened.AcknowledgeFullRescanAsync();
            CloudLocalChange observationAfterRestart = Assert.Single((await reopened.ReadPageAsync(await reopened.BeginScanAsync(), 0, 100)).Changes
                .Where(row => row.OperationId == observationId));
            Assert.Null(observationAfterRestart.ItemId);
            Assert.Equal(observationSequence, observationAfterRestart.Sequence);
            Assert.Equal(CloudLocalChangeKind.MetadataUpdate, observationAfterRestart.Kind);
            await reopened.AcknowledgeAsync([observationId]);
            AssertOriginalAccessRows(originals, await AccessRowsAsync(factory!));
            if (creationEcho)
            {
                await using ICloudStateTransaction verify = await factory!.Store!.BeginTransactionAsync();
                CloudEchoSuppressionState echo = Assert.Single(await verify.EchoSuppressions.ListActiveAsync(DateTimeOffset.UtcNow));
                Assert.Equal(echoId, echo.SuppressionId);
                Assert.Equal(1, echo.RemainingObservations);
            }
            Assert.Equal(0, fixture.Provider.Fetches);
        }
        finally
        {
            // Delete the link itself, never recurse through it into the external target.
            if (junctionCreated) { Directory.Delete(parent); }
            if (Directory.Exists(staging)) { Directory.Delete(staging); }
            if (Directory.Exists(retired)) { Directory.Move(retired, parent); }
        }

        async Task ReplaceAncestorWithJunctionAsync(CancellationToken stop)
        {
            Directory.Move(parent, retired);
            ProcessStartInfo start = new("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("mklink");
            start.ArgumentList.Add("/J");
            // Create the mount point outside the connected root before moving it into place.
            start.ArgumentList.Add(staging);
            start.ArgumentList.Add(outside);
            using Process link = Process.Start(start)!;
            await link.WaitForExitAsync(stop);
            output.WriteLine(await link.StandardOutput.ReadToEndAsync(stop));
            output.WriteLine(await link.StandardError.ReadToEndAsync(stop));
            Assert.Equal(0, link.ExitCode);
            Directory.Move(staging, parent);
            junctionCreated = true;
            Assert.True((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0);
            Assert.Equal(outside, new DirectoryInfo(parent).ResolveLinkTarget(true)!.FullName);
        }
    }

    private static async Task WriteSecurityObservationAsync(CloudItem item, CloudItemSnapshot before)
    {
        CloudProtectedLocalOperationResult applied = await item.RunProtectedLocalOperationAsync(new(before.LocalBinding!,
            item.Kind == CloudItemKind.Directory ? CloudProtectedLocalOperationMode.DirectoryMetadata : CloudProtectedLocalOperationMode.ExclusiveFile), async (scope, stop) =>
        {
            FileSystemSecurity access = await scope.ReadAccessDescriptorAsync(stop);
            access.AddAccessRule(AccessTestRule());
            await scope.ApplyAccessDescriptorAsync(access, stop);
        });
        Assert.True(applied.AccessDescriptorApplied && applied.AccessDescriptorReadBack);
    }

    public enum SecurityAncestorFailure
    {
        JunctionAtAcquisition,
        JunctionAtBinding,
        JunctionBeforeDelivery,
        SharingViolation,
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle OpenSecurityTestDirectory(string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);
}
