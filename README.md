# CfSharp

[![CI](https://github.com/mirrorpulse/cfsharp/actions/workflows/ci.yml/badge.svg)](https://github.com/mirrorpulse/cfsharp/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-84f5d3.svg)](LICENSE)
[![Windows](https://img.shields.io/badge/platform-Windows-0078d4.svg)](docs/platform-support.md)

CfSharp is a Windows-only .NET library for building Cloud Files sync providers with a safe,
idiomatic C# API. It keeps the complete Windows Cloud Files surface reachable while taking care
of native structure layout, callback lifetime, cancellation, durable coordination, and failure
translation.

> **Preview channel:** CfSharp preview packages are published from `develop` by the manually
> triggered Preview release workflow. Stable `1.0.0` is reserved for the protected `main` release
> path.

## What is included

| Package | Purpose |
| --- | --- |
| `CfSharp.Native` | ABI-accurate bindings for `cfapi.h` and `CldApi.dll`. |
| `CfSharp` | High-level sync-root, placeholder, hydration, provider, local-change, and remote-change APIs. |
| `CfSharp.Storage.Sqlite` | Optional transactional state store using a caller-owned SQLite database. |

The dependency direction is one way: `CfSharp.Native` is the interop layer, `CfSharp` provides
the safe domain API, and the SQLite package depends on `CfSharp`. Remote transport, authentication,
content bytes, and business conflict policy remain application-owned.

## Quick start

### Requirements

- Windows 10 version 1709 (build 16299) or later for the core Cloud Files API;
- .NET 10 SDK selected by [`global.json`](global.json);
- x64 or ARM64 for the current support target. x86 is outside the stable release matrix;
- a sync root directory and a durable state database located outside that sync root.

### Build from source

```powershell
git clone https://github.com/mirrorpulse/cfsharp.git
cd CfSharp
dotnet restore CfSharp.sln
dotnet build CfSharp.sln --configuration Release --no-restore
dotnet test CfSharp.sln --configuration Release --no-build
```

The first sample provider is available under
[`samples/CfSharp.SampleProvider`](samples/CfSharp.SampleProvider). It mirrors a local content
directory as online-only placeholders:

```powershell
dotnet run --project samples/CfSharp.SampleProvider -- `
  run C:\CloudContent `
  C:\CloudSyncRoot `
  --state-db C:\CloudState\cfsharp.db `
  --once
```

## A small example

CfSharp makes ownership and lifecycle boundaries explicit. The SQLite path is supplied by the
application and must not be placed inside the managed sync root.

```csharp
ICloudStateStoreFactory stateStoreFactory = new SqliteCloudStateStoreFactory(
    @"C:\ProgramData\ExampleProvider\Accounts\account-42\cfsharp.db");

SyncRootRegistrationOptions registration =
    SyncRootRegistrationOptions.CreateBuilder("Example Cloud", "1.0.0")
        .WithProviderId(providerId)
        .WithSyncRootIdentity(accountIdentity)
        .WithRootMarkedInSync()
        .Build();

await using CloudFileSystem fileSystem = CloudFileSystem.CreateBuilder(
        @"C:\Users\Example\Example Cloud")
    .WithStateStore(stateStoreFactory)
    .WithRegistration(registration)
    .WithContentProvider(contentProvider)
    .Build();

await fileSystem.StartAsync(cancellationToken);
CloudFile report = fileSystem.GetFile(@"Documents\report.pdf");
CloudItemSnapshot snapshot = await report.InspectAsync(cancellationToken);
```

`CloudFileSystem` opens the configured store only after validating the sync root and owns it after
a successful start. Disposing the facade stops process-scoped work and closes durable state, but
does not unregister the persistent Windows sync-root registration. Call
`CloudSyncRoot.Unregister()` only for explicit account removal or uninstall.

To confirm content already accepted by a remote service, use
[`CloudFile.ConfirmUploadedContentAsync`](docs/placeholders.md#protected-uploaded-content-confirmation).
It verifies the retained file/root/volume binding, complete identity, length, and SHA-256 under
exclusive native protection, with optional guarded conversion and recoverable SQLite projection.
Callers retain the upload proof and handle the typed outcome; the library performs no remote upload.

## Documentation

- [Documentation home](docs/index.md)
- [Getting started](docs/getting-started.md)
- [Architecture and ownership](docs/architecture.md)
- [Sync-root lifecycle](docs/sync-root-lifecycle.md)
- [Placeholders and hydration](docs/placeholders.md)
- [Local change feed](docs/local-change-feed.md)
- [Remote change application](docs/remote-change-application.md)
- [SQLite state](docs/state-store-sqlite.md)
- [Platform support](docs/platform-support.md)
- [Troubleshooting](docs/troubleshooting.md)

The generated API reference is published with the documentation site at
`https://mirrorpulse.github.io/cfsharp/`.

Release automation, NuGet trusted publishing, version calculation, and branch rules are described in
the [release and branch model](docs/releasing.md).

## Development notes

The repository is Windows-first and keeps unsafe interop isolated in `CfSharp.Native`. Public APIs
document ownership, lifetime, thread-safety, platform requirements, failure modes, and relevant
native behavior. Changes should follow the atomic-commit and verification rules in
[`CONTRIBUTING.md`](CONTRIBUTING.md).

Security reports must follow [`SECURITY.md`](https://github.com/mirrorpulse/cfsharp/blob/develop/SECURITY.md). Do not publish vulnerability details
in a public issue or pull request.

```powershell
dotnet format CfSharp.sln --verify-no-changes --no-restore
dotnet build CfSharp.sln --configuration Release --no-restore
dotnet test CfSharp.sln --configuration Release --no-build
pwsh ./eng/verify-platform-matrix.ps1
pwsh ./eng/verify-runtime-boundaries.ps1 -Configuration Release
```

## License

CfSharp is licensed under the [Apache License 2.0](LICENSE). Copyright © MirrorPulse Team.
