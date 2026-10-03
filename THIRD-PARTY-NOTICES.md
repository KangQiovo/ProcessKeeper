# Third-party notices

**English** | [简体中文](THIRD-PARTY-NOTICES.zh-CN.md) | [繁體中文](THIRD-PARTY-NOTICES.zh-TW.md) | [Home](README.md)

Process Keeper source is MIT-licensed. This does not relicense its dependencies, fonts, trademarks or redistributed Microsoft binaries. The original texts under [licenses](licenses) are preserved, not translated or rewritten; the localized explanations do not replace them. Recheck notices whenever dependencies change.

## Interface references and dependencies

| Project | Use | License / notices |
| --- | --- | --- |
| [Microsoft WinUI Gallery](https://github.com/microsoft/WinUI-Gallery) | Reference for NavigationView, ListView, ContentDialog, InfoBar and Fluent interaction patterns | [Original MIT license](licenses/WinUI-Gallery-LICENSE.txt) |
| [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) / Microsoft.WindowsAppSDK 2.5.1 | Modern Windows runtime and UI platform | [Package terms](licenses/Microsoft.WindowsAppSDK.2.5.1-LICENSE.txt), [notices](licenses/Microsoft.WindowsAppSDK.2.5.1-NOTICE.txt) |
| Microsoft.WindowsAppSDK.WinUI 2.3.9 | WinUI runtime dependency | [Terms](licenses/Microsoft.WindowsAppSDK.WinUI.2.3.9-LICENSE.txt), [notices](licenses/Microsoft.WindowsAppSDK.WinUI.2.3.9-NOTICE.txt) |
| [.NET runtime](https://github.com/dotnet/runtime) 8.0.31 | Self-contained x64 and ARM64 payloads; both official packages supply identical license and notice bytes | [License](licenses/Microsoft.NETCore.App.Runtime.win-x64.8.0.31-LICENSE.txt), [notices](licenses/Microsoft.NETCore.App.Runtime.win-x64.8.0.31-NOTICES.txt) |
| [WPF UI](https://github.com/lepoco/wpfui), WPF-UI / Abstractions 4.3.0 | Compatibility controls and theme | [MIT](licenses/WPF-UI.4.3.0-LICENSE.md), [notices](licenses/WPF-UI.4.3.0-ThirdPartyNotices.txt), [Abstractions](licenses/WPF-UI.Abstractions.4.3.0-LICENSE.md) |

This is an independent application, not an official WinUI Gallery distribution or a Microsoft-endorsed utility. Gallery's MIT license does not imply all Windows App SDK binaries use MIT. Package versions in the project files and restored dependency graph are authoritative for a particular build.

WPF UI notices include VirtualizingWrapPanel, Fluent UI System Icons, WPF and WinUI sources, and separate terms for the Segoe Fluent Icons font. System .NET Framework is a machine prerequisite and is not embedded in the portable EXE.

## Managed libraries and build tools

The compatibility dependency graph includes System.Text.Json 8.0.6, System.Text.Encodings.Web 8.0.0, Microsoft.Bcl.AsyncInterfaces 8.0.0, System.Threading.Tasks.Extensions 4.5.4, System.Memory 4.6.3, System.Buffers 4.6.1, System.Runtime.CompilerServices.Unsafe 6.1.2, System.ValueTuple 4.6.2 and System.Numerics.Vectors 4.6.1.

Package-supplied licenses/notices are retained under `licenses`. The maintenance packages declaring MIT use the [upstream maintenance-packages license](licenses/dotnet-maintenance-packages-LICENSE.txt), obtained from the repository revision recorded in the package metadata. [PolySharp 1.16.0](https://github.com/Sergio0694/PolySharp) generates language polyfills under [MIT](licenses/PolySharp.1.16.0-LICENSE.txt). Framework reference assemblies use the [linked upstream license](licenses/Framework-Reference-Assemblies-LICENSE.txt).

The modern dependency graph also includes System.Numerics.Tensors and Windows App SDK subpackages. Their supplied terms/notices are retained. ONNX Runtime, Brotli and zlib sources may be referenced by those redistributed components; inclusion does not mean this app performs model inference. DirectML, WebView2, Windows runtime binaries and fonts keep their own terms. Build-only SDK/analyzer/reference packages are restored from NuGet and are not checked into this source repository.

## Icons and application metadata

- GitHub and Bilibili platform paths come from [Simple Icons](https://github.com/simple-icons/simple-icons), under the retained [CC0 1.0 text](licenses/Simple-Icons-LICENSE.md).
- The Coolapk path comes from [Lawnicons](https://github.com/LawnchairLauncher/lawnicons), under [Apache-2.0](licenses/Lawnicons-LICENSE.txt).
- These paths are rendered as XAML icons for the author's profile links. Trademarks remain with their respective owners; use does not imply endorsement.
- Other program names, icons, versions and companies shown in the application are read from the user's local executables and Windows metadata. Screenshots use anonymous demonstration data. The Auradio name and icon appear with the author's permission as a demonstration cameo; no Auradio private source code is included. Their rights remain with their owner.
- Process Keeper's application icon was AI-generated. It is separate from the original icons read from local programs.

## License preservation

Keep applicable notices with any redistributed dependency. This source publication contains no runtime DLLs, executable package or signing certificate. A later binary release must preserve the licenses for the exact dependency versions it ships; source availability alone is not a replacement for that requirement.

## Behavioral references

[PCL](https://github.com/Meloong-Git/PCL) and [PCL Community](https://github.com/PCL-Community/PCL-CE) were researched for memory and download behavior. The functions are independently implemented; no launcher source, binary or private module is included. This project's MIT license does not relicense those projects. See [Utilities](docs/UTILITIES.md).

## Markdig

[Markdig 0.41.3](https://github.com/xoofx/markdig/releases/tag/0.41.3) parses release-note Markdown for both interfaces, rendered with native controls. BSD-2-Clause applies; the [original upstream license](licenses/Markdig.0.41.3-LICENSE.txt) is retained from the commit recorded by the NuGet package. The compatibility build reuses its existing System.Memory dependency.

## NSIS

[NSIS 3.13](https://nsis.sourceforge.io/) builds the installer and native uninstaller. Release packages use zlib compression; the compiler is not redistributed with the app. The official ZIP supplies the retained [copyright and license summary](licenses/NSIS.3.13-LICENSE.txt); individual components keep their own terms.
