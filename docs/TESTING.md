# Testing

**English** | [简体中文](TESTING.zh-CN.md) | [繁體中文](TESTING.zh-TW.md)

Read [BUILDING.md](BUILDING.md) for toolchain requirements and [COMPATIBILITY.md](COMPATIBILITY.md) for supported routes and limitations. Commands below run from the repository root on Windows with PowerShell 7 and the required .NET SDK. Do not run multiple builds against the same project's `obj` directory at once.

## 1.8.0 display ownership and registration grouping

Verify fresh settings start with Hide Microsoft apps off and an empty whitelist, while explicitly saved visibility preferences survive reload and backup/import. For package ownership, cover healthy registered Store/System packages with and without EXEs, independently trusted native signatures for other eligible packages, and rejection of copied roots, stale identities, development mode, untrusted signatures and publisher-only claims. A Microsoft child component must not hide a third-party parent.

Keep registration grouping and component-role failures RED before implementation. Cover native MSI ProductCode/component association, an exact registered icon directory with product/company evidence, and Burn/PEP514 provider/version/architecture/runtime paths. Reject fake GUID paths, missing registrations, conflicting vendors, another product, incomplete metadata and mismatched runtime/cache paths. Verify the real main file is the default target, icon/cache components remain present, and generic Python hosts stay protected without exact registration evidence. All original installation/action identities must remain available after merging.

Exercise both interfaces with live collection paused: incomplete publisher evidence must trigger bounded display retries, eventually change the actual visible rows, preserve original mutations and stop after completion, cancellation or closure. Test positive and negative inventory cases rather than just the initially reported screenshots. Native caption light/dark tests must inspect the selected app theme and readable customized backdrops.

The read-only modern-host sample had 103 package UI candidates, 27 Microsoft-publisher desktop records forming 18 parents, and 14 third-party records with Microsoft components forming 13 parents. Installed presentation retained all 323 original identities and 1469 distinct EXE paths in 280 parents. The 165 related uninstall registrations included 5 with sufficient evidence, 134 without readable application evidence and 26 with only icon/installer evidence. Remaining visible unknown ownership and distinct package families are coverage boundaries, not test failures to hide by name. Keep detailed local inventory and private paths out of Git; see [release notes](RELEASE-1.8.0.md).

## Retained 1.7.1v2 (numeric 1.7.2) update checks

Verify automatic ordinary and resumed downloads choose GitHub first, fallback only after official failure, retain explicit source selections, and never fetch release authority from mirrors. Test official timeout, invalid content, cancellation, resumed range offsets and checksum validation with bounded fake HTTP handlers.

For both UI routes, verify the progress-window checkbox starts unchecked, downloading and Ready do not create or reveal a window, explicit viewing preserves state, the dedicated icon/title are used, and the main-app Update and restart action requires the selected digest-bound verified download. Cancelled confirmation must retain Ready; only confirmed manual action may prepare/start an update. Inspect the new controls at minimum width in light/dark themes. About hash and version must be separate, the footer unchanged, and the project-home link must request browser confirmation.

Keep retained 1.7.1 metadata tests: its real published Core library accepts standard tag v1.7.2 and matching numeric filenames, but cannot recognize the nonstandard v1.7.1v2 tag. This is why the friendly release name and update identity differ. Binary/container verification and isolated WPF/WinUI fixtures do not certify real Windows 7/ARM64 installation or replacement.

Native backdrop tuning must retain automatic Windows defaults until customization is enabled. Check version-1 appearance migration, strict version-2 numeric/color validation, settings backup round trips and preservation while changing theme or material. In isolated WinUI windows, exercise live slider changes, debounced persistence, controller reuse, disconnection disposal, light/dark and high-contrast configuration, unsupported/disabled states and narrow layouts. The compatibility interface must retain values without pretending to render unsupported WinUI materials. Do not dim text or change whole-window opacity to simulate these parameters.

## Start with the affected suite

```powershell
# Run from the repository root in PowerShell 7.
dotnet run --project src/ProcessKeeper.Tests/ProcessKeeper.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Settings.Tests/ProcessKeeper.Settings.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.CloudProfiles.Tests/ProcessKeeper.CloudProfiles.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Tools.Tests/ProcessKeeper.Tools.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Display.Tests/ProcessKeeper.Display.Tests.csproj -c Release --framework net8.0-windows10.0.19041.0
dotnet run --project src/ProcessKeeper.Search.Tests/ProcessKeeper.Search.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Autorun.Tests/ProcessKeeper.Autorun.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Update.Tests/ProcessKeeper.Update.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Architecture.Tests/ProcessKeeper.Architecture.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Instance.Tests/ProcessKeeper.Instance.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Uninstall.Tests/ProcessKeeper.Uninstall.Tests.csproj -c Release --framework net8.0-windows10.0.19041.0
dotnet run --project src/ProcessKeeper.Performance.Tests/ProcessKeeper.Performance.Tests.csproj -c Release --framework net8.0-windows10.0.19041.0
dotnet run --project src/ProcessKeeper.CatalogUpdate.Tests/ProcessKeeper.CatalogUpdate.Tests.csproj -c Release --framework net8.0-windows10.0.19041.0
```

