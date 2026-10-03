# Process Keeper 1.7.0

**English** | [简体中文](RELEASE-1.7.0.zh-CN.md) | [繁體中文](RELEASE-1.7.0.zh-TW.md) | [Home](../README.md)

## Choose your portable package

Version 1.7.0 publishes **seven packages**: three portable editions, three corresponding installers with uninstallers, and a universal portable EXE.

| File | Intended environment |
| --- | --- |
| `ProcessKeeper-v1.7.0-win7-x86-compat.exe` | Portable \| Win7 SP1 / 8.1 and supported newer x86/x64 systems \| WPF; requires .NET Framework 4.6.2 or compatible newer 4.x. |
| `ProcessKeeper-v1.7.0-win10-x86-x64.exe` | Portable \| Win10/11 x86/x64 \| WinUI on x64 build 19041+; bundled WPF compatibility UI for x86 or older supported x64 systems. |
| `ProcessKeeper-v1.7.0-win10-arm64.exe` | Portable \| Win10 build 19041+ / Win11 ARM64 \| Native ARM64 WinUI; x86 launcher under emulation. |
| `ProcessKeeper-v1.7.0-win7-x86-compat-setup.exe` | Installer + uninstaller \| Win7 SP1 / 8.1 and supported newer x86/x64 systems. |
| `ProcessKeeper-v1.7.0-win10-x86-x64-setup.exe` | Installer + uninstaller \| Win10/11 x86/x64 \| Includes the same modern and compatibility routes. |
| `ProcessKeeper-v1.7.0-win10-arm64-setup.exe` | Installer + uninstaller \| Win10 build 19041+ / Win11 ARM64. |
| `ProcessKeeper-v1.7.0.exe` | Universal portable \| All three runtime routes \| Largest download; historical updater-compatible PK14 format. |

The native launcher is x86/Win7-compatible and uses a static C/C++ runtime. Win7 compatibility packages support both x86 and x64. The Win10 x86/x64 package includes modern x64 and compatibility payloads; x86 uses WPF, while eligible x64 uses WinUI. ARM64 has its own native WinUI package. The universal package includes all three routes. Each target also has a matching installer with an uninstaller. Missing components lead to official help/download pages without automatic execution.

## Migration from 1.6.x

**For 1.6.x in-app upgrades, select ProcessKeeper-v1.7.0.exe (Universal).** The old updater accepts PK14 and rejects PK17 split editions or installers safely. To switch to a smaller edition or an installed copy, download its corresponding EXE manually. From 1.7.0, updates select the same portable package flavor; setup files are never passed to the automatic update helper.

Run the appropriate new EXE and review administrator consent. Existing whitelist profiles and settings are stored separately from the downloaded package and remain available. Check your rules before closing applications. Fresh installations still have an **empty local whitelist**; the seven-app community template remains optional and is loaded only by an explicit cloud-profile action.

## One application, expandable exact entries

- Installed registrations and multiple versions share one application parent when executable, identity or product/publisher evidence supports it. Original installations, locations, main/uninstaller labels and distinct component paths remain inspectable.
- Startup sources for the same application share a parent. Each registry view, task or other supported source retains its exact entry, enable state and individual management action.
- Running applications and whitelist rules use the shared ownership rules; uninstall groups normalize version and architecture suffixes while preserving each registration as an independent removal target.
- Selecting a parent includes its known descendants, even while collapsed. Partial selection, search and refresh preserve exact members; selection does not change protection or enable states.

Unknown ownership and conflicting publishers remain separate. Grouping is display organization, not permission to launch, close, disable or uninstall by name. A matching file name or inferred main label can still be wrong.

## Updates

The update dialog displays the release's actual Markdown notes and assets. From 1.7.0 onward, automatic installation selects an eligible package for the current flavor and architecture. Downloads use a progress window with pause/resume and source switching; navigation and repeated checks do not discard an active download in the current app session. Recovery after closing the application is not guaranteed.

After download, the package stays ready until you click Update and restart and confirm. There is no automatic countdown or exit. Replacement and desktop-shortcut updates use the verified handoff. The previous package backup is removed only after the new application reports ready; unrelated EXEs are not cleaned up. Missing hashes, incompatible assets and network failures are shown honestly.

## Validation and remaining issues

Grouping was checked in isolated native WinUI and 32-bit WPF fixtures on the current modern Windows host, across English, Simplified Chinese and Traditional Chinese, light/dark themes and two window widths. Read-only installed/startup inventory checks confirmed that original executable paths and entry identities were retained. These checks did not close users' processes or modify their startup configuration.

**Windows 7 / 8.1 / 10 and ARM64 deployment on real machines or VMs remains unverified.** Cross-compilation and a 32-bit CLR test on a modern host do not certify those environments. The compatibility route still lacks WinRT package inventory/startup declarations, live headless-browser preview and x64 AVD environment reconstruction; ARM64 also disables that x64-specific AVD reader. Fonts, materials, graphics and old-hardware performance can differ. Full production UAC, cache extraction and public-release upgrade workflows still need field testing. See [compatibility and limitations](COMPATIBILITY.md).

Report reproducible [issues](https://github.com/KangQiovo/ProcessKeeper/issues/new/choose), including OS/build, architecture, theme/DPI, app version and sanitized steps. Contributions to [portable cloud profiles](CLOUD-PROFILES.md), translations and compatibility fixes are welcome. Do not submit personal paths, credentials, test packages or logs containing private data.

## Installers and removal

Installers are compiled with pinned NSIS 3.13 and request administrator permission. They register an owned machine-wide Programs entry, install a stable ProcessKeeper.exe plus Uninstall.exe and install.ini, and create matching shortcuts. The uninstaller removes only its owned application files, links and registration; it preserves settings/history and startup recovery backups. Optional manual own-user data cleanup is described in the README. Installation/uninstallation on Win7, x86 and ARM64 remains unverified on real target systems.

In **Settings → About → Local files**, **Clear app cache** requests verified old-version cleanup. Current payloads, files in use, downloads, whitelist profiles and settings are retained. Unconfirmed or modified cache trees are not recursively erased; the result reports actual files, bytes and retained trees.
