using System.Runtime.CompilerServices;

namespace CfSharp;

internal static class LocalChangeStartupFailure
{
    internal static void Preserve(Exception original, Exception cleanup, bool source)
    {
        string key = source ? "CfSharp.LocalChangeSource.StartupCleanupFailure" : "CfSharp.LocalChangeFeed.StartupCleanupFailure";
        try { original.Data[key] = cleanup; }
        catch { /* A custom exception can provide a read-only data dictionary. */ }
        try { Report(cleanup, source); }
        catch { /* Optional diagnostics must preserve the original startup exception. */ }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Report(Exception cleanup, bool source)
    {
        // Keep optional diagnostic assembly resolution outside normal startup compilation,
        // including when an impersonated MSIX process cannot read an unloaded dependency.
        System.Diagnostics.Activity? activity = CloudDiagnostics.StartActivity(source
            ? "cfsharp.local-change-source.startup-cleanup" : "cfsharp.local-change-feed.startup-cleanup",
            source ? "local-change-source.startup" : "local-change-feed.startup");
        CloudDiagnostics.RecordException(activity, cleanup);
        CloudDiagnostics.StopActivity(activity, "failed");
    }
}
