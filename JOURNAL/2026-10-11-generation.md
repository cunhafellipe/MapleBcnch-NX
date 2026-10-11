# 讓 Agent 能依提示詞生成地圖 — 第一輪

**日期**：2026-10-11
**承接**：[PLAN-2026-10-11.md](PLAN-2026-10-11.md)（本輪的規劃）
**最終目標**：與 agent 溝通，讓它依提示詞、用現有 WZ/NX 素材生成指定風格的地圖
**規格來源**：[MAP-STRUCTURE.md](../docs/MAP-STRUCTURE.md)（10 張地圖的實測解剖）

---

## 這輪做了什麼

三項功能，全部經過真實 API 呼叫驗證，外加**一次自我推翻**。

| # | 任務 | 狀態 | 驗收證據 |
|---|---|---|---|
| P0-1 | `PaletteSets` 驗證素材真的有像素 | ✅ | 109/109 set 回報 `drawableEntries` |
| P0-2 | `/validate` 端點（地圖規則檢查） | ✅ | 斷鏈與無效引用都能抓到；undo 可修復 |
| P1-1 | `/batch` 批量端點（單一 undo） | ✅ | 300 操作 → `undoDepth=1`、0.03 秒 |
| P1-2 | `fillFloor` 場景語意 | ⏸️ **暫緩** | 實測發現前提錯誤，見下 |

---

## 一、推翻了我上一輪的結論

上一輪我寫進規格：「API 在說謊，`ancientForest` 是無效素材，
`sets` 端點給了縮圖但實際沒有視覺 canvas，放置會寫入看不見的空引用。」

**這個結論是錯的。**

重新驗證：

```
/palette/sets?kind=Tile
  ancientForest → drawableEntries=18
/palette/preview?path=.../bsc/0
  → {"w":90,"h":60,"missing":false,"hasPixels":true}    ← 有真實像素
/place ... u=bsc no=0   → HTTP 200                        ← 放置成功
/place ... u=bsc no=10  → HTTP 400
  "Tile/ancientForest.img/bsc/10 does not resolve in the open session"
```

**兩層錯誤，都是我的：**

1. 我用 `bsc/10` 測試——**那個編號本來就不存在**。系統的回應完全正確。
2. 我把 `/palette/entries` 的 `path` 傳成**地圖路徑**，拿到的是
   「這張地圖用到的 9 筆」而非「這個 set 的全部 18 筆」。
   傳 set 路徑就正常了。

**端點一直是好的。** 我把「我傳錯參數」誤讀成「系統有 bug」，
還寫進規格文件，差點讓下一個人去修一個沒壞的地方。

> **這一輪最值得記錄的教訓**：
> 專案裡到處是註解宣稱已驗證的行為，其中不少是錯的——
> **包括我自己上一輪寫的**。懷疑程式碼之前，先確認自己的測試有沒有做對。

---

## 二、P0-1：素材像素驗證

**憑什麼做**：既然要讓 AI 選素材，palette 回報的可用性必須誠實。

**怎麼做**：
- `MapArtDto` 新增 `HasPixels`——區分「節點存在」與「節點有像素」
- `PaletteSets` 新增 `DrawableEntries`——該 set 有幾個真的畫得出來的條目
- **抽出 `LeafEntries()` 讓縮圖選擇與像素計數走同一條路徑**。
  這是關鍵：先前兩者各自實作，若哪天分歧就會重現同類問題。

**結果（實測）**：

```
109 個 Tile set
  有縮圖的        : 109
  有可繪製素材的  : 109
  空的            : 0
```

---

## 三、P0-2：`/validate` — 給 agent 的自我檢查

**憑什麼做**：地圖在畫面上看起來對，不代表能玩。
**斷鏈的 foothold 鏈在畫面上完全看不出來**，直到角色走不過去。
實測的 7 張地圖斷鏈數全部是 0，所以斷鏈是缺陷不是風格。

**檢查什麼**（全部來自 MAP-STRUCTURE.md 的實測規則）：

| 規則 | 為什麼是規則 |
|---|---|
| 鏈完整性 | `prev`/`next` 是行走路徑，斷了角色卡住。實測 156 條鏈零斷裂 |
| 怪物有 foothold | `fh` 是怪物取得高度的方式 |
| 圖塊有 u/no | 缺了就畫不出東西 |
| 物件有 oS | 同上 |
| link stub | 3/10 是 stub，幾何在別的地圖 |

**怎麼驗證**：**故意弄壞地圖，看它抓不抓得到。**

