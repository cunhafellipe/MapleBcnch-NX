# 後續任務規劃

依據 `JOURNAL/` 的實測結果。排序依「阻塞什麼」排，不是依「好不好做」排。

> **2026-10-11 更新**：地圖生成的目標線另見
> [PLAN-2026-10-11.md](PLAN-2026-10-11.md) 與
> [2026-10-11-generation.md](2026-10-11-generation.md)。
> 本檔記的是修 bug 的那條線；那兩份記的是長新能力的線。
>
> **模擬器線**另見 [PLAN-SIMULATOR.md](PLAN-SIMULATOR.md) 與
> [2026-10-11-simulator.md](2026-10-11-simulator.md)。

---

## 模擬器：階段 A 完成，基礎可信

`/api/simulate/character` 用 MapleNecrocer 的模型（角色持有 foothold、
沿角度走、到端點換段）走完整張地圖，雙向。

**階段 A 驗收**（2026-10-12）：

| 項目 | 結果 |
|---|---|
| A1 100 張 × 雙向 | 200 次行走，零錯誤，最大偏差 **3px**，5.5 秒 |
| A2 跳躍與落下 | 路徑可執行且回報正確 |
| A3 納入 `acceptance.mjs` | **23/23 全部通過**（新增 8 項模擬器檢查） |

| # | 任務 | 狀態 |
|---|---|---|
| S-0 | 決定要不要真的做模擬器 | ✅ 選 B |
| S-2 | 鏈斷在哪個 foothold | ✅ |
| S-7 | foothold 模型重寫 | ✅ |
| S-10 | 步行速度真實值 | ✅ `+=1.5` / 上限 `2.5` / 減速 `0.25` |
| S-11 | 方向參數 | ✅ 雙向驗證 |
| S-12 | 牆的 Z 層比對 | ✅ `MapleCharacter.cs:857` |
| S-13 | 接進 UI 顯示軌跡 | ✅ 含方向切換 |
| **S-8** | **跳躍與落下** | ✅ **已加入並驗證**（`jumpEvery` / `dropAtStep`） |
| S-4 | 怪物 AI | ❌ 未做 |
| S-5 | 逐幀模擬迴圈 | ❌ **不做**，理由見 PLAN-MASTER §5 |
| S-6 | 梯子與傳送門 | ❌ 未做 |
| S-9 | 視覺呈現角色本身 | ❌ 未做（只有軌跡線） |

**下一步：階段 B**（梯子、傳送門、怪物 AI），順序見
[PLAN-MASTER.md](PLAN-MASTER.md) §2。

**不要去修 `FindBelow`**——它現在是精確的，只需要 1px 的落地容差。

---

## 🔴 阻塞中：副本與主目錄都未提交

| # | 任務 | 狀態 |
|---|---|---|
| W-1 | 副本 `MapleBcnch-NX-audit` 的 6 新增 5 修改 | 🔴 未 commit |
| W-2 | 主目錄 4 個資料摧毀缺陷修正 | 🔴 已修好、已驗證，**未 commit** |
| W-3 | 推送前確認 | ⏸ 本地領先遠端 10 個 commit，**未 push** |

**W-2 是推送前必須解決的**——遠端那份程式碼還帶著這兩個會弄丞資料的 bug：

- 匯出會從開啟中的封存刪掉 UOL / `_outlink` 內容
- `wz_archive_split` 的 dry run 會關掉使用者開著的封存

---

---

## 進行中：地圖生成（Agent 驅動）

| # | 任務 | 狀態 |
|---|---|---|
| P0-1 | `PaletteSets` 像素驗證（`drawableEntries`） | ✅ 109/109 |
| P0-2 | `/validate` 地圖規則檢查 | ✅ 實測能抓斷鏈與無效引用 |
| P1-1 | `/batch` 批量端點（單一 undo） | ✅ 300 操作 → 歷史只加一筆 |
| P1-2 | **foothold 生成器**（從輪廓算鏈） | 🔴 **下一個做這個** |
| P2-1 | 範本機制 | ⏸ 原料已齊（5366 張地圖） |
| P2-2 | **MCP 地圖伺服器** | ✅ **已完成** — `MapleMcp`，10 工具，實測 ALL PASSED |

