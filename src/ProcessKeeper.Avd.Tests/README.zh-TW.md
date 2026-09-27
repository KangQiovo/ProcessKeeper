# AVD 重新啟動迴歸測試夾具

[English](README.md) | [简体中文](README.zh-CN.md) | **繁體中文**

此程式將目前 Core 原始碼連結至自己的建置輸出，不啟動產品、不放寬系統管理員保護、不讀取 Android 認證檔案，也不操作既有模擬器。

在 Windows 上使用 .NET 8 SDK，從儲存庫根目錄以 PowerShell 7 執行：

```powershell
$resultJson = Join-Path ([IO.Path]::GetTempPath()) ('processkeeper-avd-' + [guid]::NewGuid().ToString('N') + '.json')
dotnet run --project src/ProcessKeeper.Avd.Tests/ProcessKeeper.Avd.Tests.csproj -c Release -- $resultJson
```

有案例失敗時傳回非零結束代碼，並將案例數、斷言數及錯誤詳情寫入 JSON。以實際輸出為準，不規定固定歷史通過數量。

## 實際驗證範圍

- `FakeAvdHost` 提供合成身分、雜湊、參數、環境、接聽、結束狀態及視窗，只記錄結束和啟動呼叫，不執行它們。
- 測試實際的 `AvdRestartService`、`AvdArgumentPolicy` 與 `AvdGuiMatcher`，採用較短逾時，但仍要求至少三次分開的成功觀察。
- 夾具自有 Win32 視窗驗證真實類別名稱、樣式和幾何中繼資料。類別名稱符合 `Qt*QWindow*` **不代表執行了 Qt 或模擬器**。
- 原生協定測試只使用測試處理程序自身身分及其建立的隨機回送接聽；主控台伺服器記錄結束命令並回應，不結束處理程序，也不讀取使用者認證檔案。

## 涵蓋邊界

探索階段不能結束或啟動程式。計畫繫結舊處理程序身分、SDK 路徑及雜湊、參數、環境、準確裝置名稱和連接埠；含糊或已變化的關聯阻止操作。

取消時依實際完成階段回報。未完整結束、主控台/ADB 連接埠占用或 SDK 變化會阻止再次啟動。並行執行或重用過期計畫不能重複發出結束、啟動請求。

頁面成功證據必須對應新啟動的根處理程序或已核實 SDK 子處理程序，並持續穩定。終端機、損毀或輔助視窗、其他裝置、無介面核心、隱藏/最小化/被系統遮蔽的視窗、錯誤父子關係及短暫控制代碼均不計為成功。成功、失敗或取消後都會釋放持有的啟動器資源。

這些測試結合合成流程和僅歸夾具所有的原生邊界，**不能證明**使用者 AVD 已完成端對端重新啟動、Android 已完成啟動或顯示驅動程式相容。詳見[測試指南](../../docs/TESTING.zh-TW.md)，不要提交結果檔案或測試包。
