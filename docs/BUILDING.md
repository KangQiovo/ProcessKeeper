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
dotnet run --project src/ProcessKeeper.Legacy.Core.Tests/ProcessKeeper.Legacy.Core.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Compatibility tests failed.' }
dotnet build src/ProcessKeeper.Legacy.App/ProcessKeeper.Legacy.App.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Compatibility build failed.' }
./src/ProcessKeeper.UniversalLauncher/build-native.ps1 -TestsOnly
./scripts/Test-BuildPipeline.ps1
```

`build.ps1` stamps the release once, runs the managed regression suites and publishes the modern payload into `App`. Build the compatibility payload **after that same stamp**. Do not regenerate build time between the two payload builds and packaging. For a private SDK/cache, use `-DotnetPath` and `-NugetPackages`. The native tests only use owned fixture processes.

Package both payloads without launching the application:

```powershell
New-Item -ItemType Directory -Path artifacts -Force | Out-Null
./package-universal.ps1 `
  -ModernDirectory ./App `
  -LegacyDirectory ./src/ProcessKeeper.Legacy.App/bin/Release/net462 `
  -OutputPath ./artifacts/ProcessKeeper-preview.exe
```

Choose a **new output filename** each time: the packager refuses to overwrite a release. It checks both payload timestamps and channels, builds the updater, packages a manifest/hash-verified CAB and embeds it in a native x86 EXE. `package-single-file.ps1` forwards to the universal packager; it also requires both payloads. It is not a separate supported distribution route.

Directly launching `App/ProcessKeeper.exe` is a development route: it lacks the original universal launch context required by in-app updating and desktop-shortcut creation. Do not treat those unavailable features as a successful update test.

## Official stable builds

`Release` is a compiler optimization configuration, **not** permission to remove the preview notice. Unspecified channels remain `Preview`, even after a previous stable build.

Only when preparing an intentional official stable release, run the same sequence with all three explicit settings:

```powershell
./build.ps1 -StableRelease
if ($LASTEXITCODE -ne 0) { throw 'Stable modern build failed.' }
dotnet build src/ProcessKeeper.Legacy.App/ProcessKeeper.Legacy.App.csproj -c Release -p:ProcessKeeperReleaseChannel=Stable
if ($LASTEXITCODE -ne 0) { throw 'Stable compatibility build failed.' }
./package-universal.ps1 -StableRelease `
  -ModernDirectory ./App `
  -LegacyDirectory ./src/ProcessKeeper.Legacy.App/bin/Release/net462 `
  -OutputPath ./artifacts/ProcessKeeper.exe
```

Run the remaining tests and inspect the actual final package before distribution. The packager rejects mixed channels and dates; unknown channel names fail the build. Only stable UI hides the preview sentence across sidebar, About and onboarding. This repository does not automatically create releases or upload binaries. No signing key or certificate is provided. Future official release assets must match the fixed repository, semantic version, portable EXE contract and official SHA-256 required by the updater.

## Public source checks

```powershell
./scripts/Test-Repository.ps1
```

This checks tracked filenames, common credential patterns, developer-specific paths, relative documentation links and language counterparts. It is a useful guard, not a complete secret audit. Read screenshots before committing: text scanners cannot inspect image privacy.

Generated `App`, `bin`, `obj`, `artifacts`, EXEs, DLLs, packages and personal runtime state are excluded. Do not force-add them. Tests are source code and belong in the repository; **test packages do not**.

An optional GitHub Actions definition is provided at [docs/examples/source-validation.yml](examples/source-validation.yml). A repository administrator can place it under `.github/workflows/` when workflow-write authorization is available. It tests/builds without uploading executables or making a release. Local verification remains described in [Testing](TESTING.md).
