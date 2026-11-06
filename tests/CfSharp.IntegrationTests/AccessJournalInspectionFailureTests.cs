using System.Security.AccessControl;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    public async Task CloudMetadataQueryFailureRetainsSecurityObservationAndProcessesLaterEvents(bool directory, int failingInspection)
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        string path = Path.Combine(fixture.Root, "before");
        if (directory) { Directory.CreateDirectory(path); }
        else { await File.WriteAllTextAsync(path, "resident-original"); }
        CloudItem item = directory ? fixture.System.GetDirectory("before") : fixture.System.GetFile("before");
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "inspection-failure", "v1");
        await item.ConvertToPlaceholderAsync(identity);
        CloudItemSnapshot before = await item.InspectAsync();
        Guid originalId = Guid.NewGuid();
        await using (ICloudStateTransaction seed = await factory!.Store!.BeginTransactionAsync())
        {
            await seed.Operations.EnqueueAsync(new(originalId, CloudStateOperationKind.MetadataUpdate, identity.ItemId,
                new LocalChangePayload("before", null, directory, DateTimeOffset.UtcNow).Encode(), DateTimeOffset.UtcNow));
            await seed.CommitAsync();
        }
        IReadOnlyList<CloudOperationJournalEntry> originals = await AccessRowsAsync(factory!);
        CloudFilesException failure = CloudFilesException.FromHResult("CloudItem.Inspect.PlaceholderInfo", path, unchecked((int)0x80070178));
        int inspections = 0;
        CloudSecurityInspection inspect = (handle, nativePath, kind, root) =>
        {
            // Inject the exact managed failure from CfGetPlaceholderInfo at the I/O boundary.
            // Other reads, the directory source, protection handles and store are real.
            if (++inspections == failingInspection) { throw failure; }
            return CloudItemInspector.Inspect(handle, nativePath, kind, root);
        };
        Guid observationId;
        long observationSequence;
        DelayedSecuritySource source = new(fixture.Root, line => output.WriteLine(line));
        await using (CloudLocalChangeFeed feed = CloudLocalChangeFeed.CreateForTesting(fixture.Root, factory!.Store!, new(), source, inspect))
        {
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
            // A failed worker can also leave a loss marker. A later real create must commit
            // through this same feed, proving that the inspection failure did not stop it.
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
            Assert.True((await feed.BeginScanAsync(deadline.Token)).RequiresFullRescan);
            await feed.DisposeAsync();
            Assert.Equal(failingInspection, inspections);
            await using ICloudStateTransaction verify = await factory.Store!.BeginTransactionAsync();
            IReadOnlyList<CloudOperationJournalEntry> rows = await verify.Operations.ListAsync(100);
            AssertOriginalAccessRows(originals, rows);
            CloudOperationJournalEntry observation = Assert.Single(rows.Where(row => row.Kind == CloudStateOperationKind.MetadataUpdate && row.OperationId != originalId));
            observationId = observation.OperationId;
            observationSequence = observation.Sequence;
            // A failure after commit retains its already committed identity/sequence. The
            // durable fence prevents dispatch; initial/pre-commit failures remain unbound.
            Assert.Equal(failingInspection == 3 ? identity.ItemId : (Guid?)null, observation.ItemId);
            Assert.Single(await verify.Checkpoints.ListAsync(CloudLocalChangeFeed.NamespaceObservationsPrefix));
            Assert.Equal(2, (await verify.Items.ListSubtreeAsync(string.Empty)).Count);
            Assert.Equal(identity.ItemId, (await verify.Items.GetByRelativePathAsync("before"))!.ItemId);
            output.WriteLine($"Cloud metadata failure: directory={directory}; inspection={failingInspection}; HRESULT=0x{failure.HResult:X8}; observation={observationId}; original={originalId}; continued=True.");
        }
        await fixture.RestartAsync();
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed();
        await reopened.StartAsync();
        CloudLocalChangeScan scan = await reopened.BeginScanAsync();
        Assert.True(scan.RequiresFullRescan);
        Assert.Empty((await reopened.ReadPageAsync(scan, 0, 100)).Changes);
        await reopened.AcknowledgeFullRescanAsync();
        CloudLocalChangePage page = await reopened.ReadPageAsync(await reopened.BeginScanAsync(), 0, 100);
        Assert.Equal(observationSequence, Assert.Single(page.Changes.Where(row => row.OperationId == observationId)).Sequence);
        await reopened.AcknowledgeAsync([observationId]);
        AssertOriginalAccessRows(originals, await AccessRowsAsync(factory!));
        Assert.Equal(0, fixture.Provider.Fetches);
    }
}
