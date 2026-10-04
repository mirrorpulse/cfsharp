# Placeholders and hydration

Placeholder operations use immutable, kind-specific specifications rather than exposing native
unions and flag combinations directly to application code.

## Create a placeholder

```csharp
CloudFilePlaceholderSpec report = CloudFilePlaceholderSpec
    .CreateBuilder("report.pdf", "remote-report-42", length: 128_000)
    .WithRemoteRevision("etag-7")
    .WithInitialAvailability(CloudAvailabilityTarget.OnlineOnly)
    .Build();

CloudPlaceholderBatchResult result =
    await fileSystem.Root.CreatePlaceholdersAsync([report], cancellationToken);
result.ThrowIfAnyFailed();
```

The identity envelope is versioned, deterministic, and bounded by the native identity limit. Do not
put credentials or secrets in remote identifiers, revisions, or placeholder metadata.

## Availability is explicit

`OnlineOnly`, `LocallyAvailable`, and `AlwaysAvailable` describe different pin and content states.
CfSharp hydrates before applying the final pin intent when necessary and returns partial-failure
information if a transition cannot complete. A native failure remains available through the managed
exception and the post-failure snapshot.

```csharp
CloudAvailabilityChangeResult transition = await file.SetAvailabilityAsync(
    CloudAvailabilityTarget.LocallyAvailable,
    cancellationToken);

await file.SetInSyncAsync(true, cancellationToken);
```

## Ranges and reversion

Range results are normalized and kept separate for on-disk, provider-validated, and locally modified
content. Placeholder patches can condition a change on the observed USN. Move and delete operations
keep immutable path references and update durable descendants only after the file-system operation
succeeds.

## Conditional in-sync confirmation

`CloudItemSnapshot.LocalBinding` exposes the complete native volume, actual sync-root directory,
and 128-bit file object IDs for ordinary files and placeholders. Inspection reads metadata without
hydrating content. Capture the binding before uploading and retain it with the accepted proof;
re-observing the same path after replacement cannot establish that it is the uploaded object.
Bindings are immutable comparison values, not locks or conditional USN tokens. The existing
nullable 64-bit snapshot IDs retain their previous meaning and availability.