These commands are examples, not the complete release gate. Other projects cover installed catalogs, icons, activity history, AVD arguments and recovery, special windows, browser preview, window routing, startup, single-instance behavior, localization and graceful closure.

Cloud profile tests use fake unauthenticated GitHub Contents responses and isolated files: fixed directory, metadata/blob identity, UTF-8/schema/portability limits, errors and cancellation, empty defaults, the optional seven-app template, and atomic imports with revision checks and a maximum of five profiles. They do not require GitHub credentials or modify a user's whitelist. The downloader tests use controlled HTTP responses; loopback/native UI evidence does not establish Internet download speed or old-device compatibility.

For the actual x86 Framework backend:

```powershell
dotnet build src/ProcessKeeper.Legacy.Core.Tests/ProcessKeeper.Legacy.Core.Tests.csproj -c Release
& ./src/ProcessKeeper.Legacy.Core.Tests/bin/Release/net462/ProcessKeeper.Legacy.Core.Tests.exe
dotnet run --project src/ProcessKeeper.Display.Tests/ProcessKeeper.Display.Tests.csproj -c Release --framework net462
dotnet run --project src/ProcessKeeper.Uninstall.Tests/ProcessKeeper.Uninstall.Tests.csproj -c Release --framework net462
dotnet run --project src/ProcessKeeper.Performance.Tests/ProcessKeeper.Performance.Tests.csproj -c Release --framework net462
dotnet run --project src/ProcessKeeper.CatalogUpdate.Tests/ProcessKeeper.CatalogUpdate.Tests.csproj -c Release --framework net462
dotnet run --project src/ProcessKeeper.Legacy.Instance.Tests/ProcessKeeper.Legacy.Instance.Tests.csproj -c Release
```

The Legacy test executable must run on Windows with a compatible .NET Framework runtime. Building it alone is not an execution result. It uses shared fixtures, isolated files and read-only process identity checks. Its optional `--read-only-live` argument inventories actual startup sources without modifying them; omit that option for routine regression runs.

## What a passing test means

| Test type | Evidence | Does not establish |
| --- | --- | --- |
| Pure or injected backend | Rule matching, state transitions, cancellation, identity rejection and failure handling against controlled inputs | Correct behavior of every installed application |
| Isolated file/native fixture | Actual file/ACL/pipe/window APIs and exclusively owned helper processes or loopback listeners | A successful operation against a user's real emulator, service or application |
| Framework x86 run on a modern host | Generated shared code builds and runs in that host's 32-bit CLR | Windows 7/8.1 execution or old-hardware performance |
| Injected UI fixture | Rendering and interaction under the tested size, theme, language and DPI | All display drivers, screen readers or real monitor arrangements |
| Deliberate real-system test | The exact scenario, system and result documented by its tester | Untested systems or loss-free closure/cloud synchronization |

The update suite uses fake HTTP and isolated download files; it does not install a release. Startup mutation tests use memory backends or fixture files; optional inventory is a separate read-only scope. Recent boundary regressions cover malformed backup isolation, bounded timed-out VM queries and strict update asset names. Shared assertions run in multiple projects and must not be counted as distinct real-world scenarios.

See the dedicated [AVD](../src/ProcessKeeper.Avd.Tests/README.md), [launcher](../src/ProcessKeeper.Launcher.Tests/README.md), [search](../src/ProcessKeeper.Search.Tests/README.md) and [Legacy Core](../src/ProcessKeeper.Legacy.Core/README.md) notes.

## 1.7.1 regression boundaries

Exercise row-body, icon, description, keyboard and rapid repeated clicks separately from explicit checkbox actions. Check every application page in both interfaces, nested platform/application/executable/process/source/version rows, collapsed parent selection, partial deselection and state retained after refresh or search. Native selection events and cancelled or failed Keep/Enable operations must not change logical selection. Inspect light/dark colors and layout at narrow and wide window sizes.

