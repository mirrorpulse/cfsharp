using System.ComponentModel;
using System.Diagnostics;
using CfSharp.Native;
using Microsoft.Win32.SafeHandles;

namespace CfSharp;

public sealed partial class CloudFileSystem
{
    private readonly CancellationTokenSource _contentConfirmationStopping = new();

    internal async ValueTask<CloudContentConfirmationResult> ConfirmUploadedContentAsync(
        CloudFile file, CloudContentConfirmationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        long started = Stopwatch.GetTimestamp();
        EnsureStarted();
        using CancellationTokenSource deadline = new(request.Deadline);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _contentConfirmationStopping.Token, deadline.Token);
        CloudFileSystemOperationLease acquired;
        try
        {
            acquired = await AcquireOperationAsync(
                [CloudItemOperationScope.Exact(file.FullPath)], stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (stop.IsCancellationRequested)
        {
            return AdmissionFailure(TimedOut() ? CloudContentConfirmationOutcome.DeadlineExceeded : CloudContentConfirmationOutcome.Canceled,
                TimedOut() ? new TimeoutException("The confirmation deadline expired while waiting for the item lease.", exception) : exception);
        }
        catch (CloudPathReparsePointException exception)
        {
            return AdmissionFailure(CloudContentConfirmationOutcome.NotApplicable, exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            Win32Exception or NativeFileException or CloudFilesException)
        {
            Exception error = CloudProtectedContentConfirmation.Translate(exception, file.FullPath,
                CloudContentConfirmationStage.Open);
            return AdmissionFailure(error is CloudFilesException { Win32ErrorCode: 32 or 33 }
                ? CloudContentConfirmationOutcome.Busy : CloudContentConfirmationOutcome.Failed, error);
        }

        using CloudFileSystemOperationLease operation = acquired;
        CloudContentConfirmationResult receipt = await CloudProtectedContentConfirmation.RunAsync(
            () => new CloudProtectedContentSession(file.FullPath, SyncRootPath), request, file.FullPath,
            started, stop.Token).ConfigureAwait(false);
        bool expired = receipt.Outcome == CloudContentConfirmationOutcome.Canceled && TimedOut();
        CloudContentConfirmationResult native = new(request,
            expired ? CloudContentConfirmationOutcome.DeadlineExceeded : receipt.Outcome, receipt.Stage,
            receipt.NativeIdentityPrepared, receipt.NativeApplied, receipt.NativeConfirmationVerified,
            receipt.DurableProjectionCommitted, receipt.BytesVerified, receipt.SegmentsRead,
            Stopwatch.GetElapsedTime(started), receipt.LongestReference, receipt.PreparationUsn,
            expired ? new TimeoutException("The confirmation deadline expired.", receipt.Error) : receipt.Error,
            receipt.ObservedSynchronizationState, receipt.PreparationHResult, receipt.NativeMarkHResult);
        if (!native.NativeConfirmationVerified && !native.NativeIdentityPrepared)
        {
            return native;
        }

        // No protected reference or opaque owner survives RunAsync. Hold only a metadata
        // no-delete guard through projection so path replacement cannot redirect the row.
        // It permits later data writes: native in-sync remains the authority for those changes.
        // Raw CFAPI identity writers must also join the caller's coordination boundary: this
        // guard permits identity updates, and native mutation cannot be atomic with SQLite.
        long projectionStarted = Stopwatch.GetTimestamp();
        CloudSynchronizationState? observed = null;
        byte[]? observedIdentity = null;
        bool committed = false;
        bool identityConflict = false;
        try
        {
            using SafeFileHandle guard = WindowsFileMetadata.Open(file.FullPath, preventDelete: true);
            void CheckProjectionIdentity()
            {
                NativeFileMetadata facts = WindowsFileMetadata.Read(guard.DangerousGetHandle());
                observed = facts.InSync ? CloudSynchronizationState.InSync : CloudSynchronizationState.NotInSync;
                observedIdentity = facts.PlaceholderIdentity;
                if (CloudLocalFileBindingPlatform.Read(guard.DangerousGetHandle(), SyncRootPath) != request.ExpectedBinding ||
                    !observedIdentity.AsSpan().SequenceEqual(request.EncodedIdentity))
                {
                    identityConflict = true;
                    throw new IOException("The verified object or complete identity changed during durable projection; reconcile native and durable state.");
                }
            }

            CheckProjectionIdentity();
            await PersistIdentityAsync(operation.StateStore, file, request.AcceptedIdentity,
                CancellationToken.None, validateBeforeCommit: CheckProjectionIdentity,
                onCommitted: () => committed = true).ConfigureAwait(false);
            // Detect identity changes across the commit as well as later content writes.
            // Never force either the old identity or its in-sync bit back onto the file.
            CheckProjectionIdentity();
            return Projected(native.Outcome);
        }
        catch (Exception exception)
        {
            Exception error = CloudProtectedContentConfirmation.Translate(exception, file.FullPath,
                CloudContentConfirmationStage.Projection);
            return Projected(identityConflict ? CloudContentConfirmationOutcome.ProjectionConflict :
                native.NativeConfirmationVerified
                    ? CloudContentConfirmationOutcome.NativeAppliedProjectionPending : native.Outcome, error);
        }

        CloudContentConfirmationResult Projected(CloudContentConfirmationOutcome outcome, Exception? error = null) =>
            native.WithProjection(outcome, committed, Stopwatch.GetElapsedTime(projectionStarted),
                observed, observedIdentity is null ? null : new ReadOnlyMemory<byte>(observedIdentity), error);

        CloudContentConfirmationResult AdmissionFailure(CloudContentConfirmationOutcome outcome, Exception error) =>
            new(request, outcome, CloudContentConfirmationStage.Open, false, false, false, false, 0, 0,
                Stopwatch.GetElapsedTime(started), TimeSpan.Zero, null, error);

        bool TimedOut() => deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested &&
            !_contentConfirmationStopping.IsCancellationRequested;
    }
}
