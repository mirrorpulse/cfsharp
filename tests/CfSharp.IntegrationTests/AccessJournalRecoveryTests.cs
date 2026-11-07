using System.Security.AccessControl;

using CfSharp.Storage.Sqlite;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecurityObservationSurvivesRemoteCreationReplay(bool directory)
    {
        RemoteCreationRecoveryTests.FailingFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(new SqliteCloudStateStoreFactory(path)));
        CloudItemKind kind = directory ? CloudItemKind.Directory : CloudItemKind.File;
        CloudPlaceholderMetadata metadata = directory ? CloudPlaceholderMetadata.CreateDirectoryBuilder().Build()
            : CloudPlaceholderMetadata.CreateFileBuilder().Build();
        CloudRemoteChange change = new("before", directory ? CloudRemoteChangeKind.DirectoryUpsert : CloudRemoteChangeKind.FileUpsert,
            "remote-before", "r1", kind, "before", length: directory ? null : 0, metadata: metadata);
        CloudRemoteChangeBatch batch = new("pending-create", Array.Empty<byte>(), [change], new byte[] { 1 });
        factory!.FailCreationCommit = true;
        CloudRemoteApplyResult first = await fixture.System.ApplyRemoteChangesAsync(batch);
        Assert.True(first.RequiresRetry);
        Assert.Equal(CloudRemoteApplyEntryStatus.Failed, Assert.Single(first.Entries).Status);
        CloudItem item = directory ? fixture.System.GetDirectory("before") : fixture.System.GetFile("before");
        Assert.NotEmpty((await item.InspectAsync()).PlaceholderIdentity.ToArray());
        DelayedSecuritySource source = new(fixture.Root, line => output.WriteLine(line));
        Guid operationId;
        long sequence;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(25));
        await using (CloudLocalChangeFeed feed = new(fixture.Root, factory.Store!, new(), source,
            acquireOperation: async (scopes, stop) => await fixture.System.AcquireOperationAsync(scopes, stop)))
        {
            await feed.StartAsync(deadline.Token);
            if (directory)
            {
                DirectoryInfo info = new(item.FullPath);
                DirectorySecurity access = info.GetAccessControl();
                access.AddAccessRule(AccessTestRule());
                info.SetAccessControl(access);
            }
            else
            {
                FileInfo info = new(item.FullPath);
                FileSecurity access = info.GetAccessControl();
                access.AddAccessRule(AccessTestRule());
                info.SetAccessControl(access);
            }

            await source.DeliverAsync(await source.Security.Task.WaitAsync(deadline.Token));
            await feed.DisposeAsync();
            await using ICloudStateTransaction read = await factory.Store!.BeginTransactionAsync(deadline.Token);
            Assert.Single(await read.Checkpoints.ListAsync(RemoteCreationIntent.Prefix, deadline.Token));
            Assert.Single(await read.Checkpoints.ListAsync(RemoteCreationIntent.ObservationsPrefix, deadline.Token));
            CloudOperationJournalEntry pending = Assert.Single(await read.Operations.ListAsync(10, deadline.Token));
            Assert.Equal(CloudStateOperationKind.MetadataUpdate, pending.Kind);
            Assert.Null(pending.ItemId);
            operationId = pending.OperationId;
            sequence = pending.Sequence;
        }

        await fixture.RestartAsync();
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed();
        await reopened.StartAsync(deadline.Token);
        Assert.Empty((await reopened.ReadPageAsync(await reopened.BeginScanAsync(deadline.Token), 0, 10, deadline.Token)).Changes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.AcknowledgeFullRescanAsync(deadline.Token).AsTask());
        CloudRemoteApplyResult replay = await fixture.System.ApplyRemoteChangesAsync(batch,
            new CloudRemoteApplyOptions { SuppressLocalEcho = true }, deadline.Token);
        Assert.False(replay.RequiresRetry);
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, Assert.Single(replay.Entries).Status);
        await reopened.AcknowledgeFullRescanAsync(deadline.Token);
        CloudLocalChangePage page = await reopened.ReadPageAsync(await reopened.BeginScanAsync(deadline.Token), 0, 10, deadline.Token);
        CloudLocalChange security = Assert.Single(page.Changes.Where(entry => entry.OperationId == operationId));
        Assert.Equal(sequence, security.Sequence);
        Assert.Equal(CloudLocalChangeKind.MetadataUpdate, security.Kind);
        Assert.Equal("before", security.RelativePath);
        Assert.Equal(directory, security.IsDirectory);
        Assert.Null(security.ItemId);
        await reopened.AcknowledgeAsync([operationId], deadline.Token);
        await using ICloudStateTransaction verify = await factory.Store!.BeginTransactionAsync(deadline.Token);
        Assert.Null(await verify.Operations.GetAsync(operationId, deadline.Token));
    }
}
