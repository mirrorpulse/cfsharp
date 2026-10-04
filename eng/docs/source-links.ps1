function Convert-DocumentationSourceLinks {
    param([Parameter(Mandatory)] [string] $Html)

    $rewritten = [regex]::Replace(
        $Html,
        'https://github\.com/mirrorpulse/cfsharp/blob/[^"\r\n<>]+/artifacts/docs/workspace/index\.md/?#L',
        'https://github.com/mirrorpulse/cfsharp/blob/main/README.md#L',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $rewritten = [regex]::Replace(
        $rewritten,
        'https://github\.com/mirrorpulse/cfsharp/blob/[^"\r\n<>]+/artifacts/docs/workspace/articles/',
        'https://github.com/mirrorpulse/cfsharp/blob/main/docs/',
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    return $rewritten.Replace('.md/#L', '.md#L')
}
