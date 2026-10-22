# 修正：模擬器每次都要重新選地圖

時間：2026-10-14 14:09（追加，不覆蓋）

## 問題
`Simulator.open()` 只做 `refresh()`（列出地圖），不會自動載入任何一張。
所以每次進模擬器都是空白畫面，然後要從 5,366 張地圖裡手動挑 `000000002.img`。

## 修法
`simulator.js` 記住「上次走的那張圖」，下次自動載入。

- key 帶上目前開啟的 archive id（`app.currentPath`），所以換了 WZ 不會記錯別的檔案裡的地圖。
- 記在 `walk()` 而不是 picker 的 onchange，因為 `setDirection` 會重新走同一張圖，
  而 `walk()` 才是「這張圖正在被顯示」的唯一位置。
- localStorage 不可用（無痕模式）時直接略過，不影響功能。
- 記住的 path 解析不到時靜默退回選擇器，不提示 —— archive 可能被換掉了。

## 驗證
1. 首次開：記憶為空，`selected: null`（沒記憶就不強行載入）
2. 手動挑 `000000002.img` -> localStorage 寫入 `maplebench.simulator.lastMap.f1` = `f16/Map/Map0/000000002.img`
3. 重載頁面、不碰任何東西、直接開模擬器 -> `selected: "f16/Map/Map0/000000002.img"` 通過

## 一個我自己造成的 bug
第一版 `mapMemoryKey()` 憑空猜了 `app.currentFileId` / `app.fileId`，
兩個都不存在 -> key 永遠 null -> 功能是死的但沒有報錯。
實際欄位是 `app.currentPath`。已修。
