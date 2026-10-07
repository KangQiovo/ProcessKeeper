# 測試指南

[English](TESTING.md) | [简体中文](TESTING.zh-CN.md) | **繁體中文**

工具鏈要求見[建置指南](BUILDING.zh-TW.md)，執行路線和限制見[相容指南](COMPATIBILITY.zh-TW.md)。以下命令均從儲存庫根目錄，在 Windows 上使用 PowerShell 7 和所需 .NET SDK 執行。不要同時對同一專案的 `obj` 目錄執行多個建置。

## 先執行受改動影響的測試

```powershell
# 在儲存庫根目錄使用 PowerShell 7 執行。
dotnet run --project src/ProcessKeeper.Tests/ProcessKeeper.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Settings.Tests/ProcessKeeper.Settings.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.CloudProfiles.Tests/ProcessKeeper.CloudProfiles.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Tools.Tests/ProcessKeeper.Tools.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Display.Tests/ProcessKeeper.Display.Tests.csproj -c Release --framework net8.0-windows10.0.19041.0
dotnet run --project src/ProcessKeeper.Search.Tests/ProcessKeeper.Search.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Autorun.Tests/ProcessKeeper.Autorun.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Update.Tests/ProcessKeeper.Update.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Architecture.Tests/ProcessKeeper.Architecture.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Instance.Tests/ProcessKeeper.Instance.Tests.csproj -c Release
dotnet run --project src/ProcessKeeper.Uninstall.Tests/ProcessKeeper.Uninstall.Tests.csproj -c Release --framework net8.0-windows10.0.19041.0
dotnet run --project src/ProcessKeeper.Performance.Tests/ProcessKeeper.Performance.Tests.csproj -c Release --framework net8.0-windows10.0.19041.0
dotnet run --project src/ProcessKeeper.CatalogUpdate.Tests/ProcessKeeper.CatalogUpdate.Tests.csproj -c Release --framework net8.0-windows10.0.19041.0
```

這些命令只是範例，並非完整發布檢查。其他專案涵蓋已安裝應用程式目錄、圖示、操作記錄、AVD 參數與恢復、特殊視窗、瀏覽器預覽、視窗路由、啟動、單一執行個體、在地化和正常結束。

雲端設定測試使用模擬 GitHub Contents 回應及隔離檔案，涵蓋固定目錄、中繼資料與內容 SHA、UTF-8 和便攜格式限制、錯誤與取消、空白預設規則、可選七應用程式範本，以及修訂版本檢查和最多五套的原子匯入。不需 GitHub 憑據，不修改使用者白名單。下載器使用受控 HTTP 回應；本機回環與原生介面驗證不代表外網下載速度或舊裝置相容性。

驗證實際 x86 Framework 後端：

```powershell
dotnet build src/ProcessKeeper.Legacy.Core.Tests/ProcessKeeper.Legacy.Core.Tests.csproj -c Release
& ./src/ProcessKeeper.Legacy.Core.Tests/bin/Release/net462/ProcessKeeper.Legacy.Core.Tests.exe
dotnet run --project src/ProcessKeeper.Display.Tests/ProcessKeeper.Display.Tests.csproj -c Release --framework net462
dotnet run --project src/ProcessKeeper.Uninstall.Tests/ProcessKeeper.Uninstall.Tests.csproj -c Release --framework net462
dotnet run --project src/ProcessKeeper.Performance.Tests/ProcessKeeper.Performance.Tests.csproj -c Release --framework net462
dotnet run --project src/ProcessKeeper.CatalogUpdate.Tests/ProcessKeeper.CatalogUpdate.Tests.csproj -c Release --framework net462
dotnet run --project src/ProcessKeeper.Legacy.Instance.Tests/ProcessKeeper.Legacy.Instance.Tests.csproj -c Release
```

