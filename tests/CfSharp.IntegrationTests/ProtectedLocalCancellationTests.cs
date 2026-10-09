namespace CfSharp.IntegrationTests;

public sealed partial class CloudContentConfirmationTests
{
    [Theory]
    [InlineData("caller")]
    [InlineData("deadline")]
    [InlineData("owner")]
    public async Task ProtectedLocalCancellationIsObservableAndDrainsAnUncooperativeCallback(string reason)
    {
        FaultFactory? factory = null;
        await using Fixture fixture = await Fixture.StartAsync(path => factory = new FaultFactory(path));
        await File.WriteAllTextAsync(fixture.File.FullPath, "original");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource signaled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CloudProtectedLocalOperationContext? observed = null;
        CloudProtectedLocalOperationRequest request = new(binding);
        if (reason == "deadline")
        {
            request = request.WithBudget(TimeSpan.FromSeconds(2));
        }
        Task<CloudProtectedLocalOperationResult> pending = fixture.File.RunProtectedLocalOperationAsync(request, async (scope, stop) =>
        {
            observed = scope;
            using CancellationTokenRegistration registration = stop.Register(() => signaled.TrySetResult());
            entered.SetResult();
            // Deliberately ignore cancellation until the test permits actual completion.
            await release.Task;
        }, cancellation.Token).AsTask();
        Task? shutdown = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (reason == "caller")
            {
                cancellation.Cancel();
            }
            else if (reason == "owner")
            {
                shutdown = fixture.System.DisposeAsync().AsTask();
            }
            await signaled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(pending.IsCompleted);
            Assert.True(observed!.IsDraining);
            Assert.Equal(CloudProtectedLocalOperationStage.Draining, observed.CurrentStage);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observed.InspectAsync().AsTask());
            Assert.Throws<IOException>(() => File.WriteAllText(fixture.File.FullPath, "contender"));
            await using ICloudStateTransaction alive = await factory!.Store.BeginTransactionAsync();
            Assert.Empty(await alive.Operations.ListAsync(4));
        }
        finally
        {
            release.TrySetResult();
        }
        CloudProtectedLocalOperationResult result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(reason == "deadline" ? CloudProtectedLocalOperationOutcome.DeadlineExceeded : CloudProtectedLocalOperationOutcome.Canceled, result.Outcome);
        Assert.True(result.CancellationRequested);
        Assert.Equal(reason == "deadline", result.BudgetExpired);
        Assert.True(result.CallbackCompleted);
        Assert.True(result.Drained);
        Assert.Equal(CloudProtectedLocalOperationStage.Released, observed!.CurrentStage);
        Assert.False(observed.IsDraining);
        if (reason == "deadline")
        {
            Assert.IsType<TimeoutException>(result.Error);
        }
        if (shutdown is not null)
        {
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal("original", await File.ReadAllTextAsync(fixture.File.FullPath));
        Assert.Equal(0, fixture.Provider.Fetches);
    }

    [Fact]
    public async Task ProtectedLocalCancellationBudgetIncludesWaitingForPathAdmission()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        await File.WriteAllTextAsync(fixture.File.FullPath, "original");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        await using CloudItemLease lease = await fixture.File.AcquireLeaseAsync(CloudItemLeaseOptions.ExclusiveWrite);
        CloudProtectedLocalOperationResult result = await fixture.File.RunProtectedLocalOperationAsync(
            new CloudProtectedLocalOperationRequest(binding).WithBudget(TimeSpan.FromMilliseconds(100)),
            (_, _) => throw new InvalidOperationException("Expired admission cannot enter a callback."))
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CloudProtectedLocalOperationOutcome.DeadlineExceeded, result.Outcome);
        Assert.Equal(CloudProtectedLocalOperationStage.Admission, result.Stage);
        Assert.False(result.CallbackStarted);
        Assert.True(result.Drained);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public async Task ProtectedLocalCancellationHandlersAreDrainedWithoutPoisoningOwnerShutdown()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        await File.WriteAllTextAsync(fixture.File.FullPath, "original");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource callbackRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource callbackExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource handlerEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim handlerRelease = new(false);
        InvalidOperationException error = new("Synthetic application cancellation handler failure.");
        CancellationTokenRegistration registration = default;
        Task<CloudProtectedLocalOperationResult> pending = fixture.File.RunProtectedLocalOperationAsync(new(binding), async (_, stop) =>
        {
            registration = stop.Register(() =>
            {
                handlerEntered.SetResult();
                if (!handlerRelease.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("The test did not release its controlled cancellation handler.");
                }
                throw error;
            });
            entered.SetResult();
            await callbackRelease.Task;
            callbackExited.SetResult();
        }).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task shutdown = fixture.System.DisposeAsync().AsTask();
        try
        {
            await handlerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            callbackRelease.SetResult();
            await callbackExited.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(CloudFileSystemLifecycleState.Stopping, fixture.System.LifecycleState);
            Assert.False(shutdown.IsCompleted);
            Assert.False(pending.IsCompleted);
            Assert.Throws<IOException>(() => File.WriteAllText(fixture.File.FullPath, "contender"));
            Assert.Throws<ObjectDisposedException>(() => fixture.System.GetFile("new.bin"));
        }
        finally
        {
            callbackRelease.TrySetResult();
            handlerRelease.Set();
        }
        CloudProtectedLocalOperationResult result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CloudProtectedLocalOperationOutcome.Canceled, result.Outcome);
        Assert.Same(error, Assert.IsType<AggregateException>(result.CancellationCallbackError).InnerException);
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        registration.Dispose();
        Assert.Equal(CloudFileSystemLifecycleState.Disposed, fixture.System.LifecycleState);
        Assert.Equal("original", await File.ReadAllTextAsync(fixture.File.FullPath));
    }

    [Fact]
    public async Task ProtectedLocalCancellationAfterDescriptorApplicationRetainsNativeReceipts()
    {
        await using Fixture fixture = await Fixture.StartAsync();
        await File.WriteAllTextAsync(fixture.File.FullPath, "original");
        CloudLocalFileBinding binding = Assert.IsType<CloudLocalFileBinding>((await fixture.File.InspectAsync()).LocalBinding);
        using CancellationTokenSource cancellation = new();
        CloudProtectedLocalOperationResult result = await fixture.File.RunProtectedLocalOperationAsync(
            CloudProtectedLocalOperationRequest.ForLocalConversion(binding, CloudPlaceholderIdentity.Create("local-cancel-dacl")),
            async (scope, stop) =>
            {
                await scope.ApplyAccessDescriptorAsync(await scope.ReadAccessDescriptorAsync(stop), stop);
                cancellation.Cancel();
            }, cancellation.Token);
        Assert.Equal(CloudProtectedLocalOperationOutcome.Canceled, result.Outcome);
        Assert.True(result.NativeConverted);
        Assert.True(result.DurableProjectionCommitted);
        Assert.True(result.AccessDescriptorApplied);
        Assert.True(result.AccessDescriptorReadBack);
        Assert.True(result.CallbackCompleted);
        Assert.True(result.Drained);
        Assert.Equal(0, fixture.Provider.Fetches);
    }
}