```
基線（乾淨）          : ok=true  errors=0
清空一個物件的 oS   : ok=false errors=1 unresolvable=1
  → "An object has no oS (object set), so it cannot resolve to art."
把 foothold.next 改成 9999:
  ok=false brokenChains=1 errors=2
  → "Foothold 2.next points at '9999', which does not exist.
     A severed chain strands the character at runtime."
undo 一次             : broken=0  unresolvable=1
undo 兩次             : ok=true errors=0     ← 完全修復
```

**跑 10 張抽樣地圖**：全部 `ok=true`、`brokenChains=0`，
3 張 link stub 正確辨識——**與人工分析完全一致**。

---

## 四、P1-1：`/batch` — 一次呼叫多做一步

**憑什麼做**：**實測 20 次 `/place` 只需 0.17 秒。**
網路從來不是瓶頸——但 `MaxUndoDepth` 只有 **200**。
鋪一張 300 格的地面，逐格呼叫會**靜默丟掉自己的 undo 歷史**，
而且呼叫方無從得知自己已經不能復原了。

**怎麼做**：批次執行後，把期間推入的歷史條目用既有的
`Change.Group` 合併成一筆（這個機制專案裡本來就有，`place` 等處已在用）。

**結果（決定性測試）**：

```
300 個 setValue  →  ok=true  applied=300  undoDepth=1   耗時 0.03 秒
單次 undo        →  applied='300 setValue'  undoDepth=0   一次撤銷整批
```

**失敗中止行為**（測試前 5 個成功、第 6 個失敗）：

```
requested=7 applied=5 failedAt=5 partial=true undoDepth=1
error: "Operation 5 ('setValue') failed: '0' is a SubProperty and its value
        cannot be edited here. ..."
```

已套用的 5 個**合併成單一 undo**（depth=1），
呼叫方一次 undo 就能整批回退——這正是「我鋪錯了地面」需要的語意。

---

## 五、P1-2 暫緩：前提被實測推翻

原本規劃 `fillFloor`（鋪地面 + 自動 foothold），
因為規格說 foothold 的 layer 跟著視覺圖層走，鋪完順便就能生。

**實測發現 `/auto-foothold` 對實際素材完全無用**：

```
掃描 25 個 Tile set  → 帶 foothold 幾何的：0
ancientForest 18 筆  → hasFoothold 全部 False
掃描前 12 個 Obj set → 帶 foothold 幾何的：0
```

呼叫時的回應是整個專案最好的設計示範：

> "carries no foothold geometry of its own, so there is nothing to generate from.
> **Draw the chain by hand instead — generating a guess here would be exactly the
> silent invention this tool refuses.**"

`auto-foothold` 依賴素材自帶的 Convex 幾何，
而**真實的 Map.nx 裡沒有任何素材帶這個東西**。

**所以 `fillFloor` 必須自己從輪廓算 foothold**，這是實質工作量，
不是加一個端點就完事。這輪不硬做，留給下一輪專注。

已寫進規格第 9 節，避免下一個人重蹈覆轍。

---

## 六、這一輪的副產品

- `docs/MAP-STRUCTURE.md` — 地圖結構規格（10 張地圖解剖），
  含**查詢語意速查表**（第 10 節，我踩過的坑）
- `tools/anatomy/dissect.mjs` — 解剖腳本，可重跑驗證
- `tools/anatomy/sample-10.json` — 原始解剖資料
- `tools/visual-check/` — 視覺驗收工具（上一輪）

---

## 七、沒做的事

| 沒做 | 原因 |
|---|---|
| `fillFloor` 場景生成 | 前提被推翻（auto-foothold 無用），需重新設計 |
| 範本機制 | P2，本輪專注 P0/P1 |
| MCP server | P2 |
| 中文地圖名搜尋修復 | 已記錄在規格第 6.3 節，根因未查 |
| `Mob.nx`（818 MB）壓測 | 記憶體不足時硬測只是把測試機推去 OOM |
| 188 條無用翻譯條目 | 不影響主線 |

**程式碼尚未 commit** — 使用者未指示，不自行提交。

---

## 八、下一輪從哪開始

1. **foothold 生成器**（P1-2 的真正內容）：從已鋪好的圖塊輪廓算鏈條，
   保證零斷裂。這是讓 agent 生成「能玩」而非「能看」的地圖的關鍵。
2. **範本機制**（P2-1）：原料已齊（5366 張地圖、109 個 tile set），
   實測一張地圖的 doc 回傳 name/bgm/bounds/layers/footholds 足夠學風格。
3. **MCP server**（P2-2）：底層 API 已夠紮實，可以包裝了。

---

## 驗收狀態

```
建置            : 0 警告 0 錯誤
i18n 檢查       : exit 0（0 未翻譯 / 0 重複 / 0 placeholder 不符）
/validate       : 10/10 抽樣地圖 ok=true，與人工分析一致
/batch          : 300 操作 → undoDepth=1，單次 undo 全撤
```