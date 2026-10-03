#requires -Version 7.0
# Build-input consistency evidence, not a claim of reproducible compilation or code signing.
function Get-ProcessKeeperSourceInputs([string]$ProductRoot) {
    $root = [IO.Path]::GetFullPath($ProductRoot).TrimEnd('\','/')
    $files = [Collections.Generic.List[IO.FileInfo]]::new()
    foreach ($name in @('Directory.Build.props','Directory.Build.targets','global.json','LICENSE','THIRD-PARTY-NOTICES.md','THIRD-PARTY-NOTICES.zh-CN.md','THIRD-PARTY-NOTICES.zh-TW.md')) {
        $files.Add((Get-Item -LiteralPath (Join-Path $root $name)))
    }
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Filter '*.ps1') { $files.Add($file) }
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Filter 'README*.md') { $files.Add($file) }
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Filter 'CONTRIBUTING*.md') { $files.Add($file) }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'docs') -File -Filter '*.md') { $files.Add($file) }
    $pending = [Collections.Generic.Stack[string]]::new()
    foreach ($folder in @('src','scripts','rules','catalog','licenses','community','installer')) { $pending.Push((Join-Path $root $folder)) }
    while ($pending.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Build input links are not supported.' }
            if ($item.PSIsContainer) {
                if ($item.Name -notin @('obj','bin','build','test-work','artifacts','.git','.vs')) { $pending.Push($item.FullName) }
            }
            elseif ($item.Extension -in @('.cs','.xaml','.csproj','.props','.targets','.ps1','.json','.sig','.xml','.cpp','.h','.rc','.manifest','.ico','.png','.txt','.md','.nsi','.nsh')) {
                # The generated timestamp is separately bound to both Core DLLs and VERSIONINFO.
                if ($item.FullName -ne (Join-Path $root 'src/ProcessKeeper.Core/BuildInfo.cs')) { $files.Add($item) }
            }
        }
    }
    $ordered = [Collections.Generic.SortedDictionary[string,IO.FileInfo]]::new([StringComparer]::Ordinal)
    foreach ($file in $files) { $ordered[[IO.Path]::GetRelativePath($root,$file.FullName).Replace('\','/')] = $file }
    $records = @(foreach ($pair in $ordered.GetEnumerator()) {
        [pscustomobject][ordered]@{ Path=$pair.Key; Length=$pair.Value.Length; SHA256=(Get-FileHash -LiteralPath $pair.Value.FullName -Algorithm SHA256).Hash }
    })
    $text = ($records | ForEach-Object { "$($_.Path)`t$($_.Length)`t$($_.SHA256)" }) -join "`n"
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $hash = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))).Replace('-','') }
    finally { $sha.Dispose() }
    [pscustomobject][ordered]@{ Schema=1; SHA256=$hash; Files=$records; GeneratedBuildStampCheckedSeparately=$true; ReproducibleBuildProven=$false }
}
