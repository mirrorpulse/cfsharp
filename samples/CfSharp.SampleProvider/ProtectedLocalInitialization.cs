using System.Runtime.Versioning;
using System.Security.AccessControl;

using CfSharp;

// A compiled public-API example for hosts that own durable originals and permission policy.
// It adds no sample CLI command or policy and performs no source/provider/network operation.
[SupportedOSPlatform("windows10.0.16299")]
internal static class ProtectedLocalInitialization
{
    internal static async ValueTask<CloudProtectedLocalOperationResult> RunAsync(
        CloudFile file,
        CloudLocalFileBinding originalBinding,
        CloudPlaceholderIdentity localIdentity,
        Func<CloudLocalFileBinding, FileSecurity, CancellationToken, ValueTask> persistOriginals,
        Func<FileSecurity, FileSecurity> createDesiredAccess,
        Func<FileSecurity, CancellationToken, ValueTask<bool>> verifyAccess,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(originalBinding);
        ArgumentNullException.ThrowIfNull(localIdentity);
        ArgumentNullException.ThrowIfNull(persistOriginals);
        ArgumentNullException.ThrowIfNull(createDesiredAccess);
        ArgumentNullException.ThrowIfNull(verifyAccess);
        return await file.RunProtectedLocalOperationAsync(new(originalBinding), async (scope, stop) =>
        {
            FileSecurity originalAccess = (FileSecurity)await scope.ReadAccessDescriptorAsync(stop);
            // The host must durably serialize its intent, original complete binding and DACL
            // outside the sync root before this delegate returns. Never retain only a reference
            // to the mutable descriptor or put original ACL/policy data in the CfSharp store.
            await persistOriginals(originalBinding, originalAccess, stop);
            await scope.ConvertToPlaceholderAsync(localIdentity, stop);
            FileSecurity desired = createDesiredAccess(originalAccess);
            FileSecurity readback = (FileSecurity)await scope.ApplyAccessDescriptorAsync(desired, stop);
            if (!await verifyAccess(readback, stop))
            {
                throw new InvalidOperationException("Host permission verification failed; retain originals for recovery.");
            }
            // This receipt proves no tree readiness or remote acceptance. The host's separate
            // verification and recovery ledger decides what may happen after scope release.
        }, cancellationToken);
    }
}
