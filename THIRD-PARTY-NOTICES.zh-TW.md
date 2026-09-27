# 第三方來源與授權聲明

[English](THIRD-PARTY-NOTICES.md) | [简体中文](THIRD-PARTY-NOTICES.zh-CN.md) | **繁體中文** | [首頁](README.zh-TW.md)

Process Keeper 原始碼採 MIT 授權，不會因此重新授權相依元件、字型、商標或 Microsoft 二進位。 [licenses](licenses) 保留上游原文，不翻譯或改寫；三語說明不取代正式授權條款。更新相依套件時需重新核對聲明。

## 介面參考與相依元件

| 專案 | 用途 | 授權/聲明 |
| --- | --- | --- |
| [Microsoft WinUI Gallery](https://github.com/microsoft/WinUI-Gallery) | NavigationView、ListView、ContentDialog、InfoBar 及 Fluent 互動參考 | [MIT 原文](licenses/WinUI-Gallery-LICENSE.txt) |
| [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) / Microsoft.WindowsAppSDK 2.5.1 | 現代 Windows 執行階段與 UI 平台 | [套件條款](licenses/Microsoft.WindowsAppSDK.2.5.1-LICENSE.txt)、[聲明](licenses/Microsoft.WindowsAppSDK.2.5.1-NOTICE.txt) |
| Microsoft.WindowsAppSDK.WinUI 2.3.9 | WinUI 執行相依元件 | [條款](licenses/Microsoft.WindowsAppSDK.WinUI.2.3.9-LICENSE.txt)、[聲明](licenses/Microsoft.WindowsAppSDK.WinUI.2.3.9-NOTICE.txt) |
| [.NET](https://github.com/dotnet/runtime) 8.0.31 | 現代自足載荷 | [授權](licenses/Microsoft.NETCore.App.Runtime.win-x64.8.0.31-LICENSE.txt)、[聲明](licenses/Microsoft.NETCore.App.Runtime.win-x64.8.0.31-THIRD-PARTY-NOTICES.txt) |
| [WPF UI](https://github.com/lepoco/wpfui) / Abstractions 4.3.0 | 相容控制項與主題 | [MIT](licenses/WPF-UI.4.3.0-LICENSE.md)、[聲明](licenses/WPF-UI.4.3.0-ThirdPartyNotices.txt)、[Abstractions](licenses/WPF-UI.Abstractions.4.3.0-LICENSE.md) |

本專案是獨立應用程式，不是 WinUI Gallery 官方發行版，也不代表 Microsoft 背書。Gallery 的 MIT 不表示所有 Windows App SDK 二進位皆為 MIT。特定建置以專案檔及實際還原的相依圖為準。

WPF UI 聲明包含 VirtualizingWrapPanel、Fluent UI System Icons、WPF、WinUI，以及採獨立條款的 Segoe Fluent Icons 字型。系統 .NET Framework 是執行需求，不內嵌於免安裝 EXE。

## 受控程式庫與建置工具

相容相依圖包含 System.Text.Json 8.0.6、System.Text.Encodings.Web 8.0.0、Microsoft.Bcl.AsyncInterfaces 8.0.0、System.Threading.Tasks.Extensions 4.5.4、System.Memory 4.6.3、System.Buffers 4.6.1、System.Runtime.CompilerServices.Unsafe 6.1.2、System.ValueTuple 4.6.2、System.Numerics.Vectors 4.6.1。

套件內附授權與聲明保存在 `licenses`。僅宣告 MIT 運算式的維護套件使用[上游 maintenance-packages 授權](licenses/dotnet-maintenance-packages-LICENSE.txt)，來源為套件中繼資料記錄的提交版本。[PolySharp 1.16.0](https://github.com/Sergio0694/PolySharp) 產生語言補充程式碼，採 [MIT](licenses/PolySharp.1.16.0-LICENSE.txt)；Framework 引用組件使用[上游授權](licenses/Framework-Reference-Assemblies-LICENSE.txt)。

現代相依圖另含 System.Numerics.Tensors 及 Windows App SDK 子套件，隨附條款與聲明亦保留。這些元件可能引用 ONNX Runtime、Brotli、zlib；包含元件不代表本應用啟用模型推論。DirectML、WebView2、Windows 二進位和字型保留各自條款。僅供建置的 SDK、分析器與引用套件由 NuGet 還原，不提交本原始碼儲存庫。

## 圖示及軟體中繼資料

- GitHub 與 Bilibili 路徑來自 [Simple Icons](https://github.com/simple-icons/simple-icons)，保留 [CC0 1.0](licenses/Simple-Icons-LICENSE.md)。
- 酷安路徑來自 [Lawnicons](https://github.com/LawnchairLauncher/lawnicons)，採 [Apache-2.0](licenses/Lawnicons-LICENSE.txt)。
- 上述路徑以 XAML 呈現，用於作者個人頁面連結；商標屬原權利人，不代表背書。
- 其他程式名稱、圖示、版本與公司資料來自使用者本機檔案及 Windows。匿名示範截圖不會重新散布使用者本機軟體資產。
- Process Keeper 自身圖示由 AI 產生，與從本機程式讀取的原圖示分開。

## 保留授權

重新散布相依元件時應保留適用聲明。本次原始碼公開不包含執行 DLL、可執行套件或簽章憑證。未來二進位發行需保留實際所帶版本的授權，公開原始碼不能取代此要求。
