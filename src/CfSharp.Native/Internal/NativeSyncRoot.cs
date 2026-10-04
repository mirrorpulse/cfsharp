using System.Runtime.Versioning;

namespace CfSharp.Native;

[SupportedOSPlatform("windows10.0.16299")]
internal static unsafe class NativeSyncRoot
{
    private const int MaxSyncRootIdentityLength = 64 * 1024;
    private const int MoreDataHResult = unchecked((int)0x800700EA);

    internal static int Register(
        string path,
        string providerName,
        string providerVersion,
        Guid providerId,
        ReadOnlySpan<byte> syncRootIdentity,
        ReadOnlySpan<byte> fileIdentity,
        CfSyncPolicies policies,
        CfRegisterFlags flags)
    {
        fixed (char* pathPointer = path)
        fixed (char* providerNamePointer = providerName)
        fixed (char* providerVersionPointer = providerVersion)
        fixed (byte* syncRootIdentityPointer = syncRootIdentity)
        fixed (byte* fileIdentityPointer = fileIdentity)
        {
            CfSyncRegistration registration = new()
            {
                StructSize = (uint)sizeof(CfSyncRegistration),
                ProviderName = providerNamePointer,
                ProviderVersion = providerVersionPointer,
                SyncRootIdentity = syncRootIdentityPointer,
                SyncRootIdentityLength = (uint)syncRootIdentity.Length,
                FileIdentity = fileIdentityPointer,
                FileIdentityLength = (uint)fileIdentity.Length,
                ProviderId = providerId,
            };

            return CfApi.CfRegisterSyncRoot(pathPointer, &registration, &policies, flags);
        }
    }

    internal static int Query(string path, out NativeSyncRootInfo? info) => Query(path, 0, out info);

    internal static int Query(nint fileHandle, out NativeSyncRootInfo? info) => Query(null, fileHandle, out info);

    private static int Query(string? path, nint fileHandle, out NativeSyncRootInfo? info)
    {
        CfSyncRootStandardInfo layout = default;
        int identityOffset = checked((int)(layout.SyncRootIdentity - (byte*)&layout));
        int bufferLength = checked(identityOffset + 1024);
        while (true)
        {
            byte[] buffer = GC.AllocateUninitializedArray<byte>(bufferLength);
            fixed (char* pathPointer = path)
            fixed (byte* bufferPointer = buffer)
            {
                uint returnedLength = 0;
                int result = path is null
                    ? CfApi.CfGetSyncRootInfoByHandle(fileHandle, CfSyncRootInfoClass.Standard,
                        bufferPointer, (uint)buffer.Length, &returnedLength)
                    : CfApi.CfGetSyncRootInfoByPath(pathPointer, CfSyncRootInfoClass.Standard,
                        bufferPointer, (uint)buffer.Length, &returnedLength);
                if (result == MoreDataHResult)
                {
                    if (returnedLength <= buffer.Length || returnedLength > identityOffset + MaxSyncRootIdentityLength)
                    {
                        throw new InvalidDataException(
                            "Windows returned an invalid sync-root information size.");
                    }

                    bufferLength = checked((int)returnedLength);
                    continue;
                }

                if (result < 0)
                {
                    info = null;
                    return result;
                }

                CfSyncRootStandardInfo* nativeInfo = (CfSyncRootStandardInfo*)bufferPointer;
                if (returnedLength < identityOffset ||
                    nativeInfo->SyncRootIdentityLength > MaxSyncRootIdentityLength)
                {
                    throw new InvalidDataException(
                        "Windows returned an invalid variable-length sync-root identity.");
                }

                int identityLength = checked((int)nativeInfo->SyncRootIdentityLength);
                int requiredLength = checked(identityOffset + identityLength);
                if (requiredLength > buffer.Length || requiredLength > returnedLength)
                {
                    throw new InvalidDataException(
                        "Windows returned an invalid variable-length sync-root identity.");
                }

                info = new NativeSyncRootInfo(
                    nativeInfo->SyncRootFileId,
                    nativeInfo->HydrationPolicy,
                    nativeInfo->PopulationPolicy,
                    nativeInfo->InSyncPolicy,
                    nativeInfo->HardLinkPolicy,
                    nativeInfo->ProviderStatus,
                    ReadFixedString(nativeInfo->ProviderName, CfApi.MaxProviderNameLength + 1),
                    ReadFixedString(nativeInfo->ProviderVersion, CfApi.MaxProviderVersionLength + 1),
                    new ReadOnlySpan<byte>(nativeInfo->SyncRootIdentity, identityLength).ToArray());
                return result;
            }
        }
    }

    internal static string ReadFixedString(char* value, int capacity)
    {
        ReadOnlySpan<char> span = new(value, capacity);
        int terminator = span.IndexOf('\0');
        if (terminator < 0)
        {
            throw new InvalidDataException("Windows returned an unterminated sync-root string.");
        }

        return new string(span[..terminator]);
    }

    internal static int Unregister(string path)
    {
        fixed (char* pathPointer = path)
        {
            return CfApi.CfUnregisterSyncRoot(pathPointer);
        }
    }
}

internal sealed record NativeSyncRootInfo(
    long FileId,
    CfHydrationPolicy HydrationPolicy,
    CfPopulationPolicy PopulationPolicy,
    CfInSyncPolicy InSyncPolicy,
    CfHardLinkPolicy HardLinkPolicy,
    CfSyncProviderStatus ProviderStatus,
    string ProviderName,
    string ProviderVersion,
    byte[] SyncRootIdentity);
