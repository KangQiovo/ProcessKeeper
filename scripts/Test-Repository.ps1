#requires -Version 7.0
[CmdletBinding()]
param([switch]$IncludeUntracked)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $root
try {
    $files = @(& git -c core.quotepath=false ls-files)
    if ($LASTEXITCODE -ne 0) { throw 'Run this check in the source Git repository after staging files.' }
    if ($IncludeUntracked) {
        $untracked = @(& git -c core.quotepath=false ls-files --others --exclude-standard)
        if ($LASTEXITCODE -ne 0) { throw 'Could not read the local untracked source inventory.' }
        $files = @(@($files + $untracked) | Sort-Object -Unique)
    }
    if ($files.Count -eq 0) { throw 'Run this check in the source Git repository after staging files.' }
    $inventoryDescription = if ($IncludeUntracked) { 'included in the local audit' } else { 'tracked' }
    $failures = [Collections.Generic.List[string]]::new()
    $textExtensions = @('.cs','.cpp','.h','.xaml','.csproj','.props','.targets','.ps1','.cmd','.json','.xml','.pem','.md','.yml','.yaml','.txt','.manifest')
    $credentialPattern = 'gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|<(?:P|Q|D|DP|DQ|InverseQ)>[A-Za-z0-9+/=\r\n\t ]{16,}</'
    foreach ($relative in $files) {
        if ($relative -match '(?i)(^|/)(bin|obj|artifacts|work|App|App-arm64|recommendation-catalog|\.vs)/|\.(exe|dll|pdb|cab|zip|7z|msix|msixbundle|appx|nupkg|pfx|p12|key)$|\.private\.(xml|pem)$|\.package\.json$|\.log$') {
            $failures.Add("Generated package, build cache or sensitive file is ${inventoryDescription}: $relative")
        }
        if ($relative -match '(?i)(^|/)(whitelist|whitelist-scope|appearance|view|performance|onboarding|settings)\.json|(^|/)(context|job|ready|commit|accepted|execute|status)\.txt$|(^|/)\.env($|\.)') {
            $failures.Add("Personal runtime state is ${inventoryDescription}: $relative")
        }
        if ($relative -match '(?i)(^|/)update\.json' -and $relative -cne 'src/ProcessKeeper.Core/Localization/update.json') {
            $failures.Add("Personal update preferences are ${inventoryDescription}: $relative")
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
    # Each README must use captures of its own application language, not a shared
    # screenshot copied to three paths. Pixel-language correctness is reviewed visually.
    $scenes = @{}
    $captureHashes = @{}
    foreach ($edition in @(@{File='README.md';Locale='en'}, @{File='README.zh-CN.md';Locale='zh-CN'}, @{File='README.zh-TW.md';Locale='zh-TW'})) {
        $markdown = [IO.File]::ReadAllText((Join-Path $root $edition.File))
        $captures = @([regex]::Matches($markdown, '!\[[^\]]*\]\((docs/images/[^)]+)\)') | ForEach-Object { $_.Groups[1].Value })
        if ($captures.Count -lt 6) { $failures.Add("Missing application screenshots: $($edition.File)") }
        $scenes[$edition.Locale] = @($captures | ForEach-Object { [IO.Path]::GetFileName($_) } | Sort-Object -Unique)
        foreach ($capture in $captures) {
            if (-not $capture.StartsWith("docs/images/$($edition.Locale)/", [StringComparison]::Ordinal)) {
                $failures.Add("Screenshot language does not match README: $($edition.File) -> $capture")
                continue
            }
            $capturePath = Join-Path $root $capture
            if (-not (Test-Path -LiteralPath $capturePath -PathType Leaf)) { continue }
            $hash = (Get-FileHash -LiteralPath $capturePath -Algorithm SHA256).Hash
            if ($captureHashes.ContainsKey($hash) -and $captureHashes[$hash] -ne $edition.Locale) {
                $failures.Add("Identical screenshot reused across application languages: $capture")
            }
            $captureHashes[$hash] = $edition.Locale
        }
    }
    foreach ($locale in @('zh-CN','zh-TW')) {
        if (Compare-Object -ReferenceObject $scenes.en -DifferenceObject $scenes[$locale]) {
            $failures.Add("README screenshots do not cover the same scenes: $locale")
        }
    }
    if ($failures.Count) { $failures | ForEach-Object { Write-Output "FAIL $_" }; throw "$($failures.Count) repository checks failed." }
    $inventorySummary = if ($IncludeUntracked) { 'audited files (tracked and untracked)' } else { 'tracked files' }
    Write-Output "PASS source publication checks: $($files.Count) $inventorySummary; no package/cache/private-path/pattern findings; local documentation links, translations and separate language screenshots present. Pattern checks are not a complete secret audit."
} finally { Pop-Location }
