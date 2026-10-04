[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'source-links.ps1')
$cases = 0
foreach ($repository in @('mirrorpulse/cfsharp', 'MirrorPulse/CfSharp')) {
    foreach ($revision in @('main', 'fix/protected-content-confirmation', 'be89270e57fc485353a073955be912405927920b')) {
        foreach ($path in @('index.md#L1', 'index.md/#L1', 'articles/getting-started.md/#L2')) {
            $html = '<a href="https://github.com/' + $repository + '/blob/' + $revision + '/artifacts/docs/workspace/' + $path + '">Edit</a>'
            $expectedPath = if ($path.StartsWith('index.md', [StringComparison]::Ordinal)) { 'README.md#L1' } else { 'docs/getting-started.md#L2' }
            $expected = '<a href="https://github.com/mirrorpulse/cfsharp/blob/main/' + $expectedPath + '">Edit</a>'
            if ((Convert-DocumentationSourceLinks $html) -cne $expected) { throw "Source-link regression: $repository / $revision / $path" }
            $cases++
        }
    }
}
$unrelated = '<a href="https://github.com/another/repository/blob/main/artifacts/docs/workspace/index.md#L1">Edit</a>'
if ((Convert-DocumentationSourceLinks $unrelated) -cne $unrelated) { throw 'An unrelated repository link was rewritten.' }
Write-Output "Documentation source-link cases passed: $cases; unrelated repository preserved."
