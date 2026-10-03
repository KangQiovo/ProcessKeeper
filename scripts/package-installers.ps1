#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Windows7Compat','Windows10x64','Windows10arm64')][string]$PackageTarget,
    [Parameter(Mandatory)][string]$PortablePath,
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][string]$NsisCompiler,
    [Parameter(Mandatory)][string]$SetupGuardPath,
    [switch]$StableRelease,
    [switch]$ValidateOnly
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if (!$IsWindows) { throw 'Windows and PowerShell 7 are required.' }
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'SourceInputs.ps1')

function Assert-PlainPath([string]$Path,[switch]$Required) {
    $full=[IO.Path]::GetFullPath($Path)
    if ($full -match '[\x00-\x1f"\$]') { throw 'Unsupported compiler input path.' }
    if ($Required -and !(Test-Path -LiteralPath $full -PathType Leaf)) { throw 'Required compiler input is missing.' }
    $cursor=$full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked input/output paths are refused.' }
        }
        $parent=[IO.Directory]::GetParent($cursor)
        $cursor=if($parent){$parent.FullName}else{$null}
    }
    $full
}
function Read-PeMachine([string]$Path) {
    $file=[IO.File]::OpenRead($Path)
    try {
        $reader=[IO.BinaryReader]::new($file)
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw 'Expected a Windows PE file.' }
        $file.Position=0x3c; $header=$reader.ReadUInt32(); $file.Position=$header
        if ($reader.ReadUInt32() -ne 0x4550) { throw 'Invalid PE signature.' }
        $reader.ReadUInt16()
    } finally { $file.Dispose() }
}
$portable=Assert-PlainPath $PortablePath -Required
$compiler=Assert-PlainPath $NsisCompiler -Required
$guard=Assert-PlainPath $SetupGuardPath -Required
$output=Assert-PlainPath $OutputPath
if (Test-Path -LiteralPath $output) { throw 'Choose a new output path; existing installers are never overwritten.' }
if (!(Test-Path -LiteralPath (Split-Path $output) -PathType Container)) { throw 'Output directory must already exist.' }
if ((Read-PeMachine $portable) -ne 0x14c) { throw 'The official portable bootstrapper must be x86.' }
if ((Read-PeMachine $guard) -ne 0x14c -or [IO.Path]::GetExtension($guard) -ine '.dll') { throw 'The setup guard must be the native x86 DLL.' }
$compilerVersion=(& $compiler /VERSION | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $compilerVersion -cne 'v3.13') { throw 'Use the pinned NSIS 3.13 compiler.' }
$metadataPath=Assert-PlainPath "$portable.package.json" -Required
$metadata=Get-Content -LiteralPath $metadataPath -Raw|ConvertFrom-Json
$metadataDocument=[Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText($metadataPath))
try {
    $buildDate=$metadataDocument.RootElement.GetProperty('BuildDate').GetString()
    $builtAtUtc=$metadataDocument.RootElement.GetProperty('BuiltAtUtc').GetString()
} finally { $metadataDocument.Dispose() }
$expectedChannel=if($StableRelease){'Stable'}else{'Preview'}
$version=[string]$metadata.Version
if ($version -notmatch '^\d+\.\d+\.\d+$' -or $metadata.ReleaseChannel -cne $expectedChannel -or $metadata.PackageTarget -cne $PackageTarget) {
    throw 'Portable version, package flavor or release channel does not match the installer request.'
}
$portableHash=(Get-FileHash -LiteralPath $portable -Algorithm SHA256).Hash
if ($metadata.SHA256 -cne $portableHash -or [IO.Path]::GetFullPath([string]$metadata.Output) -cne $portable) { throw 'Portable package evidence does not match the actual EXE.' }
$versionInfo=[Diagnostics.FileVersionInfo]::GetVersionInfo($portable)
if ($versionInfo.ProductName -cne 'Process Keeper' -or $versionInfo.ProductVersion -cne $version -or $versionInfo.CompanyName -cne 'KangQi') { throw 'Portable VERSIONINFO identity mismatch.' }
$inputs=Get-ProcessKeeperSourceInputs $repo
if ($metadata.SourceInputs.SHA256 -cne $inputs.SHA256) { throw 'Rebuild the portable package: current source inputs do not match its evidence.' }
$nsi=Assert-PlainPath (Join-Path $repo 'installer/ProcessKeeper.nsi') -Required
$compilerHash=(Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash
$guardHash=(Get-FileHash -LiteralPath $guard -Algorithm SHA256).Hash
$nsiHash=(Get-FileHash -LiteralPath $nsi -Algorithm SHA256).Hash
$arguments=@('/V3','/WX','/NOCONFIG','/INPUTCHARSET','UTF8',"/DPORTABLE_FILE=$portable","/DPORTABLE_SHA256=$portableHash","/DSETUP_GUARD_FILE=$guard","/DOUTPUT_FILE=$output","/DAPP_VERSION=$version","/DPACKAGE_TARGET=$PackageTarget","/DBUILD_CHANNEL=$expectedChannel",("/DPRODUCT_ICON="+(Join-Path $repo 'src/ProcessKeeper.App/Assets/ProcessKeeper.ico')),("/DLICENSE_FILE="+(Join-Path $repo 'LICENSE')),$nsi)
if ($ValidateOnly) {
    [pscustomobject]@{Validated=$true;Target=$PackageTarget;Version=$version;ReleaseChannel=$expectedChannel;PortableSHA256=$portableHash;Compiler=$compilerVersion;CompilerSHA256=$compilerHash;SetupGuardSHA256=$guardHash;Output=$output}|ConvertTo-Json
    return
}
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'NSIS compilation failed.' }
if (!(Test-Path -LiteralPath $output -PathType Leaf) -or (Read-PeMachine $output) -ne 0x14c) { throw 'NSIS did not create an x86 installer.' }
if ((Get-FileHash -LiteralPath $portable -Algorithm SHA256).Hash -cne $portableHash -or
    (Get-FileHash -LiteralPath $nsi -Algorithm SHA256).Hash -cne $nsiHash -or
    (Get-FileHash -LiteralPath $guard -Algorithm SHA256).Hash -cne $guardHash -or
    (Get-ProcessKeeperSourceInputs $repo).SHA256 -cne $inputs.SHA256) { throw 'Inputs changed during packaging; do not distribute this candidate.' }
$report=[ordered]@{Version=$version;ReleaseChannel=$expectedChannel;BuildDate=$buildDate;BuiltAtUtc=$builtAtUtc;PackageTarget=$PackageTarget;Distribution='Installer';Output=$output;SHA256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash;Portable=$portable;PortableSHA256=$portableHash;CompilerVersion=$compilerVersion;CompilerSHA256=$compilerHash;SetupGuardSHA256=$guardHash;InstallerScriptSHA256=$nsiHash;SourceInputs=$inputs;ActualInstallUninstallTested=$false;RegistryView=32;RegistryKey='Software\Microsoft\Windows\CurrentVersion\Uninstall\ProcessKeeper'}
$report|ConvertTo-Json -Depth 8|Set-Content -LiteralPath "$output.package.json" -Encoding utf8NoBOM
[pscustomobject]$report|Select-Object Version,ReleaseChannel,PackageTarget,Distribution,Output,SHA256,ActualInstallUninstallTested|ConvertTo-Json
