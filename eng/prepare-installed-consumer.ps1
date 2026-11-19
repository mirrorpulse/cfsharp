[CmdletBinding()]
param([Parameter(Mandatory)][string]$ConsumerRoot, [switch]$Candidate)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted') {
    throw 'Installed consumer qualification requires a disposable GitHub-hosted runner.'
}
$library = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$consumer = (Resolve-Path -LiteralPath $ConsumerRoot).Path
$source = (& git -C $library rev-parse HEAD).Trim()
$consumerSource = (& git -C $consumer rev-parse HEAD).Trim()
if ($source -cnotmatch '^[0-9a-f]{40}$' -or $consumerSource -cne '12fadb56299e8c10ce3b141e2149ef7601c86807') {
    throw 'The qualification source revisions are invalid.'
}
$output = Join-Path $library 'artifacts/installed-consumer'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$version = '0.1.0-preview.14'
if ($Candidate) {
    $version = '0.0.0-preview.' + $env:GITHUB_RUN_NUMBER
    if ($version -cnotmatch '^0\.0\.0-preview\.[1-9][0-9]*$') { throw 'Invalid isolated package version.' }
    Push-Location $library
    try {
        & dotnet restore CfSharp.sln --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'Candidate restore failed.' }
        foreach ($project in @('CfSharp.Native', 'CfSharp', 'CfSharp.Storage.Sqlite')) {
            & dotnet pack "src/$project/$project.csproj" -c Release --no-restore --output "$output/feed" "-p:Version=$version" "-p:PackageVersion=$version"
            if ($LASTEXITCODE -ne 0) { throw "Candidate pack failed: $project." }
        }
    } finally { Pop-Location }
    $props = Join-Path $consumer 'Directory.Packages.props'
    $original = [IO.File]::ReadAllText($props)
    $updated = [regex]::Replace($original, '(<PackageVersion Include="CfSharp(?:\.Storage\.Sqlite)?" Version=")0\.1\.0-preview\.14("\s*/>)', ('${1}' + $version + '${2}'))
    if ($updated -ceq $original) { throw 'The candidate package overlay did not match the pinned consumer.' }
    [IO.File]::WriteAllText($props, $updated)
    $collector = Join-Path $consumer 'eng/installed-runtime-originals.ps1'
    $original = [IO.File]::ReadAllText($collector)
    $expected = '0.1.0-preview.14+663c3dd0bbc8e6785fb2a9817f5ae0738499cf3f'
    if (-not $original.Contains($expected)) { throw 'The pinned installed input collector changed.' }
    [IO.File]::WriteAllText($collector, $original.Replace($expected, "$version+$source"))
    Push-Location $consumer
    try {
        & dotnet nuget add source "$output/feed" --name CfSharpCandidate --configfile NuGet.config
        if ($LASTEXITCODE -ne 0) { throw 'Could not select the isolated candidate feed.' }
        $configPath = Join-Path $consumer 'NuGet.config'
        [xml]$config = Get-Content -LiteralPath $configPath -Raw
        if ($config.configuration.packageSourceMapping) {
            $mapping = $config.CreateElement('packageSource')
            $mapping.SetAttribute('key', 'CfSharpCandidate')
            $pattern = $config.CreateElement('package')
            $pattern.SetAttribute('pattern', 'CfSharp*')
            [void]$mapping.AppendChild($pattern)
            [void]$config.configuration.packageSourceMapping.AppendChild($mapping)
            $config.Save($configPath)
        }
        # Re-evaluate only the CfSharp package inputs, then require locked restore below.
        $locks = @(Get-ChildItem src, tests -Filter packages.lock.json -Recurse -File)
        $dependencies = @{}
        foreach ($lock in $locks) { $dependencies[$lock.FullName] = (Get-Content -LiteralPath $lock.FullName -Raw | ConvertFrom-Json -AsHashtable).dependencies }
        & pwsh ./eng/restore-adapter-sdk.ps1
        if ($LASTEXITCODE -ne 0) { throw 'Pinned adapter SDK restore failed.' }
        & dotnet restore MirrorPulse.sln --force-evaluate -p:RestoreLockedMode=false
        if ($LASTEXITCODE -ne 0) { throw 'Candidate consumer restore failed.' }
        foreach ($lock in $locks) {
            $after = (Get-Content -LiteralPath $lock.FullName -Raw | ConvertFrom-Json -AsHashtable).dependencies
            foreach ($target in $dependencies[$lock.FullName].Keys) {
                foreach ($name in $dependencies[$lock.FullName][$target].Keys) {
                    if ($name -clike 'CfSharp*') { continue }
                    foreach ($node in @($dependencies[$lock.FullName][$target][$name], $after[$target][$name])) {
                        if ($node.dependencies) {
                            foreach ($edge in @($node.dependencies.Keys)) {
                                if ($edge -clike 'CfSharp*') { $node.dependencies.Remove($edge) }
                            }
                        }
                    }
                    $beforeJson = $dependencies[$lock.FullName][$target][$name] | ConvertTo-Json -Depth 20 -Compress
                    $afterJson = $after[$target][$name] | ConvertTo-Json -Depth 20 -Compress
                    if ($beforeJson -cne $afterJson) { throw "The candidate changed an unrelated locked dependency: $name." }
                }
            }
        }
    } finally { Pop-Location }
}
[ordered]@{ sourceSha = $source; consumerSha = $consumerSource; candidate = $Candidate.IsPresent; packageVersion = $version } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'inputs.json') -Encoding utf8
