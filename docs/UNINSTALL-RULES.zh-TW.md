# 解除安裝建議規則庫

[English](UNINSTALL-RULES.md) | [简体中文](UNINSTALL-RULES.zh-CN.md) | [繁體中文](UNINSTALL-RULES.zh-TW.md)

本文記錄**內建登錄名稱提示庫**，不是防毒軟體的病毒特徵庫。版本 **2026.09.30.2**，來源核對日期 **2026-09-30**：共 **68 條軟體規則、117 個明確別名**，分為 **12 條關注清單**及 **56 條關聯 26 份第一方軟體報告的規則**。報告規則中，42 條來自火絨、2 條來自 360 安全團隊、12 條來自 Malwarebytes。本輪僅閱讀公開報告，沒有下載或執行樣本。

兩類規則統一顯示建議標籤，詳細資訊明確列出依據類別、匹配登錄名、原因、可取得的報告發佈日期、原文連結及規則版本。關注清單表示建議使用者核對，不代表病毒鑑定。歷史報告針對當時分析的樣本、管道和版本；例如[火絨管道分析](https://www.huorong.cn/document/tech/vir_report/1858)明確記錄了不同管道安裝套件的差異。本規則庫沒有檢測你電腦上同名軟體的目前檔案。

匹配使用完整產品名及限定的數字版本、架構後綴，不按整個廠商、泛稱或通用檔名判斷。登錄名可能被冒用。本功能不掃描檔案內容和所有磁碟，也不識別全部未登錄植入物或清理殘留服務、驅動程式。未標記不代表安全。[微軟判定標準](https://learn.microsoft.com/en-us/defender-xdr/criteria)也區分潛在不受歡迎程式與惡意軟體。

**是否解除安裝由你決定。** 標籤不會自動勾選、停止或解除安裝程式。正常解除安裝使用軟體登錄的解除安裝程式；支援的靜默命令需要再次確認。啟動前重新核對登錄項和執行檔，隨後如實報告登錄狀態。不自動刪除目錄、驅動程式，也不保證自我保護軟體已完整移除。

解除安裝頁也支援手動檢查並套用帶簽章的規則更新，請見[更新格式與維護流程](CATALOG-UPDATES.zh-TW.md)。下表記錄隨應用程式附帶的內建版本。

## 公開報告分組

軟體名稱保留原文，用於辨識。日期是報告的原始發佈日期，不是本輪核對日期。橫線表示未記錄發佈日期，仍以頂部來源核對日期為準。同一報告可以支持多條規則。

| 報告日期 | 軟體 | 來源 |
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

## 關注清單

以下 12 條是維護中的關注偏好，包括使用者要求的 360 和金山產品；本專案沒有據此認定其為病毒。

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

## 排除項與更正

- 被推廣對象、被冒用品牌和實際分析的惡意模組屬於不同證據角色。360 報告中的旋風PDF屬於額外推廣安裝對象，不證明其目前檔案包含報告中其他模組的木馬。
- 不因為攻擊者借用名稱或攻擊其程序，就給官方 QQ、WPS、7-Zip 添加惡意標籤。`commander`、`云计算`、`多标签文件管理器`、`setup.exe`、`winhost.exe` 等泛稱不獨立匹配。
- 火絨 `/1985` 在搜尋摘要中可見，但本輪無法在原頁面複核，因此沒有採用為有報告依據的規則。
- 規則隨應用程式發佈，不會持續連線取得威脅情報，也不會上傳已安裝軟體清單。

全部別名和原因維護在 [UninstallRecommendations.cs](../src/ProcessKeeper.Core/UninstallRecommendations.cs)。歡迎透過 [Issue](https://github.com/KangQiovo/ProcessKeeper/issues) 提交登錄名、發行者、版本、可信來源與重現步驟協助更正；請隱去個人路徑和標識，不上傳惡意樣本或私人檔案。
