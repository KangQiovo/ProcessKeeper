# Legacy Core

[English](README.md) | [简体中文](README.zh-CN.md) | **繁體中文**

相容後端以 .NET Framework 4.6.2 為目標，提供共用的 `ProcessKeeper.Core` API。相容應用程式使用 x86 處理程序。Windows 7 SP1 是相容目標，**不代表已經完成 Windows 7 實測**，詳見[相容指南](../../docs/COMPATIBILITY.zh-TW.md)。

## 共用原始碼與原生邊界

`GenerateCompatibleCore.ps1` 從現代 Core 在 `obj` 下產生僅供建置使用的檔案。業務規則維護在共用原始碼中，調整限於必要的框架或系統差異；不要編輯或提交產生的副本。涉及安全的原生替換會核對預期出現次數，原始碼結構變化時建置應失敗。

`LegacyWindowsCapabilities` 偵測系統版本，並使用同一個已核實的處理程序控制代碼檢查關鍵處理程序標記。缺少身分資訊或不支援的 API 不能默默授權關閉處理程序。PID、建立時間、路徑、帳戶/工作階段及白名單檢查仍保留在共用流程中。

## 能力限制

- x86 模式不能安全重建 x64 AVD 的原始環境，因此不提供重新啟動計畫；已有圖形視窗在身分可核實時仍可恢復。
- 依賴同一 x64 參數讀取器的虛擬機器或系統匣調整也可能拒絕探索。
- 此建置不提供瀏覽器即時預覽、WinRT 套件清單及封裝應用程式自動啟動宣告。
- 傳統桌面和自動啟動後端仍是真實實作，但受來源可用性及權限限制。

## 建置與測試

.NET SDK、PowerShell 7 和 NuGet 相依性見[建置指南](../../docs/BUILDING.zh-TW.md)。PowerShell 僅是建置相依性；部署後的相容應用程式需要 .NET Framework 4.6.2 或相容的更新 4.x 執行階段。

從儲存庫根目錄執行：

```powershell
dotnet build src/ProcessKeeper.Legacy.Core.Tests/ProcessKeeper.Legacy.Core.Tests.csproj -c Release
& ./src/ProcessKeeper.Legacy.Core.Tests/bin/Release/net462/ProcessKeeper.Legacy.Core.Tests.exe
```

測試使用共用夾具、隔離檔案和唯讀處理程序身分擷取。選用 `--read-only-live` 會列舉實際自動啟動來源但不修改它們，一般迴歸請省略。通過只能證明**報告中所寫主機**上的 x86 Framework 行為，不能當作舊系統或舊硬體認證。以實際測試摘要為準，不依賴固定預期數量。

請參閱[測試指南](../../docs/TESTING.zh-TW.md)和[貢獻指南](../../CONTRIBUTING.zh-TW.md)。一般 Release 建置仍為 Preview。
