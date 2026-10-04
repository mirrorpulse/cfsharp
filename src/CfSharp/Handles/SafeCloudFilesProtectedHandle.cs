using System.Runtime.Versioning;

using CfSharp.Native;

using Microsoft.Win32.SafeHandles;

namespace CfSharp;

[SupportedOSPlatform("windows10.0.16299")]
internal sealed class SafeCloudFilesProtectedHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SafeCloudFilesProtectedHandle()
        : base(ownsHandle: true)
    {
    }

    [SupportedOSPlatform("windows10.0.16299")]
    internal static unsafe SafeCloudFilesProtectedHandle Open(
        string path,
        CfOpenFileFlags flags,
        string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        fixed (char* pathPointer = path)
        {
            int result = CfApi.CfOpenFileWithOplock(pathPointer, flags, out nint handle);
            if (result < 0)
            {
                throw CloudFilesException.FromHResult(operation, path, result);
            }

            SafeCloudFilesProtectedHandle protectedHandle = new();
            protectedHandle.SetHandle(handle);
            return protectedHandle;
        }
    }

    [SupportedOSPlatform("windows10.0.16299")]
    internal CloudFilesHandleReference AcquireReference()
    {
        bool addedSafeHandleReference = false;
        DangerousAddRef(ref addedSafeHandleReference);
        try
        {
            if (CfApi.CfReferenceProtectedHandle(handle) == 0)
            {
                throw new InvalidOperationException(
                    "The Cloud Files protected handle was invalidated before it could be referenced.");
            }

            nint win32Handle = CfApi.CfGetWin32HandleFromProtectedHandle(handle);
            if (win32Handle is 0 or -1)
            {
                CfApi.CfReleaseProtectedHandle(handle);
                throw new InvalidOperationException(
                    "Windows did not expose a valid Win32 handle for the protected handle.");
            }

            return new CloudFilesHandleReference(this, win32Handle);
        }
        catch
        {
            if (addedSafeHandleReference)
            {
                DangerousRelease();
            }

            throw;
        }
    }

    protected override bool ReleaseHandle()
    {
        CfApi.CfCloseHandle(handle);
        return true;
    }

    internal sealed class CloudFilesHandleReference : IDisposable
    {
        private SafeCloudFilesProtectedHandle? _owner;

        internal CloudFilesHandleReference(SafeCloudFilesProtectedHandle owner, nint win32Handle)
        {
            _owner = owner;
            Win32Handle = win32Handle;
        }

        internal nint Win32Handle { get; }

        // CFAPI understands the opaque owner, whereas general Win32 operations require
        // Win32Handle. Both identify the same file and remain alive for this reference.
        internal nint ProtectedHandle => (_owner ?? throw new ObjectDisposedException(nameof(CloudFilesHandleReference)))
            .DangerousGetHandle();

        public void Dispose()
        {
            SafeCloudFilesProtectedHandle? owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null)
            {
                return;
            }

            CfApi.CfReleaseProtectedHandle(owner.DangerousGetHandle());
            owner.DangerousRelease();
        }
    }
}
