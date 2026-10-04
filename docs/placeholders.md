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

## Provider responsibility

CfSharp coordinates the Windows demand callback and durable state; the application supplies content
bytes and remote transport. A provider must be prepared for retries, cancellation, and a callback
that is replayed after a process restart.