**為什麼 P1-2 是下一個**：實測發現 `/auto-foothold` 依賴素材自帶的 Convex 幾何，
而真實的 `Map.nx` 裡**沒有任何素材帶這個東西**（掃 25 個 Tile set、12 個 Obj set，
全部 0）。所以鋪完地面之後**沒有辦法自動產生可走的 foothold**——
地圖看起來對，角色卻站不住。這是「能看」與「能玩」的差別。

做法：從已鋪的圖塊輪廓推導鏈條，並保證 `prev`/`next` 零斷裂
（實測所有既有地圖的斷鏈數都是 0）。

---

## P0 — 阻塞使用者的問題

### 1. 放置圖塊後存檔會殺掉行程 — ✅ 已解決（2026-10-11 實測通過）

**驗收流程全部跑完，結果全部通過**：

| 步驟 | 結果 |
|---|---|
| 開 `Map.nx`（679 MB） | 0.5 秒 |
| 開地圖 `Map0/000000001.img` | 成功 |
| 放置 tile #0（`ancientForest/bsc/0` @ -300,-200） | `dirty: true` |
| **第一次存檔** | 9.89 秒、`verified: true`、`historyKept: true`、`reopenMs: 78` |
| 放置 tile #1（`ancientForest/bsc/1` @ -250,-200） | `dirty: true` |
| **第二次存檔**（上一次會死的那一步） | 10.75 秒、`verified: true`、`historyKept: true`、`reopenMs: 83` |
| **重開行程冷讀回磁碟** | `ts=ancientForest`，兩個圖塊都在，座標與素材正確 |

檔案 711,843,272 → 711,927,510 bytes，備份鏈完整（兩份 `.bak`），log 零錯誤。
存檔當下 working set 峰值 14.76 GB（第二次），存檔後回落到 3.06 GB。

**真正的修正是什麼**：不是「死鎖」。`ReopenNx` 的轉換本來就跑在 gate 外，
而 .NET 的 `Monitor` 在同執行緒可重入——所以把 reopen 區塊移出 gate 區塊
**不是**修好死鎖的原因，而是修好了一個「不必要地持有 gate 做 679 MB 轉換」的
結構問題。修好之後**卡死消失、存檔變快**，但原本記載的「行程消失」並沒有
重現——連續兩次存檔都穩定通過。

也就是說：先前記載的「放置後存檔會殺掉行程」在目前這份建置上**無法重現**。
真正做對的是把 reopen 移出 gate（結構正確 + 不佔著 gate），
而不是先前結論所說的死鎖。

**剩下的真實問題**：記憶體峰值 14.76 GB 太靠近邊界了。見 P2-4。

**為什麼曾是 P0**：使用者放了一堆圖塊再存檔，舊版行為是行程死掉。
現在連續兩次存檔 + 冷重啟重讀都驗過了，這條可以關掉。

---

### 2. 視覺確認

**現況**：✅ **已用眼睛驗過，截圖存於 `JOURNAL/shots/`**（2026-10-11）。

驗收方式：用 Chrome headless + CDP 直接驅動頁面（`_t2/editor.mjs`、`_t2/palette.mjs`），
不走 puppeteer——這樣可以在同一支腳本裡同時取得**截圖**與**DOM 事實**，
不會又變成只驗 API 回傳值。

| 項目 | 結果 | 證據 |
|---|---|---|
| 素材調色盤格子有圖 | ✅ 109 個 tile set 全部有縮圖 | `shots/2026-10-11-palette-en.png` |
| 圖磚 / 物件 / 背景三個分頁 | ✅ API 各回 109 / 112 / 112 組 | 同上截圖（Tiles 分頁） |
| 地圖畫布有渲染 | ✅ 8 個圖層、小地圖、物件全部畫出來 | `shots/2026-10-11-map-canvas.png` |
| 語言切換後文字跟著換 | ✅ 繁中↔英文即時切換，不需重載 | 兩張截圖對照 |
| 圖層 0 顯示 `2t · 12o` | ✅ 正是存檔的那兩個圖塊 | `map-canvas.png` |

**畫布渲染驗證方式**：不只看截圖，也直接讀 canvas 像素——
`paintedPct: [100, 100]`（主畫布 1066×779、小地圖 208×140 皆已上色）。
空白畫布和有圖的畫布在截圖上肉眼難分，讀像素不會騙人。

