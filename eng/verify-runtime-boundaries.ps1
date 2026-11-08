[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [ValidateSet('win-x64', 'win-arm64')]
    [string[]]$RuntimeIdentifiers = @('win-x64', 'win-arm64')
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "samples/CfSharp.SampleProvider/CfSharp.SampleProvider.csproj"
$publishRoot = Join-Path $repoRoot "artifacts/runtime-boundaries"

if (-not (Test-Path -LiteralPath $project -PathType Leaf))
{
    throw "Sample project was not found: $project"
}

New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null

# Native AOT on Windows locates the MSVC toolchain through vswhere. Hosted runners usually
# expose it on PATH, while developer machines often install it only in the Visual Studio
# Installer directory. Add the standard location when necessary so this verifier is reproducible
# in a detached checkout and on ordinary local machines.
if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue))
{
    $installerDirectory = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer"
    $vswherePath = Join-Path $installerDirectory "vswhere.exe"
    if (Test-Path -LiteralPath $vswherePath -PathType Leaf)
    {
        $env:Path = "$installerDirectory;$env:Path"
    }
}

function Invoke-SamplePublish
{
    param(
        [Parameter(Mandatory)]
        [string]$RuntimeIdentifier,

        [Parameter(Mandatory)]
        [ValidateSet("trim", "aot")]
        [string]$Mode
    )

    $outputPath = Join-Path $publishRoot "$Mode-$RuntimeIdentifier"
    New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
    $commonArguments = @(
        "publish",
        $project,
        "--configuration", $Configuration,
        "--runtime", $RuntimeIdentifier,
        "--self-contained", "true",
        "--nologo",
        "-p:DebugType=None",
        "-o", $outputPath)
    if ($Mode -eq "trim")
    {
        $modeArguments = @(
            "-p:PublishTrimmed=true",
            "-p:TrimMode=partial",
            "-p:PublishSingleFile=true",
            "-p:ILLinkTreatWarningsAsErrors=false")
    }
    else
    {
        $modeArguments = @(
            "-p:PublishAot=true",
            "-p:StripSymbols=true")
    }

    $output = @(& dotnet @commonArguments @modeArguments 2>&1)
    $exitCode = $LASTEXITCODE
    $logPath = Join-Path $outputPath "publish.log"
    $output | ForEach-Object { $_.ToString() } | Set-Content -Encoding utf8 $logPath
    if ($exitCode -ne 0)
    {
        throw "$Mode publish failed for $RuntimeIdentifier. See $logPath"
    }

    $executable = Join-Path $outputPath "CfSharp.SampleProvider.exe"
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf))
    {
        throw "$Mode publish did not produce the sample executable: $executable"
    }

    if ($Mode -eq "trim")
    {
        $warningLines = @(
            $output |
                ForEach-Object { $_.ToString() } |
                Where-Object { $_ -match "warning IL\d+" })
        $unexpectedWarnings = @(
            $warningLines |
                Where-Object {
                    $_ -notmatch "warning IL2104" -or
                    $_ -notmatch "Microsoft.Windows.SDK.NET|WinRT.Runtime"
                })
        if ($unexpectedWarnings.Count -ne 0)
        {
            throw "Trim publish produced warnings outside the documented WinRT boundary:`n$($unexpectedWarnings -join "`n")"
        }
    }
}

foreach ($runtimeIdentifier in $runtimeIdentifiers)
{
    Invoke-SamplePublish -RuntimeIdentifier $runtimeIdentifier -Mode trim
    Invoke-SamplePublish -RuntimeIdentifier $runtimeIdentifier -Mode aot
}

Write-Output "Runtime boundary verification passed for: $($runtimeIdentifiers -join ', ')"
