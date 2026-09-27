#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $root
try {
    $files = @(& git -c core.quotepath=false ls-files)
    if ($LASTEXITCODE -ne 0 -or $files.Count -eq 0) { throw 'Run this check in the source Git repository after staging files.' }
    $failures = [Collections.Generic.List[string]]::new()
    $textExtensions = @('.cs','.cpp','.h','.xaml','.csproj','.props','.targets','.ps1','.cmd','.json','.md','.yml','.yaml','.txt','.manifest')
    $credentialPattern = 'gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'
    foreach ($relative in $files) {
        if ($relative -match '(?i)(^|/)(bin|obj|artifacts|work|App|\.vs)/|\.(exe|dll|pdb|cab|zip|7z|msix|msixbundle|appx|nupkg|pfx|p12|key)$|\.package\.json$|\.log$') {
            $failures.Add("Generated package, build cache or sensitive file is tracked: $relative")
        }
        if ($relative -match '(?i)(^|/)(whitelist|appearance|view|onboarding|settings)\.json|(^|/)(context|job|ready|commit|accepted|execute|status)\.txt$|(^|/)\.env($|\.)') {
            $failures.Add("Personal runtime state is tracked: $relative")
        }
        if ($relative -match '(?i)(^|/)update\.json' -and $relative -cne 'src/ProcessKeeper.Core/Localization/update.json') {
            $failures.Add("Personal update preferences are tracked: $relative")
        }
        $path = Join-Path $root $relative
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $failures.Add("Missing tracked file: $relative"); continue }
        if ((Get-Item -LiteralPath $path).Length -gt 5MB) { $failures.Add("Unexpected source file larger than 5 MiB: $relative") }
        $extension = [IO.Path]::GetExtension($path).ToLowerInvariant()
        if ($extension -notin $textExtensions) { continue }
        $text = [IO.File]::ReadAllText($path)
        if ($text -match $credentialPattern) { $failures.Add("Potential credential material in $relative (content suppressed)") }
        if ($text -match '(?i)[A-Z]:[\\/]+Users[\\/]+KangQi[\\/]|E:[\\/]+codex[\\/]') { $failures.Add("Developer-specific absolute path in $relative") }
        if ($extension -ne '.md' -or $relative.StartsWith('licenses/')) { continue }
        foreach ($match in [regex]::Matches($text, '(?:\]\(|(?:src|href)=["''])([^\s)"''>]+)')) {
            $link = [Net.WebUtility]::HtmlDecode($match.Groups[1].Value)
            if ($link -match '^(?:[a-zA-Z][a-zA-Z0-9+.-]*:|#|//)') { continue }
            $link = [Uri]::UnescapeDataString(($link -split '[?#]',2)[0])
            if (-not $link) { continue }
            $resolved = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $path) $link))
            if (-not $resolved.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -and $resolved -ne $root) {
                $failures.Add("Documentation link leaves the repository: $relative -> $link")
            } elseif (-not (Test-Path -LiteralPath $resolved)) { $failures.Add("Broken local documentation link: $relative -> $link") }
            elseif ((Test-Path -LiteralPath $resolved -PathType Leaf) -and [IO.Path]::GetRelativePath($root,$resolved).Replace('\','/') -notin $files) {
                $failures.Add("Documentation link points to an untracked file: $relative -> $link")
            }
        }
        if ($relative.StartsWith('.github/') -or $relative -match '\.zh-(CN|TW)\.md$') { continue }
        foreach ($locale in @('zh-CN','zh-TW')) {
            $translation = $relative.Substring(0,$relative.Length-3)+'.'+$locale+'.md'
            if ($translation -notin $files) { $failures.Add("Missing documentation translation: $translation") }
        }
    }
    if ($failures.Count) { $failures | ForEach-Object { Write-Output "FAIL $_" }; throw "$($failures.Count) repository checks failed." }
    Write-Output "PASS source publication checks: $($files.Count) tracked files; no package/cache/private-path/pattern findings; local documentation links and translations present. Pattern checks are not a complete secret audit."
} finally { Pop-Location }
