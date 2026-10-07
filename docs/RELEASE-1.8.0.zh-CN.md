# Process Keeper 1.8.0

[English](RELEASE-1.8.0.md) | **简体中文** | [繁體中文](RELEASE-1.8.0.zh-TW.md) | [首页](../README.zh-CN.md)

正式版、界面版本、更新身份及标签统一使用 **1.8.0** / `v1.8.0`。

## 本次更新

- **新设置默认关闭“隐藏微软应用”。** 保留已有用户选择；首次白名单仍为空。开启筛选只改变显示，不赋予关闭、修改自启动或卸载权限。
- 扩展微软归属验证，覆盖健康且非 development mode 的 OS 注册 Store/System 包，包括没有独立 EXE 的包；其他符合条件的注册包需要通过独立原生包签名验证。开发人员主页、HEVC 扩展等项目不再仅依赖 EXE，仍不能凭熟悉名称、复制文件或发布者文字取得归属。
- 第三方软件的原始发布者与微软归属冲突时，即使包含微软组件，也保留整项可见。
- 通过精确 MSI 快捷方式/组件身份、注册图标目录及产品/公司证据、受支持的 Burn/PEP514 运行时注册关联重复安装记录。Apple Software Update、Autodesk Access、Python 现在各显示一个软件父项，保留原始记录和组件。
- 默认定位实际注册的主程序。MSI 广告快捷方式的图标文件保留为组件；Python 缓存安装器标为 Helper，不再抢占主程序。缺少精确注册证据时，通用 Python 宿主保护保持有效。
- 暂停实时进程采集后，已请求的发布者验证仍可完成并更新筛选；有界后台重试不会重新打开实时采集。
- 原生标题栏控件跟随应用主题，自定义原生背景保留文字可读性。继续保留官方源优先下载、手动更新并重启、设置备份及材质调节。

## 下载选择

| 文件 | 适用环境 |
| --- | --- |
| [ProcessKeeper-v1.8.0.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/ProcessKeeper-v1.8.0.exe) | 免安装合包；包含全部三种运行路线，下载最大；PK14 供旧更新器使用。 |
| [ProcessKeeper-v1.8.0-win7-x86-compat.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/ProcessKeeper-v1.8.0-win7-x86-compat.exe) | 兼容免安装；Windows 7 SP1 / 8.1 及受支持的新系统 x86/x64；WPF，需要 .NET Framework 4.6.2 或兼容的较新 4.x。 |
| [ProcessKeeper-v1.8.0-win7-x86-compat-setup.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/ProcessKeeper-v1.8.0-win7-x86-compat-setup.exe) | 兼容安装版，含卸载器；运行要求与对应免安装版相同。 |
| [ProcessKeeper-v1.8.0-win10-x86-x64.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/ProcessKeeper-v1.8.0-win10-x86-x64.exe) | Windows 10/11 x86/x64 免安装；x64 19041+ 使用现代 WinUI，内置 x86 WPF 供受支持系统降级使用。 |
| [ProcessKeeper-v1.8.0-win10-x86-x64-setup.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/ProcessKeeper-v1.8.0-win10-x86-x64-setup.exe) | x86/x64 安装版，含卸载器及两种运行路线。 |
| [ProcessKeeper-v1.8.0-win10-arm64.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/ProcessKeeper-v1.8.0-win10-arm64.exe) | 原生 ARM64 免安装；Windows 10 19041+ / Windows 11 ARM64；x86 引导器通过 Windows 模拟运行。 |
| [ProcessKeeper-v1.8.0-win10-arm64-setup.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/ProcessKeeper-v1.8.0-win10-arm64-setup.exe) | ARM64 安装版，含卸载器；运行要求与对应免安装版相同。 |

请用随附的 [SHA256SUMS](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/SHA256SUMS) 校验下载。发行 EXE 尚未签名，Windows 可能显示**未知发布者**。

**1.6.x 应用内升级请选择合包 EXE。** 旧更新器支持 PK14；PK17 分包及安装器需新更新器或手动下载。当前更新器优先匹配已验证的版本类型和免安装/安装方式。更新需主动操作并确认，没有自动倒计时或静默安装；安装版更新打开标准安装向导。

## 验证范围与实际边界

现代 Windows 主机的只读采集覆盖全部 **103 个微软包 UI 候选**、**27 条微软发布者桌面记录形成的 18 个父项**，以及**含微软组件的 14 条第三方记录形成的 13 个父项**；微软候选被筛除，第三方父项保持可见。已安装展示由 **323 条记录整理为 280 个父项**，保留每条原始身份及 **1469 条不同 EXE 路径**。这是实测样本，不代表能发现或隐藏所有系统上的全部微软项目。回归覆盖精确注册、负例、组件角色及两套界面，见[测试指南](TESTING.zh-CN.md)。

同一样本中的 **165 条微软相关传统卸载登记**，仅 **5 条**具备足够应用证据用于隐藏；**134 条**没有可读应用 EXE/证据，**26 条**只有图标或安装器证据，继续显示。未知、不健康、development mode、不可读或仍等待验证的归属，不凭产品名推断。

同名不等于同一产品。不同 Windows 包 family 保留不同身份；没有精确外部目录/组件关联的稀疏包与 Win32 记录保持独立。共享宿主、不同运行时环境及无关任务也不按名称合并。分组保留关闭、修改自启动和卸载时的精确原始目标。

库存受可读登记、受支持提供程序及文件扫描上限约束；权限不足、未加载用户注册表、提供程序延迟和特殊绿色软件仍有限制。**Windows 7 / 8.1 / 10、ARM64 目标设备上的安装、升级及运行尚未通过真实设备认证。** 现代主机的 x86 测试及交叉编译不能代替。兼容界面缺少 WinRT 包库存/启动声明、浏览器实时预览及 x64 AVD 环境还原；ARM64 也禁用 x64 专用 AVD 读取器。见[兼容指南](COMPATIBILITY.zh-CN.md)。
