# 测试指南

[English](TESTING.md) | **简体中文** | [繁體中文](TESTING.zh-TW.md)

工具链要求见[构建指南](BUILDING.zh-CN.md)，运行路线和限制见[兼容指南](COMPATIBILITY.zh-CN.md)。以下命令均从仓库根目录，在 Windows 上使用 PowerShell 7 和所需 .NET SDK 执行。不要同时对同一项目的 `obj` 目录运行多个构建。

## 先运行受改动影响的测试

```powershell
# 在仓库根目录使用 PowerShell 7 执行。
dotnet run --project src/ProcessKeeper.Tests/ProcessKeeper.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Settings.Tests/ProcessKeeper.Settings.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.CloudProfiles.Tests/ProcessKeeper.CloudProfiles.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Tools.Tests/ProcessKeeper.Tools.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Display.Tests/ProcessKeeper.Display.Tests.csproj -c Release --framework net8.0-windows10.0.19041.0
dotnet run --project src/ProcessKeeper.Search.Tests/ProcessKeeper.Search.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Autorun.Tests/ProcessKeeper.Autorun.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Update.Tests/ProcessKeeper.Update.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Architecture.Tests/ProcessKeeper.Architecture.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Instance.Tests/ProcessKeeper.Instance.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Uninstall.Tests/ProcessKeeper.Uninstall.Tests.csproj -c Release --framework net8.0-windows10.0.19041.0
dotnet run --project src/ProcessKeeper.Performance.Tests/ProcessKeeper.Performance.Tests.csproj -c Release --framework net8.0-windows10.0.19041.0
dotnet run --project src/ProcessKeeper.CatalogUpdate.Tests/ProcessKeeper.CatalogUpdate.Tests.csproj -c Release --framework net8.0-windows10.0.19041.0
```

这些命令只是示例，并非完整发布检查。其他项目覆盖已安装应用目录、图标、操作记录、AVD 参数与恢复、特殊窗口、浏览器预览、窗口路由、启动、单实例、本地化和正常退出。

云端配置测试使用假 GitHub Contents 响应及隔离文件，覆盖固定目录、元数据与内容 SHA、UTF-8 和便携格式限制、错误与取消、空白默认规则、可选七应用模板，以及修订版本检查和最多五套的原子导入。无需 GitHub 凭据，不修改用户白名单。下载器使用受控 HTTP 响应；本地回环与原生界面验证不代表外网下载速度或旧设备兼容性。

验证实际 x86 Framework 后端：

```powershell
dotnet build src/ProcessKeeper.Legacy.Core.Tests/ProcessKeeper.Legacy.Core.Tests.csproj -c Release
& ./src/ProcessKeeper.Legacy.Core.Tests/bin/Release/net462/ProcessKeeper.Legacy.Core.Tests.exe
dotnet run --project src/ProcessKeeper.Display.Tests/ProcessKeeper.Display.Tests.csproj -c Release --framework net462
dotnet run --project src/ProcessKeeper.Uninstall.Tests/ProcessKeeper.Uninstall.Tests.csproj -c Release --framework net462
dotnet run --project src/ProcessKeeper.Performance.Tests/ProcessKeeper.Performance.Tests.csproj -c Release --framework net462
dotnet run --project src/ProcessKeeper.CatalogUpdate.Tests/ProcessKeeper.CatalogUpdate.Tests.csproj -c Release --framework net462
dotnet run --project src/ProcessKeeper.Legacy.Instance.Tests/ProcessKeeper.Legacy.Instance.Tests.csproj -c Release
```

Legacy 测试程序必须在装有兼容 .NET Framework 的 Windows 中实际运行；仅构建成功不代表运行通过。它使用共享夹具、隔离文件和只读进程身份检查。可选参数 `--read-only-live` 会枚举真实自启动来源但不修改它们，普通回归请省略此参数。

## 测试通过说明什么

| 测试类型 | 能证明的范围 | 不能据此证明 |
| --- | --- | --- |
| 纯逻辑或注入后端 | 在受控输入下的规则匹配、状态转换、取消、身份拒绝与错误处理 | 对所有已安装软件都有效 |
| 隔离文件或原生夹具 | 真实文件、ACL、管道、窗口 API，以及仅由夹具拥有的辅助进程或回环监听 | 已成功操作用户真实模拟器、服务或应用 |
| 现代系统上的 Framework x86 运行 | 生成的共享代码可在该系统的 32 位 CLR 中构建运行 | Windows 7/8.1 运行情况或旧硬件性能 |
| 注入式 UI 夹具 | 已测尺寸、主题、语言及 DPI 下的渲染和交互 | 所有显卡驱动、屏幕阅读器或真实显示器布局 |
| 主动进行的真实系统测试 | 测试者记录的具体场景、系统与结果 | 未测系统、无损退出或云同步完成 |

