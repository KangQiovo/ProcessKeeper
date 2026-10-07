# Process Keeper 1.8v2

[English](RELEASE-1.8.1.md) | [简体中文](RELEASE-1.8.1.zh-CN.md) | **繁體中文** | [首頁](../README.zh-TW.md)

應用程式與發佈標題顯示 **1.8v2**。更新身分、EXE 版本、標籤與檔名使用 **1.8.1** / `v1.8.1`，確保現有 1.8.0 用戶端能識別新版。

## 變更

- 更新檔案選項只列出已識別的正式應用套件；`SHA256SUMS`、文件、壓縮檔與助手程式保留於發佈資料，不再作為應用更新選項。預設仍符合已驗證的目前版本類型及安裝/免安裝方式。
- 勾選自動檢查立即儲存，並於初始化就緒後開始檢查；後續啟動會背景檢查。新版對話框可在任何頁面出現，無須先開啟關於頁。其他對話框忙碌時等待，結束時取消，未成功顯示不會誤記為已提醒。
- 選擇不同套件類型時顯示「繼續」，先關閉檔案選擇對話框，再個別確認目前與所選類型。取消不會開始下載。
- 新版啟動後於背景執行有時限的舊載荷清理，包含新版啟動後才登錄的舊版本清理工作，修正原先需等下次啟動的問題。目前載荷、仍在執行或被占用的助手、未知檔案與不安全路徑會保留。
- 指向同一受管理 EXE 的既有桌面捷徑仍於替換後更新；不會掃描刪除其他目錄的下載副本，也不會改寫無關捷徑。手動放在其他目錄的舊副本需另行處理。使用者設定與白名單保留。
- 更新進度視窗的原生標題列跟隨應用主題，保留 Windows 高對比配色；核對背景更新程式資源與實際視窗使用原有帶下載箭頭圖示。

延續 1.8 的首次使用預設值：**不勾選隱藏微軟應用程式，本機白名單為空**；已儲存的使用者設定不變。微軟身分驗證、應用合併與主程式識別見 [1.8 發佈說明](RELEASE-1.8.0.zh-TW.md)。

## 下載檔案

| 檔案 | 用途 |
| --- | --- |
| [ProcessKeeper-v1.8.1.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/ProcessKeeper-v1.8.1.exe) | 免安裝整合包；全部三種載荷，體積最大；PK14 相容舊更新程式。 |
| [ProcessKeeper-v1.8.1-win7-x86-compat.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/ProcessKeeper-v1.8.1-win7-x86-compat.exe) | 相容免安裝；Win7 SP1 / 8.1 與受支援的較新 x86/x64 系統；WPF；需 .NET Framework 4.6.2 或相容的新 4.x。 |
| [ProcessKeeper-v1.8.1-win7-x86-compat-setup.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/ProcessKeeper-v1.8.1-win7-x86-compat-setup.exe) | 附解除安裝程式的相容安裝版；執行要求相同。 |
| [ProcessKeeper-v1.8.1-win10-x86-x64.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/ProcessKeeper-v1.8.1-win10-x86-x64.exe) | Win10/11 x86、x64 免安裝；x64 19041+ 使用 WinUI，內建受支援系統的 WPF 降級載荷。 |
| [ProcessKeeper-v1.8.1-win10-x86-x64-setup.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/ProcessKeeper-v1.8.1-win10-x86-x64-setup.exe) | 附解除安裝程式的 x86/x64 安裝版；兩種載荷。 |
| [ProcessKeeper-v1.8.1-win10-arm64.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/ProcessKeeper-v1.8.1-win10-arm64.exe) | 原生 ARM64 免安裝；Win10 19041+ / Win11 ARM64；x86 引導器由系統模擬執行。 |
| [ProcessKeeper-v1.8.1-win10-arm64-setup.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/ProcessKeeper-v1.8.1-win10-arm64-setup.exe) | 附解除安裝程式的 ARM64 安裝版；執行要求相同。 |

使用 [SHA256SUMS](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.1/SHA256SUMS) 核對檔案。程式尚未簽章，Windows 可能顯示**未知的發行者**。

**1.6.x 應用內更新請選免安裝整合包。** PK17 分包與安裝程式需要較新的更新程式或手動下載。更新須由使用者點選並確認；沒有自動倒數或無聲安裝。安裝版更新開啟標準安裝精靈。

## 驗證與已知限制

回歸使用實際自有 WinUI/WPF 測試視窗、隔離設定/下載檔案、注入的網路/安裝邊界與不執行產品安裝的原生載荷樣本，涵蓋未進入關於頁的啟動、勾選儲存、全域對話框、檔案篩選、切換套件取消、進度/繼續下載與受保護的舊載荷清理。完整建置與實際套件檢查核對架構、清單、檔案雜湊、正式渠道及空白預設規則。見[測試說明](TESTING.zh-TW.md)。

這些檢查不代表完成 Win7 / 8.1 / 10 或 ARM64 真機安裝、更新與執行認證。相容介面缺少 WinRT 套件清單/啟動宣告、瀏覽器即時預覽及 x64 AVD 重建；ARM64 也停用 x64 專用 AVD 讀取器。見[相容說明](COMPATIBILITY.zh-TW.md)。清理僅處理身分驗證的所屬檔案，忙碌或無法驗證的目錄可能保留。歡迎透過 [Issues](https://github.com/KangQiovo/ProcessKeeper/issues) 提供重現資訊。
