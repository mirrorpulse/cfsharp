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
            return new(request, TimedOut() ? CloudContentConfirmationOutcome.DeadlineExceeded : CloudContentConfirmationOutcome.Canceled,
                CloudContentConfirmationStage.Open, false, false, false, false, 0, 0,
                Stopwatch.GetElapsedTime(started), TimeSpan.Zero, null,
                TimedOut() ? new TimeoutException("The confirmation deadline expired while waiting for the item lease.", exception) : exception);
        }

        using CloudFileSystemOperationLease operation = acquired;
        CloudContentConfirmationResult receipt = await CloudProtectedContentConfirmation.RunAsync(
            () => new CloudProtectedContentSession(file.FullPath, SyncRootPath), request, file.FullPath,
            stop.Token).ConfigureAwait(false);
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
        long projectionStarted = Stopwatch.GetTimestamp();
        CloudSynchronizationState? observed = null;
        bool committed = false;
        try
        {
            using SafeFileHandle guard = WindowsFileMetadata.Open(file.FullPath, preventDelete: true);
            NativeFileMetadata facts = WindowsFileMetadata.Read(guard.DangerousGetHandle());
            if (CloudLocalFileBindingPlatform.Read(guard.DangerousGetHandle(), SyncRootPath) != request.ExpectedBinding ||
                !facts.PlaceholderIdentity.AsSpan().SequenceEqual(request.EncodedIdentity))
            {
                throw new IOException("The verified object or identity changed before durable projection.");
            }

            observed = facts.InSync ? CloudSynchronizationState.InSync : CloudSynchronizationState.NotInSync;
            await PersistIdentityAsync(operation.StateStore, file, request.AcceptedIdentity,
                CancellationToken.None).ConfigureAwait(false);
            committed = true;
            // Observe later writes; never force the old receipt's in-sync bit back onto the file.
            facts = WindowsFileMetadata.Read(guard.DangerousGetHandle());
            observed = facts.InSync ? CloudSynchronizationState.InSync : CloudSynchronizationState.NotInSync;
            return Projected(native.Outcome, projected: true, native.Stage, native.Error);
        }
        catch (Exception exception)
        {
            Exception error = CloudProtectedContentConfirmation.Translate(exception, file.FullPath,
                CloudContentConfirmationStage.Projection);
            return Projected(!committed && native.NativeConfirmationVerified
                ? CloudContentConfirmationOutcome.NativeAppliedProjectionPending : native.Outcome,
                committed, CloudContentConfirmationStage.Projection, error);
        }

        CloudContentConfirmationResult Projected(CloudContentConfirmationOutcome outcome, bool projected,
            CloudContentConfirmationStage stage, Exception? error) => new(request, outcome,
                projected && native.NativeConfirmationVerified && error is null ? CloudContentConfirmationStage.Complete : stage,
                native.NativeIdentityPrepared, native.NativeApplied, native.NativeConfirmationVerified,
                projected, native.BytesVerified, native.SegmentsRead,
                native.Elapsed + Stopwatch.GetElapsedTime(projectionStarted), native.LongestReference,
                native.PreparationUsn, error, observed, native.PreparationHResult, native.NativeMarkHResult);

        bool TimedOut() => deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested &&
            !_contentConfirmationStopping.IsCancellationRequested;
    }
}
