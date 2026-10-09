# Platform support

CfSharp targets Windows Cloud Files and has explicit capability gates. The package does not silently
pretend that a newer native operation exists on an older Windows build.

## Operating-system baseline

The core Cloud Files API requires Windows 10 version 1709 (build 16299) or later. Individual
capabilities use additional build or Cloud Files integration thresholds:

| Capability family | Gate |
| --- | --- |
| Core Cloud Files API | Windows build 16299 or later |
| Rich sync-root status | Windows build 17134 or later |
| Provider progress V2 | Windows build 17763 or later |
| Placeholder management policy | Integration 784 or later |
| Full-restart hydration and force-convert flags | Integration 1280 or later |
| Hydration range information | Integration 1536 or later |
| Protected local callbacks and Access-only descriptors | Windows build 16299 or later; applicable native data/DACL access and verified same-object sharing |

Callers should feature-detect optional capabilities and handle the reported unsupported result.

## Architectures

x64 and ARM64 are the current target architectures for packages, sample validation, and CI. ARM64
uses the same managed capability contract on a supported Windows build. x86 is intentionally excluded
from the stable matrix; do not infer x86 support from a successful managed compilation.

## Validation policy

The repository keeps separate checks for managed tests, native x64 ABI probing, package platform
declarations, trim/AOT boundaries, and ARM64 execution. Run the platform and runtime boundary
scripts before treating a local build as a supported result.

## Server and desktop hosts

The core API is Windows-only. Desktop Shell behavior, MSIX/Desktop Bridge packaging, Explorer
visual evidence, and host registration are validated separately from the library's managed tests.
The host application remains responsible for registration and removal UX.

Protected local file operations reject existing writers, writable mappings and aliases. Local
conversion or file DACL application requires the actual disallowed-placeholder-hardlink policy.
Ordinary-file inspection does not provide hardlink freezing. Cold metadata and DACL work reads no
source content. Directory metadata mode pins one namespace object while members remain free to
change; it provides no tree freeze. Cancellation and short budgets stop new work, then drain actual
callbacks before release. Native driver stalls and uncooperative callbacks can delay completion.
See [Protected local operations](protected-local-operations.md) for receipts and recovery.
