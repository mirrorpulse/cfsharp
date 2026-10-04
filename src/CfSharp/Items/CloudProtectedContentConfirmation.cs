using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;

using CfSharp.Native;

using Microsoft.Win32.SafeHandles;

namespace CfSharp;

internal sealed record CloudProtectedFileFacts(CloudLocalFileBinding Binding, long Length,
    bool IsDirectory, bool IsPlaceholder, bool IsFullyLocal, bool IsSupported,
    bool InSync, byte[] Identity);

internal interface ICloudProtectedContentSession : IDisposable
{
    ICloudProtectedContentReference Reference();
}

internal interface ICloudProtectedContentReference : IDisposable
{
    CloudProtectedFileFacts Inspect();
    int Read(byte[] buffer, int count, long offset, TimeSpan budget, CancellationToken cancellationToken);
    (int HResult, long Usn) Prepare(CloudContentConfirmationRequest request, bool convert);
    int Mark();
}

[SupportedOSPlatform("windows10.0.16299")]
internal sealed class CloudProtectedContentSession : ICloudProtectedContentSession
{
    private readonly SafeCloudFilesProtectedHandle _handle;
    private readonly string _root;
    private readonly WindowsFileMetadata.FileIdentity _openedIdentity;

    internal CloudProtectedContentSession(string path, string root)
    {
        // Pin the final namespace object without delete sharing until the CFAPI open has
        // completed. OPEN_REPARSE_POINT lets us reject symlinks before CFAPI follows a path.
        using SafeFileHandle guard = WindowsFileMetadata.Open(path, preventDelete: true);
        NativeFileMetadata facts = WindowsFileMetadata.Read(guard.DangerousGetHandle());
        if (facts.Directory || facts.Links != 1 || facts.DeletePending ||
            (facts.Attributes & (uint)FileAttributes.ReparsePoint) != 0 &&
            !facts.PlaceholderState.HasFlag(CfPlaceholderState.Placeholder))
        {
            throw new NotSupportedException("Confirmation requires one regular file object or Cloud Files placeholder.");
        }

        _root = root;
        _openedIdentity = facts.Identity;
        _handle = SafeCloudFilesProtectedHandle.Open(path,
            CfOpenFileFlags.Exclusive | CfOpenFileFlags.WriteAccess, "CloudFile.ConfirmUploadedContent.Open");
    }

    public ICloudProtectedContentReference Reference() => new ReferenceOwner(_handle.AcquireReference(), _root, _openedIdentity);

    public void Dispose() => _handle.Dispose();

    private sealed class ReferenceOwner(SafeCloudFilesProtectedHandle.CloudFilesHandleReference reference,
        string root, WindowsFileMetadata.FileIdentity openedIdentity) : ICloudProtectedContentReference
    {
        public CloudProtectedFileFacts Inspect()
        {
            NativeFileMetadata facts = WindowsFileMetadata.Read(reference.Win32Handle, reference.ProtectedHandle);
            bool placeholder = facts.PlaceholderState.HasFlag(CfPlaceholderState.Placeholder);
            bool full = !placeholder || facts.Length == 0 ||
                !facts.PlaceholderState.HasFlag(CfPlaceholderState.Partial) &&
                !facts.PlaceholderState.HasFlag(CfPlaceholderState.PartiallyOnDisk) &&
                facts.OnDiskDataSize >= facts.Length;
            return new CloudProtectedFileFacts(CloudLocalFileBindingPlatform.Read(reference.Win32Handle, root),
                facts.Length, facts.Directory, placeholder, full,
                // A concurrent in-place reparse mutation during the guarded open must not
                // redirect CFAPI onto another object, even with a caller-supplied binding.
                facts.Identity.VolumeSerialNumber == openedIdentity.VolumeSerialNumber &&
                facts.Identity.FileId == openedIdentity.FileId && facts.Links == 1 && !facts.DeletePending &&
                ((facts.Attributes & (uint)FileAttributes.ReparsePoint) == 0 || placeholder),
                facts.InSync, facts.PlaceholderIdentity);
        }

        public int Read(byte[] buffer, int count, long offset, TimeSpan budget, CancellationToken cancellationToken) =>
            ProtectedFileReader.Read(reference.Win32Handle, buffer, count, offset, budget, cancellationToken);

        public (int HResult, long Usn) Prepare(CloudContentConfirmationRequest request, bool convert) =>
            ProtectedFileMutations.Prepare(reference.ProtectedHandle, request.EncodedIdentity, convert);

        public int Mark() => ProtectedFileMutations.Mark(reference.ProtectedHandle);

        public void Dispose() => reference.Dispose();
    }
}

