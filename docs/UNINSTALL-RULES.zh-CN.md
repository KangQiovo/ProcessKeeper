# 卸载建议规则库

[English](UNINSTALL-RULES.md) | [简体中文](UNINSTALL-RULES.zh-CN.md) | [繁體中文](UNINSTALL-RULES.zh-TW.md)

本文记录**内置注册名称提示库**，不是杀毒软件的病毒特征库。版本 **2026.09.30.2**，来源核对日期 **2026-09-30**：共 **68 条软件规则、117 个明确别名**，分为 **12 条关注名单**及 **56 条关联 26 份第一方软件报告的规则**。报告规则中，42 条来自火绒、2 条来自 360 安全团队、12 条来自 Malwarebytes。本轮仅阅读公开报告，没有下载或运行样本。

两类规则统一显示建议标签，详情明确列出依据类别、匹配注册名、原因、可取得的报告发布日期、原文链接及规则版本。关注名单表示建议用户核对，不代表病毒鉴定。历史报告针对当时分析的样本、渠道和版本；例如[火绒渠道分析](https://www.huorong.cn/document/tech/vir_report/1858)明确记录了不同渠道安装包的区别。本规则库没有检测你电脑上同名软件的当前文件。

匹配使用完整产品名及限定的数字版本、架构后缀，不按整个厂商、泛称或通用文件名判断。注册名可能被冒用。本功能不扫描文件内容和所有磁盘，也不识别全部未注册植入物或清理残留服务、驱动。未标记不代表安全。[微软判定标准](https://learn.microsoft.com/en-us/defender-xdr/criteria)也区分潜在不受欢迎程序与恶意软件。

**是否卸载由你决定。** 标签不会自动勾选、停止或卸载程序。正常卸载使用软件注册的卸载程序；支持的静默卸载命令需要再次确认。启动前重新核对注册项和可执行文件，随后如实报告登记状态。不自动删除目录、驱动，也不保证自我保护软件已完整移除。

卸载页也支持手动检查并应用带签名的规则更新，详见[更新格式与维护流程](CATALOG-UPDATES.zh-CN.md)。下表记录随应用附带的内置版本。

## 公开报告分组

软件名称保留原文，用于辨识。日期是报告的原始发布日期，不是本轮核对日期。横线表示未记录发布日期，仍以顶部来源核对日期为准。同一报告可以支持多条规则。

| 报告日期 | 软件 | 来源 |
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

## 关注名单

以下 12 条是维护中的关注偏好，包括用户要求的 360 和金山产品；本项目没有据此认定其为病毒。

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

## 排除项与纠错

- 被推广对象、被冒用品牌和实际分析的恶意模块属于不同证据角色。360 报告中的旋风PDF属于额外推广安装对象，不证明其当前文件包含报告中其他模块的木马。
- 不因为攻击者借用名称或攻击其进程，就给官方 QQ、WPS、7-Zip 添加恶意标签。`commander`、`云计算`、`多标签文件管理器`、`setup.exe`、`winhost.exe` 等泛称不独立匹配。
- 火绒 `/1985` 在搜索摘要中可见，但本轮无法在原页面复核，因此没有采用为有报告依据的规则。
- 规则随应用发布，不会持续联网获取威胁情报，也不会上传已安装软件清单。

全部别名和原因维护在 [UninstallRecommendations.cs](../src/ProcessKeeper.Core/UninstallRecommendations.cs)。欢迎通过 [Issue](https://github.com/KangQiovo/ProcessKeeper/issues) 提交注册名、发布者、版本、可信来源与复现步骤帮助纠错；请隐去个人路径和标识，不上传恶意样本或私人文件。
