[CmdletBinding()]
param(
    [ValidateSet('Recovery', 'Namespace', 'Content')]
    [string] $Partition,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $VerifyPlan,
    [switch] $CollectCoverage
)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repository 'tests/CfSharp.IntegrationTests/CfSharp.IntegrationTests.csproj'
$results = Join-Path $repository 'tests/CfSharp.IntegrationTests/TestResults'
$planPath = Join-Path $repository 'artifacts/integration-plan.json'
$prefix = 'CfSharp.IntegrationTests.'
$recovery = "FullyQualifiedName~${prefix}RemoteDeleteRecoveryTests|FullyQualifiedName~${prefix}RemoteMoveRecoveryTests"
$namespace = "FullyQualifiedName~${prefix}CloudDirectoryMoveTests|FullyQualifiedName~${prefix}RemoteRecreationTests"
$filters = [ordered]@{
    Recovery = "Category!=LongSoak&($recovery)"
    Namespace = "Category!=LongSoak&($namespace)"
    Content = "Category!=LongSoak&FullyQualifiedName!~${prefix}RemoteDeleteRecoveryTests&FullyQualifiedName!~${prefix}RemoteMoveRecoveryTests&FullyQualifiedName!~${prefix}CloudDirectoryMoveTests&FullyQualifiedName!~${prefix}RemoteRecreationTests"
}
$common = @($project, '--configuration', $Configuration, '--no-build', '--no-restore', '--disable-build-servers', '-m:1')

function Get-TestNames([string] $filter) {
    $listing = @(& dotnet test @common --list-tests --filter $filter 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Integration discovery failed:`n$($listing -join "`n")" }
    $names = @($listing | ForEach-Object {
        $line = $_.ToString()
        if ($line.StartsWith('    ', [StringComparison]::Ordinal)) {
            $name = $line.Trim()
            if (-not $name.StartsWith($prefix, [StringComparison]::Ordinal)) {
                throw "Unexpected integration discovery entry: $name"
            }
            $name
        }
    })
    if ($names.Count -eq 0) { throw "No integration tests discovered for: $filter" }
    $unique = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($name in $names) {
        if (-not $unique.Add($name)) { throw "Duplicate discovered test: $name" }
    }
    return $names
}

# Discover the compiled assembly rather than hard-coding a total. New native tests must
# enter exactly one partition, including additional partial-class tests in later fixes.
$all = @(Get-TestNames 'Category!=LongSoak')
$assigned = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$plans = [ordered]@{}
foreach ($name in $filters.Keys) {
    $plans[$name] = @(Get-TestNames $filters[$name])
    foreach ($test in $plans[$name]) {
        if (-not $assigned.Add($test)) { throw "Integration partitions overlap: $test" }
    }
}
if (@(Compare-Object -ReferenceObject $all -DifferenceObject @($assigned) -CaseSensitive).Count -ne 0) {
    throw 'Integration partitions omit or invent tests.'
}
New-Item -ItemType Directory -Path $results -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $planPath) -Force | Out-Null
$plans | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $planPath -Encoding utf8
Write-Output "Verified integration partition plan: $($all.Count) distinct tests, no omissions or overlaps."
if ($VerifyPlan) { exit 0 }
if ([string]::IsNullOrWhiteSpace($Partition)) { throw 'Select an integration partition, or use -VerifyPlan.' }

$logName = 'integration-' + $Partition.ToLowerInvariant() + '.trx'
$arguments = @('--filter', $filters[$Partition], '--logger', "trx;LogFileName=$logName", '--blame-hang-timeout', '2m')
if ($CollectCoverage) { $arguments += @('--collect', 'XPlat Code Coverage') }
& dotnet test @common @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
[xml] $report = Get-Content -LiteralPath (Join-Path $results $logName) -Raw
$cases = @($report.TestRun.Results.UnitTestResult)
$counts = $report.TestRun.ResultSummary.Counters
$expected = @($plans[$Partition])
$actual = @($cases | ForEach-Object { [string]$_.testName })
$unique = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($test in $actual) {
    if (-not $unique.Add($test)) { throw "Repeated integration result: $test" }
}
if ([int]$counts.total -ne $expected.Count -or [int]$counts.executed -ne $expected.Count -or
    [int]$counts.passed -ne $expected.Count -or @($cases | Where-Object outcome -ne 'Passed').Count -ne 0 -or
    @(Compare-Object -ReferenceObject $expected -DifferenceObject $actual -CaseSensitive).Count -ne 0) {
    throw 'Every discovered test in the integration partition must execute and pass exactly once.'
}
Write-Output "Integration $Partition passed: $($expected.Count) executed, $($expected.Count) passed."
