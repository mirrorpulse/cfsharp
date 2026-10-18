using System.Runtime.Versioning;
using System.Security.Principal;
using CfSharp.Storage.Sqlite;
using Xunit.Abstractions;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed class RemoteDeleteRecoveryTests
{
    private readonly ITestOutputHelper _output;

    public RemoteDeleteRecoveryTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedReaderDefersDeletionUntilItClosesWithoutLosingOriginalRecovery(bool prepared)
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(false, hydrated: true);
        if (prepared)
        {
            root.Factory.Boundary = "prepare-after";
            root.Factory.FailuresRemaining = 1;
            Assert.True((await root.ApplyAsync()).RequiresRetry);
            await root.RestartAsync();
        }

        string path = System.IO.Path.Combine(root.Path, root.Target);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await root.FileSystem.GetFile(root.Target).InspectAsync()).LocalBinding);
        using (FileStream reader = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            CloudRemoteApplyResult blocked = await root.ApplyAsync();
            Assert.True(blocked.RequiresRetry);
            Assert.Equal(0, blocked.AppliedEntryCount);
            Assert.Empty(blocked.ConflictIds);
            Assert.NotNull(blocked.Entries[0].Error);
            Assert.True(File.Exists(path));
            Assert.Equal(binding, (await root.FileSystem.GetFile(root.Target).InspectAsync()).LocalBinding);
            await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
            Assert.False((await verify.Items.GetByItemIdAsync(root.ItemId))!.IsTombstone);
            IReadOnlyList<CloudStateCheckpoint> checkpoints = await verify.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix);
            Assert.Equal(prepared ? 1 : 0, checkpoints.Count);
            Assert.All(checkpoints, checkpoint => Assert.All(RemoteDeleteIntent.Decode(checkpoint.Value).Members,
                member => Assert.Null(member.DeletedAtTicks)));
        }

        await root.RestartAsync();
        CloudRemoteApplyResult replay = await root.ApplyAsync();
        Assert.False(replay.RequiresRetry, replay.Entries[0].Error?.ToString());
        Assert.Empty(replay.ConflictIds);
        Assert.False(File.Exists(path));
        await using ICloudStateTransaction completed = await root.Factory.Store!.BeginTransactionAsync();
        Assert.True((await completed.Items.GetByItemIdAsync(root.ItemId))!.IsTombstone);
        Assert.Empty(await completed.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix));
        Assert.Equal(0, root.Provider.Reads);
    }

    [Fact]
    public async Task CompletedReceiptCannotPublishProgressThroughAReplacedParent()
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(false);
        root.Factory.Boundary = "outcome";
        root.Factory.FailuresRemaining = 1;
        await Assert.ThrowsAsync<IOException>(root.ApplyAsync);
        await root.RestartAsync();
        Directory.Move(System.IO.Path.Combine(root.Path, "Docs"), System.IO.Path.Combine(root.Path, "Retained"));
        Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Docs"));
        CloudRemoteApplyResult replay = await root.ApplyAsync();
        Assert.True(replay.RequiresRetry);
        Assert.Equal(0, replay.AppliedEntryCount);
        Assert.Empty(replay.ConflictIds);
        Assert.IsType<CloudRemoteDeleteRecoveryException>(replay.Entries[0].Error);
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        Assert.True((await verify.Items.GetByItemIdAsync(root.ItemId))!.IsTombstone);
        Assert.Single(await verify.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix));
        Assert.Equal(0, root.Provider.Reads);
    }

    [Fact]
    public async Task ExplicitKeepRemoteResolutionRetainsItsOwnDeleteRecoveryAfterProjectionFailure()
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(false);
        await root.FileSystem.GetFile(root.Target).SetInSyncAsync(false);
        CloudRemoteApplyResult conflict = await root.ApplyAsync();
        Guid conflictId = Assert.Single(conflict.ConflictIds);
        root.Factory.Boundary = "projection";
        root.Factory.FailuresRemaining = 1;
        await Assert.ThrowsAsync<IOException>(() => root.FileSystem.ResolveRemoteConflictAsync(conflictId,
            new(CloudRemoteConflictDecision.KeepRemote)).AsTask());
        await root.RestartAsync();
        Assert.NotNull(await root.FileSystem.GetRemoteConflictAsync(conflictId));
        CloudRemoteApplyEntryResult resolved = await root.FileSystem.ResolveRemoteConflictAsync(conflictId,
            new(CloudRemoteConflictDecision.KeepRemote));
        Assert.Equal(CloudRemoteApplyEntryStatus.Applied, resolved.Status);
        Assert.Null(await root.FileSystem.GetRemoteConflictAsync(conflictId));
        Assert.False(File.Exists(System.IO.Path.Combine(root.Path, root.Target)));
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        Assert.True((await verify.Items.GetByItemIdAsync(root.ItemId))!.IsTombstone);
        Assert.Empty(await verify.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix));
        Assert.Equal(0, root.Provider.Reads);
    }

    [Fact]
    public async Task ExplicitKeepRemoteRetainsConflictAndReceiptWhenReplacementAppearsAtFinalTransaction()
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(false);
        await root.FileSystem.GetFile(root.Target).SetInSyncAsync(false);
        Guid conflictId = Assert.Single((await root.ApplyAsync()).ConflictIds);
        string path = System.IO.Path.Combine(root.Path, root.Target);
        bool replaced = false;
        root.Factory.BeforeDeleteOutcome = () =>
        {
            File.WriteAllText(path, "retain replacement bytes");
            replaced = true;
        };

        await Assert.ThrowsAsync<CloudRemoteDeleteRecoveryException>(() => root.FileSystem.ResolveRemoteConflictAsync(conflictId,
            new(CloudRemoteConflictDecision.KeepRemote)).AsTask());
        Assert.True(replaced);
        Assert.Equal("retain replacement bytes", await File.ReadAllTextAsync(path));
        Assert.NotNull(await root.FileSystem.GetRemoteConflictAsync(conflictId));
        await root.RestartAsync();
        await Assert.ThrowsAsync<CloudRemoteDeleteRecoveryException>(() => root.FileSystem.ResolveRemoteConflictAsync(conflictId,
            new(CloudRemoteConflictDecision.KeepRemote)).AsTask());
        Assert.NotNull(await root.FileSystem.GetRemoteConflictAsync(conflictId));
        Assert.Equal("retain replacement bytes", await File.ReadAllTextAsync(path));
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        CloudStateCheckpoint checkpoint = Assert.Single(await verify.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix));
        Assert.All(RemoteDeleteIntent.Decode(checkpoint.Value).Members, member => Assert.NotNull(member.ProjectedAtTicks));
        Assert.True((await verify.Items.GetByItemIdAsync(root.ItemId))!.IsTombstone);
        Assert.Equal(0, root.Provider.Reads);
    }

    [Fact]
    public async Task InventoryBudgetFailurePrecedesAnyNativeDeletionEvenUnderExplicitForce()
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(true);
        string directory = System.IO.Path.Combine(root.Path, root.Target);
        for (int child = 0; child < RemoteDeleteIntent.MaximumMembers; child++)
        {
            await File.WriteAllTextAsync(System.IO.Path.Combine(directory, $"child-{child}.txt"), "retain");
        }

        CloudRemoteApplyResult result = await root.FileSystem.ApplyRemoteChangesAsync(root.Batch,
            new() { PreserveUnsynchronizedLocalContent = false });
        Assert.True(result.RequiresRetry);
        Assert.Equal(0, result.AppliedEntryCount);
        Assert.Equal(RemoteDeleteIntent.MaximumMembers, Directory.EnumerateFiles(directory).Count());
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        Assert.Empty(await verify.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix));
        Assert.False((await verify.Items.GetByItemIdAsync(root.ItemId))!.IsTombstone);
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("pending")]
    [InlineData("replacement")]
    [InlineData("restored-timestamps")]
    [InlineData("parent")]
    [InlineData("policy")]
    public async Task ChangedPreparedEvidenceRemainsUnresolvedAndPreservesTheOriginalPrefix(string scenario)
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(false, hydrated: scenario == "restored-timestamps");
        root.Factory.Boundary = "prepare-after";
        root.Factory.FailuresRemaining = 1;
        Assert.True((await root.ApplyAsync()).RequiresRetry);
        await root.RestartAsync();
        string path = System.IO.Path.Combine(root.Path, root.Target);
        switch (scenario)
        {
            case "dirty":
                await root.FileSystem.GetFile(root.Target).SetInSyncAsync(false);
                break;
            case "restored-timestamps":
                DateTime originalTime = File.GetLastWriteTimeUtc(path);
                CloudItemSnapshot originalObject = await root.FileSystem.GetFile(root.Target).InspectAsync();
                await using (FileStream writer = new(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    byte[] changedBytes = new byte[512];
                    changedBytes[0] = 88;
                    await writer.WriteAsync(changedBytes);
                }
                File.SetLastWriteTimeUtc(path, originalTime);
                await root.FileSystem.GetFile(root.Target).SetInSyncAsync(true);
                CloudItemSnapshot changedObject = await root.FileSystem.GetFile(root.Target).InspectAsync();
                Assert.Equal(originalObject.LocalBinding, changedObject.LocalBinding);
                Assert.Equal(originalObject.PlaceholderIdentity.ToArray(), changedObject.PlaceholderIdentity.ToArray());
                Assert.Equal(CloudSynchronizationState.InSync, changedObject.SynchronizationState);
                break;
            case "pending":
                await using (ICloudStateTransaction seed = await root.Factory.Store!.BeginTransactionAsync())
                {
                    await seed.Operations.EnqueueAsync(new(Guid.NewGuid(), CloudStateOperationKind.ContentUpdate, root.ItemId, [5], DateTimeOffset.UtcNow));
                    await seed.CommitAsync();
                }
                break;
            case "replacement":
                File.Move(path, path + ".original");
                await File.WriteAllTextAsync(path, "retain replacement");
                break;
            case "parent":
                Directory.Move(System.IO.Path.Combine(root.Path, "Docs"), System.IO.Path.Combine(root.Path, "Retained"));
                Directory.CreateDirectory(System.IO.Path.Combine(root.Path, "Docs"));
                await File.WriteAllTextAsync(path, "retain new parent");
                break;
            case "policy":
                await Assert.ThrowsAsync<InvalidDataException>(() => root.FileSystem.ApplyRemoteChangesAsync(root.Batch,
                    new() { PreserveUnsynchronizedLocalContent = false }).AsTask());
                break;
        }

        if (scenario != "policy")
        {
            CloudRemoteApplyResult replay = await root.ApplyAsync();
            Assert.True(replay.RequiresRetry);
            Assert.Equal(0, replay.AppliedEntryCount);
            Assert.Equal(CloudRemoteApplyEntryStatus.Failed, replay.Entries[0].Status);
            Assert.Empty(replay.ConflictIds);
        }

        Assert.True(File.Exists(path));
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        Assert.Single(await verify.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix));
        Assert.Equal(0, (await verify.RemoteBatches.GetAsync(root.Batch.BatchId))!.AppliedEntryCount);
        Assert.Equal(scenario == "pending" ? 1 : 0, (await verify.Operations.ListAsync(100)).Count);
        Assert.Equal(0, root.Provider.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnotherOperationsTombstoneNeverCertifiesTheOriginalDelete(bool directory)
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(directory);
        if (directory) { await root.FileSystem.GetDirectory(root.Target).DeleteAsync(); }
        else { await root.FileSystem.GetFile(root.Target).DeleteAsync(); }
        CloudRemoteApplyResult replay = await root.ApplyAsync();
        Assert.True(replay.RequiresRetry);
        Assert.IsType<CloudRemoteDeleteRecoveryException>(replay.Entries[0].Error);
        Assert.Equal(0, replay.AppliedEntryCount);
        Assert.Equal(new byte[] { 1 }, replay.SafeCursor.ToArray());
        Assert.Empty(replay.ConflictIds);
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        Assert.Empty(await verify.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix));
        Assert.Equal("v1", (await verify.Items.GetByItemIdAsync(root.ItemId))!.RemoteRevision);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("wrong-item")]
    public async Task GenuineInitialConflictRemainsDurableAndPubliclyDecoded(string scenario)
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(false);
        CloudRemoteChange original = root.Batch.Changes[0];
        CloudRemoteChange changed = new(original.ChangeId, original.Kind, original.RemoteId, original.RemoteRevision,
            original.ItemKind, original.RelativePath, scenario == "wrong-item" ? Guid.NewGuid() : original.ItemId,
            previousRemoteRevision: scenario == "stale" ? "v0" : "v1");
        root.UseBatch(new(root.Batch.BatchId, root.Batch.InitialCursor, [changed], root.Batch.FinalCursor));
        CloudRemoteApplyResult result = await root.ApplyAsync();
        Assert.False(result.RequiresRetry);
        Assert.Equal(CloudRemoteApplyEntryStatus.Conflict, result.Entries[0].Status);
        CloudRemoteConflictReason reason = scenario == "stale" ? CloudRemoteConflictReason.StaleRemoteRevision : CloudRemoteConflictReason.MissingItem;
        Assert.Equal(reason, result.Entries[0].Conflict!.Reason);
        Assert.True(File.Exists(System.IO.Path.Combine(root.Path, root.Target)));
        await root.RestartAsync();
        CloudRemoteConflictRecord record = (await root.FileSystem.GetRemoteConflictAsync(result.ConflictIds[0]))!;
        Assert.NotNull(record);
        Assert.Equal(reason, record.Conflict.Reason);
        Assert.Equal(changed.ChangeId, record.Conflict.Change.ChangeId);
    }

    [Theory]
    [InlineData("prepare-after")]
    [InlineData("native-after")]
    [InlineData("projection-after")]
    public async Task CancellationAfterCommitRetainsOriginalRecoveryEvidence(string boundary)
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(false);
        using CancellationTokenSource cancellation = new();
        root.Factory.Boundary = boundary;
        root.Factory.FailuresRemaining = 1;
        root.Factory.Cancel = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => root.FileSystem.ApplyRemoteChangesAsync(root.Batch,
            cancellationToken: cancellation.Token).AsTask());
        await root.RestartAsync();
        CloudRemoteApplyResult replay = await root.ApplyAsync();
        Assert.False(replay.RequiresRetry, replay.Entries[0].Error?.ToString());
        Assert.Empty(replay.ConflictIds);
        Assert.Equal(0, root.Provider.Reads);
    }

    [Fact]
    public async Task ChangedFingerprintCannotConsumeOriginalPreparation()
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(false);
        root.Factory.Boundary = "prepare-after";
        root.Factory.FailuresRemaining = 1;
        await root.ApplyAsync();
        await root.RestartAsync();
        CloudRemoteChangeBatch changed = new(root.Batch.BatchId, root.Batch.InitialCursor, root.Batch.Changes, new byte[] { 10 });
        await Assert.ThrowsAsync<ArgumentException>(() => root.FileSystem.ApplyRemoteChangesAsync(changed).AsTask());
        Assert.True(File.Exists(System.IO.Path.Combine(root.Path, root.Target)));
        Assert.False((await root.ApplyAsync()).RequiresRetry);
    }

    [Fact]
    public async Task MultiEntryFailureRetainsItsSafePrefixAndStopsTheSuffix()
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(false);
        CloudRemoteChange first = new("first", CloudRemoteChangeKind.FileUpsert, "first", "v1", CloudItemKind.File,
            "first.bin", length: 512, metadata: CloudPlaceholderMetadata.CreateFileBuilder().Build(), cursorAfter: new byte[] { 2 });
        CloudRemoteChange last = new("last", CloudRemoteChangeKind.FileUpsert, "last", "v1", CloudItemKind.File,
            "last.bin", length: 512, metadata: CloudPlaceholderMetadata.CreateFileBuilder().Build());
        root.UseBatch(new(root.Batch.BatchId, root.Batch.InitialCursor, [first, root.Batch.Changes[0], last], root.Batch.FinalCursor));
        root.Factory.Boundary = "outcome";
        root.Factory.OutcomeCount = 2;
        root.Factory.FailuresRemaining = 2;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await Assert.ThrowsAsync<IOException>(root.ApplyAsync);
            await root.RestartAsync();
            Assert.False(File.Exists(System.IO.Path.Combine(root.Path, "last.bin")));
            await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
            CloudRemoteBatchState state = (await verify.RemoteBatches.GetAsync(root.Batch.BatchId))!;
            Assert.Equal(1, state.AppliedEntryCount);
            Assert.Equal(new byte[] { 2 }, state.Cursor.ToArray());
            Assert.Equal("first", state.LastAppliedChangeId);
        }

        CloudRemoteApplyResult result = await root.ApplyAsync();
        Assert.False(result.RequiresRetry, result.Entries[1].Error?.ToString());
        Assert.Empty(result.ConflictIds);
        Assert.Equal(3, result.AppliedEntryCount);
        Assert.True(File.Exists(System.IO.Path.Combine(root.Path, "last.bin")));
        Assert.Equal(0, root.Provider.Reads);
    }

    [Fact]
    public async Task PendingDeleteFencesScansAndRetainsReplacementObservationsAfterEchoExpiry()
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(false);
        root.Factory.Boundary = "outcome";
        root.Factory.FailuresRemaining = 1;
        await Assert.ThrowsAsync<IOException>(root.ApplyAsync);
        await root.RestartAsync();
        CloudLocalChangeFeed feed = root.FileSystem.CreateLocalChangeFeed();
        await feed.StartAsync();
        Assert.True((await feed.BeginScanAsync()).RequiresFullRescan);
        await Assert.ThrowsAsync<InvalidOperationException>(() => feed.AcknowledgeFullRescanAsync().AsTask());
        await using (ICloudStateTransaction expire = await root.Factory.Store!.BeginTransactionAsync())
        {
            foreach (CloudEchoSuppressionState suppression in await expire.EchoSuppressions.ListActiveAsync(DateTimeOffset.UtcNow))
            {
                await expire.EchoSuppressions.RemoveAsync(suppression.SuppressionId);
            }

            await expire.CommitAsync();
        }

        await File.WriteAllTextAsync(System.IO.Path.Combine(root.Path, root.Target), "retain later local edit");
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(5));
        Assert.True((await feed.ReadBatchAsync(deadline.Token)).RequiresFullRescan);
        bool retainedObservation = false;
        while (!retainedObservation)
        {
            await using (ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync(deadline.Token))
            {
                retainedObservation = (await verify.Checkpoints.ListAsync(CloudLocalChangeFeed.NamespaceObservationsPrefix, deadline.Token)).Count != 0;
            }
            if (!retainedObservation)
            {
                await Task.Delay(25, deadline.Token);
            }
        }
        CloudRemoteApplyResult replay = await root.ApplyAsync();
        Assert.True(replay.RequiresRetry);
        Assert.IsType<CloudRemoteDeleteRecoveryException>(replay.Entries[0].Error);
        Assert.Equal("retain later local edit", await File.ReadAllTextAsync(System.IO.Path.Combine(root.Path, root.Target)));
        File.Move(System.IO.Path.Combine(root.Path, root.Target), System.IO.Path.Combine(root.Path, "retained.bin"));
        Assert.False((await root.ApplyAsync()).RequiresRetry);
        Assert.True((await feed.BeginScanAsync()).RequiresFullRescan);
    }

    [Theory]
    [InlineData(false, "prepare-before")]
    [InlineData(true, "prepare-before")]
    [InlineData(false, "prepare-after")]
    [InlineData(true, "prepare-after")]
    [InlineData(false, "native-before")]
    [InlineData(true, "native-before")]
    [InlineData(false, "native-after")]
    [InlineData(true, "native-after")]
    [InlineData(false, "projection-before")]
    [InlineData(true, "projection-before")]
    [InlineData(false, "projection-after")]
    [InlineData(true, "projection-after")]
    [InlineData(false, "outcome-before")]
    [InlineData(true, "outcome-before")]
    [InlineData(false, "outcome-after")]
    [InlineData(true, "outcome-after")]
    public async Task RealProcessKillPreservesOnlyProvenNativeAndDurableCompletion(bool directory, string boundary)
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(directory);
        await root.FileSystem.DisposeAsync();
        System.Diagnostics.ProcessStartInfo start = new("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        string configuration = typeof(RemoteDeleteRecoveryTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .Cast<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration;
        string harness = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory,
            "../../../../CfSharp.Storage.Sqlite.CrashHarness/bin", configuration, "net10.0-windows/CfSharp.Storage.Sqlite.CrashHarness.dll"));
        Assert.True(File.Exists(harness));
        foreach (string argument in new[] { harness, root.Database, root.Path, directory ? "directory" : "file", root.ItemId.ToString(), "delete-" + boundary })
        {
            start.ArgumentList.Add(argument);
        }

        using System.Diagnostics.Process child = System.Diagnostics.Process.Start(start)!;
        try
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (!File.Exists(root.Database + ".delete-ready") && !child.HasExited && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.True(File.Exists(root.Database + ".delete-ready"), "The child did not reach the requested native/store cut point.");
            Assert.False(child.HasExited);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }

            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        await root.RestartAsync();
        CloudRemoteApplyResult replay = await root.ApplyAsync();
        Assert.Empty(replay.ConflictIds);
        Assert.Equal(boundary == "native-before", replay.RequiresRetry);
        if (boundary == "native-before")
        {
            Assert.IsType<CloudRemoteDeleteRecoveryException>(replay.Entries[0].Error);
            Assert.Equal(0, replay.AppliedEntryCount);
        }
        else
        {
            Assert.Equal(1, replay.AppliedEntryCount);
            Assert.Equal(new byte[] { 9 }, replay.SafeCursor.ToArray());
        }

        Assert.Equal(0, root.Provider.Reads);
    }

    [Theory]
    [InlineData(false, "outcome")]
    [InlineData(true, "outcome")]
    [InlineData(false, "projection")]
    [InlineData(true, "projection")]
    [InlineData(false, "prepare")]
    [InlineData(true, "prepare")]
    [InlineData(false, "prepare-after")]
    [InlineData(true, "prepare-after")]
    [InlineData(false, "native-after")]
    [InlineData(true, "native-after")]
    [InlineData(false, "projection-after")]
    [InlineData(true, "projection-after")]
    [InlineData(false, "outcome-after")]
    [InlineData(true, "outcome-after")]
    public async Task OriginalDeleteRecoversItsOwnReceiptsAcrossOfficialStoreReopen(bool directory, string boundary)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        bool administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        string architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        _output.WriteLine($"Native producer: architecture={architecture}; OS={Environment.OSVersion.VersionString}; SID={identity.User?.Value}; administrator={administrator}.");
        if (Environment.GetEnvironmentVariable("CFSHARP_EXPECTED_TEST_SID") is string expectedSid)
        {
            Assert.Equal(expectedSid, identity.User?.Value);
            Assert.False(administrator);
            Assert.Equal(Environment.GetEnvironmentVariable("CFSHARP_EXPECTED_TEST_ARCHITECTURE"), architecture, ignoreCase: true);
        }
        await using DeleteRoot root = await DeleteRoot.OpenAsync(directory);
        root.Factory.Boundary = boundary;
        root.Factory.FailuresRemaining = 1;
        if (boundary.StartsWith("outcome", StringComparison.Ordinal))
        {
            Assert.Same(root.Factory.Error, await Assert.ThrowsAsync<IOException>(root.ApplyAsync));
        }
        else
        {
            CloudRemoteApplyResult failed = await root.ApplyAsync();
            Assert.True(failed.RequiresRetry);
            Assert.Equal(0, failed.AppliedEntryCount);
            Assert.Equal(new byte[] { 1 }, failed.SafeCursor.ToArray());
            Assert.Same(root.Factory.Error, failed.Entries[0].Error);
        }

        Assert.Equal(1, root.Factory.FailuresObserved);
        bool preparedOnly = boundary.StartsWith("prepare", StringComparison.Ordinal);
        bool projected = boundary.StartsWith("outcome", StringComparison.Ordinal) || boundary == "projection-after";
        Assert.Equal(preparedOnly, File.Exists(System.IO.Path.Combine(root.Path, root.Target)) ||
            Directory.Exists(System.IO.Path.Combine(root.Path, root.Target)));
        await using (ICloudStateTransaction interrupted = await root.Factory.Store!.BeginTransactionAsync())
        {
            CloudItemState interruptedItem = (await interrupted.Items.GetByItemIdAsync(root.ItemId))!;
            Assert.Equal(projected, interruptedItem.IsTombstone);
            Assert.Equal(projected ? "v2" : "v1", interruptedItem.RemoteRevision);
            CloudRemoteBatchState progress = (await interrupted.RemoteBatches.GetAsync(root.Batch.BatchId))!;
            Assert.Equal(boundary == "outcome-after" ? 1 : 0, progress.AppliedEntryCount);
            Assert.Equal(root.Batch.Fingerprint.ToArray(), progress.Fingerprint.ToArray());
            Assert.Empty(await interrupted.Conflicts.ListAsync());
        }
        await root.RestartAsync();
        CloudRemoteApplyResult replay = await root.ApplyAsync();
        Assert.False(replay.RequiresRetry, replay.Entries[0].Error?.ToString());
        Assert.Empty(replay.ConflictIds);
        Assert.Equal(1, replay.AppliedEntryCount);
        Assert.Equal(new byte[] { 9 }, replay.SafeCursor.ToArray());
        Assert.Equal(boundary == "outcome-after" ? CloudRemoteApplyEntryStatus.AlreadyApplied : CloudRemoteApplyEntryStatus.Applied,
            replay.Entries[0].Status);
        Assert.False(File.Exists(System.IO.Path.Combine(root.Path, root.Target)) || Directory.Exists(System.IO.Path.Combine(root.Path, root.Target)));
        await root.RestartAsync();
        Assert.Equal(CloudRemoteApplyEntryStatus.AlreadyApplied, (await root.ApplyAsync()).Entries[0].Status);
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        CloudItemState item = (await verify.Items.GetByItemIdAsync(root.ItemId))!;
        Assert.True(item.IsTombstone);
        Assert.Equal("v2", item.RemoteRevision);
        Assert.Empty(await verify.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix));
        Assert.Empty(await verify.Conflicts.ListAsync());
        Assert.Equal(0, root.Provider.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostNativeReceiptDoesNotCertifyAnAbsentOriginalObject(bool directory)
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(directory);
        root.Factory.Boundary = "native";
        root.Factory.FailuresRemaining = 1;
        Assert.True((await root.ApplyAsync()).RequiresRetry);
        await root.RestartAsync();
        CloudRemoteApplyResult replay = await root.ApplyAsync();
        Assert.True(replay.RequiresRetry);
        Assert.Empty(replay.ConflictIds);
        Assert.Equal(0, replay.AppliedEntryCount);
        CloudRemoteDeleteRecoveryException error = Assert.IsType<CloudRemoteDeleteRecoveryException>(replay.Entries[0].Error);
        Assert.Equal(root.Batch.BatchId, error.BatchId);
        Assert.Equal(root.Batch.Changes[0].ChangeId, error.ChangeId);
        Assert.Equal(0, error.EntryIndex);
        Assert.Equal(new byte[] { 1 }, replay.SafeCursor.ToArray());
        await using ICloudStateTransaction verify = await root.Factory.Store!.BeginTransactionAsync();
        Assert.False((await verify.Items.GetByItemIdAsync(root.ItemId))!.IsTombstone);
        Assert.Single(await verify.Checkpoints.ListAsync(RemoteDeleteIntent.Prefix));
        Assert.Equal(0, root.Provider.Reads);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EqualOrOmittedPreviousRevisionStillRequiresOriginalOwnership(bool directory, bool omitPrevious)
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(directory, equalRevisions: true, omitPrevious: omitPrevious);
        root.Factory.Boundary = "outcome";
        root.Factory.FailuresRemaining = 1;
        await Assert.ThrowsAsync<IOException>(root.ApplyAsync);
        await root.RestartAsync();
        CloudRemoteApplyResult replay = await root.ApplyAsync();
        Assert.False(replay.RequiresRetry, replay.Entries[0].Error?.ToString());
        Assert.Empty(replay.ConflictIds);
        Assert.Equal(0, root.Provider.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementSurvivesOriginalCompletionWithoutAnotherNativeDelete(bool placeholder)
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(false);
        root.Factory.Boundary = "outcome";
        root.Factory.FailuresRemaining = 1;
        await Assert.ThrowsAsync<IOException>(root.ApplyAsync);
        await root.RestartAsync();
        string replacement = System.IO.Path.Combine(root.Path, root.Target);
        if (placeholder)
        {
            // A different native object even with copied opaque identity is not the original.
            await root.FileSystem.GetDirectory("Docs").CreatePlaceholderAsync(
                CloudFilePlaceholderSpec.CreateBuilder("remote.bin", root.Identity, 512).WithInSyncState(true).Build());
        }
        else
        {
            await File.WriteAllTextAsync(replacement, "retain replacement bytes");
        }

        CloudRemoteApplyResult replay = await root.ApplyAsync();
        Assert.True(File.Exists(replacement));
        if (!placeholder)
        {
            Assert.Equal("retain replacement bytes", await File.ReadAllTextAsync(replacement));
        }

        Assert.True(replay.RequiresRetry); // The replacement generation must be reconciled before releasing its pending scope.

        Assert.Empty(replay.ConflictIds);
        Assert.Equal(0, root.Provider.Reads);
    }

    [Theory]
    [InlineData("new-child")]
    [InlineData("complete")]
    public async Task PartialTreeUsesItsSealedInventoryAndPreservesNewChildren(string scenario)
    {
        await using DeleteRoot root = await DeleteRoot.OpenAsync(true, children: true);
        root.Factory.Boundary = "projection";
        root.Factory.MemberCount = 2;
        root.Factory.FailuresRemaining = 1;
        CloudRemoteApplyResult interrupted = await root.ApplyAsync();
        Assert.True(interrupted.RequiresRetry);
        Assert.Equal(1, root.Factory.FailuresObserved);
        await root.RestartAsync();
        string added = System.IO.Path.Combine(root.Path, root.Target, "new.txt");
        if (scenario == "new-child")
        {
            await File.WriteAllTextAsync(added, "retain new child");
        }

        CloudRemoteApplyResult replay = await root.ApplyAsync();
        Assert.Empty(replay.ConflictIds);
        if (scenario == "new-child")
        {
            Assert.True(replay.RequiresRetry);
            Assert.Equal(0, replay.AppliedEntryCount);
            Assert.Equal("retain new child", await File.ReadAllTextAsync(added));
        }
        else
        {
            Assert.False(replay.RequiresRetry, replay.Entries[0].Error?.ToString());
            Assert.False(Directory.Exists(System.IO.Path.Combine(root.Path, root.Target)));
        }

        Assert.Equal(0, root.Provider.Reads);
    }

    private sealed class DeleteRoot(string area, CloudFileSystem fileSystem, DeleteFactory factory,
        RejectingProvider provider, CloudPlaceholderIdentity identity, CloudRemoteChangeBatch batch, string target) : IAsyncDisposable
    {
        internal CloudFileSystem FileSystem => fileSystem;
        internal string Path => fileSystem.SyncRootPath;
        internal string Database => System.IO.Path.Combine(area, "state.db");
        internal DeleteFactory Factory => factory;
        internal RejectingProvider Provider => provider;
        internal CloudPlaceholderIdentity Identity => identity;
        internal Guid ItemId => identity.ItemId;
        internal CloudRemoteChangeBatch Batch => batch;
        internal void UseBatch(CloudRemoteChangeBatch value) => batch = value;
        internal string Target => target;
        internal Task<CloudRemoteApplyResult> ApplyAsync() => fileSystem.ApplyRemoteChangesAsync(batch).AsTask();

        internal async Task RestartAsync()
        {
            string root = Path;
            await fileSystem.DisposeAsync();
            fileSystem = CloudFileSystem.CreateBuilder(root).WithStateStore(factory).WithContentProvider(provider).Build();
            await fileSystem.StartAsync();
        }

        internal static async Task<DeleteRoot> OpenAsync(bool directory, bool equalRevisions = false, bool omitPrevious = false,
            bool children = false, bool preserveInSync = true, bool hydrated = false)
        {
            string area = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CfSharp-remote-delete", Guid.NewGuid().ToString("N"));
            string root = System.IO.Path.Combine(area, "root");
            Directory.CreateDirectory(root);
            DeleteFactory factory = new(new SqliteCloudStateStoreFactory(System.IO.Path.Combine(area, "state.db")));
            RejectingProvider provider = new();
            CloudFileSystem fileSystem = CloudFileSystem.CreateBuilder(root).WithStateStore(factory)
                .WithRegistration(SyncRootRegistrationOptions.CreateBuilder("CfSharp Remote Delete Test", "1.0")
                    .WithProviderId(Guid.NewGuid()).WithRootMarkedInSync()
                    .WithInSyncPolicy(preserveInSync ? CloudInSyncPolicy.TrackAll | CloudInSyncPolicy.PreserveForSyncEngine : CloudInSyncPolicy.TrackAll)
                    .Build()).WithContentProvider(provider).Build();
            bool registered = false;
            try
            {
                await fileSystem.StartAsync();
                registered = true;
                Directory.CreateDirectory(System.IO.Path.Combine(root, "Docs"));
                CloudPlaceholderIdentity identity = new(Guid.NewGuid(), "remote-file", "v1");
                string target = directory ? "Docs\\remote" : "Docs\\remote.bin";
                if (directory)
                {
                    await fileSystem.GetDirectory("Docs").CreatePlaceholderAsync(
                        CloudDirectoryPlaceholderSpec.CreateBuilder("remote", identity).WithInSyncState(true)
                            .WithPopulationState(CloudDirectoryPopulationState.Complete).Build());
                    if (children)
                    {
                        for (int child = 0; child < 2; child++)
                        {
                            await fileSystem.GetDirectory(target).CreatePlaceholderAsync(CloudFilePlaceholderSpec.CreateBuilder(
                                $"child{child}.bin", new CloudPlaceholderIdentity(Guid.NewGuid(), $"child{child}", "v1"), 512).WithInSyncState(true).Build());
                        }

                        await fileSystem.GetDirectory(target).SetInSyncAsync(true);
                    }
                }
                else if (hydrated)
                {
                    await File.WriteAllBytesAsync(System.IO.Path.Combine(root, target), new byte[512]);
                    await fileSystem.GetFile(target).ConvertToPlaceholderAsync(identity);
                    await fileSystem.GetFile(target).SetInSyncAsync(true);
                }
                else
                {
                    await fileSystem.GetDirectory("Docs").CreatePlaceholderAsync(
                        CloudFilePlaceholderSpec.CreateBuilder("remote.bin", identity, 512).WithInSyncState(true).Build());
                }

                CloudRemoteChange delete = new("delete", CloudRemoteChangeKind.Delete, identity.RemoteId,
                    equalRevisions ? "v1" : "v2", directory ? CloudItemKind.Directory : CloudItemKind.File, target, identity.ItemId,
                    previousRemoteRevision: omitPrevious ? null : "v1");
                CloudRemoteChangeBatch batch = new("stable-original-batch", new byte[] { 1 }, [delete], new byte[] { 9 });
                return new(area, fileSystem, factory, provider, identity, batch, target);
            }
            catch
            {
                await fileSystem.DisposeAsync();
                if (registered)
                {
                    CloudSyncRoot.Open(root).Unregister();
                }

                Directory.Delete(area, recursive: true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await fileSystem.DisposeAsync();
            CloudSyncRoot.Open(Path).Unregister();
            Directory.Delete(area, recursive: true);
        }
    }

    private sealed class RejectingProvider : ICloudFileContentProvider
    {
        internal int Reads { get; private set; }
        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken cancellationToken)
        {
            Reads++;
            throw new InvalidOperationException("Remote Delete recovery must never hydrate content.");
        }
    }

    private sealed class DeleteFactory(ICloudStateStoreFactory inner) : ICloudStateStoreFactory
    {
        internal ICloudStateStore? Store { get; private set; }
        internal string Boundary { get; set; } = string.Empty;
        internal int FailuresRemaining { get; set; }
        internal int OutcomeCount { get; set; } = 1;
        internal int MemberCount { get; set; } = 1;
        internal Action? Cancel { get; set; }
        internal Action? BeforeDeleteOutcome { get; set; }
        private Action? _beforeNextTransaction;
        internal int FailuresObserved { get; private set; }
        internal IOException Error { get; } = new("Synthetic remote Delete commit interruption.");

        public async ValueTask<ICloudStateStore> OpenAsync(CloudStateStoreContext context, CancellationToken cancellationToken = default) =>
            Store = new WrappedStore(await inner.OpenAsync(context, cancellationToken), this);

        private sealed class WrappedStore(ICloudStateStore store, DeleteFactory owner) : ICloudStateStore
        {
            public async ValueTask<ICloudStateTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
            {
                ICloudStateTransaction transaction = await store.BeginTransactionAsync(cancellationToken);
                // Inject only after the final transaction has acquired store isolation.
                Action? mutation = owner._beforeNextTransaction;
                owner._beforeNextTransaction = null;
                try
                {
                    mutation?.Invoke();
                    return new Transaction(transaction, owner);
                }
                catch
                {
                    await transaction.DisposeAsync();
                    throw;
                }
            }
            public ValueTask DisposeAsync() => store.DisposeAsync();
        }

        private sealed class Transaction(ICloudStateTransaction transaction, DeleteFactory owner) : ICloudStateTransaction
        {
            public ICloudItemStateRepository Items => transaction.Items;
            public ICloudCheckpointRepository Checkpoints => transaction.Checkpoints;
            public ICloudOperationJournal Operations => transaction.Operations;
            public ICloudConflictRepository Conflicts => transaction.Conflicts;
            public ICloudRemoteBatchRepository RemoteBatches => transaction.RemoteBatches;
            public ICloudEchoSuppressionRepository EchoSuppressions => transaction.EchoSuppressions;

            public async ValueTask CommitAsync(CancellationToken cancellationToken = default)
            {
                bool completedProjection = owner.BeforeDeleteOutcome is not null &&
                    (await Checkpoints.ListAsync(RemoteDeleteIntent.Prefix, cancellationToken)).Any(checkpoint =>
                        RemoteDeleteIntent.Decode(checkpoint.Value).Members.All(member => member.ProjectedAtTicks.HasValue));
                bool inject = false;
                if (owner.FailuresRemaining > 0)
                {
                    CloudRemoteBatchState? batch = await RemoteBatches.GetAsync("stable-original-batch", cancellationToken);
                    IReadOnlyList<CloudStateCheckpoint> preparations = await Checkpoints.ListAsync(RemoteDeleteIntent.Prefix, cancellationToken);
                    bool prepared = preparations.Count != 0;
                    RemoteDeleteIntent? intent = prepared ? RemoteDeleteIntent.Decode(preparations[0].Value) : null;
                    bool projected = intent?.Members.Count(member => member.ProjectedAtTicks.HasValue) >= owner.MemberCount;
                    bool native = intent?.Members.Count(member => member.DeletedAtTicks.HasValue) >= owner.MemberCount;
                    inject = owner.Boundary.StartsWith("outcome", StringComparison.Ordinal) ? batch?.AppliedEntryCount == owner.OutcomeCount :
                        owner.Boundary.StartsWith("projection", StringComparison.Ordinal) ? projected :
                        owner.Boundary.StartsWith("native", StringComparison.Ordinal) ? native : prepared;
                    if (inject)
                    {
                        owner.FailuresRemaining--;
                        owner.FailuresObserved++;
                    }
                }

                if (inject && owner.Cancel is not null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    owner.Cancel();
                    return;
                }

                if (inject && !owner.Boundary.EndsWith("-after", StringComparison.Ordinal))
                {
                    throw owner.Error;
                }

                await transaction.CommitAsync(cancellationToken);
                if (completedProjection)
                {
                    owner._beforeNextTransaction = owner.BeforeDeleteOutcome;
                    owner.BeforeDeleteOutcome = null;
                }
                if (inject)
                {
                    throw owner.Error;
                }
            }

            public ValueTask RollbackAsync(CancellationToken cancellationToken = default) => transaction.RollbackAsync(cancellationToken);
            public ValueTask DisposeAsync() => transaction.DisposeAsync();
        }
    }
}
