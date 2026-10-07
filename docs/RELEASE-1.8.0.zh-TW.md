# Process Keeper 1.8.0

[English](RELEASE-1.8.0.md) | [简体中文](RELEASE-1.8.0.zh-CN.md) | **繁體中文** | [首頁](../README.zh-TW.md)

正式版、介面版本、更新身分及標籤統一使用 **1.8.0** / `v1.8.0`。

## 本次更新

- **新設定預設關閉「隱藏微軟應用程式」。** 保留既有使用者選擇；首次白名單仍為空。啟用篩選僅改變顯示，不授予關閉、修改啟動項目或解除安裝權限。
- 擴充微軟歸屬驗證，涵蓋健康且非 development mode 的 OS 登錄 Store/System 套件，包括沒有獨立 EXE 的套件；其他符合條件的登錄套件需要通過獨立原生套件簽章驗證。開發人員首頁、HEVC 擴充等項目不再只依賴 EXE，仍不能憑熟悉名稱、複製檔案或發行者文字取得歸屬。
- 第三方軟體的原始發行者與微軟歸屬衝突時，即使包含微軟元件，也保留整項可見。
- 透過精確 MSI 捷徑/元件身分、登錄圖示目錄與產品/公司證據、支援的 Burn/PEP514 執行階段登錄關聯重複安裝記錄。Apple Software Update、Autodesk Access、Python 現在各顯示一個軟體父項目，保留原始記錄及元件。
- 預設定位實際登錄的主程式。MSI 廣告捷徑的圖示檔案保留為元件；Python 快取安裝程式標為 Helper，不再搶占主程式。缺少精確登錄證據時，通用 Python 宿主保護保持有效。
- 暫停即時程序收集後，已請求的發行者驗證仍可完成並更新篩選；有界背景重試不會重新啟用即時收集。
- 原生標題列控制項跟隨應用程式佈景主題，自訂原生背景保留文字可讀性。繼續保留官方來源優先下載、手動更新並重新啟動、設定備份及材質調整。

## 下載選擇

| 檔案 | 適用環境 |
| --- | --- |
| [ProcessKeeper-v1.8.0.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/ProcessKeeper-v1.8.0.exe) | 免安裝整合包；包含全部三種執行路線，下載最大；PK14 供舊更新程式使用。 |
| [ProcessKeeper-v1.8.0-win7-x86-compat.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/ProcessKeeper-v1.8.0-win7-x86-compat.exe) | 相容免安裝；Windows 7 SP1 / 8.1 及支援的新系統 x86/x64；WPF，需要 .NET Framework 4.6.2 或相容的較新 4.x。 |
| [ProcessKeeper-v1.8.0-win7-x86-compat-setup.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/ProcessKeeper-v1.8.0-win7-x86-compat-setup.exe) | 相容安裝版，附解除安裝程式；執行需求與對應免安裝版相同。 |
| [ProcessKeeper-v1.8.0-win10-x86-x64.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/ProcessKeeper-v1.8.0-win10-x86-x64.exe) | Windows 10/11 x86/x64 免安裝；x64 19041+ 使用現代 WinUI，內含 x86 WPF 供支援系統降級使用。 |
| [ProcessKeeper-v1.8.0-win10-x86-x64-setup.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/ProcessKeeper-v1.8.0-win10-x86-x64-setup.exe) | x86/x64 安裝版，附解除安裝程式與兩種執行路線。 |
| [ProcessKeeper-v1.8.0-win10-arm64.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/ProcessKeeper-v1.8.0-win10-arm64.exe) | 原生 ARM64 免安裝；Windows 10 19041+ / Windows 11 ARM64；x86 引導程式透過 Windows 模擬執行。 |
| [ProcessKeeper-v1.8.0-win10-arm64-setup.exe](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/ProcessKeeper-v1.8.0-win10-arm64-setup.exe) | ARM64 安裝版，附解除安裝程式；執行需求與對應免安裝版相同。 |

請使用隨附的 [SHA256SUMS](https://github.com/KangQiovo/ProcessKeeper/releases/download/v1.8.0/SHA256SUMS) 驗證下載。發行 EXE 尚未簽章，Windows 可能顯示**未知發行者**。

**1.6.x 應用內升級請選擇整合包 EXE。** 舊更新程式支援 PK14；PK17 分包及安裝程式需新版更新程式或手動下載。目前更新程式優先符合已驗證的版本類型與免安裝/安裝方式。更新需主動操作並確認，沒有自動倒數或無訊息安裝；安裝版更新開啟標準安裝精靈。

## 驗證範圍與實際界線

現代 Windows 主機的唯讀收集涵蓋全部 **103 個微軟套件 UI 候選**、**27 筆微軟發行者桌面記錄形成的 18 個父項目**，以及**含微軟元件的 14 筆第三方記錄形成的 13 個父項目**；微軟候選被篩除，第三方父項目保持可見。已安裝顯示由 **323 筆記錄整理為 280 個父項目**，保留每筆原始身分與 **1469 個不同 EXE 路徑**。這是實測樣本，不代表能發現或隱藏所有系統上的全部微軟項目。回歸涵蓋精確登錄、負例、元件角色及兩套介面，見[測試指南](TESTING.zh-TW.md)。

同一樣本中的 **165 筆微軟相關傳統解除安裝登錄**，僅 **5 筆**具備足夠應用程式證據用於隱藏；**134 筆**沒有可讀應用程式 EXE/證據，**26 筆**只有圖示或安裝程式證據，繼續顯示。未知、不健康、development mode、不可讀或仍等候驗證的歸屬，不憑產品名稱推斷。

同名不等於同一產品。不同 Windows 套件 family 保留不同身分；沒有精確外部目錄/元件關聯的稀疏套件與 Win32 記錄保持獨立。共用宿主、不同執行階段環境及無關工作也不依名稱合併。分組保留關閉、修改啟動項目及解除安裝時的精確原始目標。

庫存受可讀登錄、支援的提供者及檔案掃描上限約束；權限不足、未載入使用者登錄、提供者延遲及特殊可攜軟體仍有限制。**Windows 7 / 8.1 / 10、ARM64 目標裝置上的安裝、升級及執行尚未通過真實裝置認證。** 現代主機的 x86 測試及交叉編譯不能取代。相容介面缺少 WinRT 套件庫存/啟動宣告、瀏覽器即時預覽及 x64 AVD 環境還原；ARM64 也停用 x64 專用 AVD 讀取器。見[相容指南](COMPATIBILITY.zh-TW.md)。
