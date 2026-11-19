[CmdletBinding()]
param([Parameter(Mandatory)][string]$ConsumerRoot, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted') {
    throw 'Unpackaged consumer comparison requires a disposable GitHub-hosted runner.'
}
$consumer = (Resolve-Path -LiteralPath $ConsumerRoot).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$payloads = @(Get-ChildItem -LiteralPath (Join-Path $consumer 'src/MirrorPulse.App/obj/Release') -Filter MirrorPulse.Host.exe -File -Recurse |
    Where-Object { $_.FullName -like '*MirrorPulseMsixPayload*host*' })
if ($payloads.Count -ne 1) { throw 'The comparison requires exactly one original published Host payload.' }
$hostDirectory = Join-Path $output 'host'
Copy-Item -LiteralPath $payloads[0].Directory.FullName -Destination $hostDirectory -Recurse
# Resolve the original public packages in a separate cache. Neither the candidate feed nor
# the product lockfiles participate in this baseline resolution.
$project = Join-Path $output 'Reference.csproj'
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><IsPackable>false</IsPackable></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="CfSharp" Version="0.1.0-preview.14" />
    <PackageReference Include="CfSharp.Native" Version="0.1.0-preview.14" />
    <PackageReference Include="CfSharp.Storage.Sqlite" Version="0.1.0-preview.14" />
  </ItemGroup>
</Project>
'@ | Set-Content -LiteralPath $project -Encoding utf8
$config = Join-Path $output 'NuGet.config'
'<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>' |
    Set-Content -LiteralPath $config -Encoding utf8
$cache = Join-Path $output 'packages'
& dotnet restore $project --configfile $config --packages $cache -p:ImportDirectoryBuildProps=false -p:ManagePackageVersionsCentrally=false -p:RestoreLockedMode=false
if ($LASTEXITCODE -ne 0) { throw 'The original public SQLite consumer inputs could not be restored.' }
$inputs = @(foreach ($name in @('CfSharp', 'CfSharp.Native', 'CfSharp.Storage.Sqlite')) {
    $assets = @(Get-ChildItem -LiteralPath (Join-Path $cache ($name.ToLowerInvariant() + '/0.1.0-preview.14/lib')) -Filter ($name + '.dll') -Recurse -File)
    if ($assets.Count -ne 1) { throw 'The original package must expose one unambiguous managed runtime asset.' }
    $assembly = $assets[0].FullName
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($assembly).ProductVersion
    if ($version -cne '0.1.0-preview.14+663c3dd0bbc8e6785fb2a9817f5ae0738499cf3f') { throw 'The unpackaged baseline library source changed.' }
    Copy-Item -LiteralPath $assembly -Destination (Join-Path $hostDirectory ($name + '.dll')) -Force
    [ordered]@{ name = $name; version = $version; sha256 = (Get-FileHash -LiteralPath $assembly).Hash }
})
$dataRoot = Join-Path $output 'data'
$syncRoot = Join-Path $output 'sync'
$start = [Diagnostics.ProcessStartInfo]::new((Join-Path $hostDirectory 'MirrorPulse.Host.exe'))
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardError = $true
$start.RedirectStandardOutput = $true
$start.ArgumentList.Add('--run-once')
$start.Environment['MIRRORPULSE_DATA_ROOT'] = $dataRoot
$start.Environment['MIRRORPULSE_SYNC_ROOT'] = $syncRoot
$since = [DateTimeOffset]::UtcNow
$process = [Diagnostics.Process]::Start($start)
if ($null -eq $process) { throw 'The unpackaged original Host did not start.' }
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()
try {
    if (-not $process.WaitForExit(120000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw 'The unpackaged original Host did not stop.'
    }
    # Read both streams without publishing user paths; the bounded product diagnostic
    # reader retains the original failure boundary and HRESULT if startup fails.
    [void]$stdout.GetAwaiter().GetResult()
    [void]$stderr.GetAwaiter().GetResult()
    . (Join-Path $consumer 'eng/installed-startup-diagnostics.ps1')
    $diagnostics = Get-InstalledHostStartupDiagnostics -DataRoots @($dataRoot) -Since $since
    [ordered]@{ schemaVersion = 1; processId = $process.Id; exitCode = $process.ExitCode; verified = ($process.ExitCode -eq 0);
        libraryInputs = $inputs; diagnostics = $diagnostics } | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $consumer 'artifacts/evidence/unpackaged-reference.json') -Encoding utf8
    if ($process.ExitCode -ne 0) { throw 'The unpackaged original Host startup failed; its bounded diagnostics were retained.' }
} finally { $process.Dispose() }
