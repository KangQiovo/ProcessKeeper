#requires -Version 7.0
param([switch]$TestsOnly, [switch]$UiFixture, [switch]$UpdaterOnly, [switch]$SetupGuardOnly, [string]$ResourceScript, [string]$OutputPath,
    [string]$FixtureCabinet, [string]$FixtureManifest, [string]$FixtureDirectory,
    [string]$VisualStudioPath, [string]$WindowsSdkRoot, [string]$WindowsSdkVersion, [string]$BuildDirectory)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The native launcher requires Windows and PowerShell 7.' }
foreach ($parameter in @('OutputPath','ResourceScript','FixtureCabinet','FixtureManifest','FixtureDirectory')) {
    $value = Get-Variable -Name $parameter -ValueOnly
    if ($value) { Set-Variable -Name $parameter -Value ([IO.Path]::GetFullPath($value)) }
}
$nativeRoot = $PSScriptRoot
$nativeBuild = if ($BuildDirectory) { [IO.Path]::GetFullPath($BuildDirectory) } else { [IO.Path]::GetFullPath((Join-Path $nativeRoot '../../artifacts/native')) }
New-Item -ItemType Directory -Path $nativeBuild -Force | Out-Null
$visualStudioRoot = if ($VisualStudioPath) { $VisualStudioPath } else { $env:VSINSTALLDIR }
if (-not $visualStudioRoot) {
    $vswhereCommand = Get-Command vswhere.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    $vswhere = if ($vswhereCommand) { $vswhereCommand.Source } else { Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe' }
    if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'Install Visual Studio C++ x86/x64 build tools, or specify -VisualStudioPath.' }
    $visualStudioRoot = (& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
}
if (-not $visualStudioRoot) { throw 'MSVC x86 build tools are required.' }
$visualStudioRoot = [IO.Path]::GetFullPath($visualStudioRoot)
$toolsVersion = (Get-Content -LiteralPath (Join-Path $visualStudioRoot 'VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt')).Trim()
$msvcRoot = Join-Path $visualStudioRoot "VC/Tools/MSVC/$toolsVersion"
$sdkRoot = if ($WindowsSdkRoot) { $WindowsSdkRoot } elseif ($env:WindowsSdkDir) { $env:WindowsSdkDir } else {
    (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -Name KitsRoot10 -ErrorAction SilentlyContinue).KitsRoot10
}
if (-not $sdkRoot) { $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10' }
$sdkRoot = [IO.Path]::GetFullPath($sdkRoot)
$sdkVersion = if ($WindowsSdkVersion) { $WindowsSdkVersion.TrimEnd('\') } elseif ($env:WindowsSDKVersion) { $env:WindowsSDKVersion.TrimEnd('\') } else {
    Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Include') -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' -and
            (Test-Path -LiteralPath (Join-Path $sdkRoot ('bin/' + $_.Name + '/x86/rc.exe'))) -and
            (Test-Path -LiteralPath (Join-Path $sdkRoot ('Lib/' + $_.Name + '/um/x86/kernel32.lib'))) -and
            (Test-Path -LiteralPath (Join-Path $sdkRoot ('Lib/' + $_.Name + '/ucrt/x86/ucrt.lib'))) } |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty Name
}
if (-not $sdkVersion -or -not (Test-Path -LiteralPath (Join-Path $sdkRoot "Include/$sdkVersion/um/windows.h"))) { throw 'A complete Windows SDK with x86 headers, libraries and rc.exe is required.' }
$previousInclude = $env:INCLUDE; $previousLib = $env:LIB; $previousPath = $env:PATH
$env:INCLUDE = @((Join-Path $msvcRoot 'include'), (Join-Path $sdkRoot "Include/$sdkVersion/ucrt"), (Join-Path $sdkRoot "Include/$sdkVersion/shared"), (Join-Path $sdkRoot "Include/$sdkVersion/um")) -join ';'
$env:LIB = @((Join-Path $msvcRoot 'lib/x86'), (Join-Path $sdkRoot "Lib/$sdkVersion/ucrt/x86"), (Join-Path $sdkRoot "Lib/$sdkVersion/um/x86")) -join ';'
$compiler = Join-Path $msvcRoot 'bin/Hostx64/x86/cl.exe'
$linker = Join-Path $msvcRoot 'bin/Hostx64/x86/link.exe'
Write-Host "Native compiler: $compiler | Windows SDK: $sdkVersion | artifacts: $nativeBuild"
$env:PATH = (Split-Path $linker) + ';' + $env:PATH
Push-Location $nativeBuild
try {
    $common = @('/nologo','/std:c++17','/EHsc','/W4','/WX','/MT','/O1','/utf-8','/DUNICODE','/D_UNICODE','/D_WIN32_WINNT=0x0601','/DWINVER=0x0601')
    $resourceCompiler = Join-Path $sdkRoot "bin/$sdkVersion/x86/rc.exe"
    & $resourceCompiler /nologo /fo instance-tests.res (Join-Path $nativeRoot 'instance-tests.rc')
    if ($LASTEXITCODE -ne 0) { throw 'Native instance fixture resource compilation failed.' }
    $sources = @('Platform.cpp','Security.cpp','SecurityTests.cpp','Payload.cpp','PayloadTests.cpp','LaunchContext.cpp','InstanceRedirect.cpp','InstanceTests.cpp','CacheCleanup.cpp','CacheCleanupTests.cpp','InstalledRegistration.cpp','SetupGuard.cpp','DesktopShortcut.cpp','UpdateTransaction.cpp','UpdateTests.cpp','PlatformTests.cpp') | ForEach-Object { Join-Path $nativeRoot $_ }
    & $compiler @common '/DPK_FIXTURE_BUILD' @sources 'instance-tests.res' '/Fe:PlatformTests.exe' '/link' '/SUBSYSTEM:CONSOLE,6.01' 'advapi32.lib' 'shell32.lib' 'user32.lib' 'ole32.lib' 'uuid.lib' 'version.lib'
    if ($LASTEXITCODE -ne 0) { throw 'Native route test build failed.' }
    $fixtureArguments = @()
    if ($FixtureCabinet -or $FixtureManifest -or $FixtureDirectory) {
        if (-not ($FixtureCabinet -and $FixtureManifest -and $FixtureDirectory)) { throw 'All three fixture paths are required.' }
        $fixtureArguments = @($FixtureCabinet, $FixtureManifest, $FixtureDirectory)
    }
    & (Join-Path $nativeBuild 'PlatformTests.exe') @fixtureArguments
    if ($LASTEXITCODE -ne 0) { throw 'Native route tests failed.' }
    & (Join-Path $msvcRoot 'bin/Hostx64/x86/dumpbin.exe') /headers /imports (Join-Path $nativeBuild 'PlatformTests.exe') | Set-Content -LiteralPath (Join-Path $nativeBuild 'PlatformTests.pe.txt')
    if (-not $TestsOnly) {
        if (-not $OutputPath) { throw 'An output path is required.' }
        if ($SetupGuardOnly) {
            $guardSources = @('Platform.cpp','Security.cpp','Payload.cpp','LaunchContext.cpp','CacheCleanup.cpp','InstalledRegistration.cpp','SetupGuard.cpp') | ForEach-Object { Join-Path $nativeRoot $_ }
            & $compiler @common '/DPK_SETUP_GUARD_BUILD' '/LD' @guardSources "/Fe:$OutputPath" '/link' '/SUBSYSTEM:WINDOWS,6.01' '/DYNAMICBASE' '/NXCOMPAT' '/EXPORT:CheckInstalledSessionW=_CheckInstalledSessionW@4' '/EXPORT:CheckPortableFileW=_CheckPortableFileW@8' '/EXPORT:CheckInstalledPackageW=_CheckInstalledPackageW@4' '/EXPORT:GetInstalledPackageTargetW=_GetInstalledPackageTargetW@4' 'advapi32.lib' 'shell32.lib' 'user32.lib' 'ole32.lib' 'uuid.lib' 'version.lib'
            if ($LASTEXITCODE -ne 0) { throw 'Native setup guard build failed.' }
            & (Join-Path $msvcRoot 'bin/Hostx64/x86/dumpbin.exe') /headers /imports /exports $OutputPath | Set-Content -LiteralPath "$OutputPath.pe.txt"
            return
        }
        if ($UpdaterOnly) { $ResourceScript = Join-Path $nativeRoot 'updater.rc' }
        if (-not $ResourceScript) { throw 'A resource script is required.' }
        $resourceCompiler = Join-Path $sdkRoot "bin/$sdkVersion/x86/rc.exe"
        & $resourceCompiler /nologo /I $nativeRoot /fo launcher.res $ResourceScript
        if ($LASTEXITCODE -ne 0) { throw 'Native resource compilation failed.' }
        $launcherNames = @('Platform.cpp','Security.cpp','Payload.cpp','LaunchContext.cpp','InstanceRedirect.cpp','CacheCleanup.cpp','InstalledRegistration.cpp','Main.cpp')
        if ($UpdaterOnly) { $launcherNames = @('Platform.cpp','Security.cpp','Payload.cpp','LaunchContext.cpp','CacheCleanup.cpp','InstalledRegistration.cpp','DesktopShortcut.cpp','UpdateTransaction.cpp','UpdaterMain.cpp') }
        $launcherSources = $launcherNames | ForEach-Object { Join-Path $nativeRoot $_ }
        $defines = @(); if ($UiFixture) { $defines += '/DPK_UI_FIXTURE' }
        & $compiler @common @defines @launcherSources 'launcher.res' "/Fe:$OutputPath" '/link' '/SUBSYSTEM:WINDOWS,6.01' '/MANIFEST:NO' '/DYNAMICBASE' '/NXCOMPAT' 'advapi32.lib' 'shell32.lib' 'user32.lib' 'gdi32.lib' 'ole32.lib' 'uuid.lib' 'version.lib'
        if ($LASTEXITCODE -ne 0) { throw 'Native launcher build failed.' }
        & (Join-Path $msvcRoot 'bin/Hostx64/x86/dumpbin.exe') /headers /imports $OutputPath | Set-Content -LiteralPath "$OutputPath.pe.txt"
    }
} finally { Pop-Location; $env:INCLUDE = $previousInclude; $env:LIB = $previousLib; $env:PATH = $previousPath }
