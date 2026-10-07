# 安裝器打包

[English](README.md) | [简体中文](README.zh-CN.md) | **繁體中文** | [首頁](../README.zh-TW.md)

三個原生 NSIS 安裝器分別包含對應的官方免安裝 EXE，安裝為固定名稱的 `ProcessKeeper.exe`，並提供原生 `Uninstall.exe` 與歸屬標記。安裝過程不執行 .NET，也不下載另一套安裝器。

| 安裝包 | 適用路線 |
| --- | --- |
| `ProcessKeeper-v1.8.1-win7-x86-compat-setup.exe` | Intel/AMD Windows 7 SP1 / 8.1 / 10 及更新系統，包括 x86 裝置；相容介面 |
| `ProcessKeeper-v1.8.1-win10-x86-x64-setup.exe` | Intel/AMD Windows 10 及更新系統；支援時使用 x64 現代介面，否則使用 x86 相容路線 |
| `ProcessKeeper-v1.8.1-win10-arm64-setup.exe` | 原生 ARM64 Windows 10 build 19041+ / Windows 11；ARM64 現代介面 |

安裝器與解除安裝器皆為原生 x86 Unicode 程式。ARM64 Windows 使用 x86 模擬執行安裝器與啟動器，現代應用負載仍為 ARM64。缺少 Framework 時仍由應用啟動器提供明確的復原指引。以上為實作目標，尚未完成真實 Windows 7/8.1/10 與 ARM64 安裝解除安裝認證。

## 編譯

從 [官方下載頁](https://nsis.sourceforge.io/Download) 取得 **NSIS 3.13 ZIP**。本次使用的官方 SourceForge 壓縮包 SHA-256 為 `BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8`。解壓至自己的工具目錄即可，無須安裝編譯器。打包指令碼檢查 `v3.13` 版本、記錄編譯器雜湊並停用外部 `nsisconf.nsh` 設定。安裝器採用 zlib 壓縮，上游授權完整保留於 [NSIS.3.13-LICENSE.txt](../licenses/NSIS.3.13-LICENSE.txt)。

先依 [建置文件](../docs/BUILDING.zh-TW.md) 產生對應的免安裝包。其旁邊的 `.package.json` 必須符合實際 EXE、目前原始碼、版本路線與發佈通道。在儲存庫根目錄使用 PowerShell 7：

```powershell
./src/ProcessKeeper.UniversalLauncher/build-native.ps1 -SetupGuardOnly `
  -OutputPath ./artifacts/ProcessKeeper.SetupGuard.dll `
  -BuildDirectory ./artifacts/setup-guard
if ($LASTEXITCODE -ne 0) { throw 'Setup guard build failed.' }
./scripts/package-installers.ps1 -PackageTarget Windows10x64 `
  -PortablePath ./artifacts/ProcessKeeper-v1.8.1-win10-x86-x64.exe `
  -OutputPath ./artifacts/ProcessKeeper-v1.8.1-win10-x86-x64-setup.exe `
  -NsisCompiler E:/Tools/nsis-3.13/makensis.exe `
  -SetupGuardPath ./artifacts/ProcessKeeper.SetupGuard.dll -StableRelease
```

其餘安裝器分別使用 `Windows7Compat`、`Windows10arm64`。僅在輸入也是預覽包時省略 `-StableRelease`；編譯最佳化模式不代表可更改發佈通道。`-ValidateOnly` 只檢查輸入，不編譯或產生安裝器。輸出檔案必須不存在；連結路徑、證據不符或編譯過程中原始碼改變皆會拒絕。編譯不會執行安裝、解除安裝或內含應用。

## 安裝檔案與更新約定

安裝器要求管理員權限，各版本統一在 **32 位元登錄檔檢視** 的 `HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\ProcessKeeper` 登記整機安裝。相容包與 x86 主機預設使用 Program Files (x86)，現代包在 64 位元主機上使用原生 Program Files 目錄。目錄選擇器可改為其他獨立本機目錄；磁碟根目錄、Windows 目錄、連結路徑與存在無關同名程式的目錄會被拒絕。

精確歸屬約定包含 `InstallLocation`、帶引號的 `UninstallString`、固定 `ProcessKeeperRepository=KangQiovo/ProcessKeeper`、`ProcessKeeperPackageTarget` 與 DWORD `ProcessKeeperInstallerContract=1`。`install.ini` 的 `Installation` 區段重複儲存儲存庫、路線與約定。`DisplayName` 始終為 **Process Keeper**，`Publisher` 為 **KangQi**，`DisplayIcon` 指向安裝的 EXE。這些中繼資料不是數位簽章，UAC 仍可能顯示未知發行者。

應用內更新替換原路徑的固定 EXE，保留解除安裝器與標記，並且只在新應用確認就緒、通過歸屬檢查後更新既有解除安裝登記的版本。同路線重裝沿用已驗證目錄。更換路線或移動安裝位置時，先解除安裝舊版本；個人設定保留。內嵌原生守衛按原始安裝檔案路徑檢查受保護工作階段，包括快取中的實際介面與待處理更新助手。發現活動工作階段或無法核實歸屬時停止操作，不終止處理程序；守衛不作為額外檔案安裝。

安裝先暫存並校驗新 EXE 雜湊，再將三個精確舊檔案移入獨立備份目錄。檔案或登錄寫入失敗時嘗試有限復原，未復原的備份於詳細資訊中明確保留。解除安裝亦先暫存三個原檔案，再移除所屬登錄。此機制降低部分替換風險，但不保證斷電後的原子復原；核實安裝狀態前請保留殘留備份。

安裝器建立所有使用者的開始功能表捷徑，以及安裝使用者真實桌面的 `Process Keeper.lnk`。無關的同名捷徑會保留。更新與解除安裝要求精確應用目標及空參數，不搜尋或刪除任意捷徑。

## 解除安裝與個人資料

透過 Windows 程式 / 已安裝應用或安裝目錄中的 `Uninstall.exe` 解除安裝。保留原生確認頁面，不登記無訊息解除安裝命令。請先退出 Process Keeper，並完成或取消待處理更新。解除安裝器核對精確目錄、登錄檔與標記，拒絕連結和重新剖析路徑，接著僅移除 `ProcessKeeper.exe`、`Uninstall.exe`、`install.ini`、歸屬明確的捷徑與固定登記。只移除空目錄，絕不遞迴刪除。

個人設定、記錄、自啟動復原備份與受保護解壓快取 **預設保留**。刪除應用檔案不會還原先前修改的自啟動設定。依 [免安裝檔案與清理](../README.zh-TW.md#免安裝檔案與清理) 可自行清理本使用者資料與 SID 目錄；其他使用者資料和無關檔案必須保留。

原始碼、PE 與編譯器檢查使用隔離的無實際功能素材，沒有進行真實安裝或解除安裝。磁碟中斷、權限、UAC 與舊系統行為仍需在可捨棄環境中測試。不要上傳編譯器壓縮包、測試安裝器、記錄或個人狀態。
