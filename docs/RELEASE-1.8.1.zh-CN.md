# Process Keeper 1.8v2

[English](RELEASE-1.8.1.md) | **简体中文** | [繁體中文](RELEASE-1.8.1.zh-TW.md) | [主页](../README.zh-CN.md)

应用与发布标题显示 **1.8v2**。更新身份、EXE 版本、标签与文件名使用 **1.8.1** / `v1.8.1`，确保现有 1.8.0 客户端能够识别新版。

## 改动

- 更新文件选项只列出已识别的正式应用包；`SHA256SUMS`、文档、压缩包与助手程序保留在发布资料中，不再作为应用更新选项。默认仍匹配经过核实的当前版本类型及安装/免安装方式。
- 勾选自动检测立即保存，并在初始化就绪后开始检查；后续启动会后台检查。新版本弹窗可在任意页面出现，无需先打开关于页。其他弹窗忙碌时等待，退出时取消，弹窗未成功显示不会被误记为已提醒。
- 选择不同包类型时显示“继续”，先关闭文件选择弹窗，再单独确认当前与所选类型。取消不会开始下载。
- 新版启动后后台执行有时限的旧载荷清理，包含新版启动之后才登记的旧版本清理任务，修复原先需等下次启动的问题。当前载荷、仍在运行或被占用的助手、未知文件及不安全路径会保留。
- 指向同一受管理 EXE 的已有桌面快捷方式仍在替换后刷新；不会扫描删除其他目录中的下载副本，也不会改写无关快捷方式。手动放在其他目录的旧副本需要单独处理。用户设置与白名单保留。
- 更新进度窗的原生标题栏跟随应用主题，保留 Windows 高对比度配色；核对后台更新器资源与实际窗口使用原有带下载箭头图标。

延续 1.8 的首次使用默认值：**不勾选隐藏微软应用，本地白名单为空**；已保存的用户设置不变。微软身份验证、应用合并与主程序识别详见 [1.8 发布说明](RELEASE-1.8.0.zh-CN.md)。

## 下载文件

| 文件 | 用途 |
| --- | --- |
| [ProcessKeeper-v1.8.1.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/ProcessKeeper-v1.8.1.exe) | 免安装合包；全部三种载荷，体积最大；PK14 兼容旧更新器。 |
| [ProcessKeeper-v1.8.1-win7-x86-compat.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/ProcessKeeper-v1.8.1-win7-x86-compat.exe) | 兼容免安装；Win7 SP1 / 8.1 及受支持的新 x86/x64 系统；WPF；需 .NET Framework 4.6.2 或兼容的新 4.x。 |
| [ProcessKeeper-v1.8.1-win7-x86-compat-setup.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/ProcessKeeper-v1.8.1-win7-x86-compat-setup.exe) | 带卸载器的兼容安装版；运行要求相同。 |
| [ProcessKeeper-v1.8.1-win10-x86-x64.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/ProcessKeeper-v1.8.1-win10-x86-x64.exe) | Win10/11 x86、x64 免安装；x64 19041+ 使用 WinUI，内置受支持系统的 WPF 降级载荷。 |
| [ProcessKeeper-v1.8.1-win10-x86-x64-setup.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/ProcessKeeper-v1.8.1-win10-x86-x64-setup.exe) | 带卸载器的 x86/x64 安装版；包含两种载荷。 |
| [ProcessKeeper-v1.8.1-win10-arm64.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/ProcessKeeper-v1.8.1-win10-arm64.exe) | 原生 ARM64 免安装；Win10 19041+ / Win11 ARM64；x86 引导器由系统模拟运行。 |
| [ProcessKeeper-v1.8.1-win10-arm64-setup.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/ProcessKeeper-v1.8.1-win10-arm64-setup.exe) | 带卸载器的 ARM64 安装版；运行要求相同。 |

用 [SHA256SUMS](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/SHA256SUMS) 核对文件。程序尚未签名，Windows 可能显示**未知发布者**。

**1.6.x 应用内更新请选择免安装合包。** PK17 分包和安装器需较新更新器或手动下载。更新必须由用户点击并确认；没有自动倒计时或静默安装。安装版更新打开标准安装向导。

## 验证与已知限制

回归使用真实的自有 WinUI/WPF 测试窗口、隔离设置/下载文件、注入的网络/安装边界和不执行产品安装的原生载荷样本，覆盖未进入关于页的启动、勾选保存、全局弹窗、文件筛选、切包取消、进度/继续下载与受保护的旧载荷清理。完整构建与实际包检查核对架构、清单、文件哈希、正式渠道及空白默认规则。见[测试说明](TESTING.zh-CN.md)。

这些检查不等于已完成 Win7 / 8.1 / 10 或 ARM64 真机安装、更新与运行认证。兼容界面缺少 WinRT 包库存/启动声明、浏览器实时预览及 x64 AVD 重建；ARM64 也禁用 x64 专用 AVD 读取器。见[兼容说明](COMPATIBILITY.zh-CN.md)。清理仅处理身份核实的所属文件，忙碌或无法验证的目录可能保留。欢迎通过 [Issues](https://github.com/KangQiovo/ProcessKeeper/issues) 提交复现信息。
