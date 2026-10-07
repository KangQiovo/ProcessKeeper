# Process Keeper 1.8.0

**English** | [简体中文](RELEASE-1.8.0.zh-CN.md) | [繁體中文](RELEASE-1.8.0.zh-TW.md) | [Home](../README.md)

The stable release, application version, update identity and tag all use **1.8.0** / `v1.8.0`.

## Changes

- **Hide Microsoft apps is off by default for new settings.** Existing saved choices are retained; new whitelist rules remain empty. Enabling the filter changes display only, without granting close, startup or uninstall authority.
- Expand Microsoft ownership checks to healthy, non-development OS-registered Store/System packages, including packages without a standalone EXE. Other eligible registered packages need independent native package-signature verification. This covers cases such as Developer Home and HEVC extensions without trusting a familiar name, copied executable or publisher string.
- Keep third-party applications visible when their own publisher conflicts with Microsoft ownership, even if their discovered files include a Microsoft component.
- Connect duplicate installed records through exact registered MSI shortcut/component identity, registered icon directory plus product/company evidence, and supported Burn/PEP514 runtime registration. Apple Software Update, Autodesk Access and Python now show one application parent while retaining their original records and components.
- Locate the actual registered main executable by default. Advertised MSI icons remain components; Python's cached installer is a Helper rather than a main candidate. Generic Python hosts still retain conservative host handling without the exact registration evidence.
- Finish pending publisher verification and refresh display filters even while live process collection is paused. Background verification retries are bounded and do not restart collection.
- Synchronize native caption controls with the selected application theme and preserve readable content over customized native backdrops. Existing official-download preference, manual Update and restart, settings backups and material controls are retained.

## Downloads

| Asset | Intended use |
| --- | --- |
| [ProcessKeeper-v1.8.0.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/ProcessKeeper-v1.8.0.exe) | Universal portable; includes all three runtime routes. Largest download; PK14 for older updaters. |
| [ProcessKeeper-v1.8.0-win7-x86-compat.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/ProcessKeeper-v1.8.0-win7-x86-compat.exe) | Compatibility portable; Windows 7 SP1 / 8.1 and supported newer x86/x64 systems. WPF; requires .NET Framework 4.6.2 or compatible newer 4.x. |
| [ProcessKeeper-v1.8.0-win7-x86-compat-setup.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/ProcessKeeper-v1.8.0-win7-x86-compat-setup.exe) | Compatibility installer with uninstaller; same runtime requirements. |
| [ProcessKeeper-v1.8.0-win10-x86-x64.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/ProcessKeeper-v1.8.0-win10-x86-x64.exe) | Windows 10/11 x86/x64 portable; modern WinUI on x64 build 19041+, bundled x86 WPF fallback on supported systems. |
| [ProcessKeeper-v1.8.0-win10-x86-x64-setup.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/ProcessKeeper-v1.8.0-win10-x86-x64-setup.exe) | x86/x64 installer with uninstaller; includes both runtime routes. |
| [ProcessKeeper-v1.8.0-win10-arm64.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/ProcessKeeper-v1.8.0-win10-arm64.exe) | Native ARM64 portable; Windows 10 build 19041+ / Windows 11 ARM64. The x86 bootstrapper uses Windows emulation. |
| [ProcessKeeper-v1.8.0-win10-arm64-setup.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/ProcessKeeper-v1.8.0-win10-arm64-setup.exe) | ARM64 installer with uninstaller; same runtime requirements. |

Verify downloads with the accompanying [SHA256SUMS](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/SHA256SUMS). Windows may show **Unknown publisher** because the release executables are unsigned.

**For 1.6.x in-app upgrades, choose the Universal EXE.** Its PK14 format is accepted by the older updater; split PK17 editions and installers require a newer updater or manual download. Current updaters prefer the verified matching edition and portable/installed type. Updating requires an explicit action and confirmation; there is no automatic countdown or silent installation. Installer updates open the standard setup wizard.

## Verification and boundaries

Read-only validation on a modern Windows host covered all **103 Microsoft package UI candidates**, **27 Microsoft-publisher desktop records forming 18 parents**, and **14 third-party records containing Microsoft components forming 13 parents**. The Microsoft candidates were filtered and the third-party parents remained visible. Installed presentation reduced **323 records to 280 parents** while retaining each original identity and **1469 distinct EXE paths**. This is a measured sample, not a claim to discover or hide every Microsoft item on every system. Regression tests cover exact registration links, negative identity cases, component roles and both UI routes; see [Testing](TESTING.md).

In the same sample, **165 Microsoft-related classic uninstall registrations** included only **5** with sufficient application evidence to hide. **134** had no readable application EXE/evidence and **26** had only icon/installer evidence; those records remain visible. Unknown, unhealthy, development-mode, unreadable or pending ownership is not inferred from the product name.

Matching display names do not prove a shared product. Distinct Windows package families retain separate identities; sparse package and Win32 records without an exact external-location/component association remain separate. Shared hosts, different runtime environments and unrelated tasks also remain distinct. Grouping preserves the exact original close, startup-change and uninstall targets.

Inventory is bounded by readable registrations, supported providers and filesystem limits. Access-denied processes, unloaded user hives, provider delays and unusual portable software remain coverage limits. **Windows 7 / 8.1 / 10 and ARM64 target-device installation, update and execution have not been certified by real-device testing.** Modern-host x86 tests and cross-compilation do not establish those outcomes. The compatibility interface lacks WinRT package inventory/startup declarations, browser live preview and x64 AVD environment reconstruction; ARM64 also disables the x64-specific AVD reader. See [Compatibility](COMPATIBILITY.md).
