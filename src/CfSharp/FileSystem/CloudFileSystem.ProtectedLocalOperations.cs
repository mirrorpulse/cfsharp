using System.ComponentModel;
using System.Diagnostics;
using CfSharp.Native;
using Microsoft.Win32.SafeHandles;

namespace CfSharp;

public sealed partial class CloudFileSystem
{
    private static readonly AsyncLocal<CloudProtectedLocalOperationContext?> s_protectedLocalContext = new();
    private readonly CancellationToken _protectedLocalStopping;

    internal async ValueTask<CloudProtectedLocalOperationResult> RunProtectedLocalOperationAsync(
        CloudItem item, CloudProtectedLocalOperationRequest request,
        Func<CloudProtectedLocalOperationContext, CancellationToken, ValueTask> callback, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(callback);
        RejectProtectedLocalReentry();
        EnsureStarted();
        long started = Stopwatch.GetTimestamp();
        using CloudProtectedLocalOperationLifetime lifetime = new(request, started, token, _protectedLocalStopping);
        CancellationToken stop = lifetime.Token;
        CloudProtectedLocalOperationStage stage = CloudProtectedLocalOperationStage.Admission;
        CloudProtectedLocalOperationContext? context = null;
        bool invoked = false;
        bool completed = false;
        CloudProtectedLocalOperationOutcome outcome = CloudProtectedLocalOperationOutcome.Completed;
        Exception? failure = null;
        try
        {
            using CloudFileSystemOperationLease operation = await AcquireOperationAsync(
                [CloudItemOperationScope.Exact(item.FullPath)], stop).ConfigureAwait(false);
            bool directory = item.Kind == CloudItemKind.Directory;
            if (directory != (request.Mode == CloudProtectedLocalOperationMode.DirectoryMetadata))
            {
                throw new CloudProtectedLocalRejectedException(CloudProtectedLocalOperationOutcome.Unsupported,
                    "Use exclusive mode for one file or metadata-only mode for one directory; tree freezing is unavailable.");
            }
            stop.ThrowIfCancellationRequested();
            stage = CloudProtectedLocalOperationStage.Open;
            using SafeFileHandle handle = CloudLocalAccessObject.Open(item.FullPath, directory);
            context = new(this, item, operation.StateStore, handle, request, stop);
            try
            {
                await context.InspectAsync(stop).ConfigureAwait(false);
                if (request.PreparationIdentity is { } identity)
                {
                    await context.ConvertToPlaceholderAsync(identity, stop).ConfigureAwait(false);
                }
                stop.ThrowIfCancellationRequested();
                // Invoke application work away from the caller's synchronization context so
                // external synchronous disposal can drain without blocking its continuation.
                await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                stop.ThrowIfCancellationRequested();
                s_protectedLocalContext.Value = context;
                try
                {
                    context.SetStage(CloudProtectedLocalOperationStage.Callback);
                    invoked = true;
                    await callback(context, stop).ConfigureAwait(false);
                    completed = true;
                }
                finally
                {
                    s_protectedLocalContext.Value = null;
                }
            }
            finally
            {
                await context.CloseAsync().ConfigureAwait(false);
                await lifetime.SealAndDrainCancellationAsync().ConfigureAwait(false);
            }
            if (context.Failure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(context.Failure);
            }
            stop.ThrowIfCancellationRequested();
        }
        catch (Exception error)
        {
            bool callbackFault = invoked && !completed && !ReferenceEquals(error, context?.Failure);
            stage = callbackFault ? CloudProtectedLocalOperationStage.Callback : ReferenceEquals(error, context?.Failure)
                ? context!.FailureStage ?? context.Stage : context?.Stage ?? stage;
            failure = callbackFault ? error : TranslateProtectedLocalError(error, item.FullPath, stage);
            outcome = error switch
            {
                _ when context is { NativeIdentityPrepared: true, DurableProjectionCommitted: false } => CloudProtectedLocalOperationOutcome.NativeAppliedProjectionPending,
                CloudProtectedLocalRejectedException rejected => rejected.Outcome,
                OperationCanceledException when !callbackFault || lifetime.IsCancellationRequested => lifetime.TimedOut
                    ? CloudProtectedLocalOperationOutcome.DeadlineExceeded : CloudProtectedLocalOperationOutcome.Canceled,
                _ when !callbackFault && stage == CloudProtectedLocalOperationStage.Open && failure is CloudFilesException { Win32ErrorCode: 32 or 33 } => CloudProtectedLocalOperationOutcome.Busy,
                _ when !callbackFault && failure is CloudFilesException { Win32ErrorCode: 6 } => CloudProtectedLocalOperationOutcome.ProtectionLost,
                _ when invoked && !completed => CloudProtectedLocalOperationOutcome.CallbackFailed,
                _ => CloudProtectedLocalOperationOutcome.Failed,
            };
            if (outcome == CloudProtectedLocalOperationOutcome.DeadlineExceeded)
            {
                failure = new TimeoutException("The protected local operation budget expired; admitted work has drained.", failure);
            }
        }
        await lifetime.SealAndDrainCancellationAsync().ConfigureAwait(false);
        context?.MarkResourcesReleased();
        return new(outcome, failure is not null ? stage : context?.Stage ?? stage, request.ExpectedBinding, context?.LastSnapshot,
            invoked, completed, Stopwatch.GetElapsedTime(started), failure, context, lifetime);
    }

    private static void RejectProtectedLocalReentry()
    {
        if (s_protectedLocalContext.Value is not null)
        {
            throw new InvalidOperationException("Use the protected context for its original object; nested facade work and owner disposal would wait for their own admission.");
        }
    }

    internal static Exception TranslateProtectedLocalError(Exception error, string path, CloudProtectedLocalOperationStage stage) =>
        error switch
        {
            Win32Exception native => CloudFilesException.FromHResult("CloudItem.ProtectedLocal." + stage, path,
                unchecked((int)(0x80070000u | (uint)native.NativeErrorCode))),
            NativeFileException native => CloudFilesException.FromHResult(native.Operation, path, native.HResult),
            _ => error,
        };
}
