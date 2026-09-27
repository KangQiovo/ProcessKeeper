# Contributing to Process Keeper

**English** | [简体中文](CONTRIBUTING.zh-CN.md) | [繁體中文](CONTRIBUTING.zh-TW.md)

Bug reports, focused fixes, translations and compatibility results are welcome. Issues and pull requests may be written in English, Simplified Chinese or Traditional Chinese; English is not required.

## Before changing behavior

Read the [project overview](README.md), [build guide](docs/BUILDING.md), [compatibility boundaries](docs/COMPATIBILITY.md) and [testing guide](docs/TESTING.md). For a vulnerability, follow [SECURITY.md](SECURITY.md) instead of posting sensitive details in a public issue.

Describe the concrete problem, the affected version or commit, Windows build, architecture and selected interface. Include minimal reproduction steps and expected versus observed behavior. Redact account names, private paths, process arguments, window titles, URLs and configuration values from logs or screenshots. Do not attach a full process inventory or settings export by default.

## Keep changes reviewable

- Make one focused change and explain its user-visible outcome.
- Keep business and protection rules in shared Core where possible. Update the narrow Legacy adaptation when a shared API changes; do not edit generated `obj` source.
- Preserve identity rechecks, confirmation boundaries, protected backups, cancellation and resource limits. A started process or command window is not proof of a recovered graphical page.
- Treat unavailable native APIs as an explicit limitation. Do not weaken protection to make an old-system test pass.
- Update documentation in English, Simplified Chinese and Traditional Chinese, keeping language links aligned. Add application-owned strings through localization; keep the application name in English.

## Validate and describe the result

Run the tests relevant to the changed behavior and retain their actual exit status and summary. The [testing guide](docs/TESTING.md) distinguishes fake backends, owned native fixtures and real-system testing. Add regression coverage for a demonstrated defect or a meaningful boundary; do not claim an unrun check passed.

A pull request should explain the problem, final behavior, tests actually run and remaining limitations. For compatibility results, record the real OS build, process architecture, runtime and test scope. An x86 Framework test on a modern host does not prove Windows 7 works.

## Source and release hygiene

Do not commit or upload built EXEs, DLLs, archives, installer/test packages, runtime caches, `bin`/`obj`, personal settings, activity logs, startup backups, credentials or signing keys. Use synthetic examples and sanitized excerpts. Check staged files even when an ignore rule exists.

Ordinary builds are **Preview**, including `Configuration=Release`. Keep the test-quality notice in contribution builds and screenshots. **Stable is an explicit official release decision**, not a setting used to make a local build look finished. Follow the build and packaging checks; do not bypass channel or identity validation.
