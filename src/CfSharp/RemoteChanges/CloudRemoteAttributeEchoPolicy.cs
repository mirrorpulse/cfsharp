namespace CfSharp;

/// <summary>Specifies how independent native attribute notifications following remote metadata are attributed.</summary>
/// <remarks>
/// Windows attribute notifications contain an object identity but no per-operation writer identity.
/// This policy does not provide native exclusion, acquire locks in other processes, or change data,
/// security-descriptor or name notification handling. It applies only when
/// <see cref="CloudRemoteApplyOptions.SuppressLocalEcho"/> is enabled.
/// </remarks>
public enum CloudRemoteAttributeEchoPolicy
{
    /// <summary>Retain possible echoes as unbound metadata observations and require explicit reconciliation.</summary>
    /// <remarks>
    /// This is the default. Reconciliation preserves each original journal ID for individual acknowledgement.
    /// A path, in-sync flag, timestamp or USN cannot by itself authorize discarding an attribute notification.
    /// </remarks>
    RequireReconciliation,

    /// <summary>Allow suppression under an explicit caller guarantee that the notifications are provider-owned.</summary>
    /// <remarks>
    /// The caller must coordinate every writer capable of producing attribute notifications for the affected
    /// objects, including deferred content writes and renames, and account for notifications already queued
    /// before application. The guarantee must hold from before calling
    /// <see cref="CloudFileSystem.ApplyRemoteChangesAsync"/> until at least
    /// <see cref="CloudRemoteApplyOptions.EchoSuppressionLifetime"/> after it returns successfully.
    /// Do not select this value for a root with uncontrolled applications or native writers.
    /// This is a provenance declaration, not a Windows isolation primitive. Security and data notifications
    /// remain observable. Failed calls, incomplete preparations, changed native identities and a new
    /// file-system owner never inherit automatic suppression authority; they require reconciliation.
    /// </remarks>
    CallerCoordinated,
}
