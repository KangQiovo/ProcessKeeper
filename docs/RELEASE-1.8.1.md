# Process Keeper 1.8v2

**English** | [简体中文](RELEASE-1.8.1.zh-CN.md) | [繁體中文](RELEASE-1.8.1.zh-TW.md) | [Home](../README.md)

The application and release title display **1.8v2**. The update identity, executable version, tag and filenames use **1.8.1** / `v1.8.1`, so existing 1.8.0 clients recognize this release as newer.

## Changes

- The update file selector lists recognized release packages only. `SHA256SUMS`, documents, archives and helper executables remain available as release metadata but cannot be selected as application updates. The default still follows the verified running edition and installed/portable type.
- Enabling automatic update checks immediately saves the setting and starts a check once startup is ready. Subsequent launches check in the background; a new-release dialog can appear over any page, including before About has been opened. Busy dialogs defer presentation, closing cancels it, and a failed presentation does not suppress a later attempt.
- Selecting a different package type uses **Continue**, closes the package chooser, then displays a separate confirmation identifying the current and selected editions. Cancel starts no download.
- A verified new launcher starts a bounded background cleanup pass for old payload receipts, including receipts registered after the new application starts. It preserves the active payload, live/locked helpers, unrecognized files and unsafe paths. This addresses cleanup formerly deferred until a later launch.
- Existing desktop shortcuts targeting the same managed EXE continue to refresh after replacement. Cleanup does not sweep other downloaded copies or rewrite unrelated shortcuts; manual copies in another folder require separate removal. Settings and whitelist rules are preserved.
- Synchronize the native progress-window title bar with the application theme while retaining the Windows high-contrast palette. Verify the updater's embedded and window icons against the original download-arrow artwork.

The new-settings defaults from 1.8 remain: **Hide Microsoft apps is off**, and the local whitelist starts empty. Existing saved settings are retained. [1.8 changes](RELEASE-1.8.0.md) describe publisher verification, grouping and main-executable recognition.

## Downloads

| Asset | Intended use |
| --- | --- |
| [ProcessKeeper-v1.8.1.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/ProcessKeeper-v1.8.1.exe) | Universal portable; all three runtime routes; largest package. PK14 supports older updaters. |
| [ProcessKeeper-v1.8.1-win7-x86-compat.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/ProcessKeeper-v1.8.1-win7-x86-compat.exe) | Compatibility portable; Windows 7 SP1 / 8.1 and supported newer x86/x64 systems; WPF; requires .NET Framework 4.6.2 or compatible newer 4.x. |
| [ProcessKeeper-v1.8.1-win7-x86-compat-setup.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/ProcessKeeper-v1.8.1-win7-x86-compat-setup.exe) | Compatibility installer with uninstaller; same runtime requirements. |
| [ProcessKeeper-v1.8.1-win10-x86-x64.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/ProcessKeeper-v1.8.1-win10-x86-x64.exe) | Windows 10/11 x86/x64 portable; WinUI on x64 build 19041+; bundled WPF fallback on supported x86/older systems. |
| [ProcessKeeper-v1.8.1-win10-x86-x64-setup.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/ProcessKeeper-v1.8.1-win10-x86-x64-setup.exe) | x86/x64 installer with uninstaller; both runtime routes. |
| [ProcessKeeper-v1.8.1-win10-arm64.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/ProcessKeeper-v1.8.1-win10-arm64.exe) | Native ARM64 portable; Windows 10 build 19041+ / Windows 11 ARM64; x86 launcher uses Windows emulation. |
| [ProcessKeeper-v1.8.1-win10-arm64-setup.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/ProcessKeeper-v1.8.1-win10-arm64-setup.exe) | ARM64 installer with uninstaller; same runtime requirements. |

Verify downloads using [SHA256SUMS](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/SHA256SUMS). Unsigned executables can show **Unknown publisher** in Windows.

**For 1.6.x in-app upgrades, choose the Universal EXE.** Split PK17 packages and installers require a newer updater or manual download. Updating requires an explicit action and confirmation; there is no automatic countdown or silent installation. Installer updates open the standard setup wizard.

## Verification and known limits

Regression coverage uses real owned WinUI/WPF windows, isolated preference/download files, injected network/install boundaries, and inert native payload fixtures. It covers hidden About startup, checkbox persistence, global dialogs, filtered asset selection, package-change cancellation, progress/resume controls and guarded old-payload cleanup. Full build and package checks verify architecture, manifests, payload hashes, stable channel and empty whitelist defaults. See [Testing](TESTING.md).

These checks do not certify real-device Windows 7 / 8.1 / 10 or ARM64 installation, update and execution. The compatibility interface lacks WinRT package inventory/startup declarations, browser live preview and x64 AVD reconstruction; ARM64 also disables the x64-specific AVD reader. See [Compatibility](COMPATIBILITY.md). Cleanup is deliberately limited to verified owned files and can retain busy or unverifiable trees. Report reproducible problems through [Issues](https://github.com/KangQiovo/ProcessKeeper/issues).
