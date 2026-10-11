# 後續任務規劃

依據 `JOURNAL/` 的實測結果。排序依「阻塞什麼」排，不是依「好不好做」排。

> **2026-10-11 更新**：地圖生成的目標線另見
> [PLAN-2026-10-11.md](PLAN-2026-10-11.md) 與
> [2026-10-11-generation.md](2026-10-11-generation.md)。
> 本檔記的是修 bug 的那條線；那兩份記的是長新能力的線。

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

1. **先做 1（存檔崩潰）**：它是資料安全問題。使用者放了一堆圖塊再存檔，
   現在的行為是行程死掉——檔案寫出來了，但使用者無法確認。
2. **再做 2（視覺確認）**：使用者唯一看得到的就是畫面，而且這一次就是因為沒看
   才漏掉的。
3. **P1 一起做**：1 和 P1 的「多封存會讓行程消失」很可能是同一個原因。
   查清楚一次能解兩塊。

---

## 本輪變更清單（尚未 commit）

```csharp
Services/MapAssetService.cs                            43/  (上輪未提交)
Services/MapEditor/MapEditorDtos.cs                    9/
Services/MapEditor/MapEditorService.Palette.cs         52/
Services/MapEditor/MapEditorService.Placement.cs      27/
Services/MapEditor/MapEditorService.cs                154/   ← 本輪：reopen 移出 gate
Services/WzSaveService.cs                             28/
Services/WzSessionService.cs                          87/    ← file.Name 不再被覆寫
Services/WzSessionService.cs (ReopenNx)                     本輪：轉換保持在 gate 外

wwwroot/index.html                                   41/    ← 本輪：補 11 個 data-i18n-attr
wwwroot/js/locale.zh-Hant.js                         14/    ← 本輪：補 5 條漏翻譯
JOURNAL/README.md                                     7/
JOURNAL/TODO.md                                       本輪：全面重寫
JOURNAL/shots/*.png                                   本輪：3 張驗證截圖

nx-probe/i18n-coverage.js                             本輪：改為委派 inventory
nx-probe/i18n-missing-keys.js                         本輪：改為委派 inventory
nx-probe/i18n-inventory.js                            本輪：修 4 處掃描盲點
```

建置 **0 警告 0 錯誤**。行尾全部 CRLF，與 HEAD 一致。
i18n 檢查 **exit 0**（0 未翻譯 / 0 重複 / 0 placeholder 不符）。

### 回歸驗證（2026-10-11，最終）

```
開 Map.nx → 開地圖 → 放 tile #2 → 存檔
  verified=true  seconds=9.55  historyKept=true  reopenMs=70  diffs=0
重開行程冷讀磁碟：
  圖層 0  ts=ancientForest  tiles=3
    no=0 @(-300,-200)  no=1 @(-250,-200)  no=2 @(-400,-200)
```

三個圖塊（含前兩輪放的）全部從磁碟讀回，素材與座標正確。

**這一輪可以提交了**——P0-1、P0-2 都有實測證據，不再是未驗證的修復。

### 還沒做的事（下一輪接手時從這裡開始）

1. **P2-3** NX 與 .wz 同時開啟只取第一種表示法——**需要產品決定**，不是 bug。
2. **P2-4** NX 畫布在 session 佔 3.1 GB（存檔峰值 14.76 GB）——真實風險，
   閒置記憶體不足時會 OOM。`MemoryPressureService` 是否接上 NX 路徑未確認。
3. **P1 剩餘三項**未測：`minimap/regenerate`（會寫檔）、`foothold-chain`（需多点 body）、
   `portal` 放置。需要另開一份素材副本再測。
4. **188 條 unreachable 翻譯條目**待批次清理。
5. `_t2/` 下的驗證腳本（shot.mjs / editor.mjs / palette.mjs）未進 git。
   應該移進 `tools/` 並在 README 記錄用法——它們是唯一能重跑視覺驗收的東西。
