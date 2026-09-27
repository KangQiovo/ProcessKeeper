# AVD 重启回归夹具

[English](README.md) | **简体中文** | [繁體中文](README.zh-TW.md)

此程序将当前 Core 源码链接到自己的构建输出中，不启动产品、不放宽管理员保护、不读取 Android 认证文件，也不操作已有模拟器。

在 Windows 上使用 .NET 8 SDK，从仓库根目录以 PowerShell 7 执行：

```powershell
$resultJson = Join-Path ([IO.Path]::GetTempPath()) ('processkeeper-avd-' + [guid]::NewGuid().ToString('N') + '.json')
dotnet run --project src/ProcessKeeper.Avd.Tests/ProcessKeeper.Avd.Tests.csproj -c Release -- $resultJson
```

有用例失败时返回非零退出码，并将用例数、断言数及错误详情写入 JSON。以实际输出为准，不规定固定历史通过数量。

## 实际验证范围

- `FakeAvdHost` 提供合成身份、哈希、参数、环境、监听、退出状态及窗口，只记录退出和启动调用，不执行它们。
- 测试实际的 `AvdRestartService`、`AvdArgumentPolicy` 与 `AvdGuiMatcher`，采用较短超时，但仍要求至少三次分开的成功观察。
- 夹具自有 Win32 窗口验证真实类名、样式和几何元数据。类名匹配 `Qt*QWindow*` **不代表运行了 Qt 或模拟器**。
- 原生协议测试只使用测试进程自身身份及其创建的随机回环监听；控制台服务器记录退出命令并响应，不结束进程，也不读取用户认证文件。

## 覆盖边界

发现阶段不能退出或启动程序。计划绑定旧进程身份、SDK 路径及哈希、参数、环境、准确设备名和端口；含糊或已变化的关联阻止操作。

取消时按实际完成阶段报告。未完整退出、控制台/ADB 端口占用或 SDK 变化会阻止再次启动。并发执行或复用过期计划不能重复发出退出、启动请求。

页面成功证据必须对应新启动的根进程或已核实 SDK 子进程，并持续稳定。终端、崩溃或辅助窗口、其他设备、无界面核心、隐藏/最小化/被系统遮蔽的窗口、错误父子关系及短暂句柄均不计为成功。成功、失败或取消后都会释放持有的启动器资源。

这些测试结合合成流程和仅归夹具所有的原生边界，**不能证明**用户 AVD 已完成端到端重启、Android 已完成启动或显卡驱动兼容。详见[测试指南](../../docs/TESTING.zh-CN.md)，不要提交结果文件或测试包。
