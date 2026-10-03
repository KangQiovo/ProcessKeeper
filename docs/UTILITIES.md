# Utilities

**English** | [简体中文](UTILITIES.zh-CN.md) | [繁體中文](UTILITIES.zh-TW.md) | [Home](../README.md)

Utilities contains memory optimization, a file downloader and performance display settings. These are independent implementations. PCL source, binaries and private modules are not included.

## Memory optimization

The local launcher supplied for research was inspected as a managed PE file without loading or running its assembly. Although its folder was named `PCL 正式版 2.10.0`, the assembly metadata reported `2.12.7.3`. Its SHA256 was `09F7467C5A52D06BBDF3A4EC5F2C4E3298E8031063FF368EB2203B2C10AAAB86`.

The `--memory` startup branch reaches `MemoryOptimizeInternal`. That routine requires elevation, enables `SeProfileSingleProcessPrivilege`, and passes memory-list commands **2, 3 and 4** to `NtSetSystemInformation` information class **80**. This is a system-wide operation. It does not target only the launcher, Minecraft, or selected processes.

Process Keeper's **Standard** option follows those three operations: trim system working sets, write modified pages back, then purge standby pages. **Deep** additionally attempts file-cache trimming, low-priority standby purging, registry-cache trimming and identical-page combining. The optional NT scopes were researched against [PCL Community's historical implementation](https://github.com/PCL-Community/PCL-CE/blob/7b5c912ffa29ef5554d069e7605d3faa99238c4a/PCL.Core/App/Tools/MemSwapWorks.cs) and Windows declarations. [Microsoft documents the file-cache trimming call](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-setsystemfilecachesize).

Before execution, the dialog explains the selected scope and asks for confirmation. **Whitelisted programs are included.** This can temporarily stall applications, increase disk reads, or reduce the displayed working set without reducing committed memory. Usage may rise again. There is no scheduled or automatic optimization.

Each step reports the actual native result, including unsupported APIs and permission failures. Cancellation stops subsequent steps; it cannot undo a kernel operation already started. A dedicated worker uses a private thread token, leaving the process primary token unchanged. The report compares available and total physical memory before and after, retaining negative changes. Other programs also affect these measurements.

Validation covers confirmation, cancellation, concurrency, error reporting and resource cleanup with injected backends. Global memory-purge calls were **not executed during development testing**. Windows 7 and ARM64 hardware behavior remains unverified; a successful cross-build does not establish runtime support for undocumented APIs.

## Downloader

[PCL's public download engine](https://github.com/Meloong-Git/PCL/blob/main/Plain%20Craft%20Launcher%202/Modules/Base/ModNet.vb) uses HTTP byte ranges, parallel transfer, retries, fallback and final assembly/checking. It dynamically splits unfinished work. Its general download engine does not itself guarantee that an inaccessible resource or authenticated page can be downloaded.

Process Keeper uses a bounded implementation intended to keep memory and concurrency predictable on older hardware:

- Accepts HTTP/HTTPS file links and public GitHub release/raw links. A GitHub file-page URL is normalized to a raw-file URL.
- Choose the save directory with the folder button beside the read-only path; no path typing is required. A separate button opens the selected directory.
- Starts with one range and dynamically splits the largest unfinished portion, assigning its tail to additional connections. Finished connections can take over slow tails. The connection ceiling is configurable from 1–32, default 16; it is a ceiling, not a fixed equal partition. Ordinary HTTP/HTTPS direct links, including query-bearing links, can use this engine without any GitHub mirror.
- Fresh parallel downloads require a known length and actual byte-range support. Servers without strong validators may use fresh parallel transfers; fragments from these servers are discarded when restarting or resuming. A strong ETag, or a qualifying strong Date/Last-Modified pair without an ETag, permits validated resume. Weak ETags are never sent as If-Range. Without a trusted expected hash, a computed digest cannot prove that unvalidated fragments came from one unchanged file.
- Optional GitHub mirrors live under Download options. Automatic mirror mode probes reachable candidates and chooses the lowest-latency one, then tries alternatives on failure. Third-party mirrors require confirmation and accept only public GitHub release/raw links without query parameters. This restriction applies to mirrors, not the direct-link engine.
- Stores fragments beside the chosen destination. Validated pause/resume retains dynamic segment boundaries only while the source URL, length and validator match. A changed validator discards old fragments. Resume is limited to the current application session.
- Checks Content-Range, response length and contradictory validators. Retries interrupted transfers and falls back to a complete single connection if range support disappears.
- Follows at most 10 redirects, checking each destination before connecting. HTTPS links cannot redirect to HTTP; invalid schemes and embedded credentials are rejected.
- Shows bytes, speed, source, actual connection/segment counts and percentage with two decimal places. A single-connection fallback explains its reason. Unknown-size downloads show transferred bytes; `100.00%` is reserved for verified completion.
- Computes SHA256 for every completed file. An optional SHA256 from a trusted publisher must match before publication. A computed hash alone does not establish authenticity.
- Preserves existing destination files. Cancel or exit attempts to remove unfinished fragments, while completed files remain. Locked temporary files are reported for later cleanup instead of crashing the page. Downloaded files are never automatically executed.

The adaptive scheduler is independently implemented; PCL source is not copied. It has no account authentication, browser-cookie import, torrent support or persistent download queue. More connections cannot bypass a server's total rate limit or guarantee full bandwidth. HTTP errors, checksum failures and unavailable sources are reported as failures.

## Performance display

The in-app display shows the application's rendering rate and memory together with total physical RAM. It stays inside the content area. The desktop display measures the computer's CPU, physical memory and **observed desktop composition rate**. This is not a game's FPS counter or the monitor refresh rate. A static desktop may produce a low composition rate; unavailable measurements remain unavailable.

Use the existing controls to choose layout, material, position, size and locking. Native-window tests cover dragging without moving the main window, bounds, locking, rounded corners, light/dark themes and disposal on the current Windows host. Acrylic and composition measurements can be unavailable on older systems.
