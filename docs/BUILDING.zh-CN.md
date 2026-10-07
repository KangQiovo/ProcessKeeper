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

## 打包 1.7.1 发行目标

前面的 ARM64 调用将现代载荷交叉编译至 `App-arm64`，复用同一时间戳。外层启动器和更新助手仍为 x86，不需要 ARM64 MSVC 工具；交叉编译成功不能替代 ARM 设备验证。

Windows7Compat 包含兼容载荷；Windows10x64 同时包含现代 x64 与支持 x86 的 WPF 回退；Windows10arm64 包含原生 ARM64。Universal 以 PK14 包含全部三种载荷。正式版发布这四个免安装文件和三个安装文件。

```powershell
New-Item -ItemType Directory -Path artifacts -Force | Out-Null
$targets = [ordered]@{
  Windows7Compat = 'win7-x86-compat'
  Windows10x64 = 'win10-x86-x64'
  Windows10arm64 = 'win10-arm64'
}
foreach ($entry in $targets.GetEnumerator()) {
  ./package-universal.ps1 -PackageTarget $entry.Key `
    -ModernDirectory ./App -Arm64Directory ./App-arm64 `
    -LegacyDirectory ./src/ProcessKeeper.Legacy.App/bin/Release/net462 `
    -OutputPath ("./artifacts/ProcessKeeper-preview-" + $entry.Value + ".exe")
  if ($LASTEXITCODE -ne 0) { throw 'Portable package failed.' }
}
```

每次选择**新输出文件名**。打包器拒绝覆盖已有文件，校验所选载荷的渠道和日期，构建原生更新助手，生成带清单/哈希的 CAB 并嵌入 x86 引导程序。打包不会启动应用。

-PackageTarget Universal 仍为默认并使用 PK14。1.7.1 正式附件保留 ProcessKeeper-v1.7.1.exe，1.6 更新器可接受该合包。旧更新器不支持 PK17 分包；迁移时选择合包，或手动下载其他版本。

直接运行 `App/ProcessKeeper.exe` 属于开发路线，缺少免安装入口提供的可信上下文，应用内更新和桌面快捷方式可能不可用；不能当作更新测试成功。

## 官方正式构建

`Release` 只是优化配置，**不会自动移除预览提示**。未指定渠道始终为 `Preview`，即使上次构建为正式版。明确准备官方发行时才指定正式渠道，并保持统一时间戳：

```powershell
./build.ps1 -StableRelease
if ($LASTEXITCODE -ne 0) { throw 'Stable modern build failed.' }
./build.ps1 -Architecture arm64 -SkipBuildStamp -SkipTests -StableRelease
if ($LASTEXITCODE -ne 0) { throw 'Stable ARM64 build failed.' }
dotnet build src/ProcessKeeper.Legacy.App/ProcessKeeper.Legacy.App.csproj -c Release -p:ProcessKeeperReleaseChannel=Stable
if ($LASTEXITCODE -ne 0) { throw 'Stable compatibility build failed.' }
New-Item -ItemType Directory -Path artifacts -Force | Out-Null
$targets = [ordered]@{
  Windows7Compat = 'win7-x86-compat'
  Windows10x64 = 'win10-x86-x64'
  Windows10arm64 = 'win10-arm64'
}
foreach ($entry in $targets.GetEnumerator()) {
  ./package-universal.ps1 -StableRelease -PackageTarget $entry.Key `
    -ModernDirectory ./App -Arm64Directory ./App-arm64 `
    -LegacyDirectory ./src/ProcessKeeper.Legacy.App/bin/Release/net462 `
    -OutputPath ("./artifacts/ProcessKeeper-v1.7.1-" + $entry.Value + ".exe")
  if ($LASTEXITCODE -ne 0) { throw 'Stable portable package failed.' }
}
```

发行前完成其余测试，并逐一检查**实际 EXE** 的路线标记、PE 架构、嵌入清单、载荷哈希、空白默认规则和公布的 SHA-256。混用渠道/日期或未知渠道会被拒绝；只有正式 UI 隐藏侧栏、关于及引导中的预览文案。仓库不自动发布 Release，也不提供签名证书。后续更新附件需符合固定仓库、语义版本、包类型、`PK17` 协议和官方 SHA-256。见 [1.7.1 更新说明](RELEASE-1.7.1.zh-CN.md)。

## 源码公开检查

```powershell
./scripts/Test-Repository.ps1
```

检查已跟踪文件名、常见凭据模式、开发者路径、文档相对链接及语言对应文件。这不是完整秘密审计，图片隐私需人工看图。

`App`、`App-arm64`、`bin`、`obj`、`artifacts`、EXE、DLL、包及个人状态均被排除，不要强制添加。测试源码可以提交，**测试包不可以**。

[可选 GitHub Actions 示例](examples/source-validation.yml) 可在管理员获得工作流写入授权后放入 `.github/workflows/`。它只测试/构建，不上传 EXE 或发版。其余见[测试说明](TESTING.zh-CN.md)。

## 安装与卸载

安装器使用固定 NSIS 3.13 编译并索要管理员权限，登记受本项目管理的计算机级程序项，安装固定文件名 ProcessKeeper.exe、Uninstall.exe、install.ini，并创建对应快捷方式。卸载器只删除所属的应用文件、快捷方式及登记；保留设置、历史和自启动恢复备份。README 说明了可选的本用户数据手动清理。Win7、x86 及 ARM64 目标系统上的真实安装和卸载仍待验证。

```powershell
./package-universal.ps1 -StableRelease -PackageTarget Universal `
  -ModernDirectory ./App -Arm64Directory ./App-arm64 `
  -LegacyDirectory ./src/ProcessKeeper.Legacy.App/bin/Release/net462 `
  -OutputPath ./artifacts/ProcessKeeper-v1.7.1.exe
if ($LASTEXITCODE -ne 0) { throw 'Universal package failed.' }
./src/ProcessKeeper.UniversalLauncher/build-native.ps1 -SetupGuardOnly `
  -OutputPath ./artifacts/ProcessKeeper.SetupGuard.dll `
  -BuildDirectory ./artifacts/setup-guard
if ($LASTEXITCODE -ne 0) { throw 'Setup guard build failed.' }
foreach ($entry in $targets.GetEnumerator()) {
  ./scripts/package-installers.ps1 -StableRelease -PackageTarget $entry.Key `
    -PortablePath ("./artifacts/ProcessKeeper-v1.7.1-" + $entry.Value + ".exe") `
    -OutputPath ("./artifacts/ProcessKeeper-v1.7.1-" + $entry.Value + "-setup.exe") `
    -NsisCompiler ./tools/nsis-3.13/makensis.exe `
    -SetupGuardPath ./artifacts/ProcessKeeper.SetupGuard.dll
  if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
}
```
