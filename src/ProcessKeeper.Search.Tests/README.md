# Search association fixtures

**English** | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md)

Synthetic metadata only: this suite does not scan the filesystem, collect or control real processes, write user settings or create windows.

Run from the repository root with the required .NET SDK:

```powershell
dotnet run --project src/ProcessKeeper.Search.Tests/ProcessKeeper.Search.Tests.csproj -c Release
```

## Coverage

- Public process fields, exact PID matching, case handling and different cultures.
- A child-process match retains its owning application.
- Whitelist searches use the caller-provided visible process scope. Disabled rules remain inspectable without changing protection.
- Installed applications and executable components associate with rules through application keys, full paths, executable names and directory boundaries.
- Identical names at different paths, missing/independent component keys, offline applications and missing metadata.
- Concurrent query isolation over large synthetic catalogs and rule sets.

Read the real exit status and assertion summary rather than a fixed expected count. These tests do not replace UI interaction, list virtualization, actual inventory latency or memory measurements. See the [testing guide](../../docs/TESTING.md).