`OperationUsn` preserves the value returned by the Cloud Files mutation. Windows can successfully
convert, update, or set the in-sync state while returning zero. Zero is not a conditional token:
[`CfSetInSyncState`](https://learn.microsoft.com/windows/win32/api/cfapi/nf-cfapi-cfsetinsyncstate)
treats an input USN of zero as an unconditional operation. CfSharp rejects zero in
`CloudInSyncChangeOptions` and `CloudPlaceholderPatch.Builder.WithExpectedUsn`.

Use `CloudItem.ReadUsnAsync` to query the last file-system USN through
[`FSCTL_READ_FILE_USN_DATA`](https://learn.microsoft.com/windows/win32/api/winioctl/ni-winioctl-fsctl_read_file_usn_data).
This attribute-only query supports ordinary files, directories, and Cloud Files placeholders on
NTFS or ReFS. It closes its handle before returning and does not hydrate content, create a journal,
or modify durable state. Native failures preserve their Win32 error as a `CloudFilesException`
HRESULT; a successful zero result remains unusable for a conditional mark.

After remote acceptance, obtain a positive token **before** independently verifying that the local
content still matches the accepted upload. Close verification streams, then pass the same token to
the conditional mark. Reading a fresh token only after hashing could confirm a later, unverified
change. For example, with `acceptedHash` from the uploaded content:

```csharp
long observedUsn = await file.ReadUsnAsync(cancellationToken);
if (observedUsn <= 0)
{
    throw new InvalidOperationException("A positive USN is required for conditional confirmation.");
}

byte[] currentHash;
await using (FileStream stream = File.OpenRead(file.FullPath))
{
    currentHash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken);
}

if (!currentHash.AsSpan().SequenceEqual(acceptedHash))
{
    throw new InvalidOperationException("The local content no longer matches the accepted upload.");
}

await file.SetInSyncAsync(
    true,
    new CloudInSyncChangeOptions(observedUsn),
    cancellationToken);
```

On verification failure, unavailable tokens, or a rejected USN condition, retain pending work and
reconcile. Do not retry with an unconditional mark. A USN is an observation of the same existing
item within the current volume journal; it is not a content hash or a durable identity across
deletion, path replacement, or journal recreation. Providers must coordinate those lifecycle
events separately. The operation lease serializes CfSharp calls, not external writers.

Reading a positive file-system USN does not guarantee that the installed Cloud Files platform will
accept it. Some Windows builds reject even an unchanged, verified token with `0x80070179`
(`ERROR_CLOUD_FILE_NOT_IN_SYNC`). CfSharp preserves this native failure. Verify successful
conditional confirmation on the target platform before relying on it in a provider's acceptance
tests; the read API alone does not establish that capability.

## Protected uploaded-content confirmation

Use `CloudFile.ConfirmUploadedContentAsync` when a remote service has accepted a complete content
proof and the installed platform does not provide usable conditional in-sync tokens. Capture
`LocalBinding` before uploading, compute the digest of the uploaded bytes, authenticate the remote
acceptance, and durably retain that proof in the application. Close upload streams and mappings
before requesting protection.

```csharp
CloudLocalFileBinding uploadedObject = uploadSnapshot.LocalBinding
    ?? throw new InvalidOperationException("A complete upload-time object binding is required.");
CloudPlaceholderIdentity acceptedIdentity = new(itemId, acceptedRemoteId, acceptedRevision);
CloudContentConfirmationRequest proof = new(
    uploadedObject,
    acceptedIdentity,
    uploadedLength,
    uploadedSha256,
    preparation: CloudContentPreparation.ConvertRegularFile);

CloudContentConfirmationResult confirmation =
    await file.ConfirmUploadedContentAsync(proof, cancellationToken);
if (confirmation.Outcome is CloudContentConfirmationOutcome.Confirmed or
    CloudContentConfirmationOutcome.AlreadyConfirmed)
{
    // Advance only the application intent covered by this retained upload proof.
}
```

The default preparation is `None`: the complete opaque placeholder identity must already match.
`ConvertRegularFile` explicitly permits conversion of the verified ordinary file. For revision
replacement, select `ReplacePlaceholderIdentity` and provide the complete previously observed
identity bytes. Both modes also permit replay when the accepted identity is already present.
Preparation checks binding, full length, and SHA-256 before changing identity, then verifies the
whole content again before marking. It never writes file content or changes pin intent.

The implementation uses one exclusive protected owner, without `Foreground`. Every bounded read
holds a reference until native I/O has completed or cancellation has drained. References are
released between segments and reacquired on the same opaque handle. A break discards the attempt;
the library never reopens by path to continue its digest. The final identity, length, object
checks and native mark share one reference. The mark passes a **null USN pointer**, which is
native-unconditional. Its safety relies on exclusive object protection and the complete proof,
not USN CAS. Existing positive-USN conditional APIs keep their contract.

Ordinary files and fully local placeholders are supported. Directories, hard links, arbitrary
reparse targets, and partial or online-only content are rejected without hydration. Default reads
use a 1 MiB buffer, a 250 ms reference budget, and a ten-minute total deadline; immutable request
options can adjust finite budgets. Pending cancellation is requested with `CancelIoEx` and drained
before buffers, events, or references are freed. Driver cancellation drain and synchronous native
calls may exceed a budget; expired budgets stop subsequent work and marking.

| Outcome | Caller action |
| --- | --- |
| `Confirmed`, `AlreadyConfirmed` | Complete only the retained accepted-content intent. |
| `ContentMismatch`, `IdentityMismatch`, `LocalObjectMismatch` | Retain intent and reconcile; replacements do not inherit it. |
| `NotFullyLocal`, `NotApplicable` | Choose an explicit application workflow; confirmation does not download content. |
| `Busy`, `ProtectionLost` | Retry the entire proof after the competing operation finishes. |
| `Canceled`, `DeadlineExceeded`, `Failed` | Inspect stage, error, and preparation facts; do not assume identity preparation was rolled back. |
| `NativeAppliedProjectionPending` | Retain the same proof and replay to repair durable identity projection. |

The receipt owns its proof and distinguishes `NativeIdentityPrepared`, `NativeApplied`,
`NativeConfirmationVerified`, and `DurableProjectionCommitted`. Actual preparation and mark
HRESULTs are retained when those calls succeed; errors preserve operation and HRESULT. A zero
`PreparationUsn` remains an observation. Already-in-sync files still undergo complete proof
verification, and an idempotent replay need not issue another mark.

Native mutation and SQLite are separate commits. Protection is released before opening the
projection transaction. A metadata-only no-delete guard verifies that the durable identity is
projected onto the same object. If preparation succeeds but later verification or marking fails,
its receipt remains explicit and projection is attempted; do not acknowledge content merely
because identity preparation or its projection succeeded. After native confirmation, caller
cancellation cannot erase the commit fact or automatically mark the object not-in-sync.

Retain the proof across process crashes and replay it after reopening the file system. Replays
recheck object, identity, and complete content before repairing the accepted revision. The store
contains existing identity coordination metadata, not content hashes, upload receipts, or remote
business data. Confirmation does not acknowledge journal operations, local-change events, rescan
generations, or unrelated echo entries. Later writes remain native not-in-sync; the receipt's
`ObservedSynchronizationState` is a later observation and never forces the old state back onto
the file. Same-path library operations serialize through existing leases; other paths can progress.

## Provider responsibility

CfSharp coordinates the Windows demand callback and durable state; the application supplies content
bytes and remote transport. A provider must be prepared for retries, cancellation, and a callback
that is replayed after a process restart.
