# 测试指南

[English](TESTING.md) | **简体中文** | [繁體中文](TESTING.zh-TW.md)

工具链要求见[构建指南](BUILDING.zh-CN.md)，运行路线和限制见[兼容指南](COMPATIBILITY.zh-CN.md)。以下命令均从仓库根目录，在 Windows 上使用 PowerShell 7 和所需 .NET SDK 执行。不要同时对同一项目的 `obj` 目录运行多个构建。

## 先运行受改动影响的测试

```powershell
# 在仓库根目录使用 PowerShell 7 执行。
dotnet run --project src/ProcessKeeper.Tests/ProcessKeeper.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Settings.Tests/ProcessKeeper.Settings.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Display.Tests/ProcessKeeper.Display.Tests.csproj -c Release --framework net8.0-windows10.0.19041.0
dotnet run --project src/ProcessKeeper.Search.Tests/ProcessKeeper.Search.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Autorun.Tests/ProcessKeeper.Autorun.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Update.Tests/ProcessKeeper.Update.Tests.csproj -c Release
```

这些命令只是示例，并非完整发布检查。其他项目覆盖已安装应用目录、图标、操作记录、AVD 参数与恢复、特殊窗口、浏览器预览、窗口路由、启动、单实例、本地化和正常退出。

验证实际 x86 Framework 后端：

```powershell
dotnet build src/ProcessKeeper.Legacy.Core.Tests/ProcessKeeper.Legacy.Core.Tests.csproj -c Release
& ./src/ProcessKeeper.Legacy.Core.Tests/bin/Release/net462/ProcessKeeper.Legacy.Core.Tests.exe
dotnet run --project src/ProcessKeeper.Display.Tests/ProcessKeeper.Display.Tests.csproj -c Release --framework net462
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

## 构建检查与手动验证

`build.ps1` 会运行脚本列出的托管测试并发布现代应用，不是仅测试命令。原生启动器、更新助手和兼容界面另有构建要求，参见构建指南。普通构建即使使用 Release 配置也仍为 **Preview**；稳定渠道参数只用于明确准备的官方 Stable 发布。

验证真实关闭、自启动或更新操作前，使用可丢弃的测试环境及你拥有的进程和数据。记录 UAC 取消、身份复核失败、中断后的恢复，以及确切受影响配置。不能把合成成功描述成真实安装或应用退出成功。

真实旧系统和旧硬件验证仍未完成。不能因为 Framework 程序在新系统运行，就标注 Windows 7 已实测。

## 报告可复现结果

记录源码提交、命令、系统构建号、架构、运行时、退出码、通过/失败/跳过数量，以及相关主题、语言、尺寸和 DPI。非零退出、失败用例或未解释的跳过不算完整通过。测试数量会变化，应以实际控制台或 JSON 结果为准，不依赖历史固定总数。

提交中不要包含程序包、缓存、完整日志或个人配置。只附脱敏摘要或最少复现数据。公开 CI 结果链接可在存在后补充，不得用虚构的通过记录替代缺失链接。
