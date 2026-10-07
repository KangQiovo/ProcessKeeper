# Process Keeper 1.7.1

**English** | [简体中文](RELEASE-1.7.1.zh-CN.md) | [繁體中文](RELEASE-1.7.1.zh-TW.md) | [Home](../README.md)

## Changes

- Click application rows, icons or text to expand/collapse their details. Only checkbox clicks select entries. This applies to both interfaces and all application lists, including nested platform, executable, process, startup and uninstall rows. Selecting a parent includes its known descendants, even when collapsed. Existing Keep/Enable actions retain their meaning; cancelled or failed actions preserve selection.
- Fix Microsoft Store Paint remaining visible with **Hide Microsoft applications** enabled. Package identity must match a healthy, OS-registered Microsoft Store/System package and its actual installation path. A copied executable, display publisher string or familiar name does not establish Microsoft ownership.
- Default update selection follows the current package target and verified distribution type. Universal stays Universal. Split editions keep their matching target; installed editions prefer their matching installer. If the matching asset is missing, the dialog asks for an explicit choice instead of silently switching editions. A different compatible package requires a separate confirmation; incompatible or unverifiable packages remain unavailable.
- The sidebar displays the running version. About adds theme-aware edition labels beside the version, distinguishing Universal, portable, installed and compatibility routes. Remove the fixed repository label above update-source controls; the official update repository remains fixed internally.
- Cache cleanup also removes inactive, application-owned completed update packages, interrupted downloads and prepared update stages. It preserves active sessions, locked or unrecognized files, settings and whitelist rules. It does not search arbitrary user download directories.
- Broaden uninstaller-role recognition across numbered `unins` files, localized names and vendor variants. Registered commands remain authoritative, including legitimate `Setup.exe` or `Update.exe` uninstall routes; filename guesses are only uncertain display labels and never executable uninstall instructions.
- After a successful registered uninstall, remove only its verified empty installation folder. Require a complete inventory, removal of the original registration, no shared ownership and unchanged directory identity. Nonempty, shared, replaced, linked or unverified folders and all parent folders are retained; this is not recursive leftover removal.

## Download selection

| Asset | Intended use |
| --- | --- |
| `ProcessKeeper-v1.7.1.exe` | Universal portable. Includes x64 WinUI, x86 WPF compatibility and ARM64 WinUI routes; largest download. |
| `ProcessKeeper-v1.7.1-win7-x86-compat.exe` | Compatibility portable for Windows 7 SP1 / 8.1 and supported newer x86/x64 systems; requires .NET Framework 4.6.2 or compatible newer 4.x. |
| `ProcessKeeper-v1.7.1-win10-x86-x64.exe` | Portable for supported Windows 10/11 x86/x64. WinUI on x64 build 19041+; bundled WPF fallback for x86 and supported older x64 systems. |
| `ProcessKeeper-v1.7.1-win10-arm64.exe` | Native ARM64 portable for Windows 10 build 19041+ / Windows 11 ARM64; the x86 launcher uses Windows emulation. |
| `ProcessKeeper-v1.7.1-win7-x86-compat-setup.exe` | Compatibility installer with uninstaller. Same runtime requirements as its portable edition. |
| `ProcessKeeper-v1.7.1-win10-x86-x64-setup.exe` | x86/x64 installer with uninstaller and the same two runtime routes. |
| `ProcessKeeper-v1.7.1-win10-arm64-setup.exe` | ARM64 installer with uninstaller. Same runtime requirements as its portable edition. |

All official assets have checksums in `SHA256SUMS`. New local whitelist rules start empty. Community cloud profiles are loaded only by explicit user action.

## Updates and known limitations

Download progress, pause/resume and source switching remain available. There is **no automatic countdown** or unsolicited exit: installation starts only after the user clicks the update action and confirms. Selecting an installer opens the standard setup wizard after the verified handoff; confirm its destination and finish the wizard. Cancelling setup does not complete an update. It is not a silent installation. Older 1.6.x updaters accept the Universal PK14 package; split PK17 packages require a newer updater or manual download.

Updating an existing installation with a portable package updates that same installation folder and retains its uninstall registration. Download separately to another folder if you want an independent portable copy. Moving between supported installed editions also requires confirmation in the setup wizard. A Universal payload keeps the Universal update default even when placed in a verified installation folder.

Packages are unsigned, so UAC can show Unknown publisher. WinUI materials and some capabilities differ in the compatibility interface. Windows 7 and ARM64 support remain targets without certification on physical devices. Package verification and isolated native/x86 interface tests do not prove a complete real-machine installation or update. Security software, file locks and network failures can still interrupt operations; results are reported without treating them as successful. Please [submit an issue](https://github.com/KangQiovo/ProcessKeeper/issues) with the edition, operating system and reproducible steps to help improve compatibility.
