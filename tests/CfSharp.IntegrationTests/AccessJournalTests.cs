using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using CfSharp.Storage.Sqlite;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData("before", false)]
    [InlineData("before", true)]
    [InlineData("after", false)]
    [InlineData("after", true)]
    [InlineData("cancel", false)]
    [InlineData("cancel", true)]
    [InlineData("invalid", false)]
    [InlineData("invalid", true)]
    [InlineData("race", false)]
    [InlineData("race", true)]
    [InlineData("truncate", false)]
    [InlineData("truncate", true)]
    [InlineData("replace", false)]
    [InlineData("replace", true)]
    [InlineData("directory", false)]
    [InlineData("directory", true)]
    [InlineData("external", false)]
    [InlineData("external", true)]
    [InlineData("prepare", false)]
    [InlineData("prepare", true)]
    [InlineData("namespace", false)]
    [InlineData("namespace", true)]
    [InlineData("attribute", false)]
    [InlineData("attribute", true)]
    public async Task AccessNotificationsDoNotHideRealWorkAcrossFailureAndRestart(string scenario, bool restartBeforeObservation)
    {
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path));
        byte[] content = "resident-original"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "access-matrix", scenario == "prepare" ? null : "v1");
        await fixture.File.ConvertToPlaceholderAsync(identity);
        string directoryPath = Path.Combine(fixture.Root, "directory");
        Directory.CreateDirectory(directoryPath);
        if (scenario == "directory")
        {
            await fixture.System.GetDirectory("directory").ConvertToPlaceholderAsync(new(Guid.NewGuid(), "access-directory", "v1"));
        }
        if (restartBeforeObservation) { await fixture.RestartAsync(); }
        CloudItem item = scenario == "directory" ? fixture.System.GetDirectory("directory") : fixture.File;
        CloudItemSnapshot before = await item.InspectAsync();
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed();
        await feed.StartAsync();
        await WriteAccessTestContentAsync(fixture.File.FullPath, content);
        await WaitForAccessRowsAsync(factory!, rows => rows.Any(row => row.Kind == CloudStateOperationKind.ContentUpdate));
        await Task.Delay(200);
        IReadOnlyList<CloudOperationJournalEntry> original = await AccessRowsAsync(factory!);
        CloudProtectedLocalOperationRequest request = scenario == "prepare"
            ? CloudProtectedLocalOperationRequest.ForLocalConversion(before.LocalBinding!, identity)
            : new(before.LocalBinding!, scenario == "directory" ? CloudProtectedLocalOperationMode.DirectoryMetadata : CloudProtectedLocalOperationMode.ExclusiveFile);
        bool applied = scenario is not ("before" or "cancel" or "invalid");
        IOException callbackFailure = new("Injected application policy failure.");
        CloudProtectedLocalOperationResult? result = null;
        if (scenario == "external")
        {
            FileSecurity access = new FileInfo(item.FullPath).GetAccessControl(AccessControlSections.Access);
            access.AddAccessRule(AccessTestRule());
            new FileInfo(item.FullPath).SetAccessControl(access);
        }
        else
        {
            result = await item.RunProtectedLocalOperationAsync(request, async (scope, stop) =>
            {
                if (scenario == "before") { throw callbackFailure; }
                FileSystemSecurity access = await scope.ReadAccessDescriptorAsync(stop);
                access.AddAccessRule(AccessTestRule());
                if (scenario == "cancel")
                {
                    using CancellationTokenSource canceled = new();
                    canceled.Cancel();
                    await scope.ApplyAccessDescriptorAsync(access, canceled.Token);
                }
                else if (scenario == "invalid")
                {
                    await scope.ApplyAccessDescriptorAsync(new DirectorySecurity(), stop);
                }
                else
                {
                    FileSystemSecurity readBack = await scope.ApplyAccessDescriptorAsync(access, stop);
                    AssertSameAccessDacl(access.GetSecurityDescriptorBinaryForm(), readBack.GetSecurityDescriptorBinaryForm());
                }
                if (scenario == "after") { throw callbackFailure; }
                if (scenario == "namespace")
                {
                    await File.WriteAllBytesAsync(Path.Combine(directoryPath, "callback-child.bin"), content, stop);
                }
                if (scenario is "race" or "truncate")
                {
                    // A native competitor overlaps the protected scope. Its rejected write
                    // is followed by a successful same-length edit/truncation immediately
                    // after release, with security observations still in flight.
                    Task contender = Task.Run(() => Assert.Throws<IOException>(() =>
                        File.WriteAllBytes(fixture.File.FullPath, content)));
                    await contender;
                }
            });
            Assert.Equal(applied, result.AccessDescriptorApplied);
            Assert.Equal(applied, result.AccessDescriptorReadBack);
            if (scenario is "before" or "after") { Assert.Same(callbackFailure, result.Error); }
            if (applied && scenario != "after") { Assert.Equal(CloudProtectedLocalOperationOutcome.Completed, result.Outcome); }
            if (scenario == "invalid") { Assert.IsType<ArgumentException>(result.Error); }
            if (scenario == "cancel") { Assert.IsAssignableFrom<OperationCanceledException>(result.Error); }
        }
        if (applied)
        {
            // Name/data and security subscriptions deliver independently. A callback can
            // create another item before the DACL observation reaches the journal.
            await WaitForAccessRowsAsync(factory!, rows => rows.Any(row => row.Kind == CloudStateOperationKind.MetadataUpdate &&
                row.ItemId == before.ItemId && original.All(old => old.OperationId != row.OperationId)));
            IReadOnlyList<CloudOperationJournalEntry> observed = await AccessRowsAsync(factory!);
            AssertOriginalAccessRows(original, observed);
            CloudOperationJournalEntry[] accessOnly = observed.Where(row => original.All(old => old.OperationId != row.OperationId) &&
                row.ItemId == before.ItemId).ToArray();
            Assert.NotEmpty(accessOnly);
            Assert.All(accessOnly, row => Assert.Equal(CloudStateOperationKind.MetadataUpdate, row.Kind));
            if (scenario == "namespace")
            {
                await WaitForAccessRowsAsync(factory!, rows => rows.Any(row => row.Kind == CloudStateOperationKind.Create));
            }
        }
        if (scenario == "replace")
        {
            await fixture.File.DeleteAsync();
            await File.WriteAllBytesAsync(fixture.File.FullPath, "replacement-bytes"u8.ToArray());
            CloudProtectedLocalOperationResult stale = await fixture.File.RunProtectedLocalOperationAsync(request,
                (_, _) => throw new IOException("A replacement must not inherit the original scope."));
            Assert.False(stale.CallbackStarted);
            Assert.Equal(CloudProtectedLocalOperationOutcome.LocalObjectMismatch, stale.Outcome);
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            while (!(await feed.BeginScanAsync()).RequiresFullRescan) { await Task.Delay(20, deadline.Token); }
        }
        else
        {
            if (scenario == "attribute")
            {
                int count = (await AccessRowsAsync(factory!)).Count;
                File.SetAttributes(fixture.File.FullPath, File.GetAttributes(fixture.File.FullPath) | FileAttributes.Hidden);
                await WaitForAccessRowsAsync(factory!, rows => rows.Count > count && rows.Any(row =>
                    row.Kind == CloudStateOperationKind.ContentUpdate && original.All(old => old.OperationId != row.OperationId)));
            }
            byte[] edit = scenario == "truncate" ? "short"u8.ToArray() : content.Select(value => (byte)(value ^ 1)).ToArray();
            await WriteAccessTestContentAsync(fixture.File.FullPath, edit);
            await WaitForAccessRowsAsync(factory!, rows => rows.Any(row => row.Kind == CloudStateOperationKind.ContentUpdate && original.All(old => old.OperationId != row.OperationId)));
            Assert.Equal(edit, await File.ReadAllBytesAsync(fixture.File.FullPath));
        }
        await Task.Delay(200);
        await feed.DisposeAsync();
        IReadOnlyList<CloudOperationJournalEntry> finalRows = await AccessRowsAsync(factory!);
        AssertOriginalAccessRows(original, finalRows);
        await fixture.RestartAsync();
        await using CloudLocalChangeFeed next = fixture.System.CreateLocalChangeFeed();
        await next.StartAsync();
        await AssertAccessPublicRowsAsync(factory!, next, requiresRescan: scenario == "replace");
        AssertOriginalAccessRows(finalRows, await AccessRowsAsync(factory!));
        Assert.Equal(0, fixture.Provider.Fetches);
    }

    private static FileSystemAccessRule AccessTestRule() => new(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
        FileSystemRights.ReadPermissions, AccessControlType.Allow);

    [Trait("Category", "AccessJournal")]
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AccessOnlyResidentWriteRetainsOriginalJournalAndGenuineLaterEdit(bool pendingEdits, bool preserveInSync)
    {
        using WindowsIdentity user = WindowsIdentity.GetCurrent();
        string architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        if (Environment.GetEnvironmentVariable("CFSHARP_EXPECTED_TEST_ARCHITECTURE") is string expected)
        {
            Assert.Equal(expected, architecture, ignoreCase: true);
        }
        if (Environment.GetEnvironmentVariable("CFSHARP_EXPECTED_TEST_SID") is string sid)
        {
            Assert.Equal(sid, user.User?.Value);
            Assert.False(new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator));
        }
        output.WriteLine($"Native producer: OS={Environment.OSVersion}; architecture={architecture}; SID={user.User?.Value}.");
        foreach (System.Reflection.Assembly assembly in new[] { typeof(CloudFileSystem).Assembly, typeof(SqliteCloudStateStoreFactory).Assembly })
        {
            using FileStream stream = File.OpenRead(assembly.Location);
            output.WriteLine($"Assembly={assembly.GetName().Name}; version={assembly.GetCustomAttributesData().Single(attribute => attribute.AttributeType == typeof(System.Reflection.AssemblyInformationalVersionAttribute)).ConstructorArguments[0].Value}; SHA256={Convert.ToHexString(SHA256.HashData(stream))}.");
        }
        AccessJournalFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new(path),
            preserveInSync ? CloudInSyncPolicy.TrackAll | CloudInSyncPolicy.PreserveForSyncEngine : CloudInSyncPolicy.None);
        byte[] content = "resident-original"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "access-journal", "v1");
        await fixture.File.ConvertToPlaceholderAsync(identity);
        await fixture.File.SetInSyncAsync(true);
        CloudItemSnapshot before = await fixture.File.InspectAsync();
        Assert.Equal(CloudContentAvailability.FullyAvailable, before.ContentAvailability);
        await using CloudLocalChangeFeed feed = fixture.System.CreateLocalChangeFeed();
        await feed.StartAsync();
        if (pendingEdits)
        {
            await WriteAccessTestContentAsync(fixture.File.FullPath, "resident-edited-1"u8.ToArray());
            await WaitForAccessRowsAsync(factory!, rows => rows.Any(row => row.Kind == CloudStateOperationKind.ContentUpdate));
            int count = (await AccessRowsAsync(factory!)).Count;
            await WriteAccessTestContentAsync(fixture.File.FullPath, "resident-edited-2"u8.ToArray());
            await WaitForAccessRowsAsync(factory!, rows => rows.Count > count);
        }
        await Task.Delay(200);
        IReadOnlyList<CloudOperationJournalEntry> original = await AccessRowsAsync(factory!);
        if (pendingEdits) { Assert.True(original.Count >= 2); }
        byte[] bytes = await File.ReadAllBytesAsync(fixture.File.FullPath);
        DateTime lastWrite = File.GetLastWriteTimeUtc(fixture.File.FullPath);
        CloudProtectedLocalOperationResult result = await fixture.File.RunProtectedLocalOperationAsync(
            new(before.LocalBinding!, CloudProtectedLocalOperationMode.ExclusiveFile), async (scope, stop) =>
            {
                FileSystemSecurity access = await scope.ReadAccessDescriptorAsync(stop);
                byte[] old = access.GetSecurityDescriptorBinaryForm();
                access.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    FileSystemRights.ReadPermissions, AccessControlType.Allow));
                Assert.NotEqual(old, access.GetSecurityDescriptorBinaryForm());
                FileSystemSecurity applied = await scope.ApplyAccessDescriptorAsync(access, stop);
                AssertSameAccessDacl(access.GetSecurityDescriptorBinaryForm(), applied.GetSecurityDescriptorBinaryForm());
                AssertSameAccessDacl(access.GetSecurityDescriptorBinaryForm(), (await scope.ReadAccessDescriptorAsync(stop)).GetSecurityDescriptorBinaryForm());
            });
        Assert.Equal(CloudProtectedLocalOperationOutcome.Completed, result.Outcome);
        Assert.True(result.AccessDescriptorApplied);
        Assert.True(result.AccessDescriptorReadBack);
        await WaitForAccessRowsAsync(factory!, rows => rows.Count > original.Count);
        await Task.Delay(200);
        await feed.DisposeAsync();
        IReadOnlyList<CloudOperationJournalEntry> accessRows = await AccessRowsAsync(factory!);
        AssertOriginalAccessRows(original, accessRows);
        CloudOperationJournalEntry[] added = accessRows.Where(row => original.All(old => old.OperationId != row.OperationId)).ToArray();
        output.WriteLine($"Access-only: original={original.Count}; added={added.Length}; content={added.Count(row => row.Kind == CloudStateOperationKind.ContentUpdate)}; architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}.");
        Assert.NotEmpty(added);
        Assert.All(added, row => Assert.Equal(CloudStateOperationKind.MetadataUpdate, row.Kind));
        Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(await File.ReadAllBytesAsync(fixture.File.FullPath)));
        Assert.Equal(lastWrite, File.GetLastWriteTimeUtc(fixture.File.FullPath));
        CloudItemSnapshot after = await fixture.File.InspectAsync();
        Assert.Equal(before.LocalBinding, after.LocalBinding);
        Assert.Equal(before.PlaceholderIdentity.ToArray(), after.PlaceholderIdentity.ToArray());
        output.WriteLine($"Native receipt: file={before.LocalBinding!.LocalFileId}; identitySHA256={Convert.ToHexString(SHA256.HashData(before.PlaceholderIdentity.Span))}; bytesSHA256={Convert.ToHexString(SHA256.HashData(bytes))}; length={bytes.Length}; lastWrite={lastWrite:o}; preserveInSync={preserveInSync}.");
        foreach (CloudOperationJournalEntry row in accessRows)
        {
            output.WriteLine($"Retained operation: id={row.OperationId}; sequence={row.Sequence}; kind={row.Kind}; payloadSHA256={Convert.ToHexString(SHA256.HashData(row.Payload.Span))}.");
        }
        await fixture.RestartAsync();
        await using CloudLocalChangeFeed reopened = fixture.System.CreateLocalChangeFeed();
        await reopened.StartAsync();
        await AssertAccessPublicRowsAsync(factory!, reopened);
        AssertOriginalAccessRows(accessRows, await AccessRowsAsync(factory!));
        await reopened.AcknowledgeAsync(added.Select(row => row.OperationId));
        AssertOriginalAccessRows(original, await AccessRowsAsync(factory!));
        byte[] edit = bytes.Select(value => (byte)(value ^ 1)).ToArray();
        await WriteAccessTestContentAsync(fixture.File.FullPath, edit);
        await WaitForAccessRowsAsync(factory!, rows => rows.Any(row => row.Kind == CloudStateOperationKind.ContentUpdate && accessRows.All(old => old.OperationId != row.OperationId)));
        await reopened.DisposeAsync();
        await fixture.RestartAsync();
        await using CloudLocalChangeFeed final = fixture.System.CreateLocalChangeFeed();
        await final.StartAsync();
        await AssertAccessPublicRowsAsync(factory!, final);
        IReadOnlyList<CloudOperationJournalEntry> genuine = await AccessRowsAsync(factory!);
        AssertOriginalAccessRows(original, genuine);
        Assert.DoesNotContain(genuine, row => added.Any(old => old.OperationId == row.OperationId));
        foreach (CloudOperationJournalEntry row in genuine.Where(row => original.All(old => old.OperationId != row.OperationId)))
        {
            output.WriteLine($"Genuine edit: id={row.OperationId}; sequence={row.Sequence}; kind={row.Kind}; payloadSHA256={Convert.ToHexString(SHA256.HashData(row.Payload.Span))}.");
        }
        Assert.Equal(edit, await File.ReadAllBytesAsync(fixture.File.FullPath));
        Assert.Equal(0, fixture.Provider.Fetches);
    }

    private static async Task WriteAccessTestContentAsync(string path, byte[] content)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        await stream.WriteAsync(content);
        stream.SetLength(content.Length);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<IReadOnlyList<CloudOperationJournalEntry>> AccessRowsAsync(AccessJournalFactory factory)
    {
        await using ICloudStateTransaction transaction = await factory.Store!.BeginTransactionAsync();
        return await transaction.Operations.ListAsync(1000);
    }

    private static async Task WaitForAccessRowsAsync(AccessJournalFactory factory, Func<IReadOnlyList<CloudOperationJournalEntry>, bool> ready)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        while (!ready(await AccessRowsAsync(factory))) { await Task.Delay(20, deadline.Token); }
    }

    private static void AssertOriginalAccessRows(IReadOnlyList<CloudOperationJournalEntry> original, IReadOnlyList<CloudOperationJournalEntry> current)
    {
        foreach (CloudOperationJournalEntry before in original)
        {
            CloudOperationJournalEntry after = Assert.Single(current.Where(row => row.OperationId == before.OperationId));
            Assert.Equal((before.Sequence, before.Kind, before.ItemId), (after.Sequence, after.Kind, after.ItemId));
            Assert.Equal(SHA256.HashData(before.Payload.Span), SHA256.HashData(after.Payload.Span));
        }
    }

    private static async Task AssertAccessPublicRowsAsync(AccessJournalFactory factory, CloudLocalChangeFeed feed, bool requiresRescan = false)
    {
        CloudLocalChangeScan scan = await feed.BeginScanAsync();
        Assert.Equal(requiresRescan, scan.RequiresFullRescan);
        CloudLocalChangePage page = await feed.ReadPageAsync(scan, 0, 1000);
        if (requiresRescan) { Assert.True(page.RequiresFullRescan); return; }
        IReadOnlyList<CloudOperationJournalEntry> rows = await AccessRowsAsync(factory);
        Assert.Equal(rows.Select(row => (row.OperationId, row.Sequence, (int)row.Kind)),
            page.Changes.Select(row => (row.OperationId, row.Sequence, (int)row.Kind)));
    }

    private sealed class AccessJournalFactory(string path) : ICloudStateStoreFactory
    {
        internal ICloudStateStore? Store { get; private set; }
        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context, CancellationToken cancellationToken = default) =>
            Store = await new SqliteCloudStateStoreFactory(path).OpenAsync(context, cancellationToken);
    }
}
