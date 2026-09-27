# Legacy Core

[English](README.md) | **简体中文** | [繁體中文](README.zh-TW.md)

兼容后端面向 .NET Framework 4.6.2，提供共享的 `ProcessKeeper.Core` API。兼容应用使用 x86 进程。Windows 7 SP1 是兼容目标，**不代表已经完成 Windows 7 实测**，详见[兼容指南](../../docs/COMPATIBILITY.zh-CN.md)。

## 共享源码与原生边界

`GenerateCompatibleCore.ps1` 从现代 Core 在 `obj` 下生成仅供构建使用的文件。业务规则维护在共享源码中，适配限于必要的框架或系统差异；不要编辑或提交生成副本。涉及安全的原生替换会核对预期出现次数，源码结构变化时构建应失败。

`LegacyWindowsCapabilities` 检测系统版本，并使用同一个已核实的进程句柄检查关键进程标志。缺失身份信息或不支持的 API 不能静默授权关闭进程。PID、创建时间、路径、账户/会话及白名单检查仍保留在共享流程中。

## 能力限制

- x86 模式不能安全重建 x64 AVD 的原始环境，因此不提供重启计划；已有图形窗口在身份可核实时仍可恢复。
- 依赖同一 x64 参数读取器的虚拟机或托盘适配也可能拒绝发现。
- 此构建不提供浏览器实时预览、WinRT 包清单及打包应用自启动声明。
- 传统桌面和自启动后端仍是真实实现，但受来源可用性及权限限制。

## 构建与测试

.NET SDK、PowerShell 7 和 NuGet 依赖见[构建指南](../../docs/BUILDING.zh-CN.md)。PowerShell 仅是构建依赖；部署后的兼容应用需要 .NET Framework 4.6.2 或兼容的更新 4.x 运行时。

从仓库根目录执行：

```powershell
dotnet build src/ProcessKeeper.Legacy.Core.Tests/ProcessKeeper.Legacy.Core.Tests.csproj -c Release
& ./src/ProcessKeeper.Legacy.Core.Tests/bin/Release/net462/ProcessKeeper.Legacy.Core.Tests.exe
```

测试使用共享夹具、隔离文件和只读进程身份采集。可选 `--read-only-live` 会枚举实际自启动来源但不修改它们，普通回归请省略。通过只能证明**报告中所写主机**上的 x86 Framework 行为，不能当作旧系统或旧硬件认证。以实际测试摘要为准，不依赖固定预期数量。

参见[测试指南](../../docs/TESTING.zh-CN.md)和[贡献指南](../../CONTRIBUTING.zh-CN.md)。普通 Release 构建仍为 Preview。
