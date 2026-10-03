# Uninstall recommendation catalog

[English](UNINSTALL-RULES.md) | [简体中文](UNINSTALL-RULES.zh-CN.md) | [繁體中文](UNINSTALL-RULES.zh-TW.md)

This documents the **built-in catalog of registration-name hints**, not an antivirus signature database. Version **2026.09.30.2**, sources checked **2026-09-30**: **68 product rules and 117 explicit aliases**, comprising **12 watchlist rules** and **56 rules linked to 26 first-party product reports**. The linked report rules include 42 from Huorong, 2 from the 360 security team and 12 from Malwarebytes. Research involved reading public reports, not downloading or executing samples.

The same recommendation label is used for both groups, but the details explicitly state the basis, matched registration name, reason, report date when available, source URL and catalog version. A watchlist entry expresses a review preference; it is not evidence that the product is malware. Historical reports concern the samples, channels and versions described there. For example, the [Huorong channel analysis](https://www.huorong.cn/document/tech/vir_report/1858) describes different packages for different download channels. A current installation with the same name has not been tested by this catalog.

Matching uses complete product names and bounded numeric version/architecture suffixes, not vendor-wide prefixes or generic filenames. Names can be impersonated. The catalog does not inspect file contents, scan every disk, identify unregistered implants or remove leftover services/drivers. An unmarked application is not certified safe. [Microsoft's criteria](https://learn.microsoft.com/en-us/defender-xdr/criteria) distinguish potentially unwanted applications from malware.

**You decide whether to uninstall.** A label does not select, stop or remove software. Normal removal uses the registered uninstaller; a supported registered quiet command requires a further confirmation. The app rechecks the registration and executable before launch, then reports the observed registration state. There is no automatic folder/driver deletion or promise that resistant software is completely removed.

The Uninstall page also supports manually checked, signed catalog updates. See [the update format and maintenance procedure](CATALOG-UPDATES.md). The table below describes the bundled version.

## Public report groups

Product names below are identifiers and remain in their original language. Dates are original publication dates, not the date of this research. A dash means a publication date has not been recorded; the source-check date above still applies. Multiple rules can share one report.

| Report date | Products | Source |
| --- | --- | --- |
| 2020-01-03 | 旋风PDF / 聚众壁纸 | [www.360.cn](https://www.360.cn/n/11465.html) |
| 2020-11-19 | Ant压缩 / 考拉壁纸 / 小树PDF | [www.huorong.cn](https://www.huorong.cn/document/tech/safety-classroom/1714) |
| 2020-03-06 | 趣压 / 拷贝兔 / 小白看图 | [www.huorong.cn](https://www.huorong.cn/document/tech/vir_report/1666.html) |
| 2018-07-11 | 快压 / WinHome主页卫士 / 小黑记事本 / ABC看图 | [www.huorong.cn](https://www.huorong.cn/document/tech/vir_report/1669.html) |
| 2025-08-14 | 小蓝鸽极速卸载 / DX强力修复 / EchoFind | [www.huorong.cn](https://www.huorong.cn/document/tech/vir_report/1839) |
| 2025-11-11 | 鲁大师 / 小鸟壁纸 / DXRepair / TabXExplorer / 花瓣护眼 / 小蓝鸽 / 大力文件粉碎 / 迅读PDF大师 / Halo壁纸 / 手机模拟大师 | [www.huorong.cn](https://www.huorong.cn/document/tech/vir_report/1858) |
| 2019-03-14 | WIFI共享大师 | [www.huorong.cn](https://www.huorong.cn/document/tech/vir_report/720.html) |
| 2018-10-25 | 2345浏览器 / 2345好压 / 2345看图 / 2345拼音 | [www.huorong.cn](https://www.huorong.cn/document/tech/vir_report/723) |
| 2017-12-01 | 云爱PE工具箱 / 美捷便签 / swf播放精灵 / 美捷闹钟 | [www.huorong.cn](https://www.huorong.cn/document/tech/vir_report/778.html) |
| 2020-02-27 | 巧压 | [www.huorong.cn](https://www.huorong.cn/document/tech/vir_report/811) |
| 2020-06-18 | 万能压缩 / 起点PDF阅读器 / 迷你看图王 / 新速压缩 / 直购助手 | [www.huorong.cn](https://www.huorong.cn/document/tech/vir_report/826) |
| 2021-08-26 | 火萤视频桌面 | [www.huorong.cn](https://www.huorong.cn/document/tech/vir_report/846.html) |
| 2022-08-19 | 即刻PDF阅读器 | [www.huorong.cn](https://www.huorong.cn/document/tech/vir_report/858.html) |
| 2023-12-04 | 云即玩 / 天空压缩 | [www.huorong.cn](https://www.huorong.cn/document/tech/vir_report/888.html) |
| — | ByteFence Anti-Malware | [www.malwarebytes.com](https://www.malwarebytes.com/blog/detections/pup-optional-bytefence) |
| — | CrossBrowse | [www.malwarebytes.com](https://www.malwarebytes.com/blog/detections/pup-optional-crossbrowse) |
| — | MyPC Backup | [www.malwarebytes.com](https://www.malwarebytes.com/blog/detections/pup-optional-mypcbackup) |
| — | OneLaunch | [www.malwarebytes.com](https://www.malwarebytes.com/blog/detections/pup-optional-onelaunch) |
| — | OneStart | [www.malwarebytes.com](https://www.malwarebytes.com/blog/detections/pup-optional-onestart) |
| — | PC Accelerate Pro | [www.malwarebytes.com](https://www.malwarebytes.com/blog/detections/pup-optional-pcacceleratepro) |
| — | Reimage Repair | [www.malwarebytes.com](https://www.malwarebytes.com/blog/detections/pup-optional-reimage) |
| — | RelevantKnowledge | [www.malwarebytes.com](https://www.malwarebytes.com/blog/detections/pup-optional-relevantknowledge) |
| — | Search Protect | [www.malwarebytes.com](https://www.malwarebytes.com/blog/detections/pup-optional-searchprotect) |
| — | Segurazo | [www.malwarebytes.com](https://www.malwarebytes.com/blog/detections/pup-optional-segurazo) |
| — | Wajam | [www.malwarebytes.com](https://www.malwarebytes.com/blog/detections/pup-optional-wajam) |
| — | Web Companion | [www.malwarebytes.com](https://www.malwarebytes.com/blog/detections/pup-optional-webcompanion) |

## Watchlist

These twelve rules are maintained review preferences, including the requested 360 and Kingsoft products. They are not classified as malware by this project.

- 360安全卫士 / 360 安全卫士 / 360安全卫士极速版 / 360 Total Security / 360 Internet Security
- 360杀毒 / 360 杀毒
- 金山毒霸 / 新毒霸 / Kingsoft Antivirus / Kingsoft Duba
- 2345安全卫士 / 2345 安全卫士 / 2345安全中心
- 2345软件管家
- 金山软件管家
- 驱动精灵 / 驱动精灵万能网卡版
- 驱动人生 / 驱动人生8 / 驱动人生9
- 小鱼压缩
- 小白压缩
- 万能看图王
- 嗨格式压缩大师

## Exclusions and corrections

- A promoted product, an impersonated brand and an analyzed malicious component are different evidence roles. The 360 report lists 旋风PDF as an additional promoted installation, not proof that its current files contain the malware analyzed elsewhere in that report.
- Official QQ, WPS and 7-Zip are not flagged merely because an attacker used their names or targeted their processes. General terms such as `commander`, `云计算`, `多标签文件管理器`, `setup.exe` and `winhost.exe` are not standalone catalog matches.
- Huorong article `/1985` was visible in search snippets but its live original page could not be corroborated during this check. It was not adopted as a report-backed rule.
- This catalog ships with the application. It does not continuously fetch threat intelligence or upload your installed-software inventory.

The exact aliases and reasons are maintained in [UninstallRecommendations.cs](../src/ProcessKeeper.Core/UninstallRecommendations.cs). Please [open an issue](https://github.com/KangQiovo/ProcessKeeper/issues) with the registered name, publisher, version, trusted source and reproduction details for a correction. Remove personal paths and identifiers; do not attach malware or private files.
