# Managed launcher fixtures

**English** | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md)

This project tests the managed ZIP/cache launcher contracts and elevation coordination. It is **not the complete native universal launcher/updater test suite**. See [BUILDING.md](../../docs/BUILDING.md) for the native build and fixture steps.

Run on Windows with the .NET 8 SDK, from the repository root in PowerShell 7. Choose a disposable fixture location:

```powershell
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('processkeeper-launcher-tests-' + [guid]::NewGuid().ToString('N'))
dotnet run --project src/ProcessKeeper.Launcher.Tests/ProcessKeeper.Launcher.Tests.csproj -c Release -- $fixtureRoot
```

The suite creates isolated files, real named pipes and owned no-op lifetime helpers. It does not execute a payload, launch Process Keeper, request real UAC, create the production ProgramData cache or close an existing user program. Production ProgramData ancestor ownership/ACL validation is read only.

## Coverage

- Archive traversal, case collisions, file/directory conflicts and manifest/file hash mismatches.
- Cache locks, held file handles, link substitution, old-cache isolation and ownership/ACL restrictions.
- Pipe client identity, process lifetime and unpredictable coordination tokens.
- Injected elevation outcomes: direct administrator startup, requested elevation, cancellation, failure and retry. Startup errors cannot become success.

Use the actual console result and exit code; there is no fixed expected assertion total. Environments that cannot create a symbolic link report the affected checks as `SKIP`. Keep those skips in the report. Fixtures are retained for inspection and must not be committed.

An optional build can embed a synthetic ZIP and manifest through `TestPayloadZip` and `TestPayloadManifest`. Such checks extract and compare the fixture; they do not execute its main program. NativeAOT publishing needs the corresponding C++ tools and Windows SDK.

## Not covered

These non-elevating fixtures do not validate accepting a real UAC prompt, first creation of the protected production cache, or a complete user update. Record those separately in a controlled manual environment. See the [testing guide](../../docs/TESTING.md); do not upload test executables or fixture packages.
