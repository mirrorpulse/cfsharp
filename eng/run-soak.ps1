[CmdletBinding()]
param(
    [ValidateRange(1, 1440)]
    [int] $DurationMinutes = 30,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot '..'),
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\soak')
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$outputPath = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
} else {
    Join-Path $root $OutputDirectory
}
$output = [System.IO.Path]::GetFullPath($outputPath)
New-Item -ItemType Directory -Force -Path $output | Out-Null

$runtimeArtifact = Join-Path $output 'runtime-soak.json'
$confirmationArtifact = Join-Path $output 'confirmation-soak.json'
$runnerArtifact = Join-Path $output 'runner-summary.json'
$savedDuration = $env:CFSHARP_SOAK_DURATION_MINUTES
$savedArtifact = $env:CFSHARP_SOAK_ARTIFACT
$savedConfirmationArtifact = $env:CFSHARP_CONFIRMATION_SOAK_ARTIFACT
$status = 'failed'
$exitCode = 1
$confirmationProcess = $null
$env:CFSHARP_SOAK_DURATION_MINUTES = $DurationMinutes.ToString(
    [System.Globalization.CultureInfo]::InvariantCulture)
$env:CFSHARP_SOAK_ARTIFACT = $runtimeArtifact
$env:CFSHARP_CONFIRMATION_SOAK_ARTIFACT = $confirmationArtifact

try {
    # Build once before the two test processes start so shared assemblies and the independent
    # native writer harness are never overwritten during an active resource measurement.
    & dotnet build CfSharp.sln --configuration $Configuration --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "Soak solution build failed with exit code $LASTEXITCODE."
    }
    $confirmationArguments = @(
        'test', 'tests/CfSharp.IntegrationTests/CfSharp.IntegrationTests.csproj',
        '--configuration', $Configuration, '--no-build', '--no-restore',
        '--filter', 'Category=LongSoak', '--logger', 'trx;LogFileName=confirmation-soak.trx',
        '--results-directory', "`"$output`"", '--blame-hang-timeout', "$($DurationMinutes + 5)m")
    # Both loops receive the full requested duration concurrently. This preserves the existing
    # manual CI wall-time budget instead of doubling it when native confirmation is added.
    $confirmationProcess = Start-Process dotnet -ArgumentList $confirmationArguments `
        -WorkingDirectory $root -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $output 'confirmation-console.log') `
        -RedirectStandardError (Join-Path $output 'confirmation-error.log')
    & dotnet test tests/CfSharp.Tests/CfSharp.Tests.csproj `
        --configuration $Configuration `
        --no-build `
        --no-restore `
        --filter 'Category=LongSoak' `
        --logger 'trx;LogFileName=long-soak.trx' `
        --results-directory $output `
        --blame-hang-timeout "$($DurationMinutes + 5)m"
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "Long-soak test failed with exit code $exitCode."
    }

    $confirmationProcess.WaitForExit()
    $exitCode = $confirmationProcess.ExitCode
    if ($exitCode -ne 0) {
        throw "Native confirmation soak failed with exit code $exitCode."
    }

    $status = 'passed'
    Write-Output "Continuous long-soak completed: $DurationMinutes minute(s)."
}
finally {
    if ($null -ne $confirmationProcess) {
        if (-not $confirmationProcess.HasExited) {
            $confirmationProcess.Kill($true)
            $confirmationProcess.WaitForExit()
        }
        $confirmationProcess.Dispose()
    }
    [pscustomobject]@{
        status = $status
        durationMinutes = $DurationMinutes
        testCategory = 'LongSoak'
        commit = (git -C $root rev-parse HEAD).Trim()
        runtimeArtifact = [System.IO.Path]::GetFileName($runtimeArtifact)
        confirmationArtifact = [System.IO.Path]::GetFileName($confirmationArtifact)
        completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json | Set-Content -LiteralPath $runnerArtifact -Encoding utf8

    if ($null -eq $savedDuration) {
        Remove-Item Env:CFSHARP_SOAK_DURATION_MINUTES -ErrorAction SilentlyContinue
    } else {
        $env:CFSHARP_SOAK_DURATION_MINUTES = $savedDuration
    }
    if ($null -eq $savedArtifact) {
        Remove-Item Env:CFSHARP_SOAK_ARTIFACT -ErrorAction SilentlyContinue
    } else {
        $env:CFSHARP_SOAK_ARTIFACT = $savedArtifact
    }
    if ($null -eq $savedConfirmationArtifact) {
        Remove-Item Env:CFSHARP_CONFIRMATION_SOAK_ARTIFACT -ErrorAction SilentlyContinue
    } else {
        $env:CFSHARP_CONFIRMATION_SOAK_ARTIFACT = $savedConfirmationArtifact
    }
}

if ($status -ne 'passed') {
    throw "The continuous long-soak did not pass. See $runnerArtifact and the test results."
}
