using System.IO.MemoryMappedFiles;

namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Fact]
    public async Task ProtectedLocalCallbackDrainsAnAdmittedInspectionEvenWhenItWasNotAwaited()
    {
        FaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new FaultFactory(path));
        await File.WriteAllTextAsync(fixture.File.FullPath, "original");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        TaskCompletionSource inspecting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource callbackExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CloudItemSnapshot>? child = null;
        Task<CloudProtectedLocalOperationResult> pending = fixture.File.RunProtectedLocalOperationAsync(new(binding), (scope, token) =>
        {
            factory!.BeforeTransaction = () =>
            {
                inspecting.SetResult();
                return new(release.Task);
            };
            child = scope.InspectAsync(token).AsTask();
            callbackExited.SetResult();
            return ValueTask.CompletedTask;
        }).AsTask();
        try
        {
            await inspecting.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await callbackExited.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(pending.IsCompleted);
            Assert.Throws<IOException>(() => File.WriteAllText(fixture.File.FullPath, "contender"));
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal(CloudProtectedLocalOperationOutcome.Completed, (await pending).Outcome);
        Assert.Equal(binding, (await child!).LocalBinding);
        await File.WriteAllTextAsync(fixture.File.FullPath, "released");
    }

    [Fact]
    public async Task ProtectedLocalCallbackRetainsOriginalObjectAcrossAwaitAndRejectsWriter()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        byte[] content = "local-unuploaded"u8.ToArray();
        await File.WriteAllBytesAsync(fixture.File.FullPath, content);
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudProtectedLocalOperationResult result = await fixture.File.RunProtectedLocalOperationAsync(new(binding), async (scope, token) =>
        {
            Assert.Equal(binding, (await scope.InspectAsync(token)).LocalBinding);
            using LocalCompetitor competitor = StartLocalCompetitor(fixture.File.FullPath, Path.Combine(fixture.Root, "other.bin"), "overwrite");
            await competitor.StartAsync();
            await competitor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10), token);
            Assert.Equal(32, competitor.ExitCode);
            await Task.Yield();
            Assert.Equal(binding, (await fixture.File.InspectAsync(token)).LocalBinding);
            Assert.Equal(content.Length, (await scope.InspectAsync(token)).Length);
        });
        Assert.Equal(CloudProtectedLocalOperationOutcome.Completed, result.Outcome);
        Assert.True(result.CallbackStarted);
        Assert.True(result.CallbackCompleted);
        Assert.True(result.Drained);
        Assert.Equal(binding, result.Snapshot!.LocalBinding);
        Assert.Equal(content, await File.ReadAllBytesAsync(fixture.File.FullPath));
        await File.WriteAllTextAsync(fixture.File.FullPath, "editable after release");
        Assert.Equal(0, fixture.Provider.Fetches);
    }

    [Fact]
    public async Task ProtectedLocalCallbackRejectsReplacementBindingAndDirectoryModeOnFile()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        await File.WriteAllTextAsync(fixture.File.FullPath, "original");
        CloudLocalFileBinding original = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        File.Move(fixture.File.FullPath, Path.Combine(fixture.Root, "original.bin"));
        await File.WriteAllTextAsync(fixture.File.FullPath, "replacement");
        int callbacks = 0;
        CloudProtectedLocalOperationResult result = await fixture.File.RunProtectedLocalOperationAsync(new(original), (_, _) =>
        {
            callbacks++;
            return ValueTask.CompletedTask;
        });
        Assert.Equal(CloudProtectedLocalOperationOutcome.LocalObjectMismatch, result.Outcome);
        CloudLocalFileBinding replacement = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        result = await fixture.File.RunProtectedLocalOperationAsync(new(replacement, CloudProtectedLocalOperationMode.DirectoryMetadata), (_, _) =>
        {
            callbacks++;
            return ValueTask.CompletedTask;
        });
        Assert.Equal(CloudProtectedLocalOperationOutcome.Unsupported, result.Outcome);
        Assert.Equal(0, callbacks);
        Assert.Equal("replacement", await File.ReadAllTextAsync(fixture.File.FullPath));
        Assert.Equal(0, fixture.Provider.Fetches);
    }

    [Fact]
    public async Task ProtectedLocalCallbackRejectsScopeEscapeReentryAndSelfDisposal()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        await File.WriteAllTextAsync(fixture.File.FullPath, "original");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        CloudProtectedLocalOperationContext? escaped = null;
        CloudFile other = fixture.System.GetFile("other.bin");
        CloudProtectedLocalOperationResult result = await fixture.File.RunProtectedLocalOperationAsync(new(binding), async (scope, token) =>
        {
            escaped = scope;
            await Assert.ThrowsAsync<InvalidOperationException>(() => other.InspectAsync().AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.System.DisposeAsync().AsTask());
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.File.RunProtectedLocalOperationAsync(new(binding),
                (_, _) => ValueTask.CompletedTask).AsTask());
            Assert.Equal(binding, (await fixture.File.InspectAsync(token)).LocalBinding);
        });
        Assert.Equal(CloudProtectedLocalOperationOutcome.Completed, result.Outcome);
        Assert.NotNull(escaped);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => escaped.InspectAsync().AsTask());
        await File.WriteAllTextAsync(fixture.File.FullPath, "released");
    }

    [Theory]
    [InlineData("io")]
    [InlineData("native")]
    [InlineData("cancellation")]
    public async Task ProtectedLocalCallbackPreservesFailureAndReleasesAllAdmission(string kind)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        await File.WriteAllTextAsync(fixture.File.FullPath, "original");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        Exception error = kind switch
        {
            "native" => new System.ComponentModel.Win32Exception(32),
            "cancellation" => new OperationCanceledException("Application-owned cancellation."),
            _ => new IOException("Synthetic callback failure."),
        };
        CloudProtectedLocalOperationResult result = await fixture.File.RunProtectedLocalOperationAsync(new(binding), (_, _) => throw error);
        Assert.Equal(CloudProtectedLocalOperationOutcome.CallbackFailed, result.Outcome);
        Assert.Equal(CloudProtectedLocalOperationStage.Callback, result.Stage);
        Assert.Same(error, result.Error);
        Assert.False(result.CallbackCompleted);
        await File.WriteAllTextAsync(fixture.File.FullPath, "released");
        Assert.Equal(binding, (await fixture.File.InspectAsync()).LocalBinding);
    }

    [Fact]
    public async Task ProtectedLocalOwnerShutdownDrainsAdmittedCallbackAndRejectsNewWork()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        await File.WriteAllTextAsync(fixture.File.FullPath, "original");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CloudProtectedLocalOperationResult> pending = fixture.File.RunProtectedLocalOperationAsync(new(binding), async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
        }).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task shutdown = fixture.System.DisposeAsync().AsTask();
        try
        {
            Assert.Equal(CloudFileSystemLifecycleState.Stopping, fixture.System.LifecycleState);
            Assert.False(shutdown.IsCompleted);
            Assert.Throws<ObjectDisposedException>(() => fixture.System.GetFile("new.bin"));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.File.InspectAsync().AsTask());
            Assert.Throws<IOException>(() => File.WriteAllText(fixture.File.FullPath, "contender"));
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal(CloudProtectedLocalOperationOutcome.Canceled, (await pending).Outcome);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CloudFileSystemLifecycleState.Disposed, fixture.System.LifecycleState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProtectedLocalPublicAdmissionRejectsExistingWriterAndMapping(bool mapped)
    {
        await using Fixture fixture = await Fixture.StartAsync();
        await File.WriteAllTextAsync(fixture.File.FullPath, "local-only");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        using FileStream writer = new(fixture.File.FullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        using MemoryMappedFile? mapping = mapped
            ? MemoryMappedFile.CreateFromFile(writer, null, 0, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, true) : null;
        using MemoryMappedViewAccessor? view = mapping?.CreateViewAccessor();
        if (mapped)
        {
            writer.Dispose();
        }
        CloudProtectedLocalOperationResult result = await fixture.File.RunProtectedLocalOperationAsync(new(binding), (_, _) =>
            throw new InvalidOperationException("A busy object must never enter the callback."));
        Assert.Equal(CloudProtectedLocalOperationOutcome.Busy, result.Outcome);
        Assert.Equal(32, Assert.IsType<CloudFilesException>(result.Error).Win32ErrorCode);
        Assert.False(result.CallbackStarted);
        Assert.Equal(0, fixture.Provider.Fetches);
    }
}
