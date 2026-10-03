# 已簽名建議規則庫更新

[English](CATALOG-UPDATES.md) | [简体中文](CATALOG-UPDATES.zh-CN.md) | [繁體中文](CATALOG-UPDATES.zh-TW.md)

本功能更新的是**註冊名稱的建議標籤**，不是病毒掃描特徵庫。使用者關注名單和歷史公開報告仍分別說明；命中名稱不代表目前檔案有毒。不會上傳軟體清單、下載執行程式碼或自動解除安裝軟體。

## 使用方式

在**應用卸載 → 規則庫**查看版本、來源核對日期及官方儲存庫。**檢查規則更新**只下載與驗證，不改變標籤；**更新規則庫**經確認後才儲存新庫並重新標記。目前不自動連網檢查或套用更新，已在確認或執行中的批次卸載不會被更新改變。

內建68條產品規則、117個別名可離線使用。進入卸載頁時載入已驗證的新快取；快取損壞會顯示錯誤並保留可信規則，之後有效的更高修訂可以修復。網路、HTTP、逾時、簽名、格式及寫入錯誤不會被當成成功。

## 信任與限制

固定來源為 `https://raw.githubusercontent.com/KangQiovo/ProcessKeeper/main/catalog/v1/catalog.json` 及同目錄 `catalog.sig`。不跟隨重新導向，不停用系統 HTTPS 憑證驗證，設定匯入不能變更網址或信任公鑰。

JSON 上限512 KiB、500條規則、共2,000個別名，每條最多20個別名，每個別名160字元，每種語言的理由最多1,500字元。串流下載時即限制大小；所有物件拒絕重複、未知及缺少欄位，嚴格檢查UTF-8、深度、日期、名稱和證據網址。比對方式固定 `exact-name-v1`，不能下載正規表示式、指令、腳本或新引擎。

RSA-3072 / SHA-256 / PKCS#1 v1.5 簽名涵蓋原始JSON位元組，公鑰內建於客戶端；`public-key.xml`是公開參考。.NET Framework使用暫時CSP金鑰環境，不建立持久的使用者或系統金鑰容器。實作使用 [.NET RSA 驗證介面](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.rsa.verifydata)。

快取為單一 `verified.catalog`：`PKCAT001`、小端32位JSON長度、原始JSON與384位元組簽名。同目錄暫存檔寫入並刷新後原子替換；獨占寫鎖涵蓋重讀目前快取、比較修訂和替換。競爭寫入會立即失敗，保留舊檔案。

只套用更高修訂；同修訂異內容或較舊版本均拒絕。修正、撤回也必須發佈**更高修訂**，可在新修訂恢復舊內容。下限為內建修訂與目前可信狀態。管理員刪除/恢復全部本機狀態、更換應用程式或發佈者私鑰洩漏，不在絕對防回退保證內；連線被封鎖也不能證明沒有新版本。

## 格式與維護

頂層欄位固定為 `schema=1`、`repository=KangQiovo/ProcessKeeper`、遞增整數 `revision`、點分數字 `version`、UTC `publishedUtc`、日期 `checkedOn`、`matching=exact-name-v1` 及 `rules`。

每條規則含 `id`、`basis`（`watchlist`或`published-report`）、`names`、有 `en/zh-Hans/zh-Hant` 的 `reason`、`sourceUrl`、`sourcePublishedOn`。關注名單來源欄位為空；公開報告需要受允許的一手HTTPS來源，未知日期不可捏造。新增證據網域需發佈經審核的客戶端。只比對規範化完整名稱與有限數字版本/架構後綴。

1. 核對一手資料，保留歷史渠道和目前檔案之間的區別，更新三語理由。
2. 增加修訂號、版本和日期；撤回規則在更高修訂中移除。保留上一版公開檔案以比較。
3. 建置Core，私鑰離線保存在儲存庫外，以PowerShell 7執行：

```powershell
./scripts/Sign-Catalog.ps1 -PrivateKeyPath 'E:\offline-keys\catalog-signing.private.xml' -PreviousCatalogPath 'E:\review\previous-catalog.json'
```

4. 執行雙框架 `ProcessKeeper.CatalogUpdate.Tests`，驗證實際Git blob、內容差異及證據，將JSON與簽名一起提交。更新期間若兩次下載剛好取得不同版本，客戶端會拒絕，可稍後重試。
5. 簽名後不要格式化JSON。檔案採UTF-8無BOM及LF，`.gitattributes`保留原始位元組。禁止提交私鑰，應離線備份；遺失或輪換金鑰需發佈含新公鑰的審核版應用程式。

首版簽名庫已在原始碼中，需發佈至固定公開儲存庫後端點才可存取；此前HTTP 404會如實顯示。
