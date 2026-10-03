# 第三方来源与许可说明

[English](THIRD-PARTY-NOTICES.md) | **简体中文** | [繁體中文](THIRD-PARTY-NOTICES.zh-TW.md) | [首页](README.zh-CN.md)

## Microsoft WinUI Gallery

- 官方项目：[microsoft/WinUI-Gallery](https://github.com/microsoft/WinUI-Gallery)
- 许可：[本地 MIT License 原文](licenses/WinUI-Gallery-LICENSE.txt)；[官方原文](https://github.com/microsoft/WinUI-Gallery/blob/main/LICENSE)
- Copyright (c) Microsoft Corporation. All rights reserved.

Process Keeper参考 Gallery 的 WinUI 控件示例及 Fluent 界面用法，包括 NavigationView、ListView、ContentDialog、InfoBar 等。程序管理、进程采集、白名单及自定义页面由本项目实现。Process Keeper是独立应用，不是 Microsoft WinUI Gallery 的官方发行版，也不代表 Microsoft 对本项目提供支持。

## Windows App SDK / WinUI

项目依赖 NuGet 包 `Microsoft.WindowsAppSDK`，版本以应用工程文件为准（当前为 2.5.1）。使用的二进制包及其依赖适用各自随包提供的许可，不能因为 Gallery 使用 MIT 许可，就将所有运行时二进制一概视为 MIT。

- [Windows App SDK 官方仓库](https://github.com/microsoft/WindowsAppSDK)
- [Microsoft.WindowsAppSDK NuGet 包](https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.5.1)
- 该 NuGet 包指定的许可文件为包内 `license.txt`（Microsoft Software License Terms — Microsoft Windows App SDK）。已原样保留 [Windows App SDK 2.5.1 许可](licenses/Microsoft.WindowsAppSDK.2.5.1-LICENSE.txt)及[第三方声明](licenses/Microsoft.WindowsAppSDK.2.5.1-NOTICE.txt)。
- 实际 WinUI 依赖为 `Microsoft.WindowsAppSDK.WinUI 2.3.9`，已原样保留其[许可](licenses/Microsoft.WindowsAppSDK.WinUI.2.3.9-LICENSE.txt)及[第三方声明](licenses/Microsoft.WindowsAppSDK.WinUI.2.3.9-NOTICE.txt)。

自包含发布会携带所需的 Windows App SDK / WinUI 运行文件。发布产物或依赖包中随附的许可及第三方声明仍然适用，应与对应组件一起保留。

## .NET

项目使用 .NET 8；本次自包含发布实际携带 `Microsoft.NETCore.App 8.0.31`。已从相同版本的 `Microsoft.NETCore.App.Runtime.win-x64` 官方包原样保留其[许可](licenses/Microsoft.NETCore.App.Runtime.win-x64.8.0.31-LICENSE.txt)及[第三方声明](licenses/Microsoft.NETCore.App.Runtime.win-x64.8.0.31-NOTICES.txt)。经逐字节校验，ARM64 官方包的这两份文件与 x64 相同，两个架构共用完整文本。

- [.NET Runtime 官方仓库](https://github.com/dotnet/runtime)
- [.NET Runtime 许可](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT)
- [.NET Runtime 第三方声明](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT)

`licenses` 中的许可及声明直接复制自官方仓库或本次实际使用的官方 NuGet 包；已核对 SHA-256 与来源一致，没有翻译、删改或拼接许可内容。其他随运行组件提供的声明继续适用。

## WPF UI 与兼容界面

兼容界面使用 [WPF UI](https://github.com/lepoco/wpfui) 和 `WPF-UI.Abstractions` 4.3.0。完整 [MIT 许可](licenses/WPF-UI.4.3.0-LICENSE.md)及[第三方声明](licenses/WPF-UI.4.3.0-ThirdPartyNotices.txt)随包保留；声明包括 VirtualizingWrapPanel、Fluent UI System Icons、WPF、WinUI 的相关来源，以及适用独立条款的 Segoe Fluent Icons 字体。不是所有字体和微软二进制运行组件都采用开源许可。

兼容业务层使用 System.Text.Json 8.0.6、System.Text.Encodings.Web 8.0.0、System.Memory 4.6.3、System.Buffers 4.6.1、System.Runtime.CompilerServices.Unsafe 6.1.2、Microsoft.Bcl.AsyncInterfaces 8.0.0、System.Threading.Tasks.Extensions 4.5.4、System.ValueTuple 4.6.2 与 System.Numerics.Vectors 4.6.1。已有包许可与原附声明保存在 `licenses` 中；仅声明 MIT 表达式的维护包共用 [dotnet maintenance-packages 许可](licenses/dotnet-maintenance-packages-LICENSE.txt)。系统安装的 .NET Framework 不包含在分享 EXE 内。

现代运行包的 System.Numerics.Tensors、Windows App SDK 子包与 WebView2 相关许可同样随附。ONNX Runtime、Brotli 和 zlib 的上游来源列于关于页；随发行组件携带它们，不表示本应用启用了模型推理或 WebView2 浏览器页面。DirectML、WebView2 及 Windows 组件的二进制授权以原始许可为准。

## 本机应用名称与图标

关于页的 [GitHub](https://github.com/simple-icons/simple-icons/blob/develop/icons/github.svg) 与 [Bilibili](https://github.com/simple-icons/simple-icons/blob/develop/icons/bilibili.svg) 图标路径来自 Simple Icons，适用随附的 [CC0 1.0 许可](licenses/Simple-Icons-LICENSE.md)。酷安图标路径来自 [Lawnicons](https://github.com/LawnchairLauncher/lawnicons/blob/develop/svgs/coolapk.svg)，适用随附的 [Apache License 2.0](licenses/Lawnicons-LICENSE.txt)。三个图标转换为 XAML Path，统一尺寸并随主题调整颜色，未增加图标背景。以上图标用于链接作者的对应平台主页；平台商标权仍属于各自权利人。

列表中的其他软件名称、文件版本信息和图标来自用户本机的相应进程、可执行文件或 Windows 元数据，用于说明当前运行的程序。相关名称、商标及图标属于各自权利人，不构成这些软件对Process Keeper的认可。

截图使用匿名演示数据。Auradio 名称及图标经作者许可作为演示彩蛋使用，未包含 Auradio 私有源代码，相关权利仍归其权利人所有。

## 构建依赖与原文

PolySharp 1.16.0 的源码生成使用 [MIT 许可](licenses/PolySharp.1.16.0-LICENSE.txt)，Framework 4.6.2 引用程序集使用[对应上游许可](licenses/Framework-Reference-Assemblies-LICENSE.txt)。NuGet 恢复的构建工具仍遵循各自条款。

本项目源码采用 [MIT](LICENSE)，不会将第三方二进制或字体自动改为 MIT。三语言说明不替代许可原文；licenses 中的第三方原文不翻译或改写。更新依赖时应重新核对许可与声明。

## 行为参考

[PCL](https://github.com/Meloong-Git/PCL) 和 [PCL Community](https://github.com/PCL-Community/PCL-CE) 用于研究内存及下载机制。功能独立实现，没有包含其源码、程序或私有模块；本项目的 MIT 不改变它们的许可。详见[杂项说明](docs/UTILITIES.zh-CN.md)。

## Markdig

[Markdig 0.41.3](https://github.com/xoofx/markdig/releases/tag/0.41.3) 用于两个界面的更新日志 Markdown 解析，使用原生控件显示。采用 BSD-2-Clause；按 NuGet 包中记录的提交保留[上游许可原文](licenses/Markdig.0.41.3-LICENSE.txt)。兼容版沿用现有的 System.Memory 依赖。

## NSIS

[NSIS 3.13](https://nsis.sourceforge.io/) 用于编译安装器和原生卸载器。正式包使用 zlib 压缩；编译工具不随应用分发。已从官方发行 ZIP 原样保留[版权及许可汇总](licenses/NSIS.3.13-LICENSE.txt)，各组件保留其各自条款。
