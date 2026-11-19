[CmdletBinding()]
param([Parameter(Mandatory)][string]$ConsumerRoot)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted') {
    throw 'Installed consumer qualification requires a disposable GitHub-hosted runner.'
}
$consumer = (Resolve-Path -LiteralPath $ConsumerRoot).Path
$consumerSource = (& git -C $consumer rev-parse HEAD | Out-String).Trim()
if ($consumerSource -cne '12fadb56299e8c10ce3b141e2149ef7601c86807') { throw 'The installed consumer source changed.' }
$env:GITHUB_SHA = $consumerSource
Write-Host "Installed consumer source: $consumerSource."
$library = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$output = Join-Path $library 'artifacts/installed-consumer'
New-Item -ItemType Directory -Path $output -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $consumer 'artifacts/evidence') -Force | Out-Null
$verifier = Join-Path $consumer 'eng/verify-msix-cloudfiles.ps1'
$original = [IO.File]::ReadAllText($verifier)
$anchor = "    `$verificationPhase = 'Shell'"
if ([regex]::Matches($original, [regex]::Escape($anchor)).Count -ne 1) { throw 'The installed lifecycle acceptance boundary changed.' }
$reopen = @'
    $firstHostId = [int]$hostStatus.data.processId
    try {
        $previousHost = [Diagnostics.Process]::GetProcessById($firstHostId)
        try { if (-not $previousHost.WaitForExit(15000)) { throw 'The first installed Host did not close cleanly.' } }
        finally { $previousHost.Dispose() }
    } catch [ArgumentException] { }
    $reopenedOutput = (& $mpCommand.Source --json status 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'The installed alias could not reopen the same durable state.' }
    $reopenedResult = $reopenedOutput | ConvertFrom-Json
    if ($reopenedResult.kind -cne 'result' -or $null -eq $reopenedResult.data.instances) { throw 'The reopened installed status is invalid.' }
    $reopenedLifecycle = (& $mpCommand.Source --json host status 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'The reopened installed Host is unavailable.' }
    $reopenedStatus = $reopenedLifecycle | ConvertFrom-Json
    if ($reopenedStatus.kind -cne 'result' -or $reopenedStatus.data.processId -le 0 -or $reopenedStatus.data.processId -eq $firstHostId) {
        throw 'The installed alias did not start a new Host after clean shutdown.'
    }
    & $mpCommand.Source --json host stop 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The reopened installed Host did not stop cleanly.' }
    [ordered]@{ schemaVersion = 1; verified = $true; sourceSha = $installedSourceSha; firstHostId = $firstHostId;
        reopenedHostId = $reopenedStatus.data.processId } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $workspaceRoot 'artifacts/evidence/installed-reopen.json') -Encoding utf8
'@
# Extend the fixed consumer's acceptance script only; its product startup and binding code
# are unchanged. Both reference and candidate must reopen the same installed private state.
[IO.File]::WriteAllText($verifier, $original.Replace($anchor, $reopen + [Environment]::NewLine + $anchor))
$session = 'CfSharpLoader-' + [guid]::NewGuid().ToString('N')
$etl = Join-Path $output 'loader.etl'
& logman create trace $session -o $etl -p '{e13c0d23-ccbc-4e12-931b-d9cc2eee27e4}' 0x4000801c 5 -bs 1024 -nb 16 64 -ets
if ($LASTEXITCODE -ne 0) { throw 'Could not start external CLR loader tracing.' }
$failure = $null
try {
    Push-Location $consumer
    try {
        & pwsh ./eng/verify-msix-cloudfiles.ps1 -Runtime win-arm64 -VerifyShell -EvidencePath artifacts/evidence/installed.json
        if ($LASTEXITCODE -ne 0) { throw 'Actual installed consumer qualification failed.' }
    } finally { Pop-Location }
    & (Join-Path $PSScriptRoot 'verify-unpackaged-consumer.ps1') -ConsumerRoot $consumer -OutputDirectory (Join-Path $output 'unpackaged-reference')
} catch { $failure = $_ }
finally {
    & logman stop $session -ets
    if ($LASTEXITCODE -ne 0) { throw 'Could not stop external CLR loader tracing.' }
}
# The raw machine trace is not uploaded. Export only the consumer Host's binding and
# failure records, with path payloads reduced to file names by the reader.
& dotnet run --project "$library/eng/loader-trace/LoaderTrace.csproj" -- $etl (Join-Path $output 'loader.json')
if ($LASTEXITCODE -ne 0) { throw 'Could not decode the installed consumer loader trace.' }
if ($failure) { throw $failure }
