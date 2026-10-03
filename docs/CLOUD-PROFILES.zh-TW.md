# 雲端白名單設定共創

[English](CLOUD-PROFILES.md) | [简体中文](CLOUD-PROFILES.zh-CN.md) | **繁體中文**

Process Keeper 首次啟動始終使用**一套空的本機白名單設定**。雲端設定只是可選參考，不會背景取得、自動匯入或安裝缺少的軟體。

## 取得設定

進入**設定 → 備份 → 白名單設定 → 雲端白名單設定**，點選**從 GitHub 載入**，選擇檔案並檢查 JSON。相容介面另有**預覽雲端設定**按鈕。點選**匯入並套用**並確認後，將建立新設定並切換目前白名單，不會覆寫既有設定或關閉程式。重名時自動加入數字後綴。

雲端匯入也計入**最多五套本機設定**，首次空設定同樣占用一套。達到五套後，請先刪除不用的非目前設定。儲存時檢查檔案修訂版本，遇到並行修改會拒絕覆寫。完整設定備份包含雲端匯入的設定，只匯出白名單時僅包含目前規則。

應用程式從公開儲存庫 [KangQiovo/ProcessKeeper 的 community/profiles](https://github.com/KangQiovo/ProcessKeeper/tree/main/community/profiles) 讀取一般 JSON 檔案；官方應用程式中的儲存庫固定。請求不需登入，不會上傳本機規則或設定。網路異常、流量限制、格式錯誤及選擇後檔案發生變化皆會如實顯示。上限為 100 個 JSON 設定、200 個目錄項目、每個檔案 1 MiB。

可選的 [`kangqi-default.json`](https://github.com/KangQiovo/ProcessKeeper/blob/main/community/profiles/kangqi-default.json) 保留 Clash Verge、Codex、QQ、Steam、火絨、UU 遠端與 TranslucentTB 及符合的子處理程序，**不是正式套件的本機預設設定**。其他電腦沒有的軟體只會顯示未符合。依處理程序名稱比對的範圍可能較廣，匯入前請核對規則和白名單生效頁面。

## 提交共創設定

1. Fork 本專案，在 **`community/profiles/`** 下直接新增不重名的 `.json`，例如 `my-work-tools.json`。檔名使用簡短英文字母、數字、`-` 或 `_`；檔名會成為設定顯示名稱，副檔名前不超過 80 個字元。
2. 使用下方便攜格式。建議從白名單匯出中保留支援的 `Application` 身分或準確的 `ProcessName` 檔名，移除本機路徑、帳號資訊及無關設定。
3. 在設定編輯器檢查格式。說明各規則保護的軟體、是否需要比對子處理程序，以及實際驗證過的版本和系統。
4. 提交 Pull Request，包含設定檔及上述說明。所有使用者都可提案；維護者審核合併後，應用程式才能取得。提交提案不等於取得主儲存庫寫入權限。

```json
{
  "Version": 1,
  "Rules": [
    {
      "Id": "example-auradio",
      "Name": "Auradio",
      "Kind": "ProcessName",
      "Value": "Auradio.exe",
      "Enabled": true,
      "IncludeDescendants": false
    }
  ]
}
```

每條規則必須包含全部六個欄位，`Id` 在檔案內唯一；`Enabled` 和 `IncludeDescendants` 必須為 JSON 布林值。根節點僅接受 `Version` 和 `Rules`。未知或重複欄位、無效類型、不合法 UTF-8、超過 1,000 條規則或超過 1 MiB 皆會被拒絕。

雲端只接受 `Application` 的便攜 `known:` 身分、有效 `package:` 套件家族身分，以及 `ProcessName` 的完整可執行檔名。未知 `known:` 身分只會顯示未符合。不接受萬用字元、`ExecutablePath`、`Directory`、`Application` 的 `path:`、符號連結、子目錄或嵌入命令。含本機規則的完整備份不應提交到此公開目錄。更多隱私與審核要求見[貢獻指南](https://github.com/KangQiovo/ProcessKeeper/blob/main/CONTRIBUTING.zh-TW.md)。

共創規則是比對條件，不是可執行指令碼，也不代表建議安裝對應軟體。名稱歧義或過寬的子處理程序比對可能保留額外程序；發現問題歡迎提交 Issue 或修正 Pull Request。
