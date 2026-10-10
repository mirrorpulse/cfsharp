using CfSharp.Native;
using Microsoft.Win32.SafeHandles;

namespace CfSharp;

internal static class CloudLocalAccessObject
{
    internal static SafeFileHandle Open(string path, bool directory = false) =>
        WindowsFileMetadata.OpenLocalAccess(path, directory);
}
