# 安装器打包

[English](README.md) | **简体中文** | [繁體中文](README.zh-TW.md) | [首页](../README.zh-CN.md)

三个原生 NSIS 安装器分别包含对应的官方免安装 EXE，安装为固定名称的 `ProcessKeeper.exe`，并提供原生 `Uninstall.exe` 和归属标记。安装过程中不运行 .NET，也不下载另一套安装器。

| 安装包 | 适用路线 |
| --- | --- |
| `ProcessKeeper-v1.8.1-win7-x86-compat-setup.exe` | Intel/AMD Windows 7 SP1 / 8.1 / 10 及更新系统，包括 x86 设备；兼容界面 |
| `ProcessKeeper-v1.8.1-win10-x86-x64-setup.exe` | Intel/AMD Windows 10 及更新系统；支持时使用 x64 现代界面，否则使用 x86 兼容路线 |
| `ProcessKeeper-v1.8.1-win10-arm64-setup.exe` | 原生 ARM64 Windows 10 build 19041+ / Windows 11；ARM64 现代界面 |

安装器和卸载器均为原生 x86 Unicode 程序。ARM64 Windows 使用 x86 模拟运行安装器和启动器，现代应用负载仍为 ARM64。缺失 Framework 时仍由应用启动器提供明确的恢复指引。以上为实现目标，尚未完成真实 Windows 7/8.1/10 与 ARM64 安装卸载认证。

## 编译

从 [官方下载页](https://nsis.sourceforge.io/Download) 获取 **NSIS 3.13 ZIP**。本次使用的官方 SourceForge 压缩包 SHA-256 为 `BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8`。解压到自己的工具目录即可，无需安装编译器。打包脚本检查 `v3.13` 版本、记录编译器哈希并禁用外部 `nsisconf.nsh` 配置。安装器采用 zlib 压缩，上游许可完整保留在 [NSIS.3.13-LICENSE.txt](../licenses/NSIS.3.13-LICENSE.txt)。

先按 [构建文档](../docs/BUILDING.zh-CN.md) 生成对应的免安装包。其旁边的 `.package.json` 必须匹配实际 EXE、当前源码、版本路线和发布通道。在仓库根目录使用 PowerShell 7：

```powershell
./src/ProcessKeeper.UniversalLauncher/build-native.ps1 -SetupGuardOnly `
  -OutputPath ./artifacts/ProcessKeeper.SetupGuard.dll `
  -BuildDirectory ./artifacts/setup-guard
if ($LASTEXITCODE -ne 0) { throw 'Setup guard build failed.' }
./scripts/package-installers.ps1 -PackageTarget Windows10x64 `
  -PortablePath ./artifacts/ProcessKeeper-v1.8.1-win10-x86-x64.exe `
  -OutputPath ./artifacts/ProcessKeeper-v1.8.1-win10-x86-x64-setup.exe `
  -NsisCompiler E:/Tools/nsis-3.13/makensis.exe `
  -SetupGuardPath ./artifacts/ProcessKeeper.SetupGuard.dll -StableRelease
```

其余安装器分别使用 `Windows7Compat`、`Windows10arm64`。仅在输入也是预览包时省略 `-StableRelease`；编译优化模式不代表可以更改发布通道。`-ValidateOnly` 只检查输入，不编译或生成安装器。输出文件必须不存在；链接路径、证据不符或编译过程中源码变化均会拒绝。编译不会执行安装、卸载或内含应用。

## 安装文件与更新约定

安装器索要管理员权限，各版本统一在 **32 位注册表视图** 的 `HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\ProcessKeeper` 注册整机安装。兼容包及 x86 主机默认使用 Program Files (x86)，现代包在 64 位主机上使用原生 Program Files 目录。目录选择器可改为其他独立本地目录；盘符根目录、Windows 目录、链接路径和存在无关同名程序的目录会被拒绝。

精确归属约定包含 `InstallLocation`、带引号的 `UninstallString`、固定 `ProcessKeeperRepository=KangQiovo/ProcessKeeper`、`ProcessKeeperPackageTarget` 和 DWORD `ProcessKeeperInstallerContract=1`。`install.ini` 的 `Installation` 节重复保存仓库、路线与约定。`DisplayName` 始终为 **Process Keeper**，`Publisher` 为 **KangQi**，`DisplayIcon` 指向安装的 EXE。这些元数据不是数字签名，UAC 仍可能显示未知发布者。

应用内更新替换原路径的固定 EXE，保留卸载器与标记，并且只在新应用确认就绪、通过归属检查后更新已有卸载登记的版本。同路线重装复用已验证目录。更换路线或移动安装位置时，先卸载旧安装；个人设置保留。嵌入的原生守卫按原始安装文件路径检查受保护会话，包括缓存中的实际界面和待处理更新助手。发现活动会话或无法核实归属时停止操作，不终止进程；守卫不作为额外文件安装。

安装先暂存并校验新 EXE 哈希，再将三个精确旧文件移入独立备份目录。文件或登记写入失败时尝试有限恢复，未恢复的备份在详情中明确保留。卸载也先暂存三个原文件，再移除所属登记。此机制降低部分替换风险，但不保证断电后的原子恢复；核实安装状态前请保留残留备份。

安装器创建所有用户的开始菜单快捷方式，以及安装用户真实桌面上的 `Process Keeper.lnk`。无关的同名快捷方式会保留。更新与卸载要求精确应用目标及空参数，不搜索或删除任意快捷方式。

## 卸载与个人数据

通过 Windows 程序 / 已安装应用或安装目录中的 `Uninstall.exe` 卸载。保留原生确认页面，不登记静默卸载命令。请先退出 Process Keeper，并完成或取消待处理更新。卸载器核对精确目录、注册表与标记，拒绝链接和重解析路径，然后仅移除 `ProcessKeeper.exe`、`Uninstall.exe`、`install.ini`、归属明确的快捷方式和固定登记。只移除空目录，绝不递归删除。

个人配置、记录、自启动恢复备份和受保护解压缓存 **默认保留**。删除应用文件不会还原之前修改的自启动设置。按 [免安装文件与清理](../README.zh-CN.md#免安装文件与清理) 可自行清理本用户数据和 SID 目录；其他用户数据和无关文件必须保留。

源码、PE 与编译器检查使用隔离的无实际功能素材，没有进行真实安装或卸载。磁盘中断、权限、UAC 与旧系统行为仍需在可丢弃环境中测试。不要上传编译器压缩包、测试安装器、日志或个人状态。