internal static class CloudProtectedContentConfirmation
{
    // The injected session is an internal test seam, not an application callback. Production
    // retains one opaque owner for both verification passes and every short reference.
    internal static async Task<CloudContentConfirmationResult> RunAsync(
        Func<ICloudProtectedContentSession> open, CloudContentConfirmationRequest request,
        string path, long started, CancellationToken cancellationToken)
    {
        // Use the public call's monotonic origin, including lease admission and this yield.
        // Cancellation timers can be delayed by a busy pool and cannot define elapsed time.
        await Task.Yield();
        CloudContentConfirmationStage stage = CloudContentConfirmationStage.Open;
        bool prepared = false;
        bool applied = false;
        bool verified = false;
        bool already = false;
        long bytes = 0;
        int segments = 0;
        long? preparationUsn = null;
        int? preparationHResult = null;
        int? markHResult = null;
        TimeSpan longest = TimeSpan.Zero;
        CloudContentConfirmationResult Result(CloudContentConfirmationOutcome outcome, Exception? error = null) =>
            new(request, outcome, stage, prepared, applied, verified, false, bytes, segments,
                Stopwatch.GetElapsedTime(started), longest, preparationUsn, error,
                preparationHResult: preparationHResult, nativeMarkHResult: markHResult);
        void CheckBudget()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) >= request.Deadline)
            {
                throw new TimeoutException("The content confirmation deadline expired.");
            }
        }

        try
        {
            CheckBudget();
            using ICloudProtectedContentSession session = open();
            byte[] buffer = GC.AllocateUninitializedArray<byte>(request.SegmentSize);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            while (true)
            {
                CheckBudget();
                stage = CloudContentConfirmationStage.Reference;
                long referenceStarted = Stopwatch.GetTimestamp();
                try
                {
                    using ICloudProtectedContentReference reference = session.Reference();
                    stage = CloudContentConfirmationStage.Verify;
                    CloudProtectedFileFacts facts = reference.Inspect();
                    CloudContentConfirmationOutcome? rejected = Validate(facts, request, prepared);
                    if (rejected is not null)
                    {
                        throw new RejectedException(rejected.Value);
                    }

                    stage = CloudContentConfirmationStage.Read;
                    // Read one additional byte at the expected EOF. A short read contributes
                    // only its actual count; premature EOF or extra bytes cannot confirm.
                    int count = offset == request.ExpectedLength ? 1 :
                        (int)Math.Min(buffer.Length, request.ExpectedLength - offset);
                    TimeSpan remaining = request.Deadline - Stopwatch.GetElapsedTime(started);
                    TimeSpan referenceRemaining = request.ReferenceBudget - Stopwatch.GetElapsedTime(referenceStarted);
                    TimeSpan budget = remaining < referenceRemaining ? remaining : referenceRemaining;
                    if (budget <= TimeSpan.Zero)
                    {
                        throw new TimeoutException("The protected reference budget expired.");
                    }

                    int read = reference.Read(buffer, count, offset, budget, cancellationToken);
                    segments++;
                    if (read < 0 || read > count)
                    {
                        throw new InvalidDataException("The reader returned an invalid transfer count.");
                    }

                    CheckReferenceBudget();

                    if (offset < request.ExpectedLength)
                    {
                        if (read == 0)
                        {
                            throw new RejectedException(CloudContentConfirmationOutcome.ContentMismatch);
                        }

                        hash.AppendData(buffer, 0, read);
                        offset = checked(offset + read);
                        bytes = checked(bytes + read);
                    }
                    else
                    {
                        stage = CloudContentConfirmationStage.Verify;
                        if (read != 0 || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), request.Hash))
                        {
                            throw new RejectedException(CloudContentConfirmationOutcome.ContentMismatch);
                        }

                        CheckBudget();
                        facts = reference.Inspect();
                        rejected = Validate(facts, request, prepared);
                        if (rejected is not null)
                        {
                            throw new RejectedException(rejected.Value);
                        }

                        if (!facts.IsPlaceholder || !facts.Identity.AsSpan().SequenceEqual(request.EncodedIdentity))
                        {
                            stage = CloudContentConfirmationStage.Prepare;
                            CheckReferenceBudget();
                            (int hresult, long usn) = reference.Prepare(request, convert: !facts.IsPlaceholder);
                            preparationHResult = hresult;
                            preparationUsn = usn;
                            prepared = true;
                            // Preparation has its own object/content guard. Reverify the whole
                            // accepted content with the prepared identity before final marking.
                            offset = 0;
                            // Preserve the successful mutation before enforcing its elapsed
                            // budget. An expired preparation must not start another hash pass.
                            CheckReferenceBudget();
                        }
                        else
                        {
                            stage = CloudContentConfirmationStage.Mark;
                            CheckReferenceBudget();
                            already = facts.InSync;
                            if (!already)
                            {
                                markHResult = reference.Mark();
                                applied = true;
                            }

                            verified = true;
                        }
                    }

                    void CheckReferenceBudget()
                    {
                        CheckBudget();
                        if (Stopwatch.GetElapsedTime(referenceStarted) >= request.ReferenceBudget)
                        {
                            throw new TimeoutException("The final reference budget expired before mutation.");
                        }
                    }
                }
                finally
                {
                    TimeSpan lifetime = Stopwatch.GetElapsedTime(referenceStarted);
                    if (lifetime > longest)
                    {
                        longest = lifetime;
                    }
                }

                if (verified)
                {
                    return Result(already ? CloudContentConfirmationOutcome.AlreadyConfirmed : CloudContentConfirmationOutcome.Confirmed);
                }

                // Include reference disposal in the observed lifetime. A successful mark
                // above is already committed and must not be rewritten as an unapplied timeout.
                if (Stopwatch.GetElapsedTime(referenceStarted) >= request.ReferenceBudget)
                {
                    throw new TimeoutException("The protected reference budget expired before the next segment.");
                }

                // Give queued writers a chance to break the oplock. Never reopen by path or
                // carry an accumulated digest onto a replacement owner after a failed reference.
                await Task.Yield();
            }
        }
        catch (RejectedException exception)
        {
            return Result(exception.Outcome);
        }
        catch (OperationCanceledException exception)
        {
            return Result(CloudContentConfirmationOutcome.Canceled, exception);
        }
        catch (TimeoutException exception)
        {
            return Result(CloudContentConfirmationOutcome.DeadlineExceeded, exception);
        }
        catch (InvalidOperationException exception) when (stage == CloudContentConfirmationStage.Reference)
        {
            return Result(CloudContentConfirmationOutcome.ProtectionLost, exception);
        }
        catch (NotSupportedException exception)
        {
            return Result(CloudContentConfirmationOutcome.NotApplicable, exception);
        }
        catch (Exception exception)
        {
            Exception error = Translate(exception, path, stage);
            bool busy = stage == CloudContentConfirmationStage.Open &&
                error is CloudFilesException native && native.Win32ErrorCode is 32 or 33;
            return Result(busy ? CloudContentConfirmationOutcome.Busy : CloudContentConfirmationOutcome.Failed, error);
        }
    }

    private sealed class RejectedException(CloudContentConfirmationOutcome outcome) : Exception
    {
        internal CloudContentConfirmationOutcome Outcome { get; } = outcome;
    }

    internal static CloudContentConfirmationOutcome? Validate(CloudProtectedFileFacts facts,
        CloudContentConfirmationRequest request, bool prepared)
    {
        if (facts.Binding != request.ExpectedBinding)
        {
            return CloudContentConfirmationOutcome.LocalObjectMismatch;
        }

        if (facts.IsDirectory || !facts.IsSupported)
        {
            return CloudContentConfirmationOutcome.NotApplicable;
        }

        if (!facts.IsFullyLocal)
        {
            return CloudContentConfirmationOutcome.NotFullyLocal;
        }

        if (facts.Length != request.ExpectedLength)
        {
            return CloudContentConfirmationOutcome.ContentMismatch;
        }

        bool accepted = facts.Identity.AsSpan().SequenceEqual(request.EncodedIdentity);
        if (facts.IsPlaceholder && !accepted &&
            (prepared || request.Preparation != CloudContentPreparation.ReplacePlaceholderIdentity ||
                !facts.Identity.AsSpan().SequenceEqual(request.PreviousIdentity)))
        {
            return CloudContentConfirmationOutcome.IdentityMismatch;
        }

        if (!facts.IsPlaceholder && (prepared || request.Preparation != CloudContentPreparation.ConvertRegularFile))
        {
            return CloudContentConfirmationOutcome.NotApplicable;
        }

        return null;
    }

    internal static Exception Translate(Exception exception, string path, CloudContentConfirmationStage stage) =>
        exception switch
        {
            NativeFileException native => CloudFilesException.FromHResult(native.Operation, path, native.HResult),
            Win32Exception win32 => CloudFilesException.FromHResult($"CloudFile.ConfirmUploadedContent.{stage}",
                path, unchecked((int)(0x80070000u | ((uint)win32.NativeErrorCode & 0xffff)))),
            IOException { InnerException: Win32Exception win32 } => Translate(win32, path, stage),
            UnauthorizedAccessException => CloudFilesException.FromException($"CloudFile.ConfirmUploadedContent.{stage}", path, exception),
            _ => exception,
        };
}
