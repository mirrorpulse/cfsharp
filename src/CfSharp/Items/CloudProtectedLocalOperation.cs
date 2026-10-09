namespace CfSharp;

/// <summary>Selects the protection provided for one local object.</summary>
public enum CloudProtectedLocalOperationMode
{
    /// <summary>Opens one file with exclusive Windows sharing, rejecting data and namespace competitors.</summary>
    /// <remarks>Ordinary files have no hardlink freeze. Opening requires read/write data and DACL access; no content is read.</remarks>
    ExclusiveFile,
    /// <summary>Pins one directory for metadata and DACL work, permitting child membership changes.</summary>
    /// <remarks>This provides no directory content, enumeration, child creation or tree freeze.</remarks>
    DirectoryMetadata,
}

/// <summary>Identifies the last work phase of a protected local operation.</summary>
public enum CloudProtectedLocalOperationStage
{
    /// <summary>Acquiring facade path admission and store ownership.</summary>
    Admission,
    /// <summary>Opening the original native file object.</summary>
    Open,
    /// <summary>Inspecting its current binding and native metadata.</summary>
    Inspection,
    /// <summary>Preparing native placeholder identity without synchronization acceptance.</summary>
    NativePreparation,
    /// <summary>Committing official item state.</summary>
    Projection,
    /// <summary>Awaiting the application callback.</summary>
    Callback,
    /// <summary>Reading or applying an Access-only descriptor.</summary>
    AccessDescriptor,
    /// <summary>Rejecting new scope work while retaining resources for admitted work.</summary>
    Draining,
    /// <summary>All callback work drained and native resources released.</summary>
    Released,
}

/// <summary>Classifies a protected operation without claiming product readiness or remote acceptance.</summary>
public enum CloudProtectedLocalOperationOutcome
{
    /// <summary>The callback and all admitted scope work completed successfully.</summary>
    Completed,
    /// <summary>Windows sharing rejected admission, including an existing writer or writable mapping.</summary>
    Busy,
    /// <summary>The requested mode or native capability is unavailable.</summary>
    Unsupported,
    /// <summary>The observed original object does not match the caller's binding.</summary>
    LocalObjectMismatch,
    /// <summary>The object has unsupported aliases, reparse data or deletion state.</summary>
    NotApplicable,
    /// <summary>Native protection or its validated object can no longer be used.</summary>
    ProtectionLost,
    /// <summary>Native preparation succeeded, but required durable projection needs recovery.</summary>
    NativeAppliedProjectionPending,
    /// <summary>The application callback failed; preceding native facts remain valid receipts.</summary>
    CallbackFailed,
    /// <summary>The caller or owner requested cancellation; actual admitted work has drained.</summary>
    Canceled,
    /// <summary>The short operation budget expired; actual admitted work has drained.</summary>
    DeadlineExceeded,
    /// <summary>An unclassified native or durable operation failed; inspect the error and stage.</summary>
    Failed,
}

/// <summary>Specifies the original object and protection mode for one awaited local callback.</summary>
/// <remarks>
/// Immutable and safe for concurrent reads. Persist the original binding before calling; a newly
/// captured replacement binding does not establish historical ownership. Windows 10 version 1709
/// or later and an already started facade are required. This contains no owning or borrowed handle.
/// </remarks>
public sealed partial class CloudProtectedLocalOperationRequest
{
    /// <summary>Creates a request for a previously captured native object.</summary>
    /// <param name="expectedBinding">Original complete volume, sync-root and file binding.</param>
    /// <param name="mode">Exclusive single-file protection or directory metadata access.</param>
    /// <exception cref="ArgumentNullException">The binding is null.</exception>
    /// <exception cref="ArgumentException">The mode is undefined.</exception>
    public CloudProtectedLocalOperationRequest(CloudLocalFileBinding expectedBinding,
        CloudProtectedLocalOperationMode mode = CloudProtectedLocalOperationMode.ExclusiveFile)
    {
        ArgumentNullException.ThrowIfNull(expectedBinding);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentException("Select a defined protection mode.", nameof(mode));
        }
        ExpectedBinding = expectedBinding;
        Mode = mode;
    }

    /// <summary>Gets the original binding that must match before the callback begins.</summary>
    public CloudLocalFileBinding ExpectedBinding { get; }
    /// <summary>Gets the requested single-object protection.</summary>
    public CloudProtectedLocalOperationMode Mode { get; }
}

