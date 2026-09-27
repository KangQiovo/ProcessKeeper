# 托管启动器夹具

[English](README.md) | **简体中文** | [繁體中文](README.zh-TW.md)

此项目验证托管 ZIP/缓存启动器契约及提权协调，**不是完整的原生通用启动器或更新助手测试套件**。原生构建与夹具步骤见[构建指南](../../docs/BUILDING.zh-CN.md)。

在 Windows 上使用 .NET 8 SDK，从仓库根目录以 PowerShell 7 执行，并选择可丢弃的夹具位置：

```powershell
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('processkeeper-launcher-tests-' + [guid]::NewGuid().ToString('N'))
dotnet run --project src/ProcessKeeper.Launcher.Tests/ProcessKeeper.Launcher.Tests.csproj -c Release -- $fixtureRoot
```

测试创建隔离文件、真实命名管道和仅归夹具所有的无操作生命周期辅助进程。它不执行载荷、不启动 Process Keeper、不请求真实 UAC、不创建生产 ProgramData 缓存，也不关闭已有用户程序。对生产 ProgramData 祖先所有者和 ACL 的验证仅为只读。

## 覆盖范围

- 压缩包路径穿越、大小写冲突、文件/目录冲突，以及清单或文件哈希不符。
- 缓存锁、持有文件句柄、链接替换、旧缓存隔离、所有者及 ACL 限制。
- 管道客户端身份、进程生命周期及不可预测的协调令牌。
- 注入式提权结果：管理员直接启动、请求授权、取消、失败和重试；启动错误不能被计为成功。

以实际控制台结果和退出码为准，不规定固定断言总数。无权创建符号链接的环境会对相关检查报告 `SKIP`，结果中须保留这些跳过信息。夹具保留供检查，不得提交到仓库。

可选构建允许通过 `TestPayloadZip` 与 `TestPayloadManifest` 嵌入合成 ZIP 和清单；此类检查只解包比对，不执行其中的主程序。NativeAOT 发布另需相应 C++ 工具和 Windows SDK。

## 未覆盖范围

这些不提权的夹具不能验证真实 UAC 同意、首次创建受保护生产缓存或完整用户更新，应在受控手动环境中另行记录。参见[测试指南](../../docs/TESTING.zh-CN.md)，不要上传测试 EXE 或夹具程序包。
