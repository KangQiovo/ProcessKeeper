# Process Keeper 1.7.1v2

**English** | [简体中文](RELEASE-1.7.2.zh-CN.md) | [繁體中文](RELEASE-1.7.2.zh-TW.md) | [Home](../README.md)

Release name **1.7.1v2** uses numeric version **1.7.2** and tag `v1.7.2`. Existing 1.7.1 update clients require standard SemVer, so this revision remains discoverable without weakening package validation.

## Changes

- Automatic updates prefer the official GitHub download source. Third-party sources are tried only when the official source cannot deliver a valid download; explicit source selections remain respected. Release metadata and SHA-256 authority always come from the fixed official repository.
- Download progress stays in the main application by default. The optional **Updater** window opens only after selecting its checkbox or explicitly viewing it. Pausing, resuming and switching sources keep the current verified download state.
- Add a native **Update and restart** action beside completed download progress in the main application. Only a verified, ready download enables it. The existing confirmation and safe handoff still run; downloading never triggers an automatic exit or installation.
- Rename the progress window **Updater** and give it a separate icon derived from the application bookmark with a download arrow. Both modern and compatibility interfaces use the same icon; the trusted native helper embeds it too.
- About shows the version label and actual build hash on separate lines. The hash wraps and can be copied; the bottom-left version presentation is unchanged.
- Appearance adds optional native backdrop tuning: tint opacity, luminosity opacity and tint color, with immediate preview and a return to native defaults. These values are included in settings backups. They affect the background only; Mica samples the wallpaper rather than revealing windows behind it. System transparency, high contrast and material support can override the effect. The compatibility interface preserves these settings but cannot render WinUI materials.
- Retain 1.7.1 improvements to row expansion, Microsoft filtering, matching package editions, owned cache cleanup and conservative empty installation-folder cleanup. [Previous changes](RELEASE-1.7.1.md).

- Add this project's own repository above referenced projects using the same native presentation and browser confirmation.

## Download selection

| Asset | Intended use |
| --- | --- |
| `ProcessKeeper-v1.7.2.exe` | Universal portable. Includes x64 WinUI, x86 WPF compatibility and ARM64 WinUI routes; largest download. |
| `ProcessKeeper-v1.7.2-win7-x86-compat.exe` | Compatibility portable for Windows 7 SP1 / 8.1 and supported newer x86/x64 systems; requires .NET Framework 4.6.2 or compatible newer 4.x. |
| `ProcessKeeper-v1.7.2-win10-x86-x64.exe` | Portable for supported Windows 10/11 x86/x64. WinUI on x64 build 19041+; bundled WPF fallback for x86 and supported older x64 systems. |
| `ProcessKeeper-v1.7.2-win10-arm64.exe` | Native ARM64 portable for Windows 10 build 19041+ / Windows 11 ARM64; the x86 launcher uses Windows emulation. |
| `ProcessKeeper-v1.7.2-win7-x86-compat-setup.exe` | Compatibility installer with uninstaller. Same runtime requirements as its portable edition. |
| `ProcessKeeper-v1.7.2-win10-x86-x64-setup.exe` | x86/x64 installer with uninstaller and the same two runtime routes. |
| `ProcessKeeper-v1.7.2-win10-arm64-setup.exe` | ARM64 installer with uninstaller. Same runtime requirements as its portable edition. |

All official assets have checksums in `SHA256SUMS`. New local whitelist rules start empty. Community cloud profiles are loaded only by explicit user action.

Existing appearance settings and backups migrate to native defaults. Backups containing the new appearance parameters require this revision or newer; older builds do not understand appearance schema 2.

## Updates and known limitations

Download progress, pause/resume and source switching remain available. There is **no automatic countdown** or unsolicited exit: installation starts only after the user clicks the update action and confirms. Selecting an installer opens the standard setup wizard after the verified handoff; confirm its destination and finish the wizard. Cancelling setup does not complete an update. It is not a silent installation. Older 1.6.x updaters accept the Universal PK14 package; split PK17 packages require a newer updater or manual download.

Updating an existing installation with a portable package updates that same installation folder and retains its uninstall registration. Download separately to another folder if you want an independent portable copy. Moving between supported installed editions also requires confirmation in the setup wizard. A Universal payload keeps the Universal update default even when placed in a verified installation folder.

Packages are unsigned, so UAC can show Unknown publisher. WinUI materials and some capabilities differ in the compatibility interface. Windows 7 and ARM64 support remain targets without certification on physical devices. Package verification and isolated native/x86 interface tests do not prove a complete real-machine installation or update. Security software, file locks and network failures can still interrupt operations; results are reported without treating them as successful. Please [submit an issue](https://github.com/KangQiovo/ProcessKeeper/issues) with the edition, operating system and reproducible steps to help improve compatibility.
