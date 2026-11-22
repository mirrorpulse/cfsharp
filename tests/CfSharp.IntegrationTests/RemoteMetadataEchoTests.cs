namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(false, true, false, true, true)]
    [InlineData(true, true, false, true, true)]
    [InlineData(false, true, false, true, false)]
    [InlineData(true, true, false, true, false)]
    [InlineData(false, false, true, true, false)]
    [InlineData(false, true, true, true, false)]
    [InlineData(true, true, true, true, false)]
    [InlineData(false, true, true, false, false)]
    public async Task RemoteDirectoryMetadataRequiresExplicitAttributeProvenance(
        bool feedBeforeCreation, bool remoteUpdate, bool localUpdate, bool suppressEcho, bool coordinated)
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "metadata-echo-directory");
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed();
        if (feedBeforeCreation) { await feed.StartAsync(); }
        await fixture.System.Root.CreatePlaceholdersAsync([
            CloudDirectoryPlaceholderSpec.CreateBuilder("Documents", identity)
                .WithPopulationState(CloudDirectoryPopulationState.Partial).WithInSyncState(true).Build(),
        ]);
        if (!feedBeforeCreation) { await feed.StartAsync(); }
        string path = Path.Combine(fixture.Root, "Documents");
        CloudItemSnapshot original = await fixture.System.GetDirectory("Documents").InspectAsync();
        Assert.NotNull(original.LocalBinding);
        Assert.Empty(await AccessRowsAsync(factory!));

        if (remoteUpdate)
        {
            CloudRemoteChange change = new("metadata", CloudRemoteChangeKind.MetadataUpdate, identity.RemoteId,
                "remote-r1", CloudItemKind.Directory, "Documents", identity.ItemId,
                metadata: CloudPlaceholderMetadata.CreateDirectoryBuilder().Build(), cursorAfter: new byte[] { 1 });
            CloudRemoteApplyResult applied = await fixture.System.ApplyRemoteChangesAsync(
                new("metadata-echo", Array.Empty<byte>(), [change], new byte[] { 1 }),
                new()
                {
                    SuppressLocalEcho = suppressEcho,
                    AttributeEchoPolicy = coordinated
                    ? CloudRemoteAttributeEchoPolicy.CallerCoordinated : CloudRemoteAttributeEchoPolicy.RequireReconciliation
                });
            Assert.Equal(CloudRemoteBatchStatus.Applied, applied.Status);
            Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single(applied.Entries).Status);
            Assert.Equal(1, applied.AppliedEntryCount);
            Assert.Equal(new byte[] { 1 }, applied.SafeCursor.ToArray());
        }

        if (localUpdate)
        {
            Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        }

        // Match the independent SDK consumer's observation window, including notifications
        // delivered after ApplyRemoteChangesAsync returns. A finite scan alone is not a drain.
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        IReadOnlyList<CloudOperationJournalEntry> durable = await AccessRowsAsync(factory!);
        bool reconcile = remoteUpdate && suppressEcho && !coordinated;
        CloudLocalChangeScan scan = await feed.BeginScanAsync();
        Assert.Equal(reconcile, scan.RequiresFullRescan);
        if (reconcile)
        {
            CloudLocalChangePage fenced = await feed.ReadPageAsync(scan, 0, 1000);
            Assert.True(fenced.RequiresFullRescan);
            Assert.Empty(fenced.Changes);
            await feed.AcknowledgeFullRescanAsync();
            AssertOriginalAccessRows(durable, await AccessRowsAsync(factory!));
            scan = await feed.BeginScanAsync();
        }
        CloudLocalChangePage page = await feed.ReadPageAsync(scan, 0, 1000);
        Assert.False(page.RequiresFullRescan);
        Assert.False(page.HasMore);
        Assert.Equal(durable.Select(row => (row.OperationId, row.Sequence, row.ItemId)),
            page.Changes.Select(row => (row.OperationId, row.Sequence, row.ItemId)));
        Assert.All(durable, row =>
        {
            Assert.Equal(CloudStateOperationKind.MetadataUpdate, row.Kind);
            Assert.Equal(reconcile ? null : (Guid?)identity.ItemId, row.ItemId);
            Assert.Equal("Documents", LocalChangePayload.Decode(row.Payload).RelativePath);
        });
        Assert.All(page.Changes, row => Assert.Equal(CloudLocalChangeKind.MetadataUpdate, row.Kind));
        if (coordinated)
        {
            Assert.Empty(durable);
        }
        else if (reconcile)
        {
            Assert.Equal(localUpdate ? 3 : 2, durable.Count);
        }
        else if (!remoteUpdate)
        {
            Assert.Single(durable);
        }
        else
        {
            // The diagnostic must observe both kinds of native effect, rather than pass
            // because the watcher failed to start or the provider mutation was omitted.
            Assert.True(durable.Count > 1);
        }

        CloudItemSnapshot after = await fixture.System.GetDirectory("Documents").InspectAsync();
        Assert.Equal(original.LocalBinding, after.LocalBinding);
        Assert.Equal(localUpdate, File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        await using (ICloudStateTransaction transaction = await factory!.Store!.BeginTransactionAsync())
        {
            CloudItemState state = Assert.IsType<CloudItemState>(await transaction.Items.GetByItemIdAsync(identity.ItemId));
            Assert.Equal(identity.ItemId, state.ItemId);
            Assert.Equal(remoteUpdate ? "remote-r1" : null, state.RemoteRevision);
        }

        await feed.DisposeAsync();
        await fixture.RestartAsync();
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed();
        await reopened.StartAsync();
        AssertOriginalAccessRows(durable, await AccessRowsAsync(factory!));
        if (coordinated)
        {
            // A new owner cannot recover the old caller's promise about external writers.
            Assert.True((await reopened.BeginScanAsync()).RequiresFullRescan);
            await reopened.AcknowledgeFullRescanAsync();
        }
        await AssertAccessPublicRowsAsync(factory!, reopened);
        if (durable.Count != 0)
        {
            await reopened.AcknowledgeAsync(durable.Select(row => row.OperationId));
            Assert.Empty(await AccessRowsAsync(factory!));
        }
        Assert.Equal(0, fixture.Provider.Fetches);
    }
}
