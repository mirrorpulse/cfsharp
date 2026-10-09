using System.Security.AccessControl;
using System.Security.Principal;

using CfSharp.Native;
using Microsoft.Win32.SafeHandles;

namespace CfSharp;

public sealed partial class CloudProtectedLocalOperationContext
{
    internal bool AccessDescriptorApplied { get; private set; }
    internal bool AccessDescriptorReadBack { get; private set; }

    /// <summary>Reads the original protected object's DACL into an independent .NET descriptor.</summary>
    /// <param name="cancellationToken">Token observed before this serialized scope step.</param>
    /// <returns>A FileSecurity or DirectorySecurity matching the object, containing only Access sections.</returns>
    /// <remarks>
    /// Reads metadata through the library-owned native file object without hydration, path reopening
    /// or source access. No owner, group or SACL is requested. The returned managed copy may outlive
    /// the scope but does not retain its protection. Ordinary files can be read without conversion;
    /// writing their DACL requires local placeholder preparation to exclude new aliases.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The scope has exited.</exception>
    /// <exception cref="OperationCanceledException">Scope or step cancellation was requested.</exception>
    /// <exception cref="UnauthorizedAccessException">Windows denies descriptor access.</exception>
    /// <exception cref="IOException">The protected object or its native metadata cannot be verified.</exception>
    public ValueTask<FileSystemSecurity> ReadAccessDescriptorAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            SetStage(CloudProtectedLocalOperationStage.AccessDescriptor);
            ValidateObject();
            FileSystemSecurity descriptor = ReadAccessCore();
            ValidateObject();
            return ValueTask.FromResult(descriptor);
        }, cancellationToken);

    /// <summary>Applies only the supplied DACL to the original object and returns a fresh native readback.</summary>
    /// <param name="descriptor">FileSecurity or DirectorySecurity matching the object; only Access is applied.</param>
    /// <param name="cancellationToken">Checked before applying the DACL; readback after application is not interrupted.</param>
    /// <returns>An independent Access-only managed descriptor read from the same native object after application.</returns>
    /// <remarks>
    /// Copies the input at admission so later caller changes do not affect the queued application.
    /// The caller must synchronize concurrent input edits while this copy is taken. Files must be
    /// placeholders under the actual disallowed-hardlink root policy, with no existing aliases.
    /// Use a preparation request to convert an ordinary file before callback entry. A failed
    /// required identity projection must recover before any later file DACL application.
    /// Directory mode provides metadata access only: Windows inheritance may affect descendants; membership and
    /// their data are not frozen. No owner, group or SACL is written. The receipt records application
    /// separately from readback; a readback is not independent permission verification or product
    /// readiness. The application owns original DACL evidence, policy, verification and recovery.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The descriptor is null.</exception>
    /// <exception cref="ArgumentException">The descriptor kind does not match the protected object.</exception>
    /// <exception cref="ObjectDisposedException">The scope has exited.</exception>
    /// <exception cref="OperationCanceledException">Cancellation occurred before application.</exception>
    /// <exception cref="UnauthorizedAccessException">Windows denies DACL application or readback.</exception>
    /// <exception cref="IOException">The object lacks required protection or native verification fails.</exception>
    public ValueTask<FileSystemSecurity> ApplyAccessDescriptorAsync(FileSystemSecurity descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        bool directory = _item.Kind == CloudItemKind.Directory;
        if (directory ? descriptor is not DirectorySecurity : descriptor is not FileSecurity)
        {
            throw new ArgumentException("The Access descriptor must match the protected object's file-system kind.", nameof(descriptor));
        }
        byte[] copy = descriptor.GetSecurityDescriptorBinaryForm();
        return ExecuteAsync(token =>
        {
            SetStage(CloudProtectedLocalOperationStage.AccessDescriptor);
            NativeFileMetadata before = ValidateObject();
            if (!directory)
            {
                if ((NativeConverted || NativeIdentityPrepared) && !DurableProjectionCommitted)
                {
                    throw new CloudProtectedLocalRejectedException(CloudProtectedLocalOperationOutcome.NativeAppliedProjectionPending,
                        "Recover the required official identity projection before applying file permissions.");
                }
                RequireHardlinkPolicy();
                if (!before.PlaceholderState.HasFlag(CfPlaceholderState.Placeholder))
                {
                    throw new CloudProtectedLocalRejectedException(CloudProtectedLocalOperationOutcome.Unsupported,
                        "File DACL application requires same-object local placeholder preparation before permissions change.");
                }
            }
            LocalAccessDescriptor access = new(_handle, directory);
            access.SetSecurityDescriptorBinaryForm(copy, AccessControlSections.Access);
            ThrowIfCancellationRequested(cancellationToken);
            token.ThrowIfCancellationRequested();
            access.Apply(_handle);
            AccessDescriptorApplied = true;
            // Once application succeeded, preserve actual readback facts even if cancellation
            // arrives. Never release this native object while this admitted step is using it.
            ValidateObject();
            FileSystemSecurity observed = ReadAccessCore();
            AccessDescriptorReadBack = true;
            ValidateObject();
            return ValueTask.FromResult(observed);
        }, cancellationToken);
    }

    private FileSystemSecurity ReadAccessCore()
    {
        bool directory = _item.Kind == CloudItemKind.Directory;
        LocalAccessDescriptor native = new(_handle, directory);
        FileSystemSecurity copy = directory ? new DirectorySecurity() : new FileSecurity();
        // Expose the BCL descriptor, preserving inherited ACE facts through binary copying.
        // The private NativeObjectSecurity adapter never exposes rule-factory reconstructions.
        copy.SetSecurityDescriptorBinaryForm(native.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        return copy;
    }

    private sealed class LocalAccessDescriptor : NativeObjectSecurity
    {
        internal LocalAccessDescriptor(SafeFileHandle handle, bool directory)
            : base(directory, ResourceType.FileObject, handle, AccessControlSections.Access) { }

        internal void Apply(SafeFileHandle handle) => Persist(handle, AccessControlSections.Access);
        public override Type AccessRightType => typeof(FileSystemRights);
        public override Type AccessRuleType => typeof(FileSystemAccessRule);
        public override Type AuditRuleType => typeof(FileSystemAuditRule);
        public override AccessRule AccessRuleFactory(IdentityReference identityReference, int accessMask, bool isInherited,
            InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AccessControlType type) =>
            new FileSystemAccessRule(identityReference, (FileSystemRights)accessMask, inheritanceFlags, propagationFlags, type);
        public override AuditRule AuditRuleFactory(IdentityReference identityReference, int accessMask, bool isInherited,
            InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AuditFlags flags) =>
            new FileSystemAuditRule(identityReference, (FileSystemRights)accessMask, inheritanceFlags, propagationFlags, flags);
    }
}