**為什麼曾是 P0**：使用者唯一的驗收標準就是畫面。這條關掉。

### 下一步怎麼重跑這套視覺驗證

```bash
# 1. 起伺服器（先關掉舊的 dotnet）
dotnet MapleBenchX.dll --port 5302 --no-warmup
# 2. 起 Chrome（headless + CDP）
chrome --headless=new --remote-debugging-port=9222 --user-data-dir=<prof>
# 3. 跑
node editor.mjs  http://127.0.0.1:5302 f1/Map/Map0/000000001.img canvas.png
node palette.mjs http://127.0.0.1:5302 pal
```

腳本放在 `agent-wz/_t2/`（未進 git，屬臨時驗證工具）。
若要長期保留，應該移進 `tools/` 並在 README 記錄用法。

---

## P1 — 素材功能還沒測到的部分

以下都因為 `Mob.nx`（857 MB）開啟會拖垮行程而未驗證：

| 功能 | 端點 | 狀態 |
|---|---|---|
| mob / npc 素材 | `GET /palette/life` | ✅ **可用**（`namesAvailable: true`、圖示有出來）。<br>注意：`q` 必須帶查詢詞，空字串與過濾詞回 0 筆，`q=a` 才回得出來 |
| reactor 素材 | `GET /palette/reactors` | ✅ **可用**，`available: true`，回傳真實 reactor 清單與 iconPath |
| minimap 規劃 | `GET /minimap/plan` | ✅ **可用**，回傳 5803×790 畫布、mag 4、sharer 掃描完成 |
| minimap 重新產生 | `POST /minimap/regenerate` | 未測（會寫檔，需另開一份素材做） |
| foothold 鏈 | `POST /foothold-chain` | 未測（需要 ≥2 個點的 request body） |
| portal 放置 | `POST /place` kind=portal | 未測 |

### 「開多個大封存會讓行程消失」— 在目前建置上沒有重現

一次開啟 `Map.nx`（679 MB）+ `Npc.nx` + `Reactor.nx` + `String.nx` + `Character.nx`（186 MB）：

```
Map.nx        0.5s   ws=0.27GB
Npc.nx        0.0s   ws=0.28GB
Reactor.nx    0.0s   ws=0.28GB
String.nx     0.1s   ws=3.05GB
Character.nx  0.3s   ws=3.48GB
```

行程全程存活，工作集 3.48 GB。`Mob.nx`（818 MB）**沒有測**——
本機當時只剩 19.6 GB 閒置，而存檔峰值已知會到 14.76 GB（見 P0-1），
硬測可能只是把測試機推進 OOM，不能拿來判定程式對錯。

**結論**：P0-1 修好之後，「開大封存會死」這個症狀消失了。
先前記載的 Mob.nx 拖垮行程，很可能就是同一個問題的表現。

**驗收**：每一項都能在 NX 上跑通並持久化。

---

## P2 — 已知但未處理

### 3. NX 與 .wz 同時開啟只取第一種表示法 — ❓ **需要產品決定**

`MapEditorService.MapRoots` 在找到第一種表示法後 `yield break`。
同一個 session 開了 `Map.nx` 和 `Map.wz`，`.wz` 兄弟檔會被跳過。

**為什麼還沒動**：這是需要產品決定的行為，不是單純的 bug——
合併兩個客戶端的同名素材可能正是使用者要避免的。

**需要的決定**：NX 與 .wz 同時開啟時，應該合併、還是維持只取優先序較高者？
若是合併，`Source` 欄位要能顯示「這份來自哪個封存」。

### 4. NX 畫布在 session 佔 3.1 GB

開一張地圖後 working set 到 3.1 GB，沒有 OOM 但離邊界不遠。
`MemoryPressureService` 有現成的機制，但 NX 路徑有沒有接上尚未確認。

---

## 接手複驗（2026-10-11，`ec3e03c`）

專案搬到 `MapleBcnch-NX`。以下每項都重跑過，**不是照抄 HANDOVER 的說法**。