For Microsoft filtering, test the OS-registered Store Paint package and reject copied executables, mismatched roots, unhealthy or development packages and display-only publisher claims. Update candidates now include matching installers: verify exact target/distribution defaults, installed Universal payloads, missing matches, unknown identities, manual edition-change confirmation and explicit installer handoff. Keep old installer ownership markers readable across portable updates; validate migration and rollback against isolated metadata and inert packages.

Uninstaller filename matches are labels, never commands. Use isolated folders and an injected uninstall backend to test successful empty-folder removal, registration remaining, cancellation, nonzero exit, incomplete inventory, shared directories, reparse points, replacement races, unexpected files and preservation of parent folders. Cache fixtures cover active sessions, running helpers/installers, locked files, unknown content and owned abandoned download/job stages. Do not perform real uninstalls or delete real application folders as part of routine gates.

## 1.7.0 regression boundaries

Single-instance checks must span different package paths, versions and UI routes in one user/session. Verify that the winner's identity comes from its live process and protected launcher receipt, a losing launch cannot open a delayed onboarding/main window, and releasing a window or lease does not permit a successor before the previous process exits. A retained-instance exit is not update readiness. Use only isolated, owned fixture processes; do not close a user's real applications for this check.

The installed-application suite includes duplicate registrations, version/architecture suffixes, another drive, blank and conflicting publishers, shared command hosts, retained executable-role evidence, exact startup entries and individual whitelist states. Uninstall tests retain every registration while grouping related versions. Grouping must preserve source identities: a shorter list alone is not a pass.

Update tests use seven published-asset candidates and verify that only a compatible flavor is eligible. Check Markdown notes and links, progress-window pause/resume and source switching, navigation during a download, idle-after-download behavior, explicit final update consent and canceled confirmation preserving ready bytes, and old-backup cleanup only after replacement readiness. Download state persists within the application's lifetime; do not claim resume across a full application exit.

Update fixtures use all seven asset kinds. Only the exact matching portable flavor is eligible; installer executables remain manual. Inspect PK17 split manifests (Win10 has modern + compatibility), PK14 universal manifests, actual architecture and hashes. The real released 1.6 validator must accept Universal and refuse split/setup candidates without replacing a user application. Installer source/resource/compiler checks do not replace actual install/uninstall testing on each target OS.

Native grouping fixtures exercised three languages, light/dark and two window widths on the current modern host, including collapsed selection, partial deselection, search, stable row refresh and independent enable/protection state. Separate read-only real inventory checks compared the full sets of executable paths and source entry identities before/after grouping. Neither modifies real startup configuration or closes users' applications. Security software blocking or missing fixture output is interference to investigate, not a passing test or a verified product failure. Do not disable protection or approve prompts automatically.

## Build gates and manual checks

`build.ps1` runs the managed gates listed in that script and publishes the modern application; it is not a test-only command. Native launcher/updater and compatibility UI builds have separate requirements and steps in the build guide. Ordinary builds remain **Preview**, even in Release configuration. Use stable-channel flags only for an explicitly prepared official Stable release.

Before exercising real close, startup or update behavior, use a disposable test environment and processes/data you own. Record UAC cancellation, failed identity checks, recovery after interruption and the exact configuration affected. A synthetic success must not be reported as a successful real installation or application shutdown.

Real old-system and old-hardware validation remains open. Do not label a Windows 7 build tested solely because the Framework executable ran on a newer host.

## Report reproducible results

### Native loading-page themes

[Test-StartupTheme.ps1](../src/ProcessKeeper.UniversalLauncher/Test-StartupTheme.ps1) accepts an explicit `-FixturePath` and `-OutputDirectory`. Build the isolated native launcher with `build-native.ps1 -UiFixture` and its required resource arguments first. The script checks the fixture-only PE export before execution and rejects production packages or unrelated binaries. Redirect temporary files and screenshots to your chosen test workspace.

The matrix checks three languages, light/dark and injected high-contrast palettes, recovery/permission pages, actual text contrast, live theme messages and GDI resource growth. It reads the current Windows app preference without changing the host theme. This tests owned fixture windows on the current machine; it does not exercise production extraction, UAC, or old-system hardware.

Record the source commit, command, OS build, architecture, runtime, exit code, passed/failed/skipped counts and any relevant theme, language, size and DPI. Nonzero exit, a failed case, or an unexplained skip is not a clean pass. Test counts change; use the actual console/JSON result instead of a historical expected total.

Keep packages, caches, full logs and personal settings out of commits. Attach only sanitized summaries or minimal reproduction data. Public CI links may be added when available; a missing link must not be replaced with an invented passing run.
