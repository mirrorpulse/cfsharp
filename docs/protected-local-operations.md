# Protected local operations

`CloudItem.RunProtectedLocalOperationAsync` awaits short application work while CfSharp retains
facade path admission, official state-store ownership and the original Windows file object.
Capture and durably retain the original `CloudLocalFileBinding` before beginning an operation.
A replacement's newly captured binding does not establish ownership of the old object.

```csharp
CloudItemSnapshot original = await file.InspectAsync(cancellationToken);
CloudLocalFileBinding binding = original.LocalBinding
    ?? throw new NotSupportedException("Complete native IDs are required.");

// The application durably records its intent and original binding before calling.
CloudProtectedLocalOperationResult receipt = await file.RunProtectedLocalOperationAsync(
    new CloudProtectedLocalOperationRequest(binding),
    async (scope, stop) =>
    {
        CloudItemSnapshot current = await scope.InspectAsync(stop);
        // Await only necessary local work. The original object stays protected across await.
        await Task.Yield();
        current = await file.InspectAsync(stop); // Exact-item calls reuse this scope.
    }, cancellationToken);
```

Exclusive file mode uses one library-owned Windows file object with read/write data and DACL
access and share-none. It rejects existing writers and writable mappings; competing data or
namespace opens receive native sharing failures. No content is read and no source provider is
called. This deliberately avoids composing a CFAPI exclusive oplock with a second DACL writer:
that writer can break the oplock and wait for the very reference its caller is retaining.
CfSharp neither borrows an automatically invalidatable opaque pointer nor exposes an owning handle
to application code. This API requires Windows 10 version 1709 or later and applicable native access.
It does not use the existing `CloudItemLease` as an outer wrapper around path-based conversion.

`DirectoryMetadata` mode provides same-object metadata access and pins the directory name;
its members, descendant content and enumeration can change. Exclusive mode on a directory is
`Unsupported`. Neither mode provides whole-tree freezing. Ordinary files have no hardlink-freeze
claim: placeholder registration policy cannot protect ordinary-file aliases.

For a permission initialization that requires placeholder hardlink exclusion, use
`CloudProtectedLocalOperationRequest.ForLocalConversion(binding, localIdentity)`. It checks the
actual root's disallowed-hardlink policy, rejects existing aliases, prepares the exact local
identity and commits its official projection before invoking the callback. The identity must not
contain an accepted remote revision. The native conversion uses the same exclusive file object,
preserving bytes, binding and DACL without marking in sync or dehydrating content.

`scope.ConvertToPlaceholderAsync` provides the same preparation within a callback. An exact-item
`file.ConvertToPlaceholderAsync` call also reuses the scope, with only content-preserving defaults.
Existing placeholder identities must match exactly; this API cannot replace identity or rekey
known pending work. It preserves prior acknowledged revision and local ID fields, journal rows,
conflicts and checkpoints. No source content is read, including when inspecting a cold placeholder.

The receipt distinguishes `NativeConverted`, `NativeIdentityPrepared` and
`DurableProjectionCommitted`, retaining the conversion HRESULT and USN when available. After
native success, required projection completes even if cancellation arrives. A failed projection
returns `NativeAppliedProjectionPending` and prevents a preparation-gated callback from starting.
Keep the original binding and identity, then retry a new request after recovery or restart. A
matching already prepared object needs no second conversion; a replacement is rejected. A
transaction-disposal error after commit retains the true committed fact and its original error.

Inside the callback, `ReadAccessDescriptorAsync` returns an independent `FileSecurity` or
`DirectorySecurity`. `ApplyAccessDescriptorAsync` snapshots the input, applies only
`AccessControlSections.Access` on the original native file object, then returns a fresh readback.
Owner, group and audit sections are never written, even if supplied. Files require an actual
placeholder under disallowed hardlink policy before application; ordinary-file reads are allowed.
No original handle is exposed, attached to a stream or transferred to application code. Caller
descriptor edits must be synchronized while the input copy is taken.

`AccessDescriptorApplied` and `AccessDescriptorReadBack` record separate actual facts. An exception
after application does not erase the application receipt or imply rollback. Persist the original
DACL before starting and independently compare the readback against the host's policy. Directory
inheritance may affect descendants; this scope does not freeze those descendants or their membership.
No role SIDs, permission policy, original DACL or credentials are stored in the official database.

Use scope methods for protected work. Exact-item `InspectAsync` calls reuse the context.
Other nested facade work, another protected callback and owner disposal from the callback fail
immediately instead of waiting for the admission already held by that callback. Scope methods
serialize their native and state phases and are drained on callback exit, including admitted calls
the callback neglected to await. Escaped contexts reject new work after exit. The callback itself
never owns a SQLite transaction.

Cancellation stops new scope work and requests cooperative callback termination. CfSharp awaits
the callback's actual completion and admitted scope work before releasing native or store resources.
An uncooperative callback or stalled native driver can delay return; there is no safe forced abort.
External owner disposal rejects new public work and drains admitted operations before closing the
state store. Keep callbacks short and local, without network, worker RPC or user interaction.

Requests have a ten-second cooperative budget, including path admission. `WithBudget` returns an
immutable copy with a positive duration of at most one minute. Callback and context tokens combine
caller cancellation, owner shutdown and budget expiry. `scope.CurrentStage` and `scope.IsDraining`
are safe to observe from another thread: cancellation shows `Draining` while actual work retains
the native object, then `Released` after cleanup. Neither expiry nor cancellation forcibly aborts
arbitrary application work. The returned receipt separates cancellation and budget-expiry facts
from native mutation, durable commit and descriptor application. A delayed callback can return
after the budget without weakening protection.

Application cancellation handlers run away from the owner's lifecycle lock and are also drained
before native release. A throwing handler is reported as `CancellationCallbackError`; it does not
throw through owner shutdown or erase the original work outcome. Such handlers must also be short
and must not wait for owner disposal. No additional transaction spans the callback or its handlers.

An admitted scope-method failure remains in the receipt even if application code handles its
exception. Callback-origin errors remain the original exceptions, distinct from translated native
failures. The first failed scope phase is retained while other already admitted calls drain.

The immutable receipt records the observed binding/snapshot, last work stage, callback progress,
error and elapsed lifetime. It does not declare independently verified permissions, whole-tree
readiness, remote acceptance, synchronization confirmation or journal ACK. The application owns
its intent, original DACL, roles, namespace fence, permission verification and recovery policy.
