# Build from source

**English** | [简体中文](BUILDING.zh-CN.md) | [繁體中文](BUILDING.zh-TW.md) | [Home](../README.md)

Build on a modern **x64 Windows** development machine. Windows 7 is a deployment target for the compatibility route, not a supported host for this toolchain. Administrator permission is not required for compilation or the isolated test suites.

## Prerequisites

- [PowerShell 7](https://github.com/PowerShell/PowerShell), available as `pwsh`. Scripts do not depend on Windows PowerShell 5.1. Set `PROCESSKEEPER_PWSH` or the supported `-PowerShellPath` argument when using a nonstandard location.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0), x64, not just the runtime. `global.json` stays within .NET 8 and selects the newest installed feature band.
- Visual Studio 2022 / Build Tools with **Desktop development with C++**, MSVC x86/x64 tools and a Windows SDK. Native scripts discover VS through `vswhere` and select a complete installed SDK; explicit `-VisualStudioPath`, `-WindowsSdkRoot` and `-WindowsSdkVersion` overrides are available.
- Internet access to restore the NuGet packages declared by the project files, including Framework 4.6.2 reference assemblies and WinUI build dependencies. The modern target uses Windows SDK projection version 10.0.26100.0 and minimum runtime 19041; this distinction does not raise the native compatibility route to Windows 11.

Framework reference assemblies allow compiling the legacy project on the development machine. The target computer still needs a compatible Framework runtime. See [compatibility](COMPATIBILITY.md).

## Preview build — the default

From the repository root in PowerShell 7:

```powershell
./build.ps1
if ($LASTEXITCODE -ne 0) { throw 'Modern build failed.' }
./build.ps1 -Architecture arm64 -SkipBuildStamp -SkipTests
if ($LASTEXITCODE -ne 0) { throw 'ARM64 build failed.' }
dotnet run --project src/ProcessKeeper.Legacy.Core.Tests/ProcessKeeper.Legacy.Core.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Compatibility tests failed.' }
dotnet build src/ProcessKeeper.Legacy.App/ProcessKeeper.Legacy.App.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Compatibility build failed.' }
./src/ProcessKeeper.UniversalLauncher/build-native.ps1 -TestsOnly
./scripts/Test-BuildPipeline.ps1
```

`build.ps1` stamps the release once, runs the managed regression suites and publishes the modern payload into `App`. Build the compatibility payload **after that same stamp**. Do not regenerate build time between the three payload builds and packaging. For a private SDK/cache, use `-DotnetPath` and `-NugetPackages`. The native tests only use owned fixture processes.

## Package the 1.7.1 release targets

The ARM64 build above cross-compiles the modern payload into `App-arm64` using the same stamp. The outer launcher and updater remain x86 and do not require ARM64 MSVC tools. Cross-compilation does not validate execution on an ARM device.

Windows7Compat packages the compatibility payload. Windows10x64 packages modern x64 **and** x86-compatible WPF fallback. Windows10arm64 packages native ARM64. Universal retains all three under PK14; formal 1.7.1 publishes all four portable outputs plus three installers.

```powershell
New-Item -ItemType Directory -Path artifacts -Force | Out-Null
$targets = [ordered]@{
  Windows7Compat = 'win7-x86-compat'
  Windows10x64 = 'win10-x86-x64'
  Windows10arm64 = 'win10-arm64'
}
foreach ($entry in $targets.GetEnumerator()) {
  ./package-universal.ps1 -PackageTarget $entry.Key `
    -ModernDirectory ./App -Arm64Directory ./App-arm64 `
    -LegacyDirectory ./src/ProcessKeeper.Legacy.App/bin/Release/net462 `
    -OutputPath ("./artifacts/ProcessKeeper-preview-" + $entry.Value + ".exe")
  if ($LASTEXITCODE -ne 0) { throw 'Portable package failed.' }
}
```

Choose a **new output filename** each time. The packager refuses to overwrite an existing file, checks the selected payload's channel and timestamp, builds the native updater, creates a manifest/hash-verified CAB and embeds it in an x86 bootstrapper. No application is launched by packaging.

-PackageTarget Universal remains the default and uses PK14. Formal 1.7.1 includes ProcessKeeper-v1.7.1.exe, which the released 1.6 updater accepts. PK17 split packages remain unsupported by that old updater; select Universal for migration or manually download another edition.

Directly launching `App/ProcessKeeper.exe` is a development route and lacks the original portable-launch context required for in-app updating and desktop-shortcut creation. An unavailable update route is not a successful update test.

## Official stable builds

`Release` is a compiler optimization configuration, **not** permission to remove the preview notice. Unspecified channels remain `Preview`, even after a previous stable build. Only use explicit stable-channel settings for an intentional official release, keeping one shared build stamp:

```powershell
./build.ps1 -StableRelease
if ($LASTEXITCODE -ne 0) { throw 'Stable modern build failed.' }
./build.ps1 -Architecture arm64 -SkipBuildStamp -SkipTests -StableRelease
if ($LASTEXITCODE -ne 0) { throw 'Stable ARM64 build failed.' }
dotnet build src/ProcessKeeper.Legacy.App/ProcessKeeper.Legacy.App.csproj -c Release -p:ProcessKeeperReleaseChannel=Stable
if ($LASTEXITCODE -ne 0) { throw 'Stable compatibility build failed.' }
New-Item -ItemType Directory -Path artifacts -Force | Out-Null
$targets = [ordered]@{
  Windows7Compat = 'win7-x86-compat'
  Windows10x64 = 'win10-x86-x64'
  Windows10arm64 = 'win10-arm64'
}
foreach ($entry in $targets.GetEnumerator()) {
  ./package-universal.ps1 -StableRelease -PackageTarget $entry.Key `
    -ModernDirectory ./App -Arm64Directory ./App-arm64 `
    -LegacyDirectory ./src/ProcessKeeper.Legacy.App/bin/Release/net462 `
    -OutputPath ("./artifacts/ProcessKeeper-v1.7.1-" + $entry.Value + ".exe")
  if ($LASTEXITCODE -ne 0) { throw 'Stable portable package failed.' }
}
```

Run the remaining tests and inspect **every actual EXE**, its route marker, PE architecture, embedded manifest, payload hashes, empty default rules and published SHA-256 before distribution. Mixed channels/dates or unknown channels are rejected. Only stable UI hides the preview sentence across sidebar, About and onboarding. The repository does not automatically publish releases or provide a signing certificate. Compatible future update assets must match the fixed repository, semantic version, package flavor, `PK17` contract and official SHA-256. See [1.7.1 release notes](RELEASE-1.7.1.md).

## Public source checks

```powershell
./scripts/Test-Repository.ps1
```

This checks tracked filenames, common credential patterns, developer-specific paths, relative documentation links and language counterparts. It is a useful guard, not a complete secret audit. Read screenshots before committing: text scanners cannot inspect image privacy.

Generated `App`, `App-arm64`, `bin`, `obj`, `artifacts`, EXEs, DLLs, packages and personal runtime state are excluded. Do not force-add them. Tests are source code and belong in the repository; **test packages do not**.

An optional GitHub Actions definition is provided at [docs/examples/source-validation.yml](examples/source-validation.yml). A repository administrator can place it under `.github/workflows/` when workflow-write authorization is available. It tests/builds without uploading executables or making a release. Local verification remains described in [Testing](TESTING.md).

## Installers and removal

Installers are compiled with pinned NSIS 3.13 and request administrator permission. They register an owned machine-wide Programs entry, install a stable ProcessKeeper.exe plus Uninstall.exe and install.ini, and create matching shortcuts. The uninstaller removes only its owned application files, links and registration; it preserves settings/history and startup recovery backups. Optional manual own-user data cleanup is described in the README. Installation/uninstallation on Win7, x86 and ARM64 remains unverified on real target systems.

```powershell
./package-universal.ps1 -StableRelease -PackageTarget Universal `
  -ModernDirectory ./App -Arm64Directory ./App-arm64 `
  -LegacyDirectory ./src/ProcessKeeper.Legacy.App/bin/Release/net462 `
  -OutputPath ./artifacts/ProcessKeeper-v1.7.1.exe
if ($LASTEXITCODE -ne 0) { throw 'Universal package failed.' }
./src/ProcessKeeper.UniversalLauncher/build-native.ps1 -SetupGuardOnly `
  -OutputPath ./artifacts/ProcessKeeper.SetupGuard.dll `
  -BuildDirectory ./artifacts/setup-guard
if ($LASTEXITCODE -ne 0) { throw 'Setup guard build failed.' }
foreach ($entry in $targets.GetEnumerator()) {
  ./scripts/package-installers.ps1 -StableRelease -PackageTarget $entry.Key `
    -PortablePath ("./artifacts/ProcessKeeper-v1.7.1-" + $entry.Value + ".exe") `
    -OutputPath ("./artifacts/ProcessKeeper-v1.7.1-" + $entry.Value + "-setup.exe") `
    -NsisCompiler ./tools/nsis-3.13/makensis.exe `
    -SetupGuardPath ./artifacts/ProcessKeeper.SetupGuard.dll
  if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
}
```
