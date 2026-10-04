using System.Runtime.Versioning;
using System.Security.Cryptography;

using CfSharp;
using CfSharp.Storage.Sqlite;

[SupportedOSPlatform("windows10.0.16299")]
internal static class ProtectedConfirmationDeadline
{
    internal static async Task<int> RunAsync(string databasePath, string root)
    {
        await using CloudFileSystem system = CloudFileSystem.CreateBuilder(root)
            .WithStateStore(new SqliteCloudStateStoreFactory(databasePath))
            .WithContentProvider(new NoFetchProvider()).Build();
        await system.StartAsync();
        CloudFile file = system.GetFile("content.bin");
        CloudItemSnapshot before = await file.InspectAsync();
        if (!before.IsPlaceholder || before.Length != 0 || before.SynchronizationState != CloudSynchronizationState.NotInSync)
        {
            throw new InvalidOperationException("The deadline fixture requires an empty, not-in-sync placeholder.");
        }

        CloudContentConfirmationRequest proof = new(before.LocalBinding!,
            CloudPlaceholderIdentity.Decode(before.PlaceholderIdentity.Span), 0, SHA256.HashData([]),
            referenceBudget: TimeSpan.FromMilliseconds(500), deadline: TimeSpan.FromMilliseconds(500));
        TaskCompletionSource<CloudContentConfirmationResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Restrict only this dedicated child process. The parent test runner's pool and
        // synchronization context are untouched, including when the regression fails.
        Thread controller = new(() =>
        {
            try { completion.SetResult(DelayInitialContinuation(file, proof)); }
            catch (Exception exception) { completion.SetException(exception); }
        })
        { IsBackground = true };
        controller.Start();
        CloudContentConfirmationResult result = await completion.Task;
        CloudItemSnapshot after = await file.InspectAsync();
        Console.WriteLine($"Outcome={result.Outcome}; Applied={result.NativeApplied}; Verified={result.NativeConfirmationVerified}; Committed={result.DurableProjectionCommitted}; ElapsedMs={result.Elapsed.TotalMilliseconds}; BudgetMs={proof.Deadline.TotalMilliseconds}; State={after.SynchronizationState}");
        return result.Outcome == CloudContentConfirmationOutcome.DeadlineExceeded &&
            result.Stage == CloudContentConfirmationStage.Open && result.Elapsed >= proof.Deadline &&
            !result.NativeIdentityPrepared && !result.NativeApplied && !result.NativeConfirmationVerified &&
            !result.DurableProjectionCommitted && after.SynchronizationState == CloudSynchronizationState.NotInSync
            ? 0 : 3;
    }

    private static CloudContentConfirmationResult DelayInitialContinuation(CloudFile file, CloudContentConfirmationRequest proof)
    {
        ThreadPool.GetMinThreads(out int minimum, out int minimumIo);
        ThreadPool.GetMaxThreads(out int maximum, out int maximumIo);
        using ManualResetEventSlim occupied = new();
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim drained = new();
        bool queued = false;
        try
        {
            if (!ThreadPool.SetMinThreads(1, minimumIo) || !ThreadPool.SetMaxThreads(1, maximumIo))
            {
                throw new InvalidOperationException("The isolated process could not control its thread pool.");
            }

            queued = ThreadPool.QueueUserWorkItem(_ =>
            {
                occupied.Set();
                release.Wait();
                drained.Set();
            });
            if (!queued || !occupied.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The timer-blocking worker did not start.");
            }

            using CancellationTokenSource timerProbe = new(proof.Deadline);
            Task<CloudContentConfirmationResult> pending = file.ConfirmUploadedContentAsync(proof).AsTask();
            if (pending.IsCompleted)
            {
                throw new InvalidOperationException("The initial confirmation continuation bypassed the occupied pool.");
            }

            Thread.Sleep(1000);
            if (timerProbe.IsCancellationRequested)
            {
                throw new InvalidOperationException("The timer callback ran while the pool was occupied.");
            }

            // Both the context-free confirmation continuation and timer callbacks are delayed.
            // After release either cancellation or the shared monotonic clock must prevent work.
            release.Set();
            ThreadPool.SetMaxThreads(maximum, maximumIo);
            return pending.GetAwaiter().GetResult();
        }
        finally
        {
            release.Set();
            ThreadPool.SetMaxThreads(maximum, maximumIo);
            ThreadPool.SetMinThreads(minimum, minimumIo);
            if (queued)
            {
                drained.Wait();
            }
        }
    }

    private sealed class NoFetchProvider : ICloudFileContentProvider
    {
        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromException<Stream>(new InvalidOperationException("The deadline fixture must not hydrate content."));
    }
}