Legacy 測試程式必須在裝有相容 .NET Framework 的 Windows 中實際執行；僅建置成功不代表執行通過。它使用共用測試夾具、隔離檔案和唯讀處理程序身分檢查。選用參數 `--read-only-live` 會列舉真實自動啟動來源但不修改它們，一般迴歸請省略此參數。

## 測試通過說明什麼

| 測試類型 | 能證明的範圍 | 不能據此證明 |
| --- | --- | --- |
| 純邏輯或注入後端 | 在受控輸入下的規則比對、狀態轉換、取消、身分拒絕與錯誤處理 | 對所有已安裝軟體都有效 |
| 隔離檔案或原生測試夾具 | 真實檔案、ACL、管道、視窗 API，以及僅由夾具擁有的輔助處理程序或回送接聽 | 已成功操作使用者真實模擬器、服務或應用程式 |
| 現代系統上的 Framework x86 執行 | 產生的共用程式碼可在該系統的 32 位元 CLR 中建置執行 | Windows 7/8.1 執行情況或舊硬體效能 |
| 注入式 UI 測試夾具 | 已測尺寸、佈景主題、語言及 DPI 下的呈現和互動 | 所有顯示驅動程式、螢幕閱讀器或真實顯示器配置 |
| 主動進行的真實系統測試 | 測試者記錄的具體情境、系統與結果 | 未測系統、無損結束或雲端同步完成 |

更新測試使用假 HTTP 和隔離下載檔案，不安裝真實發布。自動啟動修改測試使用記憶體後端或夾具檔案；選用的實際列舉屬於另一項唯讀範圍。近期邊界迴歸涵蓋損壞備份隔離、逾時虛擬機器查詢數量限制及嚴格的更新資產名稱。共用斷言會在多個專案中執行，不能累加成不同的真實情境。

詳細說明見 [AVD](../src/ProcessKeeper.Avd.Tests/README.zh-TW.md)、[啟動器](../src/ProcessKeeper.Launcher.Tests/README.zh-TW.md)、[搜尋](../src/ProcessKeeper.Search.Tests/README.zh-TW.md)及 [Legacy Core](../src/ProcessKeeper.Legacy.Core/README.zh-TW.md)。

## 1.7.1 回歸邊界

分別檢查應用程式列、圖示、說明、鍵盤與快速連續點選，以及獨立核取方塊操作。涵蓋兩套介面全部應用程式頁面、巢狀平台/應用程式/檔案/處理程序/來源/版本、摺疊主項全選、局部取消及重新整理搜尋後的狀態。原生選取事件和取消或失敗的保留、啟用操作不能改變邏輯選取。檢查深淺佈景主題及窄、寬視窗版面。

Microsoft 篩選應驗證系統登記的商店版小畫家，拒絕複製檔案、路徑不符、不健康或開發套件及僅顯示的發行者名稱。更新候選包含安裝套件：檢查精確類型與執行方式預設選取、安裝目錄中的整合包、缺少符合、未知身分、手動切換確認和明確的安裝精靈交接。免安裝更新後舊解除安裝程式歸屬標記應仍有效；使用隔離中繼資料與靜態測試包驗證遷移和回復。

解除安裝程式名稱比對只是標籤，不能變成執行命令。透過隔離目錄和注入後端檢查空目錄清理、登記仍存在、取消、非零退出、不完整清單、共享目錄、連結、路徑替換競態、意外檔案及上層目錄保留。快取案例涵蓋作用中工作階段、執行中的助手或安裝程式、鎖定檔案、未知內容及應用程式擁有的遺留下載和準備目錄。常規驗證不得解除安裝真實軟體或刪除真實安裝目錄。

## 1.7.0 回歸邊界

單一執行個體檢查應涵蓋同一使用者及工作階段內的不同套件路徑、版本和介面路線。確認獲選者身分來自實際處理程序與受保護的啟動憑據；未獲選的啟動不能因延遲回呼重新開啟引導或主視窗；關閉視窗或釋放鎖後，仍須等待舊處理程序實際結束。保留現有執行個體的結束狀態不能算更新就緒。僅使用隔離且由測試擁有的處理程序，不為此關閉使用者真實應用。

