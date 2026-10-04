# Contributing to CfSharp

CfSharp values correctness, reviewability, and a clear project history. Contributions must follow the rules below.

## Language

Use English for source code, identifiers, comments, documentation, tests, examples, commit messages, and project metadata.

Project-owned copyright, package author, and primary attribution metadata must use `MirrorPulse Team`. Individual contributors remain identified by their Git commit authorship.

## Atomic Changes

- Keep each commit limited to one logical change or one fix.
- Do not combine unrelated fixes, refactoring, formatting, dependency updates, or documentation changes in one commit.
- Split preparatory refactoring from behavior changes when each can be reviewed and tested independently.
- Keep every commit buildable and testable whenever technically possible.
- Add or update tests in the same commit as the behavior they verify.

## Commit Messages

Use Conventional Commits:

```text
<type>[optional scope][!]: <description>
```

Allowed types include:

- `feat`: a user-visible capability;
- `fix`: a defect correction;
- `docs`: documentation-only changes;
- `test`: test-only changes;
- `refactor`: behavior-preserving code restructuring;
- `perf`: a performance improvement;
- `build`: build system or dependency changes;
- `ci`: continuous-integration changes;
- `chore`: repository maintenance;
- `revert`: a prior commit reversal.

Use an imperative, concise description without a trailing period. Add a body when the reason, tradeoff, compatibility impact, or verification is not obvious. Mark breaking changes with `!` and a `BREAKING CHANGE:` footer.

Examples:

```text
feat(items): add range hydration
fix(native): correct CF_OPERATION_INFO layout on ARM64
docs: explain local and remote change flows
```

## Verification

- Run the narrowest relevant tests while developing and the complete required suite before submission.
- Treat native ABI size, offset, calling-convention, and constant checks as mandatory for interop changes.
- Include Windows integration coverage for changes that alter sync roots, placeholders, callbacks, or file-system state.
- Document tests that cannot be run and explain the remaining risk.

The required GitHub checks for a pull request into `main` are `CI / build-test-pack`,
`CI / native-abi-x64`, `CI / test-arm64`, and `Branch policy / validate-main-pr`. The latter
requires the pull request to originate from the repository's `develop` branch and to carry exactly
one release label: `breaking`, `feature`, or `fix`.

## Branches and pull requests

- Create short-lived feature or fix branches from `develop`.
- Keep `develop` as the integration branch; direct pushes are allowed when the change is ready for
  integration, but a pull request is preferred for review.
- Never push directly to `main`. A `develop` to `main` pull request is required, must pass the
  protected checks, and must have at least one approval.
- Keep the release label on a main-bound pull request aligned with the intended semantic-version
  increment: `breaking` for a major change, `feature` for a minor change, and `fix` for a patch.
- Do not add more than one of those release labels. Resolve review conversations before merging.

See [docs/releasing.md](docs/releasing.md) for preview/stable publication, version calculation,
protected tags, and NuGet Trusted Publishing. Report vulnerabilities privately according to
[SECURITY.md](SECURITY.md), rather than opening a public issue.

## Documentation and generated output

Public source and documentation are English. Update XML comments and the relevant guide in the
same logical change as a public API change. Generated API pages are produced by the DocFX scripts;
edit their source comments or guide files, not generated output under `artifacts/`.

Do not commit `draft/`, `artifacts/`, downloaded logs, local package feeds, credentials, build
output, or IDE state. Keep the working tree clean before committing.

## Local gates

Run the smallest applicable gate first, then the full Release checks when practical:

```powershell
dotnet format CfSharp.sln --verify-no-changes --no-restore
dotnet build CfSharp.sln --configuration Release --no-restore
dotnet test CfSharp.sln --configuration Release --no-build
pwsh ./eng/verify-platform-matrix.ps1
pwsh ./eng/verify-runtime-boundaries.ps1 -Configuration Release
```

Native or Windows-state changes also require the relevant ABI probe, integration, SQLite recovery,
or sample-provider tests. The 30-minute x64/ARM64 `LongSoak` is a manual workflow gate, not a
substitute for the normal test suite.

The integration test project can also be built and tested independently in either configuration:

```powershell
dotnet test tests/CfSharp.IntegrationTests/CfSharp.IntegrationTests.csproj --configuration Release
dotnet test tests/CfSharp.IntegrationTests/CfSharp.IntegrationTests.csproj --configuration Debug
```

Its project reference builds the crash harness without referencing its executable assembly.
Confirmation subprocess tests select the harness configuration recorded in the test assembly;
no prior solution build or Release output is required for a Debug run.

## Scope And Generated Content

- Do not commit local research, downloaded documentation, credentials, build output, or IDE state.
- Keep generated changes reproducible and commit the generator or source-of-truth update with the output when required.
- Avoid drive-by cleanup outside the contribution's stated purpose.
