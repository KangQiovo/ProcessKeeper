# Compatibility and known limitations

**English** | [简体中文](COMPATIBILITY.zh-CN.md) | [繁體中文](COMPATIBILITY.zh-TW.md) | [Home](../README.md)

## Runtime routes

| Environment | Route | Prerequisite |
| --- | --- | --- |
| Windows 10 build 19041+ x64 / Windows 11 x64 | Modern WinUI 3, .NET 8 | Included runtime in the x64 package |
| Windows 10 build 19041+ ARM64 / Windows 11 ARM64 | Native ARM64 WinUI 3, .NET 8 | Included runtime; x86 bootstrapper uses Windows emulation |
| Windows 7 SP1 x86/x64, Windows 8.1 x86/x64, earlier/x86 Windows 10 | x86 WPF / WPF UI, .NET Framework | Framework 4.6.2 or a compatible newer 4.x runtime |
| Windows 7 without SP1, Windows 8 initial release, XP, Vista, ARM32 | Requirements page only | No supported management route |

The native launcher is x86/Win7-compatible and uses a static C/C++ runtime. Win7 compatibility packages support both x86 and x64. The Win10 x86/x64 package includes modern x64 and compatibility payloads; x86 uses WPF, while eligible x64 uses WinUI. ARM64 has its own native WinUI package. The universal package includes all three routes. Each target also has a matching installer with an uninstaller. Missing components lead to official help/download pages without automatic execution.

These are implementation targets. **Windows 7, Windows 8.1, Windows 10 and ARM64 devices have not been validated on actual machines or VMs for this project.** Existing x86 execution tests ran on a modern Windows host. No minimum RAM, GPU or old-CPU performance claim has been established.

On ARM64, the package selects the native ARM64 modern payload; the x86 compatibility fallback is not offered. The x64-specific AVD environment reader is disabled on ARM64. Cross-compilation and native route tests on x64 are not ARM device validation.

**For 1.6.x in-app upgrades, select ProcessKeeper-v1.7.0.exe (Universal).** The old updater accepts PK14 and rejects PK17 split editions or installers safely. To switch to a smaller edition or an installed copy, download its corresponding EXE manually. From 1.7.0, updates select the same portable package flavor; setup files are never passed to the automatic update helper.

## Feature differences

| Capability | Modern | Compatibility |
| --- | --- | --- |
| Process identity, whitelist, close confirmations | Shared core | Shared core with explicit native/BCL adaptations |
| Classic installed software, startup sources | Yes, subject to access | Yes, subject to access |
| WinRT package inventory and startup declarations | Supported where accessible | Not implemented |
| Headless browser live preview | Supported verified cases | Not implemented |
| Reconstruct x64 AVD launch environment | Supported verified cases | Disabled in x86 mode; existing GUI recovery may work |
| Mica / acrylic and animations | Where supported, configurable | Opaque WPF theme; appearance can differ |
| Rules/settings file format | Shared | Shared |

The compatibility UI is WPF, **not WinUI 3 running on Windows 7**. Fonts, themes, DPI rounding, graphics acceleration and scrolling can differ. WPF may fall back to software rendering. Missing critical process identity or native functionality must fail safely, not silently bypass protection.

Some VM and tray adapters also require reliable cross-architecture argument/environment reading. The x86 compatibility process rejects those routes when it cannot verify an x64 target; restoring an already existing window may still be available.

## Known limitations and unverified scenarios

- **Real deployment:** production UAC consent/cancellation, first protected ProgramData extraction, and end-to-end public-release upgrading remain unverified as a complete user workflow. Isolated launch/update tests are not substitutes.
- **Unsigned builds:** Windows may show an unknown publisher. No production signing certificate is included. Internal hashes do not authenticate an arbitrarily rewritten executable.
- **Old Windows networking:** certificates, TLS support and system updates affect GitHub access. The application does not disable certificate validation or change the machine's TLS configuration to work around failures.
- **Provider delays:** WMI, COM, services and filesystem calls can block beyond an observation timeout. VM read concurrency is bounded; a stuck provider can remain busy until it returns or the app exits. Not every native call can be cancelled.
- **Inventory completeness:** unreadable processes, unloaded user hives, unusual portable apps and third-party startup mechanisms can be absent. A startup entry is not proof that it launched the current process.
- **Game grouping and publishers:** Steam ACF/VDF, Epic `.item`, Ubisoft installation records and supported EA Games registry entries are recognized; incomplete, conflicting or unsupported records remain ungrouped. This is not a complete EA app library inventory. Microsoft publisher checks are bounded and cached; missing or delayed signature evidence keeps the application visible.
- **Startup modification:** protected/system/policy/driver entries, unrecognized approval formats and services without verified original startup type remain read-only. Corrupt backups are rejected; other valid backups can still be inspected.
- **Application adapters:** Steam saving/cloud synchronization, real AVD restart, VM console attachment and tray activation depend on external software. No successful real-user AVD restart or Steam cloud-completion guarantee is claimed.
- **Window recovery:** a daemon may have no graphical window. Terminals, transient helpers and internal AVD windows are not evidence of a recovered page. Focus can be denied even when a window becomes visible.
- **Uninstall:** only readable classic uninstall registrations are covered. Portable and Store apps are not an exhaustive inventory. Quiet mode requires a registered native command. A surviving registration is not reported as successful removal; resistant software may require vendor-specific recovery.
- **Performance:** the in-app display measures this app's UI callbacks and memory alongside total physical memory. The desktop display measures system CPU, physical memory and observed desktop composition FPS; it does not measure game FPS or monitor refresh rate. Unavailable measurements remain unknown. Acrylic availability and old-GPU performance vary; see [Utilities](UTILITIES.md).
- **Settings:** rollback handles ordinary failures but cannot make multiple files atomic through power loss. Inspect backups after an interrupted import.
- **Updates:** no release or no official SHA-256 means no automatic installation. Third-party source availability is not guaranteed. API restrictions, network errors and incompatible assets are reported.
- **Risk mode:** skipping selected extra prompts can increase data-loss and desktop-disruption risk. Core identity and critical-process checks remain, and the mode is not persisted or exported.

## Help improve older-system support

Open an [issue](https://github.com/KangQiovo/ProcessKeeper/issues/new/choose) with Windows edition/build, SP level, x86/x64/ARM64, Framework version, RAM/GPU, DPI, theme, application version/build date and reproducible steps. Redact paths, account information and tokens. Screenshots should include the actual message and the affected controls.

Useful test cases include cold start, cancelled UAC, missing Framework recovery, high DPI, small displays, large lists and reversible startup changes **using test-owned entries only**. Do not validate by terminating Windows critical processes. A VM snapshot or separate test machine is preferable for environment work.

## Upstream references

- [WinUI 3](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/)
- [.NET Framework system requirements](https://learn.microsoft.com/en-us/dotnet/framework/get-started/system-requirements)
- [Framework versions on Windows](https://learn.microsoft.com/en-us/dotnet/framework/install/on-windows-and-server)
- [WPF rendering tiers](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/graphics-rendering-tiers)
- [WPF UI](https://github.com/lepoco/wpfui)

Upstream requirements explain dependencies; they do not certify this application's entire workflow.
