# Community whitelist profiles

**English** | [简体中文](README.zh-CN.md) | [繁體中文](README.zh-TW.md)

Place portable single-profile JSON files directly in this directory. Everyone may propose a new profile through a Fork and Pull Request; maintainers review submissions before merging. Read the [format, limits and contribution instructions](../../docs/CLOUD-PROFILES.md) first.

`kangqi-default.json` is an optional seven-application reference. New installations still start with an empty local whitelist. The application loads these files only after a user explicitly requests them and imports a reviewed profile after confirmation. Imports count toward the maximum of five local profiles.

Do not submit private paths, local settings, credentials, symlinks, nested directories or executable content. Community rules are matching instructions and may need adjustment for another user's environment.
