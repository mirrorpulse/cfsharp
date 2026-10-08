using System.Diagnostics;
using System.Runtime.Versioning;

namespace CfSharp.IntegrationTests;

[SupportedOSPlatform("windows10.0.16299")]
public sealed partial class CloudDirectoryMoveTests
{
    [Fact]
    public async Task IndependentConsumerTriggersActualRenameApprovalAndCompletion()
    {
        RenameProvider provider = new();
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync(provider);
        CloudDirectory source = root.FileSystem.GetDirectory("Docs");
        CloudDirectoryMoveProof proof = await source.PrepareMoveAsync(root.FileSystem.Root, "Moved");
        using NamespaceProcess consumer = new(source.FullPath, Path.Combine(root.RootPath, "Moved"), "rename");
        await consumer.WaitForReadyAsync();
        consumer.Go.Set();
        await consumer.WaitForExitAsync();
        Assert.Equal(0, consumer.ExitCode);
        Assert.True(consumer.Attempted.WaitOne(0));
        Assert.True(consumer.Completed.WaitOne(0));
        CloudProviderRenameRequest approval = await provider.Approval.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(approval.IsDirectory && approval.SourceInScope && approval.TargetInScope);
        Assert.Equal(proof.ExpectedPlaceholderIdentity.ToArray(), approval.FileIdentity.ToArray());
        CloudProviderCompletionNotification completion = await provider.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.EndsWith("\\Moved", completion.NormalizedPath);
        Assert.EndsWith("\\Docs", completion.RelatedPath);
        Assert.Equal(1, provider.Approvals);
        Assert.Equal(1, provider.Completions);
        CloudDirectoryMoveReconciliationResult result = await source.ReconcileMoveAsync(proof);
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.Projected, result.Outcome);
        Assert.True(result.NativeMoveObserved && result.DurableProjectionCommitted);
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("delete")]
    [InlineData("create")]
    [InlineData("edit")]
    public async Task IndependentNamespaceCompetitionReportsNativeAndCommittedFacts(string action)
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync(new RenameProvider());
        CloudDirectory source = root.FileSystem.GetDirectory("Docs");
        CloudDirectoryMoveProof proof = await source.PrepareMoveAsync(root.FileSystem.Root, "Moved");
        string target = Path.Combine(root.RootPath, "Moved");
        Directory.Move(source.FullPath, target);
        using NamespaceProcess competitor = new(action == "rename" ? target : Path.Combine(target, "child.txt"),
            action == "rename" ? Path.Combine(root.RootPath, "Elsewhere") : Path.Combine(target, "new.txt"), action);
        await competitor.WaitForReadyAsync();
        root.Faults.BeforeCommit = () =>
        {
            root.Faults.BeforeCommit = null;
            competitor.Go.Set();
            competitor.WaitForExit();
            Assert.True(competitor.Attempted.WaitOne(0) && competitor.Completed.WaitOne(0));
        };
        CloudDirectoryMoveReconciliationResult result = await source.ReconcileMoveAsync(proof);
        Assert.True(result.NativeMoveObserved && result.DurableProjectionCommitted);
        if (action == "rename")
        {
            Assert.True(competitor.ExitCode is 0 or 32, $"Unexpected native consumer error: {competitor.ExitCode}");
            Assert.Equal(competitor.ExitCode == 0 ? CloudDirectoryMoveReconciliationOutcome.Conflict : CloudDirectoryMoveReconciliationOutcome.Projected,
                result.Outcome);
            Assert.Equal(competitor.ExitCode == 0, result.RequiresFullRescan);
            Assert.Equal(competitor.ExitCode == 0, Directory.Exists(Path.Combine(root.RootPath, "Elsewhere")));
            // Cloud Files approval can permit a namespace change while our metadata handle
            // remains open. The post-commit actual-path check must expose that split fact.
        }
        else
        {
            Assert.Equal(0, competitor.ExitCode);
            // Truncating a placeholder can also change its native placeholder metadata.
            // Those detected changes report a committed projection plus a reconciliation fence.
            Assert.Equal(action is "delete" or "edit", result.RequiresFullRescan);
            Assert.Equal(action is "delete" or "edit" ? CloudDirectoryMoveReconciliationOutcome.Conflict : CloudDirectoryMoveReconciliationOutcome.Projected,
                result.Outcome);
        }

        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        Assert.Equal("Moved", (await verify.Items.GetByItemIdAsync(proof.RootItemId))!.RelativePath);
        Assert.NotNull(await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ReceiptName(proof.ProofId)));
    }

    [Fact]
    public async Task CopiedPlaceholderIdentityOnDifferentNativeDirectoryCannotReplaceOriginalBinding()
    {
        await using DirectoryMoveTestRoot root = await DirectoryMoveTestRoot.OpenAsync();
        CloudDirectory source = root.FileSystem.GetDirectory("Docs");
        CloudDirectoryMoveProof proof = await source.PrepareMoveAsync(root.FileSystem.Root, "Moved");
        Directory.Move(source.FullPath, Path.Combine(root.RootPath, "Away"));
        Directory.Move(Path.Combine(root.RootPath, "Ordinary"), Path.Combine(root.RootPath, "Moved"));
        await using (ICloudStateTransaction read = await root.Store.BeginTransactionAsync())
        {
            CloudItemState original = (await read.Items.GetByItemIdAsync(proof.RootItemId))!;
            await read.RollbackAsync();
            await root.FileSystem.GetDirectory("Moved").ConvertToPlaceholderAsync(root.DirectoryIdentity);
            await using ICloudStateTransaction restore = await root.Store.BeginTransactionAsync();
            await restore.Items.UpsertAsync(original);
            await restore.CommitAsync();
        }

        CloudItemSnapshot target = await root.FileSystem.GetDirectory("Moved").InspectAsync();
        Assert.Equal(proof.ExpectedPlaceholderIdentity.ToArray(), target.PlaceholderIdentity.ToArray());
        Assert.NotEqual(proof.ExpectedBinding, target.LocalBinding);
        CloudDirectoryMoveReconciliationResult result = await source.ReconcileMoveAsync(proof);
        Assert.Equal(CloudDirectoryMoveReconciliationOutcome.Conflict, result.Outcome);
        Assert.False(result.NativeMoveObserved || result.DurableProjectionCommitted);
        await using ICloudStateTransaction verify = await root.Store.BeginTransactionAsync();
        Assert.Equal("Docs", (await verify.Items.GetByItemIdAsync(proof.RootItemId))!.RelativePath);
        Assert.Null(await verify.Checkpoints.GetAsync(CloudDirectoryMoveEvidence.ReceiptName(proof.ProofId)));
    }

    private sealed class RenameProvider : ICloudDemandProvider
    {
        internal int Approvals;
        internal int Completions;
        internal TaskCompletionSource<CloudProviderRenameRequest> Approval { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<CloudProviderCompletionNotification> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream>(new MemoryStream());
        public ValueTask<CloudProviderPolicyDecision> ApproveRenameAsync(CloudProviderRenameRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Approvals);
            Approval.TrySetResult(request);
            return ValueTask.FromResult(CloudProviderPolicyDecision.Allow);
        }

        public ValueTask<CloudProviderPolicyDecision> ApproveDeleteAsync(CloudProviderDeleteRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(CloudProviderPolicyDecision.Allow);

        public ValueTask OnCompletionAsync(CloudProviderCompletionNotification notification, CancellationToken cancellationToken)
        {
            if (notification.Kind == CloudProviderNotificationKind.RenameCompleted)
            {
                Interlocked.Increment(ref Completions);
                Completion.TrySetResult(notification);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class NamespaceProcess : IDisposable
    {
        private readonly Process _process;
        private readonly EventWaitHandle _ready;
        internal EventWaitHandle Go { get; }
        internal EventWaitHandle Attempted { get; }
        internal EventWaitHandle Completed { get; }
        internal int ExitCode => _process.ExitCode;

        internal NamespaceProcess(string source, string target, string action)
        {
            string signal = "Local\\CfSharp-directory-race-" + Guid.NewGuid().ToString("N");
            _ready = new(false, EventResetMode.ManualReset, signal);
            Go = new(false, EventResetMode.ManualReset, signal + "-go");
            Attempted = new(false, EventResetMode.ManualReset, signal + "-attempted");
            Completed = new(false, EventResetMode.ManualReset, signal + "-completed");
            ProcessStartInfo start = new("dotnet") { UseShellExecute = false, CreateNoWindow = true };
            foreach (string argument in new[] { DirectoryHarnessPath(), source, target, signal, action, "namespace-consumer" })
            {
                start.ArgumentList.Add(argument);
            }

            _process = Process.Start(start)!;
        }

        internal async Task WaitForReadyAsync()
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            while (!_ready.WaitOne(0))
            {
                Assert.False(_process.HasExited);
                await Task.Delay(10, timeout.Token);
            }
        }

        internal Task WaitForExitAsync() => _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        internal void WaitForExit() => Assert.True(_process.WaitForExit(20000));
        public void Dispose()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit();
            }

            _process.Dispose();
            _ready.Dispose();
            Go.Dispose();
            Attempted.Dispose();
            Completed.Dispose();
        }
    }

    private static string DirectoryHarnessPath()
    {
        DirectoryInfo? repository = new(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "CfSharp.sln")))
        {
            repository = repository.Parent;
        }

        Assert.NotNull(repository);
        string configuration = typeof(CloudDirectoryMoveTests).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
            .Cast<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration;
        string path = Path.Combine(repository!.FullName, "tests", "CfSharp.Storage.Sqlite.CrashHarness", "bin", configuration,
            "net10.0-windows", "CfSharp.Storage.Sqlite.CrashHarness.dll");
        Assert.True(File.Exists(path));
        return path;
    }
}
