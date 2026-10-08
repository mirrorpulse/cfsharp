using System.ComponentModel;
using System.Runtime.Versioning;

using CfSharp.Native;

using Microsoft.Win32.SafeHandles;

namespace CfSharp;

[SupportedOSPlatform("windows10.0.16299")]
internal static class CloudDirectoryNativeValidation
{
    internal static bool MatchesProof(DirectoryNativeObservation observation, CloudDirectoryMoveProof proof) =>
        observation.Binding == proof.ExpectedBinding && observation.Identity.AsSpan().SequenceEqual(proof.ExpectedPlaceholderIdentity.Span);

    internal static (List<(string Path, NativeFileMetadata Metadata)> Observations, bool RequiresRescan) ObserveMembers(
        string syncRootPath, CloudDirectoryMoveProof proof, IReadOnlyList<CloudDirectoryMember> members, IReadOnlyList<CloudItemState> current)
    {
        Dictionary<Guid, CloudDirectoryMember> known = members.ToDictionary(member => member.ItemId);
        List<(string Path, NativeFileMetadata Metadata)> observations = [];
        bool requiresRescan = false;
        foreach (CloudItemState item in current.Where(item => item.ItemId != proof.RootItemId))
        {
            string path = Path.Combine(syncRootPath, CloudDirectoryStateProjection.MemberPath(proof.DestinationRelativePath, known[item.ItemId].Suffix));
            using CloudPathHandleLease parents = CloudPathHandleLease.OpenParentChains(syncRootPath, [path]);
            NativeFileMetadata metadata;
            try
            {
                using SafeFileHandle handle = WindowsFileMetadata.Open(path);
                metadata = WindowsFileMetadata.Read(handle.DangerousGetHandle());
            }
            catch (Win32Exception exception) when (item.IsTombstone && exception.NativeErrorCode is 2 or 3)
            {
                continue;
            }

            if (item.IsTombstone)
            {
                requiresRescan = true;
                continue;
            }

            if (metadata.DeletePending || metadata.Directory != (item.Kind == CloudItemKind.Directory) ||
                (metadata.PlaceholderIdentity.Length != 0 && CloudPlaceholderIdentity.Decode(metadata.PlaceholderIdentity).ItemId != item.ItemId) ||
                (metadata.PlaceholderIdentity.Length == 0 && !item.RemoteId.StartsWith("local:", StringComparison.Ordinal)))
            {
                throw new CloudDirectoryProjectionConflictException("A native descendant no longer matches its known durable membership.");
            }

            requiresRescan |= metadata.PlaceholderIdentity.Length == 0;
            observations.Add((path, metadata));
        }

        return (observations, requiresRescan);
    }

    internal static void Validate(string syncRootPath, SafeFileHandle guard, DirectoryNativeObservation expected,
        List<(string Path, NativeFileMetadata Metadata)> children)
    {
        if (!expected.Matches(CloudDirectoryMoveEvidence.Capture(guard.DangerousGetHandle(), syncRootPath)))
        {
            throw new CloudDirectoryProjectionConflictException("The native directory changed during projection.");
        }

        foreach ((string path, NativeFileMetadata original) in children)
        {
            using CloudPathHandleLease parents = CloudPathHandleLease.OpenParentChains(syncRootPath, [path]);
            using SafeFileHandle handle = WindowsFileMetadata.Open(path);
            NativeFileMetadata actual = WindowsFileMetadata.Read(handle.DangerousGetHandle());
            if (original.Identity.VolumeSerialNumber != actual.Identity.VolumeSerialNumber || original.Identity.FileId != actual.Identity.FileId ||
                original.Directory != actual.Directory || actual.DeletePending ||
                !original.PlaceholderIdentity.AsSpan().SequenceEqual(actual.PlaceholderIdentity))
            {
                throw new CloudDirectoryProjectionConflictException("A native descendant changed during projection; reconcile the retained journal.");
            }
        }
    }
}