| 項目 | 結果 |
|---|---|
| 調色盤修復（`EnumerateChildren`/`PropertiesOf`） | 在，5 處 |
| `ReopenNx` 在 session gate **之外** | 是（呼叫在 1456，gate 在 1353）← 上一輪死結已解 |
| `file.Name` 不再被覆寫成空字串 | 是 ← 上一輪我改壞的那個 |
| `acceptance.mjs` | **15/15 PASS**，`save verifies — 12.72s reopenMs=85` |
| 冷啟動持久化 | 放 1 塊 → 存檔 `verified:true` → 重啟 → 讀回**剛好 1 塊**，位置素材正確 |

### 我又踩了 §4.1 的坑

第一次複驗看到 layer 0 有**兩個**圖塊、座標素材完全相同，
我判定是重複寫入 bug。**錯了**——`acceptance.mjs` 每次都在同一座標放一塊，
我跑了兩遍。**測試沒有冪等，不是程式有問題。**

---

## 新發現，尚未處理

### 9. `GET /api/mapedit/maps` 會讓行程消失 🔴

列出 5366 張地圖時行程沒了，回應為空。

**注意這與交接文件矛盾**：`HANDOVER.md` §2.3 把「列舉地圖（5366 張，含中文名稱）」
列為 agent 做得到的能力，`acceptance.mjs` 也通過。但我在乾淨的行程上
單獨打這個端點，行程消失。

可能是**累積狀態**而非端點本身：接手時我已經在同一個 session 裡開了
`Map.nx` + `Npc.nx` + `Etc.nx`，再列 5366 張。
也可能是端點本身。**兩者都還沒排除。**

- 記憶體：63.9 GB 中閒置 21.9 GB，**不是 OOM**
- log：無任何錯誤
- 表現與 P0-1 同類（大量工作 → 無輸出、行程消失），但成因不同

**懷疑**：`ListMapsUnderGate` 在 session gate 內枚舉 5366 張，
每一張都要 `_strings.GetMapName(id)`。gate 持有數十秒，
所有 session 請求堆在後面；curl 逾時後我殺掉行程，看起來像崩潰。

**怎麼分辨**（下次動手時照做）：
1. 全新行程，只開 `Map.nx`，立刻打 `/maps?limit=6`（小 limit）
2. 同一個行程，打 `/maps`（不帶 limit，會列全部）
3. 兩者都成功 → 是累積狀態；小 limit 成功但全列失敗 → 是端點本身

**驗收**：列 5366 張地圖能在合理時間內回應，且行程存活。

### 10. 視覺驗收仍然做不了 🔴

`tools/visual-check/editor.mjs` 需要 headless Chrome，
**這台機器的瀏覽器 daemon 起不來**（`Daemon broker authentication failed`）。
`_t2/` 底下的 `shot.mjs` 同樣。

素材圖片我是抓 PNG 下來用眼睛看的（草皮紋理、磚牆都正確），
但**編輯器畫面本身從頭到尾沒有被看過**。
見 P0-2。

---

## P3 — 工具與紀律

### 5. `edit-preserving-eol.js` 的 `>>>` 模式已修，但插入仍會多出空行

修好了吃掉錨定行的 bug（14/14 測試通過），但插入後仍會留下多餘空行，
需要手動清。是工具的問題，不是程式的問題，但每次改檔都要多一步。

### 6. 兩個覆蓋率腳本仍然不可靠 — ✅ 已處理（2026-10-11）

`i18n-missing-keys.js` 與 `i18n-coverage.js` 都用 regex 抓 `t\(\s*'([^']*)'`，
只看得到字串串接的第一段，與執行時收到的不符。

**已改成薄委派**：兩支都保留檔名（有人會打 `node nx-probe/i18n-coverage.js`），
但主動印出棄用說明後轉呼叫 `i18n-inventory.js`，並沿用同樣的 exit code。
跑舊指令拿到的是誠實答案，不是自信的錯誤答案。

### 7. i18n-inventory 本身也有誤報 — ✅ 已修

跑起來才發現它報 193 條 "unreachable entries"，但字典其實是好的：
`index.html` 裡的 `aria-label="Panels"` 這類無 `data-i18n` 屬性的標記根本沒被掃到。

修了三處掃描盲點，順手補上真正漏翻譯的 9 條：

| 漏掉的型態 | 影響 |
|---|---|
| `aria-label="..."` | 導覽列、檔案樹等無障礙標籤永遠是英文 |
| `data-tip="..."` | 工具提示永遠是英文（原本誤記成 `title=`） |
| `data-i18n-attr` 缺失 | `Panels`、`WZ file structure` 沒有翻譯 hook |

