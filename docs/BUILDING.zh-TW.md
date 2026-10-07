# 從原始碼建置

[English](BUILDING.md) | [简体中文](BUILDING.zh-CN.md) | **繁體中文** | [首頁](../README.zh-TW.md)

請在現代 **x64 Windows** 開發機建置。Win7 是相容路線的執行目標，不是這套工具鏈的開發主機。編譯及隔離測試不需系統管理員權限。

## 需求

- [PowerShell 7](https://github.com/PowerShell/PowerShell)，命令為 `pwsh`。不依賴 Windows PowerShell 5.1；非標準位置可設定 `PROCESSKEEPER_PWSH` 或支援的 `-PowerShellPath`。
- x64 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)，只有執行階段不夠。`global.json` 在已安裝 .NET 8 中選擇最新 feature band，不跨主版本。
- Visual Studio 2022 / Build Tools 的 **C++ 桌面開發**、MSVC x86/x64 及 Windows SDK。腳本透過 `vswhere` 發現 VS 並選擇完整 SDK，亦可指定 `-VisualStudioPath`、`-WindowsSdkRoot`、`-WindowsSdkVersion`。
- 網路可還原工程宣告的 NuGet 套件，包含 Framework 4.6.2 引用組件及 WinUI 建置相依元件。現代工程使用 10.0.26100.0 SDK 投影、最低執行版本 19041，不會將原生相容路線提高到 Win11。

引用組件供開發機編譯，接收者仍需適用的 Framework 執行階段，見[相容性](COMPATIBILITY.zh-TW.md)。

## 預設預覽建置

在儲存庫根目錄使用 PowerShell 7：

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

`build.ps1` 產生一次統一時間戳記、執行受控回歸並將現代載荷輸出至 `App`。之後基於**相同時間戳記**建置相容載荷，不要在期間重新產生建置時間。自備 SDK/快取可傳 `-DotnetPath`、`-NugetPackages`，原生測試僅使用自行擁有的測試程序。

## 封裝 1.8.0 發行目標

前面的 ARM64 呼叫將現代載荷交叉編譯至 `App-arm64`，共用相同時間戳記。外層啟動器與更新助手仍為 x86，不需要 ARM64 MSVC 工具；交叉編譯成功無法取代 ARM 裝置驗證。

Windows7Compat 包含相容載荷；Windows10x64 同時包含現代 x64 與支援 x86 的 WPF 回退；Windows10arm64 包含原生 ARM64。Universal 以 PK14 包含全部三種載荷。正式版發布這四個免安裝檔案及三個安裝檔案。

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

每次選擇**新輸出檔名**。封裝器拒絕覆寫已有檔案，驗證所選載荷的渠道與日期、建置原生更新助手、產生具清單/雜湊的 CAB 並嵌入 x86 引導程序。封裝不會啟動應用。

-PackageTarget Universal 仍為預設並使用 PK14。1.8.0 正式附件保留 ProcessKeeper-v1.8.0.exe，1.6 更新程式可接受該整合包。舊更新程式不支援 PK17 分包；遷移時選擇整合包，或手動下載其他版本。

直接執行 `App/ProcessKeeper.exe` 屬開發路線，缺少免安裝入口提供的可信內容，應用內更新與桌面捷徑可能不可用；不能當作更新測試成功。

## 官方正式建置

`Release` 只是最佳化設定，**不會自動移除預覽提示**。未指定渠道一律為 `Preview`，即使上次建置為正式版。明確準備官方發行時才指定正式渠道，並保持統一時間戳記：

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
    -OutputPath ("./artifacts/ProcessKeeper-v1.8.0-" + $entry.Value + ".exe")
  if ($LASTEXITCODE -ne 0) { throw 'Stable portable package failed.' }
}
```

發行前完成其餘測試，並逐一檢查**實際 EXE** 的路線標記、PE 架構、嵌入清單、載荷雜湊、空白預設規則與公布的 SHA-256。混用渠道/日期或未知渠道會被拒絕；只有正式 UI 隱藏側欄、關於與導覽中的預覽文字。儲存庫不自動發行 Release，亦不提供簽章憑證。後續更新附件需符合固定儲存庫、語意版本、套件類型、`PK17` 協定與官方 SHA-256。見 [1.8.0 更新說明](RELEASE-1.8.0.zh-TW.md)。

## 原始碼公開檢查

```powershell
./scripts/Test-Repository.ps1
```

檢查已追蹤檔名、常見憑證模式、開發者路徑、文件相對連結及語言對應檔。這不是完整機密稽核，圖片隱私需人工看圖。

`App`、`App-arm64`、`bin`、`obj`、`artifacts`、EXE、DLL、套件及個人狀態皆排除，不要強制加入。測試原始碼可以提交，**測試套件不可以**。

[可選 GitHub Actions 範例](examples/source-validation.yml) 可在管理員取得工作流程寫入授權後放入 `.github/workflows/`。它只測試/建置，不上傳 EXE 或發行。其餘見[測試說明](TESTING.zh-TW.md)。

## 安裝與解除安裝

安裝程式以固定 NSIS 3.13 編譯並請求管理員權限，登錄由本專案管理的電腦級程式項目，安裝固定檔名 ProcessKeeper.exe、Uninstall.exe、install.ini，並建立對應捷徑。解除安裝程式只刪除所屬應用檔案、捷徑及登錄；保留設定、歷史與啟動復原備份。README 說明可選的本使用者資料手動清理。Win7、x86 及 ARM64 目標系統的實際安裝與解除安裝仍待驗證。

```powershell
./package-universal.ps1 -StableRelease -PackageTarget Universal `
  -ModernDirectory ./App -Arm64Directory ./App-arm64 `
  -LegacyDirectory ./src/ProcessKeeper.Legacy.App/bin/Release/net462 `
  -OutputPath ./artifacts/ProcessKeeper-v1.8.0.exe
if ($LASTEXITCODE -ne 0) { throw 'Universal package failed.' }
./src/ProcessKeeper.UniversalLauncher/build-native.ps1 -SetupGuardOnly `
  -OutputPath ./artifacts/ProcessKeeper.SetupGuard.dll `
  -BuildDirectory ./artifacts/setup-guard
if ($LASTEXITCODE -ne 0) { throw 'Setup guard build failed.' }
foreach ($entry in $targets.GetEnumerator()) {
  ./scripts/package-installers.ps1 -StableRelease -PackageTarget $entry.Key `
    -PortablePath ("./artifacts/ProcessKeeper-v1.8.0-" + $entry.Value + ".exe") `
    -OutputPath ("./artifacts/ProcessKeeper-v1.8.0-" + $entry.Value + "-setup.exe") `
    -NsisCompiler ./tools/nsis-3.13/makensis.exe `
    -SetupGuardPath ./artifacts/ProcessKeeper.SetupGuard.dll
  if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
}
```
