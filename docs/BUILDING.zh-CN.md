# 从源码构建

[English](BUILDING.md) | **简体中文** | [繁體中文](BUILDING.zh-TW.md) | [首页](../README.zh-CN.md)

请在现代 **x64 Windows** 开发机上构建。Win7 是兼容路线的运行目标，不是这套工具链的开发主机。编译及隔离测试不需要管理员权限。

## 依赖

- [PowerShell 7](https://github.com/PowerShell/PowerShell)，命令为 `pwsh`。不依赖 Windows PowerShell 5.1；非标准位置可设置 `PROCESSKEEPER_PWSH` 或支持的 `-PowerShellPath` 参数。
- x64 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)，仅运行时不够。`global.json` 在已安装的 .NET 8 中选择最新 feature band，不跨大版本。
- Visual Studio 2022 / Build Tools 的 **C++ 桌面开发**、MSVC x86/x64 及 Windows SDK。脚本通过 `vswhere` 发现 VS 并选择完整 SDK，也可指定 `-VisualStudioPath`、`-WindowsSdkRoot`、`-WindowsSdkVersion`。
- 网络可恢复工程声明的 NuGet 包，包括 Framework 4.6.2 引用程序集及 WinUI 构建依赖。现代工程使用 10.0.26100.0 SDK 投影、最低运行版本 19041；这不把原生兼容路线提高到 Win11。

引用程序集用于开发机编译，接收者仍需安装适用的 Framework 运行时，见[兼容性](COMPATIBILITY.zh-CN.md)。

## 默认预览构建

在仓库根目录使用 PowerShell 7：

```powershell
./build.ps1
if ($LASTEXITCODE -ne 0) { throw 'Modern build failed.' }
./build.ps1 -Architecture arm64 -SkipBuildStamp -SkipTests
if ($LASTEXITCODE -ne 0) { throw 'ARM64 build failed.' }
dotnet run --project src/ProcessKeeper.Legacy.Core.Tests/ProcessKeeper.Legacy.Core.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Compatibility tests failed.' }
dotnet build src/ProcessKeeper.Legacy.App/ProcessKeeper.Legacy.App.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Compatibility build failed.' }
./src/ProcessKeeper.UniversalLauncher/build-native.ps1 -TestsOnly
./scripts/Test-BuildPipeline.ps1
```

`build.ps1` 生成一次统一时间戳、运行托管回归并将现代载荷输出到 `App`。随后基于**同一时间戳**构建兼容载荷，期间不要重新生成构建时间。自备 SDK/缓存可传 `-DotnetPath`、`-NugetPackages`。原生测试只使用自有夹具进程。

打包三套载荷，不启动应用：

第二次构建调用将现代 ARM64 载荷交叉编译至 `App-arm64`，复用同一时间戳。外层启动器和更新助手仍为 x86，不需要 ARM64 MSVC 工具来编译它们；在 x64 上编译成功不能替代 ARM 设备运行验证。

```powershell
New-Item -ItemType Directory -Path artifacts -Force | Out-Null
./package-universal.ps1 `
  -ModernDirectory ./App `
  -Arm64Directory ./App-arm64 `
  -LegacyDirectory ./src/ProcessKeeper.Legacy.App/bin/Release/net462 `
  -OutputPath ./artifacts/ProcessKeeper-preview.exe
```

每次选择**新输出文件名**，脚本拒绝覆盖已有包。它校验日期与渠道，构建更新助手，生成带清单/哈希的 CAB 并嵌入原生 x86 EXE。`package-single-file.ps1` 转交通用打包器，也需要三套载荷，不是另一条发行路线。

直接运行 `App/ProcessKeeper.exe` 属于开发路线，缺少通用入口的可信上下文，应用内更新和快捷方式会显示不可用，不能当成更新测试成功。

## 官方正式构建

`Release` 只是优化配置，**不会自动移除预览提示**。未指定渠道始终为 `Preview`，即使上次构建为正式版。

明确准备官方正式发行时，使用显式渠道参数：

```powershell
./build.ps1 -StableRelease
if ($LASTEXITCODE -ne 0) { throw 'Stable modern build failed.' }
./build.ps1 -Architecture arm64 -SkipBuildStamp -SkipTests -StableRelease
if ($LASTEXITCODE -ne 0) { throw 'Stable ARM64 build failed.' }
dotnet build src/ProcessKeeper.Legacy.App/ProcessKeeper.Legacy.App.csproj -c Release -p:ProcessKeeperReleaseChannel=Stable
if ($LASTEXITCODE -ne 0) { throw 'Stable compatibility build failed.' }
./package-universal.ps1 -StableRelease `
  -ModernDirectory ./App `
  -Arm64Directory ./App-arm64 `
  -LegacyDirectory ./src/ProcessKeeper.Legacy.App/bin/Release/net462 `
  -OutputPath ./artifacts/ProcessKeeper.exe
```

发布前还需完成其余测试并检查实际成品。混用渠道/日期的包会被拒绝，未知渠道会构建失败；只有正式 UI 隐藏侧栏、关于、引导的预览文案。仓库不自动创建 Release 或上传二进制，不提供签名私钥。未来官方附件需符合固定仓库、语义版本、通用 EXE 及官方 SHA-256 要求。

## 源码公开检查

```powershell
./scripts/Test-Repository.ps1
```

检查已跟踪文件名、常见凭据模式、开发者路径、文档相对链接及语言对应文件。这不是完整秘密审计，图片隐私需人工看图。

`App`、`App-arm64`、`bin`、`obj`、`artifacts`、EXE、DLL、包及个人状态均被排除，不要强制添加。测试源码可以提交，**测试包不可以**。

[可选 GitHub Actions 示例](examples/source-validation.yml) 可在管理员获得工作流写入授权后放入 `.github/workflows/`。它只测试/构建，不上传 EXE 或发版。其余见[测试说明](TESTING.zh-CN.md)。
