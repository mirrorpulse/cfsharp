# CfSharp documentation

CfSharp is a Windows-only .NET library for building Cloud Files sync providers. These guides
explain the ownership, lifecycle, state, and platform boundaries that are easy to get wrong when
working directly with `cfapi.h` and `CldApi.dll`.

## Choose a path

| If you want to… | Start here |
| --- | --- |
| Build the repository and run the sample | [Getting started](getting-started.md) |
| Understand the package boundaries | [Architecture](architecture.md) |
| Register and operate a sync root | [Sync-root lifecycle](sync-root-lifecycle.md) |
| Create placeholders or hydrate content | [Placeholders and hydration](placeholders.md) |
| Upload local changes | [Local change feed](local-change-feed.md) |
| Apply remote metadata changes | [Remote change application](remote-change-application.md) |
| Choose and protect durable state | [SQLite state](state-store-sqlite.md) |
| Initialize one local object's identity and permissions | [Protected local operations](protected-local-operations.md) |
| Check OS and architecture support | [Platform support](platform-support.md) |
| Diagnose a failed operation | [Troubleshooting](troubleshooting.md) |
| Understand branches and releases | [Release and branch model](releasing.md) |

## API reference

The API reference is generated from the Release assemblies and XML documentation comments for the
three distributable packages. It is kept separate from these hand-written guides and will appear
under `api/` in the published documentation site.

## Scope and status

The guides describe the current development preview. They do not provide a remote service,
authentication implementation, or business conflict policy. Those responsibilities stay with the
application hosting CfSharp. If a guide and the generated API reference disagree, treat the API
reference and the tested source as authoritative and open an issue with a minimal reproduction.
