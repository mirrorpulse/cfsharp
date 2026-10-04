namespace CfSharp;

public sealed partial class CloudFile
{
    /// <summary>Verifies remotely accepted content and confirms the same protected local file object.</summary>
    /// <param name="request">Copied upload-time object binding, length, full hash, and accepted identity.</param>
    /// <param name="cancellationToken">Cancellation observed during lease admission, before marking, and while draining reads.</param>
    /// <returns>A typed outcome and receipt retaining native preparation, commit, and projection facts.</returns>
    /// <remarks>
    /// <para>
    /// Requires a started owner on Windows 10 version 1709 or later. Operations on this path are
    /// coordinated with existing item operations and shutdown. Fully local files are read through
    /// exclusive CFAPI protection in bounded segments; partial content is rejected without hydration.
    /// No remote calls, user callbacks, native handles, or proof hashes enter the official store.
    /// </para>
    /// <para>
    /// The final native mark uses a null USN pointer and is therefore native-unconditional. Safety
    /// comes from complete verification and exclusive protection of the same file object through
    /// marking, not USN CAS. Existing conditional APIs and their positive-token checks are unchanged.
    /// Identity preparation is opt-in and guarded before mutation; its success is retained even if
    /// later confirmation fails. Native confirmation and durable projection are separate commits.
    /// </para>
    /// <para>
    /// After a successful native mark, cancellation cannot report that nothing happened. Projection
    /// completes without caller cancellation or returns a recoverable receipt. Replay the same proof
    /// to repair a pending projection; each replay verifies the entire content, including already
    /// in-sync objects. This operation does not acknowledge change-feed or journal entries. A later
    /// write remains a new local change. Retain the request durably in the caller before invoking.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">The request is null.</exception>
    /// <exception cref="ObjectDisposedException">The owning file system is stopping or disposed.</exception>
    /// <exception cref="InvalidOperationException">The owning file system has not started.</exception>
    public ValueTask<CloudContentConfirmationResult> ConfirmUploadedContentAsync(
        CloudContentConfirmationRequest request, CancellationToken cancellationToken = default) =>
        Owner.ConfirmUploadedContentAsync(this, request, cancellationToken);
}
