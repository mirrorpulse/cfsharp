using System.Diagnostics;
using System.Security.AccessControl;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task SecurityAncestorJunctionRetainsObservationAndProcessesLaterEvents(bool directory, bool failAcquisition)
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
        if (directory) { Directory.CreateDirectory(path); Directory.CreateDirectory(target); }
        else { await File.WriteAllTextAsync(path, "resident-original"); }
        string sentinel = directory ? Path.Combine(target, "sentinel.bin") : target;
        await File.WriteAllTextAsync(sentinel, "outside-unchanged");
        CloudItem item = directory ? fixture.System.GetDirectory(relativePath) : fixture.System.GetFile(relativePath);
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "security-path-protection", "v1");
        await item.ConvertToPlaceholderAsync(identity);
        CloudItemSnapshot before = await item.InspectAsync();
        Guid retainedId = Guid.NewGuid();
        CloudItemState originalState;
        await using (ICloudStateTransaction seed = await factory!.Store!.BeginTransactionAsync())
        {
            originalState = (await seed.Items.GetByItemIdAsync(identity.ItemId))!;
            await seed.Operations.EnqueueAsync(new(retainedId, CloudStateOperationKind.MetadataUpdate, identity.ItemId,
                new LocalChangePayload(relativePath, null, directory, DateTimeOffset.UtcNow).Encode(), DateTimeOffset.UtcNow));
            await seed.CommitAsync();
        }
        IReadOnlyList<CloudOperationJournalEntry> originals = await AccessRowsAsync(factory!);
        int acquisitions = 0;
        int inspections = 0;
        bool junctionCreated = false;
        Guid observationId;
        long observationSequence;
        DelayedSecuritySource source = new(fixture.Root, line => output.WriteLine(line), relativePath);
        try
        {
            await using CloudLocalChangeFeed feed = new(fixture.Root, factory!.Store!, new(), source,
                acquireOperation: async (scopes, stop) =>
                {
                    Assert.Equal(Path.Combine(fixture.Root, relativePath), Assert.Single(scopes).Path);
                    Assert.Equal(1, ++acquisitions);
                    // This callback runs after lexical path resolution and before either
                    // native guard. Replace the actual ancestor, not the guard's result.
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
                    // Cloud Files can reject setting a mount-point tag inside the root.
                    // Create it outside first, then rename the real junction into place.
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
                    // The acquisition case uses the production facade lease. The binding
                    // case holds only the root so TryBind must independently guard parent.
                    return await fixture.System.AcquireOperationAsync(failAcquisition ? scopes : [CloudItemOperationScope.Exact(fixture.Root)], stop);
                }, securityInspection: (handle, nativePath, kind, root) =>
                {
                    inspections++;
                    return CloudItemInspector.Inspect(handle, nativePath, kind, root);
                });
            await feed.StartAsync();
            CloudProtectedLocalOperationResult applied = await item.RunProtectedLocalOperationAsync(new(before.LocalBinding!,
                directory ? CloudProtectedLocalOperationMode.DirectoryMetadata : CloudProtectedLocalOperationMode.ExclusiveFile), async (scope, stop) =>
            {
                FileSystemSecurity access = await scope.ReadAccessDescriptorAsync(stop);
                access.AddAccessRule(AccessTestRule());
                await scope.ApplyAccessDescriptorAsync(access, stop);
            });
            Assert.True(applied.AccessDescriptorApplied && applied.AccessDescriptorReadBack);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
            await source.DeliverAsync(await source.Security.Task.WaitAsync(deadline.Token));
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
            Assert.Equal(1, acquisitions);
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
            output.WriteLine($"Security junction: directory={directory}; acquisition={failAcquisition}; nativeFile={before.LocalFileId}; unbound={observationId}; continued=True; outsideUnchanged=True.");
        }
        finally
        {
            // Delete the link itself, never recurse through it into the external target.
            if (junctionCreated) { Directory.Delete(parent); }
            if (Directory.Exists(staging)) { Directory.Delete(staging); }
            if (Directory.Exists(retired)) { Directory.Move(retired, parent); }
        }
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
        Assert.Equal(0, fixture.Provider.Fetches);
    }
}
