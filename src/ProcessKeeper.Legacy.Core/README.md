# Legacy Core

**English** | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md)

The compatibility backend targets .NET Framework 4.6.2 and exposes the shared `ProcessKeeper.Core` API. The compatibility application uses an x86 process. Windows 7 SP1 is a target, **not a claim of completed Windows 7 testing**; see the [compatibility guide](../../docs/COMPATIBILITY.md).

## Shared source and native boundaries

`GenerateCompatibleCore.ps1` generates build-only files under `obj` from modern Core. Maintain business rules in the shared source and limit adaptations to required framework/OS differences. Never edit or commit generated copies. Security-sensitive native replacements check expected occurrence counts and fail the build when their source shape changes.

`LegacyWindowsCapabilities` detects the OS version and uses the same verified process handle for critical-process checks. Missing identity or an unsupported API cannot silently authorize closing a process. PID, creation time, path, account/session and whitelist checks remain in the shared workflows.

## Capability limits

- x86 mode cannot safely reconstruct an x64 AVD's original environment, so no restart plan is offered. Existing graphical windows can still be restored where their identity is verifiable.
- VM/tray adapters that require the same x64 command-line reader may also refuse discovery.
- Browser live preview and WinRT package inventory/startup declarations are unavailable in this build.
- Traditional desktop and startup backends remain real implementations; source availability and access restrictions still apply.

## Build and test

Use the [build guide](../../docs/BUILDING.md) for the .NET SDK, PowerShell 7 and NuGet dependencies. PowerShell is a build dependency; the deployed compatibility application needs .NET Framework 4.6.2 or a compatible newer 4.x runtime.

From the repository root:

```powershell
dotnet build src/ProcessKeeper.Legacy.Core.Tests/ProcessKeeper.Legacy.Core.Tests.csproj -c Release
& ./src/ProcessKeeper.Legacy.Core.Tests/bin/Release/net462/ProcessKeeper.Legacy.Core.Tests.exe
```

Tests use shared fixtures, isolated files and read-only process identity collection. `--read-only-live` optionally enumerates actual startup sources without changing them. Omit it for routine regression. A successful run proves x86 Framework behavior on the **reported host**, not old-OS or old-hardware certification. Use the actual test summary instead of a fixed expected count.

See [testing](../../docs/TESTING.md) and [contributing](../../CONTRIBUTING.md). Ordinary Release builds remain Preview.
