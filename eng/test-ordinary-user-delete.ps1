[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [ValidateSet('Delete', 'Recreation')]
    [string] $Suite = 'Delete'
)

$ErrorActionPreference = 'Stop'
# Account creation belongs only to a disposable hosted runner, never a developer machine.
if ($env:GITHUB_ACTIONS -ne 'true') {
    throw 'Fresh-user namespace recovery verification is restricted to disposable GitHub Actions runners.'
}
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Fresh-user test setup requires an elevated runner; the test process itself must not be elevated.'
}
if ($env:RUNNER_ARCH -notin @('X64', 'ARM64')) {
    throw 'The ordinary-user producer requires an explicit supported runner architecture.'
}

$repository = Split-Path -Parent $PSScriptRoot
$id = [Guid]::NewGuid().ToString('N')
$userName = 'CfSharp-' + $id.Substring(0, 8)
$executionRoot = Join-Path $env:PUBLIC "CfSharpOrdinaryTests/$id"
$suiteName = $Suite.ToLowerInvariant()
$filter = if ($Suite -eq 'Delete') { 'FullyQualifiedName~OriginalDeleteRecoversItsOwnReceiptsAcrossOfficialStoreReopen' }
    else { 'FullyQualifiedName~ExplicitSuccessorAtRetiredPathPreservesBothGenerationsAcrossReopen' }