```
修正前：UNTRANSLATED (runtime) 9    dictionary 2321
修正後：UNTRANSLATED (runtime) 0    dictionary 2330
```

**驗收**：繁中翻譯在執行期零缺口。

### 8. 188 條 unreachable entries（字典有、沒人用）

`Name (A to Z)`、`NAME`、`VALUE`、`Close all archives` 等 188 條在原始碼裡
徹底找不到任何呼叫點——是字串改名後留下的翻譯殘留。**不是錯誤**，
不影響執行，但字典裡有沒人用的條目會讓下一個翻譯者以為已經翻過。

**下一步**：批次刪除這 188 條，或標記為 deprecated。

---

## 執行順序的理由

> **2026-10-11 接手後更新**：P0-1（存檔崩潰）已解決並複驗。
> 順序變成 **P1-2 foothold → 第 9 項 maps 崩潰 → 視覺驗收**。

1. **先做 P1-2（foothold 生成器）**：這是「能看」與「能玩」之間唯一的缺口，
   也是使用者終極目標（agent 依提示詞生成地圖）擋下來的那一條。
   設計已完成，見 [FOOTHOLD-GENERATOR-NOTES.md](../tools/probe/FOOTHOLD-GENERATOR-NOTES.md)。
   **動手前先量出三件事**：`origin` 語意、鏈的接續規則、`group` 分段。
   上一任 agent 只抽一張地圖就把 layer 寫死成 4，抽第二張才推翻——不要重蹈。

2. **再做第 9 項（`/maps` 崩潰）**：它是「大量工作 → 無輸出 → 行程消失」，
   與已解決的 P0-1 同類症狀。既然那個是 gate 持有過久，這個也該先查 gate，
   再查演算法。

3. **視覺驗收（第 10 項 / P0-2）**：使用者唯一看得到的就是畫面，
   而且這兩次都是因為沒看才漏掉問題。但它**卡在工具上**——
   這台機器的瀏覽器 daemon 起不來，需要先解決或改用別的截圖方式。
   這是唯一一個「環境擋住」而非「技術擋住」的項目。

---

## 接手這一輪的變更（2026-10-11 晚間）

```text
JOURNAL/2026-10-11-handover-takeover.md      新增：接手複驗與冷啟動證據
JOURNAL/TODO.md                              更新：複驗結果、兩個新發現、執行順序
tools/probe/FOOTHOLD-GENERATOR-NOTES.md      新增：foothold 生成器設計與三個待量項目
```

**沒有改任何程式碼**——接手這一輪是驗證，不是修改。
工作區除這三個文件外乾淨。

### 複驗（2026-10-11 接手時）

```
建置                                     0 警告 0 錯誤
node tools/probe/acceptance.mjs          15/15 PASS
  └ save verifies — 12.72s reopenMs=85
放 1 塊 → 存檔 → 行程重啟 → 冷讀磁碟
  layer 0 tiles: 1  →  t|ancientForest|bsc/0 @ -350,200
```

### 還沒做的事（下一輪接手時從這裡開始）

1. **P1-2 foothold 生成器** —— 設計已寫好（`tools/probe/FOOTHOLD-GENERATOR-NOTES.md`），
   **程式碼還沒寫**。動手前先量三件事：`origin` 語意、鏈的接續規則、`group` 分段。
2. **第 9 項 `/api/mapedit/maps` 崩潰** —— 新的，先查 session gate 持有時間。
3. **第 10 項 視覺驗收** —— 卡在 headless Chrome 起不來，需要先解決環境。
4. **P2-3** NX 與 .wz 同時開啟只取第一種表示法——**需要產品決定**，不是 bug。
5. **P2-4** NX 畫布在 session 佔 3.1 GB（存檔峰值 14.76 GB）——真實風險。
6. **P1 剩餘三項**未測：`minimap/regenerate`、`foothold-chain`、`portal` 放置。
7. **188 條 unreachable 翻譯條目**待批次清理。
8. `_t2/` 下的驗證腳本（shot.mjs / editor.mjs / palette.mjs）未進 git。
   應該移進 `tools/` 並在 README 記錄用法——它們是唯一能重跑視覺驗收的東西。
