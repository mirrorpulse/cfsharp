using System.Runtime.Versioning;

namespace CfSharp.Native;

[SupportedOSPlatform("windows10.0.16299")]
internal static unsafe class ProtectedFileMutations
{
    internal static (int HResult, long Usn) Prepare(nint handle, ReadOnlySpan<byte> identity, bool convert)
    {
        fixed (byte* pointer = identity)
        {
            long usn = 0;
            int result = convert
                ? CfApi.CfConvertToPlaceholder(handle, pointer, (uint)identity.Length,
                    CfConvertFlags.None, &usn, null)
                : CfApi.CfUpdatePlaceholder(handle, null, pointer, (uint)identity.Length,
                    null, 0, CfUpdateFlags.ClearInSync, &usn, null);
            if (result < 0)
            {
                throw new NativeFileException(convert ? "CfConvertToPlaceholder" : "CfUpdatePlaceholder", result);
            }

            return (result, usn);
        }
    }

    internal static int Mark(nint handle)
    {
        // NULL is intentionally native-unconditional, not USN CAS. The caller must retain
        // exclusive protection over the same object from final proof checks through this call.
        int result = CfApi.CfSetInSyncState(handle, CfInSyncState.InSync, CfSetInSyncFlags.None, null);
        if (result < 0)
        {
            throw new NativeFileException("CfSetInSyncState", result);
        }

        return result;
    }
}