更新测试使用假 HTTP 和隔离下载文件，不安装真实发布。自启动修改测试使用内存后端或夹具文件；可选的实际枚举属于另一项只读范围。近期边界回归覆盖损坏备份隔离、超时虚拟机查询数量限制及严格的更新资产名称。共享断言会在多个项目中运行，不能累加成不同的真实场景。

详细说明见 [AVD](../src/ProcessKeeper.Avd.Tests/README.zh-CN.md)、[启动器](../src/ProcessKeeper.Launcher.Tests/README.zh-CN.md)、[搜索](../src/ProcessKeeper.Search.Tests/README.zh-CN.md)及 [Legacy Core](../src/ProcessKeeper.Legacy.Core/README.zh-CN.md)。

## 1.7.0 回归边界

单实例检查应覆盖同一用户及会话内的不同包路径、版本和界面路线。确认获选者身份来自实际进程与受保护的启动凭据；未获选的启动不能因延迟回调重新打开引导或主窗口；关闭窗口或释放锁后，仍须等待旧进程实际退出。保留已有实例的退出状态不能算更新就绪。只使用隔离且由测试拥有的进程，不为此关闭用户真实应用。

已安装应用测试覆盖重复登记、版本/架构后缀、不同盘符、缺失及冲突的发布者、共享命令宿主、保留组件角色信息、准确启动入口和独立白名单状态。卸载测试合并相关版本时保留每条登记。分组必须保留来源身份，仅列表变短不能算通过。

更新测试使用七种发行附件候选，验证只有匹配包类型的文件可自动安装。应检查 Markdown 日志和链接、进度窗暂停/继续及切换源、下载中切换页面、下载后保持运行、仅明确确认才更新及取消确认后保留下载，以及新包就绪后才清理旧包备份。下载状态只在应用生命周期内保留，不宣称完整退出后继续恢复。

更新夹具覆盖七种附件，仅精确匹配的免安装类型可自动安装，安装器保持手动下载。检查 PK17 分包清单（Win10 含现代与兼容两条路线）、PK14 合包、实际架构及哈希。真正的 1.6 验证器应接受合包并拒绝分包/安装器，且不替换用户应用。安装器的源码、资源、编译器检查不能代替各目标系统的安装及卸载实测。

当前现代主机上的原生分组夹具覆盖三语言、深浅模式和两种窗口宽度，包括折叠选择、部分取消、搜索、稳定刷新及独立启用/保护状态。另有只读真实清单检查，对比分组前后的全部可执行路径和来源入口身份，不修改真实启动配置或关闭用户应用。安全软件拦截或夹具输出缺失属于待核查的测试干扰，不是通过结果或已确认的产品故障；不得关闭防护或自动批准提示。

## 构建检查与手动验证

`build.ps1` 会运行脚本列出的托管测试并发布现代应用，不是仅测试命令。原生启动器、更新助手和兼容界面另有构建要求，参见构建指南。普通构建即使使用 Release 配置也仍为 **Preview**；稳定渠道参数只用于明确准备的官方 Stable 发布。

验证真实关闭、自启动或更新操作前，使用可丢弃的测试环境及你拥有的进程和数据。记录 UAC 取消、身份复核失败、中断后的恢复，以及确切受影响配置。不能把合成成功描述成真实安装或应用退出成功。

真实旧系统和旧硬件验证仍未完成。不能因为 Framework 程序在新系统运行，就标注 Windows 7 已实测。

## 报告可复现结果

### 原生加载页主题

[Test-StartupTheme.ps1](../src/ProcessKeeper.UniversalLauncher/Test-StartupTheme.ps1) 要求显式指定 `-FixturePath` 和 `-OutputDirectory`。先使用 `build-native.ps1 -UiFixture` 及其要求的资源参数构建隔离启动器。脚本执行前检查仅测试夹具导出的 PE 标识，拒绝生产包及无关程序。临时文件和截图应重定向至选定的测试目录。

矩阵检查三语言、深浅色和注入的高对比度配色、权限与恢复页面、实际文字对比度、主题变更消息及 GDI 资源增长。脚本只读取当前 Windows 应用主题，不修改主机主题；这些检查仅覆盖本机自有测试窗口，不覆盖生产解包、UAC 或旧系统硬件。

记录源码提交、命令、系统构建号、架构、运行时、退出码、通过/失败/跳过数量，以及相关主题、语言、尺寸和 DPI。非零退出、失败用例或未解释的跳过不算完整通过。测试数量会变化，应以实际控制台或 JSON 结果为准，不依赖历史固定总数。

提交中不要包含程序包、缓存、完整日志或个人配置。只附脱敏摘要或最少复现数据。公开 CI 结果链接可在存在后补充，不得用虚构的通过记录替代缺失链接。