已安裝應用測試涵蓋重複登錄、版本/架構後綴、不同磁碟、缺失或衝突的發行者、共用命令宿主、保留元件角色資訊、精確啟動入口與獨立白名單狀態。解除安裝測試合併相關版本時保留每條登錄。分組必須保留來源身分，列表變短本身不能算通過。

更新測試使用七種發行附件候選，驗證只有符合套件類型的檔案可自動安裝。應檢查 Markdown 說明與連結、進度視窗暫停/繼續與切換來源、下載中切換頁面、下載後保持執行、只在明確確認後更新，以及取消確認後保留下載，以及新套件就緒後才清理舊套件備份。下載狀態僅在應用生命週期保留，不宣稱完整退出後恢復。

更新夾具涵蓋七種附件，只有精確符合的免安裝類型可自動安裝，安裝程式維持手動下載。檢查 PK17 分包清單（Win10 含現代與相容兩條路線）、PK14 整合包、實際架構及雜湊。真正的 1.6 驗證器應接受整合包並拒絕分包/安裝程式，且不替換使用者應用。安裝程式的原始碼、資源與編譯器檢查不能代替各目標系統的安裝及解除安裝實測。

目前現代主機的原生分組測試涵蓋三語言、深淺模式與兩種視窗寬度，包括折疊選取、部分取消、搜尋、穩定更新與獨立啟用/保護狀態。另有唯讀真實清單檢查，比較分組前後全部可執行路徑與來源入口身分，不修改真實啟動設定或關閉使用者應用。安全軟體阻擋或測試輸出缺失屬待核查的干擾，不是通過結果或已確認的產品故障；不得關閉防護或自動批准提示。

## 建置檢查與手動驗證

`build.ps1` 會執行腳本列出的受控測試並發布現代應用程式，不是僅測試命令。原生啟動器、更新助手和相容介面另有建置要求，請參閱建置指南。一般建置即使使用 Release 組態也仍為 **Preview**；穩定通道參數只用於明確準備的官方 Stable 發布。

驗證真實關閉、自動啟動或更新操作前，使用可捨棄的測試環境及你擁有的處理程序和資料。記錄 UAC 取消、身分複核失敗、中斷後的恢復，以及確切受影響設定。不能把合成成功描述成真實安裝或應用程式結束成功。

真實舊系統和舊硬體驗證仍未完成。不能因為 Framework 程式在新系統執行，就標示 Windows 7 已實測。

## 回報可重現結果

### 原生載入頁主題

[Test-StartupTheme.ps1](../src/ProcessKeeper.UniversalLauncher/Test-StartupTheme.ps1) 要求明確指定 `-FixturePath` 與 `-OutputDirectory`。先使用 `build-native.ps1 -UiFixture` 及其要求的資源參數建置隔離啟動器。指令碼執行前檢查僅測試夾具匯出的 PE 標識，拒絕正式套件與無關程式。暫存檔案與截圖應重新導向選定的測試目錄。

矩陣檢查三語言、深淺色與注入的高對比度配色、權限與復原頁面、實際文字對比度、主題變更訊息與 GDI 資源增長。指令碼只讀取目前 Windows 應用程式主題，不修改主機主題；這些檢查僅涵蓋本機自有測試視窗，不涵蓋正式解壓、UAC 或舊系統硬體。

記錄原始碼提交、命令、系統組建號、架構、執行階段、結束代碼、通過/失敗/略過數量，以及相關佈景主題、語言、尺寸和 DPI。非零結束、失敗案例或未解釋的略過不算完整通過。測試數量會變化，應以實際主控台或 JSON 結果為準，不依賴歷史固定總數。

提交中不要包含程式包、快取、完整記錄或個人設定。只附去識別化摘要或最少重現資料。公開 CI 結果連結可在存在後補充，不得用虛構的通過記錄取代缺失連結。
