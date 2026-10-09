using System.ComponentModel;
using System.Runtime.InteropServices;

internal static class ProtectedLocalCompetitor
{
    internal static int Run(string path, string eventName, string operation, string target)
    {
        using EventWaitHandle ready = EventWaitHandle.OpenExisting(eventName);
        using EventWaitHandle go = EventWaitHandle.OpenExisting(eventName + "-go");
        using EventWaitHandle attempted = EventWaitHandle.OpenExisting(eventName + "-attempted");
        ready.Set();
        if (!go.WaitOne(TimeSpan.FromSeconds(15)))
        {
            return 2;
        }

        try
        {
            attempted.Set();
            switch (operation)
            {
                case "overwrite":
                    File.WriteAllText(path, "replacement");
                    break;
                case "rename":
                    File.Move(path, target);
                    break;
                case "replace":
                    File.Move(target, path, overwrite: true);
                    break;
                case "link":
                    if (!CreateHardLink(target, path, 0))
                    {
                        return Marshal.GetLastPInvokeError();
                    }
                    break;
                default:
                    return 2;
            }

            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return exception.HResult & 0xffff;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string link, string existing, nint security);
}
