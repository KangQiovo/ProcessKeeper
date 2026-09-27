# 受控啟動器測試夾具

[English](README.md) | [简体中文](README.zh-CN.md) | **繁體中文**

此專案驗證受控 ZIP/快取啟動器契約及權限提升協調，**不是完整的原生通用啟動器或更新助手測試套件**。原生建置與夾具步驟見[建置指南](../../docs/BUILDING.zh-TW.md)。

在 Windows 上使用 .NET 8 SDK，從儲存庫根目錄以 PowerShell 7 執行，並選擇可捨棄的夾具位置：

```powershell
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('processkeeper-launcher-tests-' + [guid]::NewGuid().ToString('N'))
dotnet run --project src/ProcessKeeper.Launcher.Tests/ProcessKeeper.Launcher.Tests.csproj -c Release -- $fixtureRoot
```

測試建立隔離檔案、真實具名管道和僅歸夾具所有的無操作生命週期輔助處理程序。它不執行載荷、不啟動 Process Keeper、不請求真實 UAC、不建立正式 ProgramData 快取，也不關閉既有使用者程式。對正式 ProgramData 上層擁有者和 ACL 的驗證僅為唯讀。

## 涵蓋範圍

- 壓縮檔路徑穿越、大小寫衝突、檔案/目錄衝突，以及資訊清單或檔案雜湊不符。
- 快取鎖定、持有檔案控制代碼、連結替換、舊快取隔離、擁有者及 ACL 限制。
- 管道用戶端身分、處理程序生命週期及不可預測的協調權杖。
- 注入式權限提升結果：系統管理員直接啟動、請求授權、取消、失敗和重試；啟動錯誤不能計為成功。

以實際主控台結果和結束代碼為準，不規定固定斷言總數。無權建立符號連結的環境會對相關檢查回報 `SKIP`，結果中須保留這些略過資訊。夾具保留供檢查，不得提交至儲存庫。

選用建置允許透過 `TestPayloadZip` 與 `TestPayloadManifest` 嵌入合成 ZIP 和資訊清單；此類檢查只解壓縮比對，不執行其中的主程式。NativeAOT 發布另需相應 C++ 工具和 Windows SDK。

## 未涵蓋範圍

這些不提升權限的夾具不能驗證真實 UAC 同意、首次建立受保護正式快取或完整使用者更新，應在受控手動環境中另行記錄。請參閱[測試指南](../../docs/TESTING.zh-TW.md)，不要上傳測試 EXE 或夾具程式包。