$expectedCount = if ($Suite -eq 'Delete') { 14 } else { 2 }
$evidence = Join-Path $repository ('artifacts/ordinary-user-' + $suiteName)
New-Item -ItemType Directory -Path $executionRoot, $evidence -Force | Out-Null
$account = $null
$process = $null
$password = $null
try {
    # This unrelated ephemeral password stays in memory. No credentials enter command lines,
    # generated scripts, logs or artifacts. UseNewEnvironment excludes the caller's user env.
    $password = ConvertTo-SecureString ([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) + 'Aa1!') -AsPlainText -Force
    $account = New-LocalUser -Name $userName -Password $password -PasswordNeverExpires -Description 'Ephemeral CfSharp CI test identity'
    $users = Get-LocalGroup -SID 'S-1-5-32-545'
    Add-LocalGroupMember -Group $users -Member $account
    $credential = [PSCredential]::new("$env:COMPUTERNAME\$userName", $password)
    $acl = Get-Acl -LiteralPath $executionRoot
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($account.SID, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    Set-Acl -LiteralPath $executionRoot -AclObject $acl

    foreach ($project in @('CfSharp.IntegrationTests', 'CfSharp.Storage.Sqlite.CrashHarness')) {
        $relative = "tests/$project/bin/$Configuration/net10.0-windows"
        $source = Join-Path $repository $relative
        if (-not (Test-Path -LiteralPath $source -PathType Container)) {
            throw "Build the integration tests and crash harness first: $source"
        }
        $destinationParent = Split-Path -Parent (Join-Path $executionRoot $relative)
        New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destinationParent -Recurse
    }
    Copy-Item -LiteralPath (Join-Path $repository 'CfSharp.sln'), (Join-Path $repository 'global.json') -Destination $executionRoot
    $childScript = Join-Path $executionRoot 'run-tests.ps1'
    @'
param([string] $Assembly, [string] $Results, [string] $Dotnet, [string] $ExpectedSid, [string] $ExpectedArchitecture, [string] $Filter, [string] $SuiteName)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$administrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($administrator -or $identity.User.Value -ne $ExpectedSid) {
    throw 'The test child must run as the newly created ordinary user.'
}
$profileKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\' + $ExpectedSid
$profilePath = [Environment]::ExpandEnvironmentVariables((Get-ItemProperty -LiteralPath $profileKey -Name ProfileImagePath).ProfileImagePath)
if (-not [IO.Path]::IsPathFullyQualified($profilePath)) { throw 'The new user profile path is not absolute.' }
$env:USERPROFILE = $profilePath
$env:LOCALAPPDATA = Join-Path $profilePath 'AppData/Local'
$env:APPDATA = Join-Path $profilePath 'AppData/Roaming'
$temporary = Join-Path $env:LOCALAPPDATA 'Temp'
New-Item -ItemType Directory -Path $temporary -Force | Out-Null
$env:TEMP = $temporary
$env:TMP = $temporary
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:CFSHARP_EXPECTED_TEST_SID = $ExpectedSid
$env:CFSHARP_EXPECTED_TEST_ARCHITECTURE = $ExpectedArchitecture
New-Item -ItemType Directory -Path $Results -Force | Out-Null
@{
    sid = $identity.User.Value
    administrator = $administrator
    executorArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    expectedTestArchitecture = $ExpectedArchitecture
    osVersion = [Environment]::OSVersion.VersionString
    temporaryDirectory = $temporary
    filter = $Filter
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Results 'identity.json')
& $Dotnet --info | Set-Content -LiteralPath (Join-Path $Results 'dotnet-info.log')
if ($LASTEXITCODE -ne 0) { throw 'The ordinary-user SDK host could not be inspected.' }
& $Dotnet vstest $Assembly "/Platform:$ExpectedArchitecture" "/TestCaseFilter:$Filter" "/ResultsDirectory:$Results" "/Logger:trx;LogFileName=ordinary-user-$SuiteName.trx"
exit $LASTEXITCODE
'@ | Set-Content -LiteralPath $childScript -Encoding utf8

    $assembly = Join-Path $executionRoot "tests/CfSharp.IntegrationTests/bin/$Configuration/net10.0-windows/CfSharp.IntegrationTests.dll"
    $results = Join-Path $executionRoot 'results'
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    # All quoted arguments are validated configuration, generated identifiers or Windows paths
    # (which cannot contain a quote). No shell or credential text is interpolated into a command.
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'RemoteSigned', '-File', ('"{0}"' -f $childScript), '-Assembly', ('"{0}"' -f $assembly),
        '-Results', ('"{0}"' -f $results), '-Dotnet', ('"{0}"' -f $dotnet), '-ExpectedSid', $account.SID.Value, '-ExpectedArchitecture', $env:RUNNER_ARCH,
        '-Filter', ('"{0}"' -f $filter), '-SuiteName', $suiteName)
    $process = Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList $arguments -Credential $credential `
        -LoadUserProfile -UseNewEnvironment -WorkingDirectory $executionRoot -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $executionRoot 'stdout.log') -RedirectStandardError (Join-Path $executionRoot 'stderr.log')
    if (-not $process.WaitForExit(600000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw 'Fresh-user namespace tests exceeded the bounded ten-minute process budget.'
    }
    if ($process.ExitCode -ne 0) {
        throw "Fresh-user $Suite tests failed with exit code $($process.ExitCode). Inspect the retained original evidence."
    }
    [xml] $report = Get-Content -LiteralPath (Join-Path $results "ordinary-user-$suiteName.trx") -Raw
    $counts = $report.TestRun.ResultSummary.Counters
    if ([int] $counts.total -ne $expectedCount -or [int] $counts.passed -ne $expectedCount -or [int] $counts.executed -ne $expectedCount) {
        throw "All $expectedCount required file/empty-directory cases must execute and pass for the new ordinary user."
    }
    if ($Suite -eq 'Recreation') {
        $cases = @($report.TestRun.Results.UnitTestResult)
        foreach ($kind in @('False', 'True')) {
            if (@($cases | Where-Object { $_.testName -match "ExplicitSuccessorAtRetiredPathPreservesBothGenerationsAcrossReopen\(directory: $kind\)" -and $_.outcome -eq 'Passed' }).Count -ne 1) {
                throw 'Each distinct file/directory recreation case must pass exactly once.'
            }
        }
    }
    Write-Output "Fresh ordinary-user $Suite recovery passed: $expectedCount executed, $expectedCount passed."
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit()
    }
    foreach ($name in @('stdout.log', 'stderr.log', 'results')) {
        $path = Join-Path $executionRoot $name
        if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $evidence -Recurse -Force }
    }
    if ($null -ne $account) { Remove-LocalUser -SID $account.SID }
    if ($null -ne $password) { $password.Dispose() }
    # The disposable runner reclaims the unloaded profile and public execution copy. Retaining
    # those paths on failure also avoids hiding original native/SQLite evidence during diagnosis.
}
