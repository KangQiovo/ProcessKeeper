#requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$ModernDirectory,
    [Parameter(Mandatory)][string]$LegacyDirectory,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$IntermediateDirectory = (Join-Path $PSScriptRoot 'artifacts/packages'),
    [string]$PowerShellPath,
    [string]$VisualStudioPath,
    [string]$WindowsSdkRoot,
    [string]$WindowsSdkVersion,
    [switch]$StableRelease
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Universal packaging requires Windows and PowerShell 7.' }
. (Join-Path $PSScriptRoot 'scripts/BuildTools.ps1')
. (Join-Path $PSScriptRoot 'scripts/SourceInputs.ps1')
$sourceInputs = Get-ProcessKeeperSourceInputs $PSScriptRoot
$resolvedPowerShell = Resolve-ProcessKeeperPowerShell $PowerShellPath
$expectedChannel = if ($StableRelease) { 'Stable' } else { 'Preview' }
$nativeRoot = Join-Path $PSScriptRoot 'src/ProcessKeeper.UniversalLauncher'
$outputFile = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $outputFile) { throw 'Choose a new output path. This script never overwrites a release.' }
if (-not (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($outputFile)) -PathType Container)) { throw 'The output parent directory must already exist.' }

function Assert-PlainDirectory([string]$Path) {
    $item = Get-Item -LiteralPath ([IO.Path]::GetFullPath($Path))
    if (-not $item.PSIsContainer) { throw 'Payload roots must be directories.' }
    $cursor = $item
    while ($null -ne $cursor) {
        if ($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked payload roots are not allowed.' }
        $cursor = $cursor.Parent
    }
    return $item.FullName
}
function Assert-RelativePath([string]$Path) {
    if ($Path.Length -gt 1024 -or $Path -match '[^\x20-\x7e]' -or $Path -match '[\\:"<>|*?]') { throw "Unsupported package file name: $Path" }
    foreach ($segment in $Path.Split('/')) {
        if (-not $segment -or $segment -in @('.','..') -or $segment.EndsWith('.') -or $segment.EndsWith(' ') -or $segment.Length -gt 240 -or
            $segment.Split('.')[0] -match '^(CON|NUL|PRN|AUX|CLOCK\$|COM[1-9]|LPT[1-9])$') { throw "Unsafe package path: $Path" }
    }
    # The native Win7 route intentionally avoids long-path assumptions. Account SID length can vary.
    if ($Path.Length -gt 86) { throw "Package path exceeds the protected Win7 cache budget: $Path" }
}
function Get-PublishedFiles([string]$Root) {
    $pending = [Collections.Generic.Stack[string]]::new(); $pending.Push($Root)
    while ($pending.Count) {
        $directory = $pending.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Payload links are not allowed.' }
            if ($item.PSIsContainer) {
                if ($item.Name -notin @('src','obj','bin','.git','work','backups')) { $pending.Push($item.FullName) }
                continue
            }
            if ($item.Extension -in @('.pdb','.log') -or $item.Name -ilike 'whitelist.json*' -or $item.Name -ilike 'settings.json*' -or
                $item.Name -ilike 'appearance.json*' -or $item.Name -ilike 'view.json*' -or $item.Name -ilike 'profile.json*' -or
                $item.Name -ilike 'activity.log*' -or $item.Name -ilike 'errors.log*' -or $item.Name -ilike 'update.json*' -or
                $item.Name -in @('context.txt','job.txt','ready.txt','status.txt','commit.txt','accepted.txt','execute.txt','使用说明.md','兼容性评估.md') -or $item.Name -like '*.pending*') { continue }
            $item
        }
    }
}
function Read-Machine([string]$Path) {
    $stream = [IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw 'Payload entry point is not a Windows PE image.' }
        $stream.Position = 0x3c; $header = $reader.ReadUInt32(); $stream.Position = $header
        if ($reader.ReadUInt32() -ne 0x4550) { throw 'Invalid payload PE header.' }
        return $reader.ReadUInt16()
    } finally { $stream.Dispose() }
}
function Read-BuildMetadata([string]$Path, [string]$ExpectedChannel = 'Preview') {
    # System.Text.Json keeps date literals intact on every PowerShell 7 version.
    $document = [System.Text.Json.JsonDocument]::Parse([string](Get-Content -LiteralPath $Path -Raw))
    try {
        if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw 'Invalid build metadata object.' }
        $fields = @{}
        foreach ($property in $document.RootElement.EnumerateObject()) {
            if ($property.Name -notin @('Version','ReleaseChannel','BuiltAtUtc','BuiltAtUtc8') -or $fields.ContainsKey($property.Name) -or $property.Value.ValueKind -ne [System.Text.Json.JsonValueKind]::String) { throw 'Invalid or duplicate build metadata field.' }
            $fields[$property.Name] = $property.Value.GetString()
        }
        if ($fields.Count -ne 4) { throw 'Incomplete build metadata.' }
        $info = [pscustomobject]$fields
    } finally { $document.Dispose() }
    if ($ExpectedChannel -cnotin @('Preview','Stable') -or $info.ReleaseChannel -cne $ExpectedChannel) { throw 'Build channel mismatch. Stable packaging requires explicit -StableRelease and matching Stable payloads.' }
    if ($info.Version -cne '1.5.0' -or $info.BuiltAtUtc -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$' -or
        $info.BuiltAtUtc8 -notmatch '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\+08:00$') { throw 'Invalid fixed release build timestamp.' }
    $utc = [DateTimeOffset]::ParseExact($info.BuiltAtUtc,"yyyy-MM-dd'T'HH:mm:ss'Z'",[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::AssumeUniversal)
    $utc8 = [DateTimeOffset]::ParseExact($info.BuiltAtUtc8,"yyyy-MM-dd'T'HH:mm:sszzz",[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::None)
    if ($utc.UtcDateTime.Ticks -ne $utc8.UtcDateTime.Ticks) { throw 'UTC and UTC+8 build timestamps do not describe the same instant.' }
    return $info
}
function Assert-CoreBuildStamp([string]$Path, [object]$Info) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -gt 32MB -or $bytes.Length -lt 512) { throw 'Invalid Core assembly size for build stamp validation.' }
    $even = [Text.Encoding]::Unicode.GetString($bytes)
    $odd = [Text.Encoding]::Unicode.GetString($bytes,1,$bytes.Length-1)
    foreach ($stamp in @($Info.BuiltAtUtc,$Info.BuiltAtUtc8,('ProcessKeeper.Build.Channel:' + $Info.ReleaseChannel))) {
        if (-not $even.Contains($stamp,[StringComparison]::Ordinal) -and -not $odd.Contains($stamp,[StringComparison]::Ordinal)) {
            throw "Payload Core assembly has a different build timestamp: $Path"
        }
    }
    $opposite = 'ProcessKeeper.Build.Channel:' + $(if ($Info.ReleaseChannel -ceq 'Preview') { 'Stable' } else { 'Preview' })
    if ($even.Contains($opposite,[StringComparison]::Ordinal) -or $odd.Contains($opposite,[StringComparison]::Ordinal)) { throw 'Core assembly contains mixed release channel markers.' }
}
$buildInfoPath = Join-Path $PSScriptRoot 'build-info.json'
$buildInfo = Read-BuildMetadata $buildInfoPath $expectedChannel
$roots = [ordered]@{ modern = (Assert-PlainDirectory $ModernDirectory); legacy = (Assert-PlainDirectory $LegacyDirectory) }
foreach ($root in $roots.Values) { if ($outputFile.StartsWith($root.TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'The outer EXE must be written outside both published payload directories.' } }
Assert-CoreBuildStamp (Join-Path $roots.modern 'ProcessKeeper.Core.dll') $buildInfo
Assert-CoreBuildStamp (Join-Path $roots.legacy 'ProcessKeeper.Legacy.Core.dll') $buildInfo
if ((Read-Machine (Join-Path $roots.modern 'ProcessKeeper.exe')) -ne 0x8664) { throw 'The modern payload must be x64.' }
if ((Read-Machine (Join-Path $roots.legacy 'ProcessKeeper.exe')) -ne 0x14c) { throw 'The compatibility payload must be an x86/AnyCPU PE.' }
if (-not (Test-Path -LiteralPath (Join-Path $roots.modern 'Microsoft.UI.Xaml.dll'))) { throw 'The modern payload must include its self-contained WinUI runtime.' }
if (-not (Test-Path -LiteralPath (Join-Path $roots.legacy 'ProcessKeeper.exe.config'))) { throw 'The compatibility payload runtime configuration is missing.' }
$intermediateRoot = [IO.Path]::GetFullPath($IntermediateDirectory)
foreach ($root in $roots.Values) { if ($intermediateRoot.StartsWith($root.TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase) -or $intermediateRoot.Equals($root,[StringComparison]::OrdinalIgnoreCase)) { throw 'Packaging intermediates must stay outside both published payload roots.' } }
$packageWork = Join-Path $intermediateRoot ('universal-package-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $packageWork | Out-Null
$updater = Join-Path $packageWork 'ProcessKeeper.Updater.exe'
$nativeOptions = @('-BuildDirectory', (Join-Path $packageWork 'native'))
if ($VisualStudioPath) { $nativeOptions += @('-VisualStudioPath', $VisualStudioPath) }
if ($WindowsSdkRoot) { $nativeOptions += @('-WindowsSdkRoot', $WindowsSdkRoot) }
if ($WindowsSdkVersion) { $nativeOptions += @('-WindowsSdkVersion', $WindowsSdkVersion) }
& $resolvedPowerShell -NoProfile -File (Join-Path $nativeRoot 'build-native.ps1') -UpdaterOnly -OutputPath $updater @nativeOptions
if ($LASTEXITCODE -ne 0) { throw 'Trusted updater compilation failed.' }
$cabinetDirectory = Join-Path $packageWork 'cabinet'
New-Item -ItemType Directory -Path $cabinetDirectory | Out-Null
$records = [Collections.Generic.List[object]]::new()
$destinations = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$totalLength = 0L
foreach ($variant in $roots.Keys) {
    $root = $roots[$variant]
    $packageFiles = [Collections.Generic.List[object]]::new()
    $packageFiles.Add([pscustomobject]@{File=(Get-Item -LiteralPath $updater); Relative='ProcessKeeper.Updater.exe'; Extra=$false})
    foreach ($file in Get-PublishedFiles $root | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\','/')
        $packageFiles.Add([pscustomobject]@{File=$file; Relative=$relative; Extra=$false})
    }
    # Each extracted route is independently accompanied by the complete product notices.
    $productLicenseRoot = Assert-PlainDirectory (Join-Path $PSScriptRoot 'licenses')
    $licenseItems = @(Get-ChildItem -LiteralPath $productLicenseRoot -Recurse -Force)
    if ($licenseItems.Where({ $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Linked license artifacts are not allowed.' }
    foreach ($file in $licenseItems.Where({ -not $_.PSIsContainer })) {
        $packageFiles.Add([pscustomobject]@{File=$file; Relative=('licenses/' + [IO.Path]::GetRelativePath($productLicenseRoot,$file.FullName).Replace('\','/')); Extra=$true})
    }
    $packageFiles.Add([pscustomobject]@{File=(Get-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md')); Relative='THIRD-PARTY-NOTICES.md'; Extra=$true})
    $packageFiles.Add([pscustomobject]@{File=(Get-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE')); Relative='LICENSE'; Extra=$true})
    foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'README*.md' -File) { $packageFiles.Add([pscustomobject]@{File=$file; Relative=$file.Name; Extra=$true}) }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'docs') -Filter 'COMPATIBILITY*.md' -File) { $packageFiles.Add([pscustomobject]@{File=$file; Relative=('docs/' + $file.Name); Extra=$true}) }
    foreach ($default in @('default-rules.json','default-settings.json')) {
        $packageFiles.Add([pscustomobject]@{File=(Get-Item -LiteralPath (Join-Path $PSScriptRoot "rules/$default")); Relative="rules/$default"; Extra=$true})
    }
    foreach ($packageFile in $packageFiles) {
        $file = $packageFile.File
        $relative = $variant + '/' + $packageFile.Relative
        Assert-RelativePath $relative
        if (-not $destinations.Add($relative)) { if ($packageFile.Extra) { continue }; throw 'Case-insensitive duplicate payload path.' }
        if ($file.Length -gt 1GB) { throw 'A payload file exceeds the native size limit.' }
        $totalLength += $file.Length
        if ($totalLength -gt 4GB -or $destinations.Count -gt 50000) { throw 'The payload exceeds the native manifest limits.' }
        $staged = Join-Path $packageWork ('files/' + $relative)
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($staged)) -Force | Out-Null
        $inputStream = [IO.File]::Open($file.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        try {
            $outputStream = [IO.File]::Open($staged,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
            try { $inputStream.CopyTo($outputStream); $outputStream.Flush($true) } finally { $outputStream.Dispose() }
        } finally { $inputStream.Dispose() }
        if ($relative -in @('modern/ProcessKeeper.Core.dll','legacy/ProcessKeeper.Legacy.Core.dll')) { Assert-CoreBuildStamp $staged $buildInfo }
        $records.Add([pscustomobject]@{ Relative=$relative; Staged=$staged; Length=(Get-Item -LiteralPath $staged).Length; Hash=(Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash.ToLowerInvariant() })
    }
}
$directives = [Collections.Generic.List[string]]::new()
foreach ($line in @('.OPTION EXPLICIT','.Set Cabinet=ON','.Set Compress=ON','.Set CompressionType=LZX','.Set CompressionMemory=21','.Set CabinetNameTemplate=payload.cab',
    ".Set DiskDirectoryTemplate=`"$cabinetDirectory`"",'.Set MaxDiskSize=0','.Set MaxCabinetSize=0','.Set FolderSizeThreshold=2147483647','.Set FolderFileCountThreshold=50000','.Set CabinetFileCountThreshold=0')) { $directives.Add($line) }
$previousVariant = ''
foreach ($record in $records) {
    $variant = $record.Relative.Split('/')[0]
    if ($previousVariant -and $previousVariant -ne $variant) { $directives.Add('.New Folder') }
    $directives.Add('"' + $record.Staged + '" "' + $record.Relative.Replace('/','\') + '"')
    $previousVariant = $variant
}
$ddf = Join-Path $packageWork 'payload.ddf'
[IO.File]::WriteAllLines($ddf,$directives,[Text.UTF8Encoding]::new($false))
$cabLog = Join-Path $packageWork 'makecab.log'
Push-Location $packageWork
try { & "$env:SystemRoot/System32/makecab.exe" /F $ddf > $cabLog; if ($LASTEXITCODE -ne 0) { throw "Cabinet creation failed. See $cabLog" } }
finally { Pop-Location }
$cabinet = Join-Path $cabinetDirectory 'payload.cab'
$cabinetHash = (Get-FileHash -LiteralPath $cabinet -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = Join-Path $packageWork 'payload.manifest'
$manifestText = "PK14`t$cabinetHash`n" + (($records | ForEach-Object { "$($_.Relative)`t$($_.Length)`t$($_.Hash)" }) -join "`n") + "`n"
[IO.File]::WriteAllText($manifest,$manifestText,[Text.UTF8Encoding]::new($false))
$resource = Join-Path $packageWork 'launcher.rc'
$icon = Join-Path $PSScriptRoot 'src/ProcessKeeper.App/Assets/ProcessKeeper.ico'
$appManifest = Join-Path $nativeRoot 'launcher.manifest'
$resourceText = @"
#include <windows.h>
1 RT_MANIFEST "$($appManifest.Replace('\','/'))"
101 RCDATA "$($manifest.Replace('\','/'))"
102 RCDATA "$($cabinet.Replace('\','/'))"
201 ICON "$($icon.Replace('\','/'))"
1 VERSIONINFO
FILEVERSION 1,5,0,0
PRODUCTVERSION 1,5,0,0
FILEFLAGS $(if ($StableRelease) { '0' } else { 'VS_FF_PRERELEASE' })
FILEOS VOS_NT_WINDOWS32
FILETYPE VFT_APP
BEGIN
 BLOCK "StringFileInfo"
 BEGIN
  BLOCK "040904B0"
  BEGIN
   VALUE "CompanyName", "KangQi\0"
   VALUE "FileDescription", "Process Keeper\0"
   VALUE "FileVersion", "1.5.0\0"
   VALUE "ProductName", "Process Keeper\0"
   VALUE "ProductVersion", "1.5.0\0"
   VALUE "BuildDate", "$($buildInfo.BuiltAtUtc8)\0"
   VALUE "BuildDateUtc", "$($buildInfo.BuiltAtUtc)\0"
   VALUE "ReleaseChannel", "$($buildInfo.ReleaseChannel)\0"
  END
 END
 BLOCK "VarFileInfo"
 BEGIN
  VALUE "Translation", 0x409, 1200
 END
END
"@
[IO.File]::WriteAllText($resource,$resourceText,[Text.UTF8Encoding]::new($false))
& $resolvedPowerShell -NoProfile -File (Join-Path $nativeRoot 'build-native.ps1') -ResourceScript $resource -OutputPath $outputFile @nativeOptions
if ($LASTEXITCODE -ne 0) { throw 'Universal launcher compilation failed.' }
$currentBuildInfo = Read-BuildMetadata $buildInfoPath $expectedChannel
if ($currentBuildInfo.BuiltAtUtc -cne $buildInfo.BuiltAtUtc -or $currentBuildInfo.BuiltAtUtc8 -cne $buildInfo.BuiltAtUtc8) { throw 'The release build timestamp changed during packaging; this candidate must not be delivered.' }
$currentSourceInputs = Get-ProcessKeeperSourceInputs $PSScriptRoot
if ($currentSourceInputs.SHA256 -cne $sourceInputs.SHA256) { throw 'Source inputs changed during packaging; this candidate must not be delivered.' }
$report = [ordered]@{ Version='1.5.0'; ReleaseChannel=$buildInfo.ReleaseChannel; BuildDate=$buildInfo.BuiltAtUtc8; BuiltAtUtc=$buildInfo.BuiltAtUtc; Output=$outputFile; SHA256=(Get-FileHash -LiteralPath $outputFile -Algorithm SHA256).Hash; FileCount=$records.Count; UncompressedBytes=$totalLength; PackageWork=$packageWork; Modern='x64 Windows 10 build 19041 or later'; Compatibility='Intel/AMD Windows 7 SP1, Windows 8.1, Windows 10/11 | .NET Framework 4.6.2 or later'; TestedOnActualWindows7=$false }
$report['SourceInputs'] = $sourceInputs
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath "$outputFile.package.json" -Encoding utf8NoBOM
[pscustomobject]$report | Select-Object Version,ReleaseChannel,BuildDate,BuiltAtUtc,Output,SHA256,FileCount,PackageWork | ConvertTo-Json
