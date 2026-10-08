[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-preview\.\d+)?$')]
    [string] $Version = '0.1.0-preview.1',
    [ValidateSet('Preview', 'Stable')]
    [string] $Channel = 'Preview',
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot '..'),
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\preview')
)

$ErrorActionPreference = 'Stop'
$isPreview = $Version -match '-preview\.\d+$'
if (($Channel -eq 'Preview') -ne $isPreview) {
    throw "Version '$Version' does not match the requested $Channel channel."
}
$root = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$outputPath = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
} else {
    Join-Path $root $OutputDirectory
}
$output = [System.IO.Path]::GetFullPath($outputPath)
$versionOutput = Join-Path $output $Version
$packageOutput = Join-Path $versionOutput 'packages'
$smokeRoot = Join-Path $versionOutput 'consumer-smoke'
$repositoryCommit = (git -C $root rev-parse HEAD).Trim()
$outputBoundary = [System.IO.Path]::TrimEndingDirectorySeparator($output) + [System.IO.Path]::DirectorySeparatorChar
if (-not ([System.IO.Path]::GetFullPath($versionOutput)).StartsWith($outputBoundary, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The resolved version output must stay inside the explicitly selected output directory.'
}

if (Test-Path -LiteralPath $versionOutput) {
    Remove-Item -LiteralPath $versionOutput -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $packageOutput | Out-Null

$projects = @(
    'src/CfSharp.Native/CfSharp.Native.csproj',
    'src/CfSharp/CfSharp.csproj',
    'src/CfSharp.Storage.Sqlite/CfSharp.Storage.Sqlite.csproj'
)
foreach ($relativeProject in $projects) {
    $project = Join-Path $root $relativeProject
    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
        throw "Preview project is missing: $relativeProject"
    }

    $packArguments = @(
        $project,
        '--configuration', $Configuration,
        '--no-restore',
        '--disable-build-servers',
        '-m:1',
        '-p:UseSharedCompilation=false',
        '--output', $packageOutput,
        '-p:TargetPlatformDisplayName=Windows',
        "/p:Version=$Version",
        "/p:PackageVersion=$Version",
        '/p:DebugSymbols=true',
        '/p:DebugType=portable',
        '/p:IncludeSymbols=true',
        '/p:SymbolPackageFormat=snupkg'
    )
    & dotnet pack @packArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Preview pack failed for $relativeProject with exit code $LASTEXITCODE."
    }
}

$packageFiles = @(Get-ChildItem -LiteralPath $packageOutput -Filter '*.nupkg' -File)
$symbolFiles = @(Get-ChildItem -LiteralPath $packageOutput -Filter '*.snupkg' -File)
if ($packageFiles.Count -ne $projects.Count) {
    throw "Expected $($projects.Count) preview packages, found $($packageFiles.Count)."
}
if ($symbolFiles.Count -ne $projects.Count) {
    throw "Expected $($projects.Count) symbol packages, found $($symbolFiles.Count)."
}

$expectedPackageIds = @('CfSharp.Native', 'CfSharp', 'CfSharp.Storage.Sqlite')
$packageEvidence = foreach ($package in $packageFiles | Sort-Object Name) {
    $escapedVersion = [regex]::Escape($Version)
    if ($package.Name -notmatch "\.$escapedVersion\.nupkg$") {
        throw "Package has an unexpected preview version: $($package.Name)"
    }

    $archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $nuspecEntry = $archive.Entries |
            Where-Object { $_.FullName -like '*.nuspec' } |
            Select-Object -First 1
        if ($null -eq $nuspecEntry) {
            throw "Package has no nuspec: $($package.Name)"
        }

        $reader = [System.IO.StreamReader]::new($nuspecEntry.Open())
        try {
            [xml] $nuspec = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        $metadata = $nuspec.package.metadata
        if ($metadata.authors -ne 'MirrorPulse Team' -or
            $metadata.license.InnerText -ne 'Apache-2.0' -or
            $metadata.version -ne $Version) {
            throw "Package metadata attribution/license is invalid: $($package.Name)"
        }

        if ([string]$metadata.repository.commit -ne $repositoryCommit) {
            throw "Package source commit does not match HEAD: $($package.Name)"
        }

        if ($expectedPackageIds -notcontains [string]$metadata.id) {
            throw "Unexpected package id: $($metadata.id)"
        }

        $symbol = $symbolFiles |
            Where-Object { $_.Name -eq "$($package.BaseName).snupkg" } |
            Select-Object -First 1
        if ($null -eq $symbol) {
            throw "Missing symbol package for $($package.Name)"
        }

        $dependencies = @($nuspec.SelectNodes(
            "//*[local-name()='dependencies']/*[local-name()='group']/*[local-name()='dependency']") | ForEach-Object {
            [pscustomobject]@{
                id = [string]$_.id
                version = [string]$_.version
            }
        })

        [pscustomobject]@{
            id = [string]$metadata.id
            version = [string]$metadata.version
            file = $package.Name
            bytes = $package.Length
            sha256 = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash
            symbolFile = $symbol.Name
            symbolBytes = $symbol.Length
            symbolSha256 = (Get-FileHash -LiteralPath $symbol.FullName -Algorithm SHA256).Hash
            dependencies = $dependencies
            repositoryCommit = [string]$metadata.repository.commit
            entries = @($archive.Entries | ForEach-Object FullName)
        }
    }
    finally {
        $archive.Dispose()
    }
}

