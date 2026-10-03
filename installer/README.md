# Installer packaging

**English** | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md) | [Home](../README.md)

The three native NSIS installers contain their corresponding official portable EXE. They install one stable `ProcessKeeper.exe`, a native `Uninstall.exe` and an ownership marker. They do not run .NET or download another installer during setup.

| Installer | Intended route |
| --- | --- |
| `ProcessKeeper-v1.7.0-win7-x86-compat-setup.exe` | Intel/AMD Windows 7 SP1 / 8.1 / 10 or later; compatibility UI, including x86 machines |
| `ProcessKeeper-v1.7.0-win10-x86-x64-setup.exe` | Intel/AMD Windows 10 or later; x64 modern UI where supported, x86 compatibility route otherwise |
| `ProcessKeeper-v1.7.0-win10-arm64-setup.exe` | Native ARM64 Windows 10 build 19041+ / Windows 11; ARM64 modern UI |

All setup/uninstall executables are native x86 Unicode programs. ARM64 Windows uses x86 emulation for the installer and bootstrapper; the modern app payload is ARM64. Missing Framework dependencies remain the app launcher's explicit recovery workflow. These are implementation targets; real Windows 7/8.1/10 and ARM64 installation/uninstallation has not been certified.

## Compile

Obtain the **NSIS 3.13 ZIP** from the [official download page](https://nsis.sourceforge.io/Download). This build used its official SourceForge archive with SHA-256 `BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8`. Extract it into your own tool directory; installing the compiler is unnecessary. The packager checks version `v3.13`, records the compiler hash, and disables external `nsisconf.nsh` configuration. The installer uses zlib compression. Its upstream license is preserved in [NSIS.3.13-LICENSE.txt](../licenses/NSIS.3.13-LICENSE.txt).

First build the matching portable target using the [build guide](../docs/BUILDING.md). Its adjacent `.package.json` must match the actual EXE, current source inputs, flavor and release channel. From the repository root in PowerShell 7:

```powershell
./src/ProcessKeeper.UniversalLauncher/build-native.ps1 -SetupGuardOnly `
  -OutputPath ./artifacts/ProcessKeeper.SetupGuard.dll `
  -BuildDirectory ./artifacts/setup-guard
if ($LASTEXITCODE -ne 0) { throw 'Setup guard build failed.' }
./scripts/package-installers.ps1 -PackageTarget Windows10x64 `
  -PortablePath ./artifacts/ProcessKeeper-v1.7.0-win10-x86-x64.exe `
  -OutputPath ./artifacts/ProcessKeeper-v1.7.0-win10-x86-x64-setup.exe `
  -NsisCompiler E:/Tools/nsis-3.13/makensis.exe `
  -SetupGuardPath ./artifacts/ProcessKeeper.SetupGuard.dll -StableRelease
```

Use `Windows7Compat` or `Windows10arm64` for the other two installers. Omit `-StableRelease` only for matching preview inputs. A compiler optimization configuration does not authorize changing the release channel. `-ValidateOnly` checks the inputs without compiling or writing an installer. Output files must not already exist; linked paths, mismatched evidence and source changes during compilation are refused. Compilation never executes setup, uninstall or the embedded application.

## Installed files and update contract

Setup requests administrator permission and registers a machine-wide installation in the **32-bit view** of `HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\ProcessKeeper` for every flavor. Defaults use Program Files (x86) for the compatibility package and for an x86 host; modern packages use the native Program Files directory on 64-bit hosts. The directory picker can choose another dedicated local directory. Drive roots, Windows directories, linked paths and unrelated same-name programs are refused.

The exact contract consists of `InstallLocation`, quoted `UninstallString`, fixed `ProcessKeeperRepository=KangQiovo/ProcessKeeper`, `ProcessKeeperPackageTarget` and DWORD `ProcessKeeperInstallerContract=1`. The `install.ini` file repeats the repository, target and contract in an `Installation` section. `DisplayName` stays **Process Keeper**, `Publisher` is **KangQi**, and `DisplayIcon` points to the installed EXE. These metadata fields do not sign an executable; UAC may still display an unknown publisher.

In-app updates replace the stable EXE at the same path, preserve the uninstaller and marker, and update the existing owned uninstall registration's version only after the replacement reports ready and passes the identity checks. Reinstalling the same flavor reuses a verified directory. To change flavors or relocate an installed copy, uninstall the previous copy first; personal settings are retained. The embedded native guard checks protected sessions for the exact installed original, including cached UI and pending updater helpers. Active sessions or unverifiable ownership stop setup without terminating a process. The guard is not installed as a sidecar.

Installation stages and hashes the new EXE before moving the three exact original files into a unique backup folder. Checked file and registration failures attempt bounded restoration; any unrestored backup stays visible in the details. Uninstallation likewise stages all three originals before removing the owned registration. This reduces partial replacement risks; it does not guarantee atomic recovery after power loss. Keep a retained backup until you verify the installation.

Setup creates an all-users Start menu shortcut and the installing user's actual Desktop `Process Keeper.lnk`. Existing unrelated same-name shortcuts are preserved. Update/uninstall checks require the exact application target and empty arguments; they do not search or delete arbitrary shortcuts.

## Uninstall and personal data

Use Windows Programs / Installed apps or the installation's `Uninstall.exe`. Native confirmation remains visible; no quiet uninstall command is registered. Close Process Keeper and complete or cancel pending updates first. The uninstaller checks the exact directory, registry and marker, rejects links/reparse points, then removes only `ProcessKeeper.exe`, `Uninstall.exe`, `install.ini`, owned shortcuts and the fixed registration. Directories are removed only when empty; recursive deletion is never used.

Personal profiles, history, startup recovery backups and protected extraction caches are **preserved by default**. Removing application files does not revert startup settings you previously changed. See [portable files and removal](../README.md#portable-files-and-removal) for optional cleanup of your own user data and SID directory. Other users' data and unrelated files must remain untouched.

The source/PE/compiler checks use isolated inert assets and do not exercise an actual installation or uninstallation. Disk interruption, permissions, UAC and older-system behavior still require testing in a disposable environment. Do not upload compiler archives, fixture installers, logs or personal state to Git.
