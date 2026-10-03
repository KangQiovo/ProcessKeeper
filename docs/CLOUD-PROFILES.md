# Community cloud whitelist profiles

**English** | [简体中文](CLOUD-PROFILES.zh-CN.md) | [繁體中文](CLOUD-PROFILES.zh-TW.md)

Process Keeper starts with **one empty local whitelist profile**. Community profiles are optional reference configurations. There is no background fetch, automatic import or installation of missing software.

## Load a profile

Open **Settings → Backup → Whitelist profiles → Cloud whitelist profiles**. Select **Load from GitHub**, choose a file and review its JSON. The compatibility interface provides a separate **Preview cloud profile** button. **Import and apply** asks for confirmation, creates a new local profile and activates it. Existing profiles remain intact; no program is closed. A name already in use gets a numeric suffix.

Imported profiles count toward the **five local profiles** maximum, including the initial empty profile. At five, delete an unused inactive profile before importing. Revision checks reject concurrent edits instead of overwriting them. A complete settings backup includes imported profiles; a whitelist-only export contains the active rules.

The application reads regular JSON files from the public repository [KangQiovo/ProcessKeeper → community/profiles](https://github.com/KangQiovo/ProcessKeeper/tree/main/community/profiles). The repository is fixed in the official application. Requests are unauthenticated; the application does not send local rules or settings. Network failures, rate limits, malformed files and files changed after listing are reported. Loading is bounded to 100 JSON profiles, 200 directory entries and 1 MiB per file.

The optional [`kangqi-default.json`](https://github.com/KangQiovo/ProcessKeeper/blob/main/community/profiles/kangqi-default.json) protects Clash Verge, Codex, QQ, Steam, Huorong Security, UU Remote and TranslucentTB, including matching descendants. It is **not the local release default**. Uninstalled applications simply do not match. Process-name rules are broader than verified application identities: inspect every rule and the protection scope before importing.

## Contribute a profile

1. Fork the repository and create a uniquely named `.json` file directly under **`community/profiles/`**, for example `my-work-tools.json`. Use a short ASCII filename containing letters, numbers, `-` or `_`; its filename becomes the displayed profile name. Avoid filenames longer than 80 characters before `.json`.
2. Use the portable schema below. Prefer a whitelist-only export containing supported `Application` identities or exact `ProcessName` filenames. Remove machine paths, account information and unrelated settings.
3. Validate the JSON with Process Keeper's profile editor. Explain which applications each rule protects, why descendant matching is necessary and what versions or systems you actually tested.
4. Submit a Pull Request with the file and that explanation. Anyone can propose a file; repository maintainers review and merge it before it appears in the application. Publishing a proposal does not give direct write access to the main repository.

```json
{
  "Version": 1,
  "Rules": [
    {
      "Id": "example-auradio",
      "Name": "Auradio",
      "Kind": "ProcessName",
      "Value": "Auradio.exe",
      "Enabled": true,
      "IncludeDescendants": false
    }
  ]
}
```

All six rule fields are required. `Id` must be unique within the file; `Enabled` and `IncludeDescendants` must be JSON booleans. Only root fields `Version` and `Rules` are accepted. Unknown/duplicate fields, invalid enum names, invalid UTF-8, more than 1,000 rules or files larger than 1 MiB are rejected.

Cloud profiles accept `Application` with a portable `known:` identity or a valid `package:` family identity, and `ProcessName` with an exact executable filename. Unknown `known:` IDs remain unmatched. No wildcards, `ExecutablePath`, `Directory`, `Application` `path:` identities, symlinks, subdirectories or embedded commands are accepted. Local full backups may contain machine-specific rules, but they belong outside this public directory. See [Contributing](https://github.com/KangQiovo/ProcessKeeper/blob/main/CONTRIBUTING.md) for review and privacy guidance.

Community rules are matching instructions, not executable scripts or an endorsement of the listed software. They can protect more processes than intended if a name is ambiguous or descendant matching is too broad. Please report incorrect rules through an Issue or submit a focused correction.
