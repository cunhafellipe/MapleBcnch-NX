# 驗證工具

這個資料夾是**驗收用的工具**，不是程式碼本身。
程式碼在 `MapleBench/` 與 `dependencies/`，這裡的東西只在你需要
「親眼確認結果」時才會用到。

為什麼需要它：這個專案被「API 回 200 但畫面是空的」騙過一次。
回傳值正確不等於畫面上有東西，也不等於資料真的寫進磁碟。

---

## 目錄

| 資料夾 | 內容 |
|---|---|
| `visual-check/` | 截圖 + 讀 canvas 像素，驗收 UI |
| `anatomy/` | 批量解剖地圖，產出 `docs/MAP-STRUCTURE.md` 的證據 |
| `probe/i18n/` | 翻譯完整性檢查（字典 vs 執行期呼叫點） |
| `probe/nx/` | NX 封存格式的探測與驗證 |
| `probe/check/` | 各項針對性檢查（EOL、BOM、區段語言切換…） |

---

## i18n 檢查 — 最常跑的一支

```bash
cd tools/probe/i18n
node i18n-inventory.js
```

它比對字典條目與執行期真的會呼叫到的字串，兩邊都查：

- **執行期有、字典沒有** → 使用者會看到英文（`UNTRANSLATED`）
- **字典有、執行期沒有** → 翻譯殘留，會讓下一個翻譯者以為已翻過（`unreachable entries`）

輸出檔寫在 `tools/probe/i18n-inventory.txt`。

> **2026-10-11 的實測**：字典 2330 條、執行期呼叫點 2142 個、
> **未翻譯 0、重複鍵 0、placeholder 不符 0**，exit code 0。

`i18n-coverage.js` 與 `i18n-missing-keys.js` 是它的薄包裝，
保留舊檔名是為了讓打舊指令的人拿到誠實答案而不是錯誤答案。

**這支工具的價值在於它曾經報錯過**：初版用 regex 抓 `t('a ' + 'b')`
只看得到第一段，把 112 條從沒渲染過的碎片回報成「已翻譯」。
現在改用 AST，並且連 `data-i18n-attr` 引用的 key 都會驗。

---

## 視覺驗收

```bash
# 1. 起伺服器
cd MapleBench/bin/Release/net10.0-windows
dotnet MapleBenchX.dll --port 5302 --no-warmup

# 2. 起 headless Chrome（另開終端機）
chrome --headless=new --remote-debugging-port=9222 \
       --user-data-dir=<臨時資料夾> --no-first-run --disable-gpu about:blank

# 3. 跑
cd ../../../tools/visual-check
node editor.mjs  http://127.0.0.1:5302 f1/Map/Map0/000000001.img canvas.png
node palette.mjs http://127.0.0.1:5302 pal
```

用 CDP 直接驅動，不依賴 puppeteer。**注意輸出路徑請用相對或純 ASCII**，
路徑含中文時 Node 的 argv 編碼會壞掉。

詳細用法與兩個選擇器的坑見 `visual-check/README.md`。

---

## 地圖解剖

```bash
cd tools/anatomy
node dissect.mjs http://127.0.0.1:5302 sample.json 10
```

會把每張抽樣地圖的圖層、tile set、物件前綴、foothold 拓撲、背景參數
全部記錄下來。`sample-10.json` 是 2026-10-11 的實測結果，
`docs/MAP-STRUCTURE.md` 的每一條規則都出自它。

**規格裡沒有的東西不要信，要自己去量。**

---

## 完整驗收

```bash
# 1. 起後端並開啟封存
curl -X POST http://127.0.0.1:5330/api/files/open \
     -H "Content-Type: application/json" \
     -d '{"path":"<Map.nx 的絕對路徑>"}'

# 2. 跑
cd tools/probe
node acceptance.mjs http://127.0.0.1:5330 acceptance.json
```

15 項檢查，四段：調色盤誠實性、`/validate` 與 `/batch`、寫入存檔、
批次歷史塌縮。

> **2026-10-11 實測：15/15 通過。**
> 調色盤 109/109 有像素、地圖 8 圖層、斷鏈偵測有效、
> 300 操作→歷史只加一筆、存檔 9.12 秒 verified。

**這支腳本抓過我的 bug**：第一次跑時 batch 那段因為地圖圖層是空的
而靜默跳過，卻報「11/11 通過」。改成「找不到可編輯節點就算失敗」之後，
才暴露出斷言寫錯（該比對批次前後的差異，不是絕對值 1）。
**會靜默跳過的測試比沒有測試更危險。**

---

## MCP 探測

```bash
node tools/probe/mcp-probe.mjs <path-to>/maple-mcp.exe
```

真的走 JSON-RPC 握手並列出工具數與工具名。
報告見 `docs/CLI-AND-MCP.md`。

---

## MCP 完整流程驗收

```bash
# 1. 啟動後端並開啟地圖封存（預設 5340）
# 2. 跑
node tools/probe/mcp-flow.mjs \
     MapleMcp/bin/Release/net10.0-windows/maple-mcp.exe \
     "<Map.nx 的絕對路徑>"
```

**只經 MCP**，全程不碰 HTTP API——這是「agent 真的能用」的證明，
而不是「端點回 200」的證明。

> **2026-10-11 實測：ALL PASSED**（14 項）
> capabilities → list → open → palette → place → validate
> → batch（300 操作、歷史 10→11）→ save

**這支腳本教會我的事**：探測 batch 時我寫的探針沒帶 `commit=true`，
於是讀回自己的預覽、找不到可寫節點，一度誤以為 MCP 壞了。
後來才發現真正的問題是**容器不能編輯、只能編輯葉節點**——
於是補了 `wz_map_node` 工具讓 agent 能自己找到葉節點。
**測試失敗三次，前兩次都是測試本身錯。**

---

## 為什麼工具放在這裡而不是各自散落

它們原本在 `追風WZ編輯器/nx-probe/`——一個和舊版專案混在一起的資料夾，
沒被任何 git 追蹤。搬家時只帶走**可重複執行的腳本**（48 支），
一次性輸出（`.txt` 清單、`__pycache__`、log）留在原地。

帶走的路徑全部改成相對於專案根，
所以這個資料夾可以整包移動而不會壞掉。