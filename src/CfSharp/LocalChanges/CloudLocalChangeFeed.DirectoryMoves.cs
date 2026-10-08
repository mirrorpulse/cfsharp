using System.ComponentModel;
using System.Runtime.Versioning;

using CfSharp.Native;

using Microsoft.Win32.SafeHandles;

namespace CfSharp;

public sealed partial class CloudLocalChangeFeed
{
    internal const string NamespaceObservationsPrefix = "cfsharp/namespace/observations";

    [SupportedOSPlatform("windows10.0.16299")]
    private async ValueTask<DirectoryFeedProjection> ProjectDirectoryObservationAsync(ICloudStateTransaction transaction,
        CloudDirectoryMoveProof proof, CloudItemPath path, CloudItemPath previousPath, DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<CloudDirectoryMember>? members = await CloudDirectoryMoveEvidence.AuthenticateAsync(transaction,
            proof, cancellationToken).ConfigureAwait(false);
        if (members is null)
        {
            throw new CloudNamespaceObservationUncertainException(true);
        }

        CloudPathHandleLease? parents = null;
        SafeFileHandle? guard = null;
        try
        {
            parents = CloudPathHandleLease.OpenParentChains(_syncRootPath, [path.FullPath, previousPath.FullPath]);
            guard = WindowsFileMetadata.Open(path.FullPath, preventDelete: true);
            DirectoryNativeObservation target = CloudDirectoryMoveEvidence.Capture(guard.DangerousGetHandle(), _syncRootPath);
            if (!CloudDirectoryNativeValidation.MatchesProof(target, proof) ||
                !string.Equals(Path.GetRelativePath(_syncRootPath, target.ActualPath), proof.DestinationRelativePath, StringComparison.Ordinal))
            {
                throw new CloudNamespaceObservationUncertainException(true);
            }

            try
            {
                using SafeFileHandle sourceHandle = WindowsFileMetadata.Open(previousPath.FullPath);
                DirectoryNativeObservation source = CloudDirectoryMoveEvidence.Capture(sourceHandle.DangerousGetHandle(), _syncRootPath);
                if (!CloudDirectoryNativeValidation.MatchesProof(source, proof) ||
                    string.Equals(Path.GetRelativePath(_syncRootPath, source.ActualPath), proof.SourceRelativePath, StringComparison.Ordinal))
                {
                    throw new CloudNamespaceObservationUncertainException(true);
                }
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode is 2 or 3)
            {
                // An absent source is expected after a rename; the retained binding proves origin.
            }

            VerifiedDirectoryProjection projection = await CloudDirectoryStateProjection.ProjectVerifiedAsync(transaction,
                proof, members, observedAt, cancellationToken).ConfigureAwait(false);
            var children = CloudDirectoryNativeValidation.ObserveMembers(_syncRootPath, proof, members, projection.CurrentItems);
            if (!projection.AlreadyCompleted)
            {
                await CloudDirectoryProvenance.ProjectPathsAsync(transaction, proof, members, projection.CurrentItems,
                    cancellationToken).ConfigureAwait(false);
            }

            DirectoryFeedProjection result = new(_syncRootPath, proof.RootItemId, guard, parents, target,
                children.Observations, children.RequiresRescan);
            guard = null;
            parents = null;
            return result;
        }
        catch (Exception exception) when (exception is Win32Exception or NativeFileException or InvalidOperationException or CloudDirectoryEvidenceUnavailableException)
        {
            // The caller disposes this transaction before persisting an unresolved observation.
            // Never commit partial projector writes after a failed native or membership check.
            throw new CloudNamespaceObservationUncertainException(true, exception);
        }
        finally
        {
            guard?.Dispose();
            parents?.Dispose();
        }
    }

    [SupportedOSPlatform("windows10.0.16299")]
    private sealed class DirectoryFeedProjection(string syncRootPath, Guid itemId, SafeFileHandle guard,
        CloudPathHandleLease parents, DirectoryNativeObservation expected,
        List<(string Path, NativeFileMetadata Metadata)> children, bool requiresRescan) : IDisposable
    {
        internal Guid ItemId => itemId;
        internal bool RequiresRescan => requiresRescan;

        internal void Validate()
        {
            try
            {
                CloudDirectoryNativeValidation.Validate(syncRootPath, guard, expected, children);
            }
            catch (Exception exception) when (exception is Win32Exception or NativeFileException or InvalidOperationException or CloudDirectoryEvidenceUnavailableException)
            {
                throw new CloudNamespaceObservationUncertainException(true, exception);
            }
        }

        public void Dispose()
        {
            guard.Dispose();
            parents.Dispose();
        }
    }
}

internal sealed class CloudNamespaceObservationUncertainException(bool isDirectory, Exception? inner = null)
    : IOException("The namespace observation requires full reconciliation.", inner)
{
    internal bool IsDirectory => isDirectory;
}
