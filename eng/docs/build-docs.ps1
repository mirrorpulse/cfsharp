[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Version = '0.1.0-preview.1'
)

$ErrorActionPreference = 'Stop'
$docsDirectory = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $docsDirectory '..\..')).Path
$artifactRoot = Join-Path $repoRoot 'artifacts\docs'
$workspaceRoot = Join-Path $artifactRoot 'workspace'
$outputRoot = Join-Path $artifactRoot 'cfsharp'
$articlesRoot = Join-Path $workspaceRoot 'articles'
$manifestPath = Join-Path $outputRoot 'manifest.json'

function Set-Utf8File {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [Parameter(Mandatory)] [string]$Value
    )

    Set-Content -LiteralPath $Path -Value $Value -Encoding utf8NoBOM
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory)] [string]$Command,
        [Parameter(Mandatory)] [string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE`: $Command $($Arguments -join ' ')"
    }
}

if (-not (Test-Path (Join-Path $repoRoot 'README.md'))) {
    throw "Repository root was not detected: $repoRoot"
}

if (Test-Path $artifactRoot) {
    Remove-Item -LiteralPath $artifactRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $articlesRoot -Force | Out-Null
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

Push-Location $repoRoot
try {
    Invoke-Checked 'dotnet' @('restore', 'CfSharp.sln')
    Invoke-Checked 'dotnet' @('build', 'CfSharp.sln', '--configuration', $Configuration, '--no-restore', '--disable-build-servers', '-m:1')

    $readmeText = Get-Content -LiteralPath (Join-Path $repoRoot 'README.md') -Raw
    $readmeText = $readmeText.Replace('](docs/','](articles/')
    $readmeText = $readmeText.Replace('](samples/CfSharp.SampleProvider)','](https://github.com/mirrorpulse/cfsharp/tree/main/samples/CfSharp.SampleProvider)')
    $readmeText = $readmeText.Replace('](global.json)','](https://github.com/mirrorpulse/cfsharp/blob/main/global.json)')
    $readmeText = $readmeText.Replace('](CONTRIBUTING.md)','](https://github.com/mirrorpulse/cfsharp/blob/main/CONTRIBUTING.md)')
    $readmeText = $readmeText.Replace('](SECURITY.md)','](https://github.com/mirrorpulse/cfsharp/blob/develop/SECURITY.md)')
    $readmeText = $readmeText.Replace('](LICENSE)','](https://github.com/mirrorpulse/cfsharp/blob/main/LICENSE)')
    Set-Utf8File (Join-Path $workspaceRoot 'index.md') $readmeText

    $sourceDocsRoot = Join-Path $repoRoot 'docs'
    foreach ($sourceFile in Get-ChildItem -LiteralPath $sourceDocsRoot -Recurse -File -Filter '*.md') {
        $relativePath = [System.IO.Path]::GetRelativePath($sourceDocsRoot, $sourceFile.FullName)
        $destination = Join-Path $articlesRoot $relativePath
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $sourceFile.FullName -Destination $destination -Force
    }

    $toc = @'
- name: Overview
  href: index.md
  items:
    - name: Documentation home
      href: articles/index.md
- name: Guides
  items:
    - name: Getting started
      href: articles/getting-started.md
    - name: Installation and prerequisites
      href: articles/installation.md
    - name: Architecture and ownership
      href: articles/architecture.md
    - name: Sync-root lifecycle
      href: articles/sync-root-lifecycle.md
    - name: Placeholders and hydration
      href: articles/placeholders.md
    - name: Local change feed
      href: articles/local-change-feed.md
    - name: Remote change application
      href: articles/remote-change-application.md
    - name: SQLite state store
      href: articles/state-store-sqlite.md
    - name: CfSharp Sample
      href: articles/sample-provider.md
    - name: Platform support
      href: articles/platform-support.md
    - name: Troubleshooting
      href: articles/troubleshooting.md
    - name: Contributing
      href: articles/contributing.md
- name: API reference
  href: api/toc.yml
'@
    Set-Utf8File (Join-Path $workspaceRoot 'toc.yml') $toc

    Push-Location $docsDirectory
    try {
        Invoke-Checked 'dotnet' @('tool', 'run', 'docfx', 'metadata', 'docfx.json')
        Invoke-Checked 'dotnet' @('tool', 'run', 'docfx', 'build', 'docfx.json')
    }
    finally {
        Pop-Location
    }

    if (-not (Test-Path (Join-Path $outputRoot 'index.html'))) {
        throw "DocFX did not produce $outputRoot\index.html"
    }

    # DocFX sees the staged integration tree rather than the repository's
    # authored files. Rewrite contribution links back to their real sources
    # before publishing the bundle; otherwise "Edit this page" points at the
    # non-existent artifacts/docs/workspace tree.
    $sourceLinkReplacements = @(
        @{
            Old = 'https://github.com/mirrorpulse/cfsharp/blob/main/artifacts/docs/workspace/index.md/#L'
            New = 'https://github.com/mirrorpulse/cfsharp/blob/main/README.md#L'
        },
        @{
            Old = 'https://github.com/mirrorpulse/cfsharp/blob/main/artifacts/docs/workspace/articles/'
            New = 'https://github.com/mirrorpulse/cfsharp/blob/main/docs/'
        }
    )

    foreach ($htmlFile in Get-ChildItem -LiteralPath $outputRoot -Recurse -File -Filter '*.html') {
        $htmlText = Get-Content -LiteralPath $htmlFile.FullName -Raw
        $rewritten = $htmlText
        foreach ($replacement in $sourceLinkReplacements) {
            $rewritten = $rewritten.Replace($replacement.Old, $replacement.New)
        }
        $rewritten = [regex]::Replace(
            $rewritten,
            'https://github\.com/mirrorpulse/cfsharp/blob/[^/]+/artifacts/docs/workspace/index\.md/#L',
            'https://github.com/mirrorpulse/cfsharp/blob/main/README.md#L')
        $rewritten = [regex]::Replace(
            $rewritten,
            'https://github\.com/mirrorpulse/cfsharp/blob/[^/]+/artifacts/docs/workspace/articles/',
            'https://github.com/mirrorpulse/cfsharp/blob/main/docs/')
        $rewritten = $rewritten.Replace('.md/#L', '.md#L')

        # The modern template does not load a custom public/main.js by itself.
        # Load it from every generated page so breadcrumb repairs apply to
        # pages at the root and at arbitrary nested paths alike.
        $relativeDirectory = [System.IO.Path]::GetRelativePath($outputRoot, $htmlFile.DirectoryName).Replace('\', '/')
        $depth = if ($relativeDirectory -eq '.') { 0 } else { ($relativeDirectory -split '/').Count }
        $publicPrefix = if ($depth -eq 0) { './' } else { ('../' * $depth) }
        $customScript = '      <script type="module" src="' + $publicPrefix + 'public/main.js"></script>'
        if ($rewritten -notlike '*public/main.js*') {
            $rewritten = $rewritten.Replace('</head>', "$customScript`r`n  </head>")
        }

        if ($rewritten -cne $htmlText) {
            Set-Utf8File -Path $htmlFile.FullName -Value $rewritten
        }
    }

    $docfxVersion = '2.81.0'
    $fileRecords = @(
        foreach ($file in Get-ChildItem -LiteralPath $outputRoot -Recurse -File) {
            if ($file.FullName -eq $manifestPath) {
                continue
            }
            $relative = [System.IO.Path]::GetRelativePath($outputRoot, $file.FullName).Replace('\','/')
            $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            [ordered]@{
                path = $relative
                bytes = $file.Length
                sha256 = $hash
            }
        }
    )

    $manifest = [ordered]@{
        schemaVersion = 1
        project = 'CfSharp'
        sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
        generatedAtUtc = [DateTime]::UtcNow.ToString('o')
        generator = [ordered]@{
            name = 'DocFX'
            version = $docfxVersion
        }
        configuration = $Configuration
        sourceVersion = $Version
        contentRoot = 'cfsharp'
        files = @($fileRecords)
    }
    Set-Utf8File $manifestPath ($manifest | ConvertTo-Json -Depth 8)
    Write-Output "CfSharp documentation generated: $outputRoot"
}
finally {
    Pop-Location
}