if (Test-Path -LiteralPath $smokeRoot) {
    Remove-Item -LiteralPath $smokeRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null
$consumerProject = Join-Path $smokeRoot 'PreviewConsumer.csproj'
$consumerProgram = Join-Path $smokeRoot 'Program.cs'
$consumerNuGetConfig = Join-Path $smokeRoot 'NuGet.config'
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
    <RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>
    <PublishAot>true</PublishAot>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Remove="Microsoft.DotNet.ILCompiler;Microsoft.NET.ILLink.Tasks" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="CfSharp.Native" Version="$Version" />
    <PackageReference Include="CfSharp" Version="$Version" />
    <PackageReference Include="CfSharp.Storage.Sqlite" Version="$Version" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $consumerProject -Encoding utf8
@'
using System.Runtime.Versioning;

using CfSharp;
using CfSharp.Storage.Sqlite;

await Smoke.VerifyPagingAsync();
if (args.Contains("--native", StringComparer.Ordinal))
{
    if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
    {
        throw new PlatformNotSupportedException("Native package smoke requires Cloud Files.");
    }

    await Smoke.VerifyNativeAsync();
}

Console.WriteLine("Package capability smoke passed.");

internal static class Smoke
{
    internal static async Task VerifyPagingAsync()
    {
        string area = Path.Combine(Path.GetTempPath(), "CfSharp-package-paging", Guid.NewGuid().ToString("N"));
        string root = Path.Combine(area, "root");
        Directory.CreateDirectory(root);
        try
        {
            await using ICloudStateStore store = await new SqliteCloudStateStoreFactory(Path.Combine(area, "state.db"))
                .OpenAsync(new CloudStateStoreContext(root));
            Guid first = Guid.NewGuid();
            await using (ICloudStateTransaction write = await store.BeginTransactionAsync())
            {
                await write.Operations.EnqueueAsync(new(first, CloudStateOperationKind.Create, null, new byte[] { 1 }, DateTimeOffset.UtcNow));
                await write.Operations.EnqueueAsync(new(Guid.NewGuid(), CloudStateOperationKind.Delete, null, new byte[] { 2 }, DateTimeOffset.UtcNow));
                await write.CommitAsync();
            }

            await using ICloudStateTransaction read = await store.BeginTransactionAsync();
            ICloudOperationJournalPaging paging = read.Operations as ICloudOperationJournalPaging
                ?? throw new InvalidOperationException("The package lacks official SQLite paging.");
            long through = await paging.GetHighWaterSequenceAsync();
            CloudOperationJournalPage page = await paging.ReadPageAsync(0, through, 1);
            if (page.Operations.Count != 1 || page.Operations[0].OperationId != first || !page.HasMore ||
                (await paging.ReadPageAsync(page.LastScannedSequence, through, 1)).Operations.Count != 1 ||
                (await read.Operations.ListAsync(4)).Count != 2)
            {
                throw new InvalidOperationException("Package paging lost pending identities or its finite boundary.");
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(area, recursive: true);
        }
    }

    [SupportedOSPlatform("windows10.0.16299")]
    internal static async Task VerifyNativeAsync()
    {
        string area = Path.Combine(Path.GetTempPath(), "CfSharp-package-native", Guid.NewGuid().ToString("N"));
        string root = Path.Combine(area, "root");
        Directory.CreateDirectory(root);
        Guid provider = Guid.NewGuid();
        bool registered = false;
        try
        {
            await using CloudFileSystem system = CloudFileSystem.CreateBuilder(root)
                .WithStateStore(new SqliteCloudStateStoreFactory(Path.Combine(area, "state.db")))
                .WithRegistration(SyncRootRegistrationOptions.CreateBuilder("CfSharp Package Smoke", "1.0-test")
                    .WithProviderId(provider).WithSyncRootIdentity(provider.ToByteArray())
                    .WithHydrationPolicy(CloudHydrationPolicy.Progressive).WithPopulationPolicy(CloudPopulationPolicy.Partial)
                    .WithRootMarkedInSync().Build()).WithContentProvider(new EmptyProvider()).Build();
            await system.StartAsync();
            registered = true;
            await system.Root.CreatePlaceholderAsync(CloudDirectoryPlaceholderSpec.CreateBuilder("Docs", "directory")
                .WithPopulationState(CloudDirectoryPopulationState.Complete).Build());
            await system.GetDirectory("Docs").CreatePlaceholderAsync(CloudFilePlaceholderSpec.CreateBuilder("child.txt", "child", 0).Build());
            CloudLocalChangeFeed feed = system.CreateLocalChangeFeed();
            await feed.StartAsync();
            CloudDirectory original = system.GetDirectory("Docs");
            CloudDirectoryMoveProof proof = await original.PrepareMoveAsync(system.Root, "Moved");
            string applicationProofPath = Path.Combine(area, "move-proof.bin");
            await File.WriteAllBytesAsync(applicationProofPath, proof.Encode());
            Directory.Move(original.FullPath, Path.Combine(root, "Moved"));
            CloudDirectoryMoveProof retained = CloudDirectoryMoveProof.Decode(await File.ReadAllBytesAsync(applicationProofPath));
            CloudDirectoryMoveReconciliationResult result = await original.ReconcileMoveAsync(retained);
            if (!result.NativeMoveObserved || !result.DurableProjectionCommitted || result.RequiresFullRescan ||
                result.Outcome is not (CloudDirectoryMoveReconciliationOutcome.Projected or CloudDirectoryMoveReconciliationOutcome.AlreadyProjected))
            {
                throw new InvalidOperationException("Package proof recovery failed.", result.Error);
            }

            CloudItemMoveResult replay = await original.MoveToAsync(system.Root, "Moved", new CloudMoveOptions(retained));
            if (replay.DirectoryReconciliation?.Outcome != CloudDirectoryMoveReconciliationOutcome.AlreadyProjected)
            {
                throw new InvalidOperationException("The package cannot replay an original source proof.");
            }

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
            bool observed = false;
            while (!observed)
            {
                CloudLocalChangeScan scan = await feed.BeginScanAsync(timeout.Token);
                long after = 0;
                do
                {
                    CloudLocalChangePage page = await feed.ReadPageAsync(scan, after, 4, timeout.Token);
                    if (page.RequiresFullRescan)
                    {
                        throw new InvalidOperationException("Package native scan requires unexpected reconciliation.");
                    }

                    observed |= page.Changes.Any(change => change.Kind == CloudLocalChangeKind.Move && change.RelativePath == "Moved");
                    after = page.LastScannedSequence;
                    if (!page.HasMore)
                    {
                        break;
                    }
                } while (true);
                if (!observed)
                {
                    await Task.Delay(20, timeout.Token);
                }
            }
        }
        finally
        {
            if (registered)
            {
                CloudSyncRoot.Open(root).Unregister();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(area, recursive: true);
        }
    }

    private sealed class EmptyProvider : ICloudFileContentProvider
    {
        public ValueTask<Stream> OpenReadAsync(CloudFileFetchRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Stream>(new MemoryStream());
    }
}
'@ | Set-Content -LiteralPath $consumerProgram -Encoding utf8
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="preview" value="$packageOutput" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="preview">
      <package pattern="CfSharp*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath $consumerNuGetConfig -Encoding utf8

$savedUserProfile = $env:USERPROFILE
$savedAppData = $env:APPDATA
$savedDotnetCliHome = $env:DOTNET_CLI_HOME
$consumerUserProfile = Join-Path $versionOutput '.consumer-user'
$env:USERPROFILE = $consumerUserProfile
$env:APPDATA = Join-Path $consumerUserProfile 'AppData/Roaming'
$env:DOTNET_CLI_HOME = Join-Path $consumerUserProfile '.dotnet'
New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:DOTNET_CLI_HOME | Out-Null
$consumerEvidence = [System.Collections.Generic.List[object]]::new()
$initialCache = Join-Path $versionOutput 'initial-packages'
$lockedCache = Join-Path $versionOutput 'locked-packages'
$hostRuntime = 'win-' + [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
if (-not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) {
    $installerDirectory = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer'
    if (Test-Path -LiteralPath (Join-Path $installerDirectory 'vswhere.exe') -PathType Leaf) {
        $env:Path = "$installerDirectory;$env:Path"
    }
}
try {
    & dotnet restore $consumerProject --configfile $consumerNuGetConfig --packages $initialCache `
        -p:RestoreLockedMode=false -p:TargetPlatformDisplayName=Windows
    if ($LASTEXITCODE -ne 0) {
        throw "Preview consumer initial restore failed with exit code $LASTEXITCODE."
    }
    & dotnet restore $consumerProject --configfile $consumerNuGetConfig --packages $lockedCache --locked-mode `
        -p:TargetPlatformDisplayName=Windows
    if ($LASTEXITCODE -ne 0) {
        throw "Preview consumer restore failed with exit code $LASTEXITCODE."
    }
    foreach ($runtime in @('win-x64', 'win-arm64')) {
        foreach ($mode in @('trim', 'aot')) {
            $consumerOutput = Join-Path $versionOutput "$mode-$runtime"
            & dotnet publish $consumerProject --configuration $Configuration --no-restore --runtime $runtime `
                --self-contained true --disable-build-servers -m:1 -p:UseSharedCompilation=false `
                "-p:PublishAot=$($mode -eq 'aot')" -p:PublishTrimmed=true -p:ILLinkTreatWarningsAsErrors=true `
                -p:TargetPlatformDisplayName=Windows --output $consumerOutput
            if ($LASTEXITCODE -ne 0) {
                throw "Preview $mode consumer publish failed for $runtime with exit code $LASTEXITCODE."
            }
            $executable = Join-Path $consumerOutput 'PreviewConsumer.exe'
            if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
                throw "Preview $mode consumer executable is missing for $runtime."
            }
            $executed = $runtime -eq $hostRuntime
            if ($executed) {
                & $executable --native
                if ($LASTEXITCODE -ne 0) {
                    throw "Preview $mode native consumer failed for $runtime with exit code $LASTEXITCODE."
                }
            }
            $consumerEvidence.Add([pscustomobject]@{ runtime = $runtime; mode = $mode; compiled = $true; executed = $executed })
        }
    }
}
finally {
    $env:USERPROFILE = $savedUserProfile
    $env:APPDATA = $savedAppData
    $env:DOTNET_CLI_HOME = $savedDotnetCliHome
}

$repositoryCommit = (git -C $root rev-parse HEAD).Trim()
$created = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
$sbomPackages = foreach ($package in $packageEvidence) {
    [ordered]@{
        SPDXID = "SPDXRef-Package-$($package.id)"
        name = $package.id
        versionInfo = $package.version
        downloadLocation = 'NOASSERTION'
        filesAnalyzed = $false
        licenseConcluded = 'Apache-2.0'
        licenseDeclared = 'Apache-2.0'
        copyrightText = 'Copyright (c) MirrorPulse Team'
    }
}
$sbomRelationships = foreach ($package in $packageEvidence) {
    [ordered]@{
        spdxElementId = 'SPDXRef-DOCUMENT'
        relationshipType = 'DESCRIBES'
        relatedSpdxElement = "SPDXRef-Package-$($package.id)"
    }
}
$sbom = [ordered]@{
    spdxVersion = 'SPDX-2.3'
    dataLicense = 'CC0-1.0'
    SPDXID = 'SPDXRef-DOCUMENT'
    name = "CfSharp $Version $($Channel.ToLowerInvariant()) package set"
    documentNamespace = "https://mirrorpulse.github.io/cfsharp/sbom/$Version"
    creationInfo = [ordered]@{
        created = $created
        creators = @('Tool: CfSharp preview dry-run')
    }
    packages = @($sbomPackages)
    relationships = @($sbomRelationships)
}
$sbomPath = Join-Path $versionOutput 'sbom.spdx.json'
$sbom | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $sbomPath -Encoding utf8

$markdownTick = [char]96
$packageLines = ($packageEvidence | ForEach-Object {
    "- $($_.id) $($_.version) ($markdownTick$($_.file)$markdownTick, symbols $markdownTick$($_.symbolFile)$markdownTick)"
}) -join [Environment]::NewLine
$publicationNote = if ($isPreview) {
    'This file is generated by eng/prepare-preview.ps1 and is a non-publishing preview dry-run.'
} else {
    'This file is generated by eng/prepare-preview.ps1 for the stable release candidate package set.'
}
$publicationChecks = if ($isPreview) {
    '- No NuGet push, tag, GitHub release, or MSIX signing is performed.'
} else {
    '- Publication is performed only by the protected stable release workflow after verification.'
}
@"
# CfSharp $Version

$publicationNote

## Packages

$packageLines

## Verification

- Package metadata requires MirrorPulse Team attribution and Apache-2.0 licensing.
- Package and symbol versions are $Version.
- The clean consumer restores and builds all three packages from the isolated preview source.
- The generated SPDX 2.3 document is sbom.spdx.json.
$publicationChecks
"@ | Set-Content -LiteralPath (Join-Path $versionOutput 'release-notes.md') -Encoding utf8

@"
# CfSharp $Version support matrix

| Area | $Channel policy |
| --- | --- |
| Target framework | net10.0-windows |
| Windows baseline | Windows 10 build 16299 for core APIs |
| Capability gates | Rich status 17134; provider progress V2 17763; placeholder management 784; restart/force convert 1280; range information 1536 |
| Architectures | win-x64 and win-arm64 |
| x86 | Not supported |
| Native component | CfSharp.Native exposes the complete Cloud Files ABI surface |
| Durable state | CfSharp.Storage.Sqlite requires a caller-supplied database path outside the managed sync root |

$(if ($isPreview) {
    'This preview is not a stable 1.0.0 compatibility promise. API, native ABI, and behavior changes remain subject to the compatibility policy before stable release.'
} else {
    'This stable package set follows the published compatibility policy for the 1.0.0 release line.'
})
"@ | Set-Content -LiteralPath (Join-Path $versionOutput 'support-matrix.md') -Encoding utf8

$packageProject = Join-Path $root 'samples/CfSharp.SampleProvider.Package/CfSharp.SampleProvider.Package.wapproj'
[xml] $packageProjectXml = Get-Content -LiteralPath $packageProject -Raw
$packagePropertyGroup = @($packageProjectXml.Project.PropertyGroup) |
    Where-Object { $_.Platforms -or $_.TargetPlatformMinVersion } |
    Select-Object -First 1
$platforms = [string]$packagePropertyGroup.Platforms
$minimumVersion = [string]$packagePropertyGroup.TargetPlatformMinVersion

$manifest = [pscustomobject]@{
    version = $Version
    channel = $Channel.ToLowerInvariant()
    configuration = $Configuration
    repositoryCommit = $repositoryCommit
    packages = @($packageEvidence)
    symbols = @($symbolFiles | Sort-Object Name | ForEach-Object {
        [pscustomobject]@{
            file = $_.Name
            bytes = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
    sbom = [pscustomobject]@{
        file = 'sbom.spdx.json'
        format = 'SPDX-2.3'
        sha256 = (Get-FileHash -LiteralPath $sbomPath -Algorithm SHA256).Hash
    }
    releaseNotes = 'release-notes.md'
    supportMatrix = 'support-matrix.md'
    consumerSmoke = 'passed'
    consumerCapabilities = @($consumerEvidence)
    consumerLockSha256 = (Get-FileHash -LiteralPath (Join-Path $smokeRoot 'packages.lock.json') -Algorithm SHA256).Hash
    msix = [pscustomobject]@{
        project = 'samples/CfSharp.SampleProvider.Package/CfSharp.SampleProvider.Package.wapproj'
        platforms = $platforms -split ';'
        targetPlatformMinVersion = $minimumVersion
        signing = 'not-required-for-sample'
        build = 'not-invoked'
    }
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $versionOutput 'manifest.json')
Write-Output "Package preparation completed: $versionOutput"
