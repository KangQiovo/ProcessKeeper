# Security policy

**English** | [简体中文](SECURITY.zh-CN.md) | [繁體中文](SECURITY.zh-TW.md)

Process Keeper can run with administrator privileges and change supported process or startup state. Its confirmations, identity checks, backups and update validation reduce specific risks; they are not a promise of complete safety.

## Report a vulnerability privately

Open this repository's [Security tab](https://github.com/KangQiovo/ProcessKeeper/security). **If “Report a vulnerability” is available**, use GitHub private vulnerability reporting. Do not assume this feature is enabled merely because this document exists.

There is no dedicated security email address or promised response time. If private reporting is unavailable, open a minimal issue asking the maintainer to arrange a private reporting channel. Do not include the vulnerability details there. Do not publish exploit payloads, credentials, private keys, personal configurations or another person's data in issues, discussions or pull requests.

A private report should include the affected version/commit, OS build and architecture, affected component, impact and minimal reproducible steps. Prefer a harmless fixture or redacted evidence. State whether the problem was observed in an actual packaged build, an injected test or a source review. Do not test against systems or accounts you do not control.

## Areas worth reporting

- An action reaching an unconfirmed or protected process, or stale PID identity being accepted.
- Startup changes bypassing source restrictions, losing recoverable state or overwriting unrelated changes.
- Update metadata, download, extraction or installer handoff accepting an unintended repository, file or execution target.
- Path traversal, link substitution, unsafe cache permissions, sensitive-data disclosure or incorrect privilege handling.

Ordinary UI defects and compatibility reports belong in [Issues](https://github.com/KangQiovo/ProcessKeeper/issues), with sensitive content removed.

## Release and verification boundaries

Work normally targets the current source preview. This document does not promise maintenance for historical builds. Only a release explicitly designated by the project as official Stable is stable; local Release builds remain Preview by default.

Update authority is fixed to `KangQiovo/ProcessKeeper`. Third-party nodes transport release assets; they are not authorities for metadata or hashes. A checksum is not a publisher signature, and an arbitrary modified local executable cannot be made impossible to patch. Do not interpret the repository name or a successful fixture as proof of binary authenticity.

The [compatibility guide](docs/COMPATIBILITY.md) and [testing guide](docs/TESTING.md) describe unverified old-system and real-operation boundaries. Report precise evidence; do not infer Windows 7 certification or successful recovery of user data from synthetic tests.
