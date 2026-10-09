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

An admitted scope-method failure remains in the receipt even if application code handles its
exception. Callback-origin errors remain the original exceptions, distinct from translated native
failures. The first failed scope phase is retained while other already admitted calls drain.

The immutable receipt records the observed binding/snapshot, last work stage, callback progress,
error and elapsed lifetime. It does not declare independently verified permissions, whole-tree
readiness, remote acceptance, synchronization confirmation or journal ACK. The application owns
its intent, original DACL, roles, namespace fence, permission verification and recovery policy.
