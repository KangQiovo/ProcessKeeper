# AVD restart regression fixtures

**English** | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md)

This executable links current Core sources into its own build output. It does not launch the product, weaken its administrator guard, read Android authentication files or operate an existing emulator.

Run from the repository root on Windows with the .NET 8 SDK, using PowerShell 7:

```powershell
$resultJson = Join-Path ([IO.Path]::GetTempPath()) ('processkeeper-avd-' + [guid]::NewGuid().ToString('N') + '.json')
dotnet run --project src/ProcessKeeper.Avd.Tests/ProcessKeeper.Avd.Tests.csproj -c Release -- $resultJson
```

The process exits nonzero for a failed case and writes JSON with case/assertion counts and failure details. Use that actual output; there is no fixed historical pass count.

## What is exercised

- `FakeAvdHost` supplies synthetic identities, hashes, arguments, environment, listeners, exit state and windows. Shutdown and launch calls are recorded, not performed.
- The actual `AvdRestartService`, `AvdArgumentPolicy` and `AvdGuiMatcher` are exercised with short timeouts and at least three separate successful observations.
- Owned Win32 windows test real class/style/geometry metadata. A fixture class matching `Qt*QWindow*` does **not** run Qt or an emulator.
- Native protocol tests use only the test process identity and random loopback listeners it owns. Its console server records a shutdown command and replies; it never terminates a process. No user authentication file is read.

## Boundaries covered

Discovery must not shut down or launch anything. A plan binds the old process identities, SDK path/hash, arguments, environment, exact device name and ports. Ambiguous or changed bindings prevent action.

Cancellation reports the stages actually reached. An incomplete exit, occupied console/ADB port or changed SDK prevents a second launch. Concurrent execution or reusing a stale plan cannot duplicate shutdown/launch requests.

Successful page evidence must match the launched root or a verified SDK child and remain stable. Terminals, crash/helper windows, another device, headless engines, hidden/minimized/cloaked windows, invalid ancestry and transient handles do not count. Held launcher resources are released after success, failure or cancellation.

These tests combine synthetic orchestration with exclusively owned native boundaries. They do **not** demonstrate end-to-end restart of a user's AVD, Android boot completion or graphics-driver compatibility. See [TESTING.md](../../docs/TESTING.md); do not commit result files or test packages.
