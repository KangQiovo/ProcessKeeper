# Compatibility and known limitations

**English** | [简体中文](COMPATIBILITY.zh-CN.md) | [繁體中文](COMPATIBILITY.zh-TW.md) | [Home](../README.md)

## Runtime routes

| Environment | Route | Prerequisite |
| --- | --- | --- |
| Windows 10 build 19041+ x64 / Windows 11 x64 | Modern WinUI 3, .NET 8 | Included runtime in a universal package |
| Windows 7 SP1 x86/x64, Windows 8.1 x86/x64, earlier/x86 Windows 10 | x86 WPF / WPF UI, .NET Framework | Framework 4.6.2 or a compatible newer 4.x runtime |
| Windows 7 without SP1, Windows 8 initial release, XP, Vista, ARM | Requirements page only | No supported management route |

The native launcher targets x86 Windows 7 and uses a static C/C++ runtime. It selects a supported route without trying to load .NET 8 on Windows 7. Missing dependencies lead to official download/help pages, not automatic installer execution. If the modern process has exited after a failed launch, a user-selected compatibility fallback is offered; it does not start a second live management instance.

These are implementation targets. **Windows 7, Windows 8.1 and Windows 10 have not been validated on actual machines or VMs for this project.** Existing x86 execution tests ran on a modern Windows host. No minimum RAM, GPU or old-CPU performance claim has been established.

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
- **Settings:** rollback handles ordinary failures but cannot make multiple files atomic through power loss. Inspect backups after an interrupted import.
- **Updates:** no release or no official SHA-256 means no automatic installation. Third-party source availability is not guaranteed. API restrictions, network errors and incompatible assets are reported.
- **Risk mode:** skipping selected extra prompts can increase data-loss and desktop-disruption risk. Core identity and critical-process checks remain, and the mode is not persisted or exported.

## Help improve older-system support

Open an [issue](https://github.com/KangQiovo/ProcessKeeper/issues/new/choose) with Windows edition/build, SP level, x86/x64, Framework version, RAM/GPU, DPI, theme, application version/build date and reproducible steps. Redact paths, account information and tokens. Screenshots should include the actual message and the affected controls.

Useful test cases include cold start, cancelled UAC, missing Framework recovery, high DPI, small displays, large lists and reversible startup changes **using test-owned entries only**. Do not validate by terminating Windows critical processes. A VM snapshot or separate test machine is preferable for environment work.

## Upstream references

- [WinUI 3](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/)
- [.NET Framework system requirements](https://learn.microsoft.com/en-us/dotnet/framework/get-started/system-requirements)
- [Framework versions on Windows](https://learn.microsoft.com/en-us/dotnet/framework/install/on-windows-and-server)
- [WPF rendering tiers](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/graphics-rendering-tiers)
- [WPF UI](https://github.com/lepoco/wpfui)

Upstream requirements explain dependencies; they do not certify this application's entire workflow.
