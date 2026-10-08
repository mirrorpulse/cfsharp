# Sync-root lifecycle

A Windows sync-root registration is persistent OS state. It is not the same thing as a process
session and must not be removed merely because a provider process is stopping.

## Register once for an account

Build a validated registration specification and register the local directory explicitly:

```csharp
SyncRootRegistrationOptions registration =
    SyncRootRegistrationOptions.CreateBuilder("Example Cloud", "1.0.0")
        .WithProviderId(providerId)
        .WithSyncRootIdentity(accountIdentity)
        .WithRootMarkedInSync()
        .Build();

CloudSyncRoot root = CloudSyncRoot.Register(localDirectory, registration);
CloudSyncRootInfo info = root.GetInfo();
```

Registration requires a valid local directory and provider identity. Windows owns the persistent
registration after the call succeeds.

## Start a process session

`CloudFileSystem.StartAsync` opens the configured durable store, verifies or applies the persistent
registration, and connects the optional content provider. Work admitted after start holds an
explicit operation lease.

Disposal rejects new public work, stops the local notification producer, and drains accepted
notifications and admitted operations before stopping the provider session and closing durable
state. Accepted renames retain internal path-scope admission during `Stopping`; disposal does not
hold the lifecycle admission lock while awaiting their completion. Concurrent disposal attempts
remain serialized. It intentionally leaves the sync-root registration installed.

## Unregister only for removal

Call `CloudSyncRoot.Unregister()` only when removing an account or uninstalling the provider. Windows
may traverse the tree and remove placeholder content that is not locally complete. For routine
shutdown, dispose the process-scoped `CloudFileSystem` instead.

## Recovery rule

If a process exits unexpectedly, reopen the same state store and sync root. Do not create a second
database for the same root or infer completion from an in-memory callback token. Durable checkpoints
and transaction boundaries are the recovery source of truth.
