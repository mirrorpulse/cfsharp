using System.ComponentModel;

using CfSharp.Native;

using Microsoft.Win32.SafeHandles;

namespace CfSharp;

public sealed partial class CloudFileSystem
{
    internal async ValueTask<CloudDirectoryMoveReconciliationResult> ReconcileDirectoryMoveAsync(CloudDirectory source,
        CloudDirectoryMoveProof proof, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (!CloudDirectoryStateProjection.SamePath(source.RelativePath, proof.SourceRelativePath))
        {
            throw new ArgumentException("Recovery must be called on the original source reference.", nameof(proof));
        }

        CloudDirectoryMoveReconciliationStage stage = CloudDirectoryMoveReconciliationStage.Acquisition;
        bool observed = false;
        bool committed = false;
        int updatedCount = 0;
        string? durableRootPath = null;
        try
        {
            CloudDirectory target = GetDirectory(proof.DestinationRelativePath);
            using CloudFileSystemOperationLease operation = await AcquireOperationAsync(
                [CloudItemOperationScope.Subtree(source.FullPath), CloudItemOperationScope.Subtree(target.FullPath)],
                cancellationToken).ConfigureAwait(false);
            stage = CloudDirectoryMoveReconciliationStage.PreparationValidation;
            await using (ICloudStateTransaction authentication = await operation.StateStore.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                if (await CloudDirectoryMoveEvidence.AuthenticateAsync(authentication, proof, cancellationToken).ConfigureAwait(false) is null)
                {
                    return Result(CloudDirectoryMoveReconciliationOutcome.NotApplicable);
                }

                durableRootPath = (await authentication.Items.GetByItemIdAsync(proof.RootItemId, cancellationToken).ConfigureAwait(false))?.RelativePath;
                await authentication.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            stage = CloudDirectoryMoveReconciliationStage.NativeObservation;
            DirectoryNativeObservation? atSource = ObserveMoveNative(() => TryObserveDirectory(source.FullPath, SyncRootPath));
            if (atSource is not null && MatchesProof(atSource, proof) &&
                string.Equals(Path.GetRelativePath(SyncRootPath, atSource.ActualPath), proof.SourceRelativePath, StringComparison.Ordinal))
            {
                stage = CloudDirectoryMoveReconciliationStage.Completed;
                return Result(CloudDirectoryMoveReconciliationOutcome.NotMoved,
                    durableRootPath is null || !CloudDirectoryStateProjection.SamePath(durableRootPath, proof.SourceRelativePath));
            }

            using SafeFileHandle guard = ObserveMoveNative(() => WindowsFileMetadata.Open(target.FullPath, preventDelete: true));
            DirectoryNativeObservation atTarget = ObserveMoveNative(() => CloudDirectoryMoveEvidence.Capture(guard.DangerousGetHandle(), SyncRootPath));
            if (!MatchesProof(atTarget, proof) ||
                !string.Equals(Path.GetRelativePath(SyncRootPath, atTarget.ActualPath), proof.DestinationRelativePath, StringComparison.Ordinal))
            {
                return Result(CloudDirectoryMoveReconciliationOutcome.Conflict, requiresRescan: true);
            }

            observed = true;
            if (atSource is not null && !MatchesProof(atSource, proof))
            {
                return Result(CloudDirectoryMoveReconciliationOutcome.Conflict, requiresRescan: true);
            }

            stage = CloudDirectoryMoveReconciliationStage.DurableProjection;
            VerifiedDirectoryProjection projection;
            List<(string Path, NativeFileMetadata Metadata)> children;
            bool unprovenLocalMembers;
            await using (ICloudStateTransaction transaction = await operation.StateStore.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                IReadOnlyList<CloudDirectoryMember>? members = await CloudDirectoryMoveEvidence.AuthenticateAsync(transaction, proof, cancellationToken).ConfigureAwait(false);
                if (members is null)
                {
                    return Result(CloudDirectoryMoveReconciliationOutcome.Conflict, requiresRescan: true);
                }

                projection = await CloudDirectoryStateProjection.ProjectVerifiedAsync(transaction, proof, members,
                    DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                committed = projection.AlreadyCompleted;
                (children, unprovenLocalMembers) = ObserveMoveNative(() => ObserveDirectoryMembers(proof, members, projection.CurrentItems));
                if (!projection.AlreadyCompleted)
                {
                    await CloudDirectoryProvenance.ProjectPathsAsync(transaction, proof, members, projection.CurrentItems, cancellationToken).ConfigureAwait(false);
                }

                ObserveMoveNative(() => { ValidateDirectoryObservation(guard, atTarget, children); return true; });
                if (projection.AlreadyCompleted)
                {
                    await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                    committed = true;
                }
                else
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    committed = true;
                    updatedCount = projection.UpdatedCount;
                }
            }

            stage = CloudDirectoryMoveReconciliationStage.CompletionValidation;
            ObserveMoveNative(() => { ValidateDirectoryObservation(guard, atTarget, children); return true; });
            stage = CloudDirectoryMoveReconciliationStage.Completed;
            return Result(projection.AlreadyCompleted ? CloudDirectoryMoveReconciliationOutcome.AlreadyProjected :
                CloudDirectoryMoveReconciliationOutcome.Projected, unprovenLocalMembers);
        }
        catch (OperationCanceledException exception)
        {
            return Result(CloudDirectoryMoveReconciliationOutcome.Canceled, observed && !committed, exception);
        }
        catch (CloudDirectoryProjectionConflictException exception)
        {
            return Result(CloudDirectoryMoveReconciliationOutcome.Conflict, true, exception);
        }
        catch (NotSupportedException exception) when (stage is CloudDirectoryMoveReconciliationStage.PreparationValidation or CloudDirectoryMoveReconciliationStage.NativeObservation)
        {
            return Result(CloudDirectoryMoveReconciliationOutcome.NotApplicable, observed, exception);
        }
        catch (CloudDirectoryNativeObservationException exception)
        {
            CloudDirectoryMoveReconciliationOutcome outcome = exception.Win32ErrorCode is 32 or 33
                ? CloudDirectoryMoveReconciliationOutcome.Busy : exception.Win32ErrorCode is 2 or 3
                    ? CloudDirectoryMoveReconciliationOutcome.Conflict : CloudDirectoryMoveReconciliationOutcome.Failed;
            return Result(outcome, observed || outcome == CloudDirectoryMoveReconciliationOutcome.Conflict,
                exception.InnerException, exception.HResult);
        }
        catch (InvalidOperationException exception) when (stage == CloudDirectoryMoveReconciliationStage.NativeObservation)
        {
            return Result(CloudDirectoryMoveReconciliationOutcome.Conflict, true, exception);
        }
        catch (Exception exception)
        {
            return Result(observed && !committed ? CloudDirectoryMoveReconciliationOutcome.NativeObservedProjectionPending :
                CloudDirectoryMoveReconciliationOutcome.Failed, observed, exception);
        }

        CloudDirectoryMoveReconciliationResult Result(CloudDirectoryMoveReconciliationOutcome outcome,
            bool requiresRescan = false, Exception? error = null, int? nativeHResult = null) =>
            new(outcome, stage, observed, committed, requiresRescan, error, nativeHResult, updatedCount);
    }

    private static bool MatchesProof(DirectoryNativeObservation observation, CloudDirectoryMoveProof proof) =>
        observation.Binding == proof.ExpectedBinding && observation.Identity.AsSpan().SequenceEqual(proof.ExpectedPlaceholderIdentity.Span);

    private static T ObserveMoveNative<T>(Func<T> observe)
    {
        try
        {
            return observe();
        }
        catch (Win32Exception exception)
        {
            throw new CloudDirectoryNativeObservationException(exception,
                unchecked((int)(0x80070000u | (uint)exception.NativeErrorCode)), exception.NativeErrorCode);
        }
        catch (NativeFileException exception)
        {
            throw new CloudDirectoryNativeObservationException(exception, exception.HResult, null);
        }
    }

    private static DirectoryNativeObservation? TryObserveDirectory(string path, string syncRootPath)
    {
        try
        {
            using SafeFileHandle handle = WindowsFileMetadata.Open(path);
            return CloudDirectoryMoveEvidence.Capture(handle.DangerousGetHandle(), syncRootPath);
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
        {
            return null;
        }
    }

    private (List<(string Path, NativeFileMetadata Metadata)> Observations, bool RequiresRescan) ObserveDirectoryMembers(
        CloudDirectoryMoveProof proof, IReadOnlyList<CloudDirectoryMember> members, IReadOnlyList<CloudItemState> current) =>
        CloudDirectoryNativeValidation.ObserveMembers(SyncRootPath, proof, members, current);

    private void ValidateDirectoryObservation(SafeFileHandle guard, DirectoryNativeObservation expected,
        List<(string Path, NativeFileMetadata Metadata)> children) =>
        CloudDirectoryNativeValidation.Validate(SyncRootPath, guard, expected, children);
}
internal sealed class CloudDirectoryNativeObservationException : IOException
{
    internal CloudDirectoryNativeObservationException(Exception inner, int hresult, int? win32ErrorCode)
        : base("Native directory observation failed.", inner)
    {
        HResult = hresult;
        Win32ErrorCode = win32ErrorCode;
    }

    internal int? Win32ErrorCode { get; }
}