/// <summary>Records immutable facts after a protected operation and its admitted work have drained.</summary>
/// <remarks>
/// Safe for concurrent reads; owns no native or database resource. Callback completion never means
/// independent MP permission verification, whole-tree readiness, upload acceptance or journal ACK.
/// The stage identifies the last work phase, even though resources have been released on return.
/// </remarks>
public sealed partial class CloudProtectedLocalOperationResult
{
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.16299")]
    internal CloudProtectedLocalOperationResult(CloudProtectedLocalOperationOutcome outcome,
        CloudProtectedLocalOperationStage stage, CloudLocalFileBinding expected,
        CloudItemSnapshot? snapshot, bool callbackStarted, bool callbackCompleted,
        TimeSpan elapsed, Exception? error, CloudProtectedLocalOperationContext? context)
    {
        Outcome = outcome;
        Stage = stage;
        ExpectedBinding = expected;
        Snapshot = snapshot;
        CallbackStarted = callbackStarted;
        CallbackCompleted = callbackCompleted;
        Elapsed = elapsed;
        Error = error;
        NativeIdentityPrepared = context?.NativeIdentityPrepared ?? false;
        NativeConverted = context?.NativeConverted ?? false;
        DurableProjectionCommitted = context?.DurableProjectionCommitted ?? false;
        PreparationHResult = context?.PreparationHResult;
        PreparationUsn = context?.PreparationUsn;
        AccessDescriptorApplied = context?.AccessDescriptorApplied ?? false;
        AccessDescriptorReadBack = context?.AccessDescriptorReadBack ?? false;
    }

    /// <summary>Gets the outcome without any synchronization or product-readiness claim.</summary>
    public CloudProtectedLocalOperationOutcome Outcome { get; }
    /// <summary>Gets the last work phase, retaining the failure location.</summary>
    public CloudProtectedLocalOperationStage Stage { get; }
    /// <summary>Gets the caller's original object binding.</summary>
    public CloudLocalFileBinding ExpectedBinding { get; }
    /// <summary>Gets the last successful same-object snapshot, or null before inspection succeeded.</summary>
    /// <remarks>A receipt snapshot is historical; inspect again in another protected operation before future changes.</remarks>
    public CloudItemSnapshot? Snapshot { get; }
    /// <summary>Gets whether the application callback was invoked.</summary>
    public bool CallbackStarted { get; }
    /// <summary>Gets whether the application callback returned normally.</summary>
    public bool CallbackCompleted { get; }
    /// <summary>Gets elapsed time including admission, callback completion and draining.</summary>
    public TimeSpan Elapsed { get; }
    /// <summary>Gets the original callback error or a translated native error preserving its HRESULT.</summary>
    public Exception? Error { get; }
    /// <summary>Gets whether the exact native identity was observed under protection.</summary>
    /// <remarks>This includes retry of an already prepared native object, and implies no content acceptance.</remarks>
    public bool NativeIdentityPrepared { get; }
    /// <summary>Gets whether this operation performed native conversion; false on an already prepared retry.</summary>
    public bool NativeConverted { get; }
    /// <summary>Gets whether required official identity projection actually committed, independently of later errors.</summary>
    public bool DurableProjectionCommitted { get; }
    /// <summary>Gets the conversion HRESULT, or null when no conversion call completed successfully.</summary>
    public int? PreparationHResult { get; }
    /// <summary>Gets the USN returned by this operation's native conversion, or null when it did not convert.</summary>
    public long? PreparationUsn { get; }
    /// <summary>Gets whether a DACL application succeeded on the original native object, independently of later failures.</summary>
    public bool AccessDescriptorApplied { get; }
    /// <summary>Gets whether native readback completed after a DACL application.</summary>
    /// <remarks>This is a library readback fact, not independent policy verification or product readiness.</remarks>
    public bool AccessDescriptorReadBack { get; }
    /// <summary>Gets whether all admitted work drained before native and facade resources were released.</summary>
    public bool Drained { get; } = true;
}

public abstract partial class CloudItem
{
    /// <summary>Awaits a short protected local operation on the caller's original native object.</summary>
    /// <param name="request">Previously captured binding and requested protection.</param>
    /// <param name="callback">Application work awaited to its actual completion; keep it local and short.</param>
    /// <param name="cancellationToken">Requests cancellation and stops new scope work without aborting callback draining.</param>
    /// <returns>A receipt after the callback, admitted scope operations and resources have drained.</returns>
    /// <remarks>
    /// The library retains one owned Windows file object, path admission and the state-store lifetime
    /// across callback awaits. File share-none rejects conflicting data and namespace opens; it does
    /// not depend on an automatically invalidatable CFAPI opaque handle. Directory mode provides
    /// metadata and DACL access only. Neither mode freezes tree membership or ordinary-file aliases.
    /// Use the context for same-object inspection. Unsupported nested facade operations and owner
    /// disposal from this callback fail immediately to avoid self-waiting. Escaped contexts reject
    /// work after callback exit. An uncooperative callback or native driver may delay cancellation;
    /// no native resource is released while admitted work can still use it. No SQLite transaction
    /// remains open around application code. No source content, remote credentials or network work
    /// is supplied, and this API does not mark in sync or ACK journal entries.
    /// </remarks>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="InvalidOperationException">The owner has not started, or the call reenters another protected callback.</exception>
    /// <exception cref="ObjectDisposedException">The owner is stopping or disposed before admission.</exception>
    public ValueTask<CloudProtectedLocalOperationResult> RunProtectedLocalOperationAsync(
        CloudProtectedLocalOperationRequest request,
        Func<CloudProtectedLocalOperationContext, CancellationToken, ValueTask> callback,
        CancellationToken cancellationToken = default) =>
        Owner.RunProtectedLocalOperationAsync(this, request, callback, cancellationToken);
}
