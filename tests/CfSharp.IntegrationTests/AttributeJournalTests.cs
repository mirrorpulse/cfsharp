using System.Security.Cryptography;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Theory]
    [InlineData(false, false, "none")]
    [InlineData(false, true, "none")]
    [InlineData(true, false, "none")]
    [InlineData(true, true, "none")]
    [InlineData(false, false, "before")]
    [InlineData(false, true, "before")]
    [InlineData(true, false, "before")]
    [InlineData(true, true, "before")]
    [InlineData(false, false, "between")]
    [InlineData(false, true, "between")]
    [InlineData(true, false, "between")]
    [InlineData(true, true, "between")]
    [InlineData(false, false, "after")]
    [InlineData(false, true, "after")]
    [InlineData(true, false, "after")]
    [InlineData(true, true, "after")]
    [InlineData(false, false, "overlap")]
    [InlineData(false, true, "overlap")]
    [InlineData(true, false, "overlap")]
    [InlineData(true, true, "overlap")]
    public async Task AttributeNotificationsPreserveMovesAndIndependentSameLengthWrites(bool placeholder, bool caseOnly, string writeAt)
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path),
            CloudInSyncPolicy.TrackAll | CloudInSyncPolicy.PreserveForSyncEngine);
        byte[] original = "original-resident-bytes"u8.ToArray();
        byte[] edited = original.Select(value => (byte)(value ^ 1)).ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, original);
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "attribute-original", "accepted");
        if (placeholder)
        {
            await fixture.File.ConvertToPlaceholderAsync(identity);
            await fixture.File.SetInSyncAsync(true);
        }
        else
        {
            await using ICloudStateTransaction seed = await factory!.Store!.BeginTransactionAsync();
            await seed.Items.UpsertAsync(new(identity.ItemId, identity.RemoteId, "content.bin", CloudItemKind.File,
                identity.RemoteRevision, null, false, DateTimeOffset.UtcNow));
            await seed.CommitAsync();
        }

        CloudItemSnapshot before = await fixture.File.InspectAsync();
        DateTime lastWrite = File.GetLastWriteTimeUtc(fixture.File.FullPath);
        string middleName = caseOnly ? "CONTENT.bin" : "middle.bin";
        string finalName = caseOnly ? "CoNtEnT.bin" : "moved.bin";
        string middle = Path.Combine(fixture.Root, middleName);
        string final = Path.Combine(fixture.Root, finalName);
        IReadOnlyList<CloudOperationJournalEntry> retained;
        await using (CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed())
        {
            await feed.StartAsync();
            if (writeAt == "before")
            {
                await WriteAccessTestContentAsync(fixture.File.FullPath, edited);
                await WaitForAccessRowsAsync(factory!, HasContent);
            }
            await using FileStream? writer = writeAt == "overlap"
                ? new(fixture.File.FullPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete) : null;
            if (writer is not null)
            {
                await writer.WriteAsync(edited);
                writer.Flush(flushToDisk: true);
            }
            File.Move(fixture.File.FullPath, middle);
            await WaitForAccessRowsAsync(factory!, rows => rows.Any(row => row.Kind == CloudStateOperationKind.Move));
            File.SetAttributes(middle, File.GetAttributes(middle) | FileAttributes.Hidden);
            await WaitForAccessRowsAsync(factory!, rows => rows.Any(row => row.Kind == CloudStateOperationKind.MetadataUpdate));
            if (writeAt == "between")
            {
                await WriteAccessTestContentAsync(middle, edited);
                await WaitForAccessRowsAsync(factory!, HasContent);
            }
            File.Move(middle, final);
            await WaitForAccessRowsAsync(factory!, rows => rows.Count(row => row.Kind == CloudStateOperationKind.Move) == 2);
            if (writer is not null)
            {
                await writer.DisposeAsync();
                await WaitForAccessRowsAsync(factory!, HasContent);
            }
            if (writeAt == "after")
            {
                await WriteAccessTestContentAsync(final, edited);
                await WaitForAccessRowsAsync(factory!, HasContent);
            }
            await feed.DisposeAsync();
            retained = await AccessRowsAsync(factory!);
        }

        CloudOperationJournalEntry[] moves = retained.Where(row => row.Kind == CloudStateOperationKind.Move).ToArray();
        Assert.Equal(2, moves.Length);
        Assert.All(moves, row => Assert.Equal(identity.ItemId, row.ItemId));
        Assert.Equal(("content.bin", middleName), MovePaths(moves[0]));
        Assert.Equal((middleName, finalName), MovePaths(moves[1]));
        Assert.True(moves[0].Sequence < moves[1].Sequence);
        CloudOperationJournalEntry[] writes = retained.Where(row => row.Kind == CloudStateOperationKind.ContentUpdate).ToArray();
        if (writeAt == "none")
        {
            Assert.Empty(writes);
            Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(final));
        }
        else
        {
            Assert.NotEmpty(writes);
            Assert.All(writes, row => Assert.Equal(identity.ItemId, row.ItemId));
            Assert.Contains(writes, row => writeAt switch
            {
                "before" => row.Sequence < moves[0].Sequence,
                "between" => row.Sequence > moves[0].Sequence && row.Sequence < moves[1].Sequence,
                "overlap" => true,
                _ => row.Sequence > moves[1].Sequence,
            });
        }
        Assert.Equal(writeAt == "none" ? original : edited, await File.ReadAllBytesAsync(final));
        CloudItemSnapshot after = await fixture.System.GetFile(finalName).InspectAsync();
        Assert.Equal(before.LocalBinding, after.LocalBinding);
        if (placeholder)
        {
            Assert.Equal(before.PlaceholderIdentity.ToArray(), after.PlaceholderIdentity.ToArray());
            Assert.Equal("accepted", after.RemoteRevision);
            Assert.Equal(CloudContentAvailability.FullyAvailable, after.ContentAvailability);
        }
        await fixture.RestartAsync();
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed();
        await reopened.StartAsync();
        CloudLocalChangeScan scan = await reopened.BeginScanAsync();
        if (scan.RequiresFullRescan)
        {
            await reopened.AcknowledgeFullRescanAsync();
            scan = await reopened.BeginScanAsync();
        }
        CloudLocalChangePage page = await reopened.ReadPageAsync(scan, 0, 1000);
        Assert.Equal(retained.Select(row => (row.OperationId, row.Sequence, (int)row.Kind)),
            page.Changes.Select(row => (row.OperationId, row.Sequence, (int)row.Kind)));
        await reopened.AcknowledgeAsync(retained.Select(row => row.OperationId));
        Assert.Empty(await AccessRowsAsync(factory!));
        Assert.Equal(0, fixture.Provider.Fetches);
        output.WriteLine($"placeholder={placeholder}; caseOnly={caseOnly}; writeAt={writeAt}; original={before.LocalBinding}; bytes={Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(final)))}; rows={retained.Count}.");

        static bool HasContent(IReadOnlyList<CloudOperationJournalEntry> rows) => rows.Any(row => row.Kind == CloudStateOperationKind.ContentUpdate);
        static (string?, string) MovePaths(CloudOperationJournalEntry row)
        {
            LocalChangePayload change = LocalChangePayload.Decode(row.Payload);
            return (change.PreviousRelativePath, change.RelativePath);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttributesBindDiscoveredOrdinaryObjectsWithoutInventingContent(bool directory)
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        string path = Path.Combine(fixture.Root, "ordinary");
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed();
        await feed.StartAsync();
        if (directory) { Directory.CreateDirectory(path); }
        else { await File.WriteAllTextAsync(path, "unchanged bytes"); }
        await WaitForAccessRowsAsync(factory!, rows => rows.Any(row => row.Kind == CloudStateOperationKind.Create));
        await feed.DisposeAsync();
        IReadOnlyList<CloudOperationJournalEntry> originals = await AccessRowsAsync(factory!);
        Guid itemId = originals.First(row => row.Kind == CloudStateOperationKind.Create).ItemId!.Value;
        await using (ICloudStateTransaction read = await factory!.Store!.BeginTransactionAsync())
        {
            Assert.NotNull((await read.Items.GetByItemIdAsync(itemId))!.LocalFileId);
        }
        await fixture.RestartAsync();
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed();
        await reopened.StartAsync();
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        await WaitForAccessRowsAsync(factory!, rows => rows.Any(row => row.Kind == CloudStateOperationKind.MetadataUpdate));
        await reopened.DisposeAsync();
        IReadOnlyList<CloudOperationJournalEntry> retained = await AccessRowsAsync(factory!);
        Assert.Equal(originals.Select(row => row.OperationId), retained.Take(originals.Count).Select(row => row.OperationId));
        Assert.All(retained.Skip(originals.Count), row =>
        {
            Assert.Equal(CloudStateOperationKind.MetadataUpdate, row.Kind);
            Assert.Equal(itemId, row.ItemId);
        });
        await fixture.RestartAsync();
        await using CloudLocalChangeFeed reader = fixture.System.CreateLocalChangeFeed();
        await reader.StartAsync();
        CloudLocalChangeScan scan = await reader.BeginScanAsync();
        Assert.False(scan.RequiresFullRescan);
        CloudLocalChangePage page = await reader.ReadPageAsync(scan, 0, 100);
        Assert.Equal(retained.Select(row => row.OperationId), page.Changes.Select(row => row.OperationId));
        await reader.AcknowledgeAsync(page.Changes.Select(row => row.OperationId));
        Assert.Equal(0, fixture.Provider.Fetches);
    }
}
