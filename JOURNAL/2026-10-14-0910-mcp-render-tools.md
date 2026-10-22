# 2026-10-14 09:10 — MCP 優先：新增 2 個 render 工具，補上 3 年沒解的視覺驗收

接續 [2026-10-14-0834-mcp-first-plan.md](2026-10-14-mcp-first-plan.md)（同一輪，不覆蓋）。
計畫 4 項全部完成。

---

## 1. 環境真相（本輪實測，與文件不符處已修正到文件）

| 項目 | 文件 | 實測 |
|---|---|---|
| 後端埠 | 5100 | **5480**（5100 無服務） |
| MapleMcp 執行 | `maple-mcp.exe` | 會報 `You must install .NET 10`，要用 SDK 10 跑 `.dll` |
| 指向後端 | 直覺是寫死 | `WZ_SERVER` 可覆寫（`ArchiveClient.cs:33`）|
| 工具數 | 41（2026-10-11）| 50 → **52**（本輪新增 2）|

封裝成 `tools/maple_mcp.py`（`MapleMcp()` 類別）：自動探埠、走 SDK 10、
`open_client()` 一次開 16 個 NX。

```python
from maple_mcp import MapleMcp
with MapleMcp() as m:              # 自動找到活著的後端
    m.open_client()                # 16 個 NX，0.8 秒
    m.call("wz_map_doc", {"path": "f7/Map/Map0/000040001.img"})
```

---

## 2. 新增兩個工具

### `wz_map_render` — 整張地圖出 PNG

原本 50 個工具**全部回傳 JSON**。`wz_map_cast` 給了 foothold、角色軌跡、
portal、spawn——畫一張地圖需要的全部數字，但 agent 拿著它答不出
「這張地圖看起來對不對」。`wz_asset_export_canvas` 只匯出單一 canvas 節點，
而 v83 地圖是 `Tile/<set>.img/<variant>/<n>` 上千個葉節點，
**沒有任何一個節點是整張地圖**。

這就是 `TODO.md` 第 10 項「視覺驗收仍然做不了」真正的卡點：
不是環境擋住，是沒有任何一條路能把地圖畫成圖。

繪製順序：Back 背景 → tile 圖層（依 `zm` 由高到低）→ foothold 線 →
角色軌跡 → spawn 標記。

| 地圖 | 圖磚 | foothold | 軌跡 | spawn | 耗時 |
|---|---|---|---|---|---|
| 000010000 小菇菇 | 271 | 83 | 8 | 3 | 0.4s |
| 000040001 蝸牛打獵場II | 284 | 133 | 263 | 27 | 0.5s |
| 000050000 危險的樹林 | 171 | 65 | 201 | 19 | 0.2s |

### `wz_map_render_mob` — 單一姿勢逐影格出 PNG

同時回報 `originX` / `delay` / `cornerPivotErrorPx`。
後者是 `-2 × originX`，即鏡像軸畫在圖角而非影格 origin 時的偏移量。

實測 2220100（藍蘑菇）：

| frame | originX | originY | delay | cornerPivotErrorPx | 尺寸 |
|---|---|---|---|---|---|
| stand/0 | 27 | 58 | 180 | **-54** | 80×80 |
| stand/1 | 27 | 55 | 180 | **-54** | 80×80 |

54px 超過怪物體型一半——這就是 `walkview.js` 鏡像軸該錨在哪裡的可驗證數字。
（該軸已於 08:0x 修為 `x + ox`，此處提供獨立驗證手段。）

---

## 3. 踩到的四個坑（都是同一類：欄位名猜錯但靜默通過）

| # | 症狀 | 真相 | 修法 |
|---|---|---|---|
| 1 | `'Tile' not found under 'f1'` | 封存 id 按**開啟順序**發，`f1` 在這台機是 UI.nx | 從地圖自己的位址取第一段 |
| 2 | `'grassySoil' not found under 'f7/Tile'` | 實際是 `grassySoil.img`（`art` 字串不含副檔名）| 加 `.img` |
| 3 | `/bsc/2/canvas` → 404 | **葉節點本身就是 canvas**，多一段反而 404 | 去掉 `/canvas` |
| 4 | foothold 線呈放射星芒 | 欄位是 `x1,y1,x2,y2`；寫成 `x0,y0,x1,y1` 全部讀到 0 | 改欄位名 |

第 4 個特別值得記：**`x0` 不存在時 C# 的 `?.GetValue<int>() ?? 0` 不報錯**，
133 條線全部從原點畫出來，但程式「成功」執行。
這與工作室守則 §2.3「探針回傳 undefined = 探針壞掉」同一族——
**靜默的 0 和錯誤的答案長得一樣**。

Back 層的路徑也與直覺相反：`Back/<set>.img/back/<variant>` 本身就是 canvas
（列出來只有 `origin`、`z`），沒有 frame 索引。

---

## 4. 診斷結論：11 張「卡住」的地圖不是缺陷

`wz_map_simulate_batch` 40 張：29 通過、11 到盡頭、耗時 0.1 秒。

逐一查證後確認那 11 張是**地圖本來就只走到那裡**：

```
000010000  wz_map_advise → foothold 78 結尾在 x=1395
           wz_map_validate → ok:true, brokenChains:0, bounds.right=1395
```

角色停在 1345，foothold 結尾 1395，等於地圖邊界。這是 v83 客戶端地圖的
正常終點，不是模擬器的 bug。`brokenChains: 0` 是關鍵證據——
所有既有地圖的斷鏈數都是 0。

---

## 5. 怪物系統驗收（12 次巡邏）

| 地圖 | seed | 離圖 | 轉向 | 路程 | 被擊 | 追擊幀 |
|---|---|---|---|---|---|---|
| 000040001 ×3 | 1/7/42 | 全 True | 0/1/2 | 171–260 | 各 1 | 各 21 |
| 000040002 ×3 | 1/7/42 | 全 True | 2/0/5 | 184–278 | 各 1 | 各 21 |
| 000050000 ×3 | 1/7/42 | 全 True | 0/10/3 | 48–185 | 各 1 | 各 21 |
| 000050001 ×3 | 1/7/42 | 全 True | 1/3/5 | 96–195 | 各 1 | 各 21 |

**12/12 留在地圖上、12/12 有被擊反應**（`attackerRedirected: true`）。
5 種怪物 ID：`0002005` `0100100` `0100101` `0130100` `0130101`。

---

## 6. 這一輪真正的浪費

前一輪（06:00–08:30）約 2.5 小時用 curl + 手寫 CDP 做的事，
專案裡本來就有：

| 我做的 | 專案裡的 |
|---|---|
| curl `/api/files` 開 NX | `wz_archive_open` |
| curl `/api/children` | `wz_archive_children` |
| curl `/api/simulate/session` | `wz_map_session_open` / `wz_map_session_step` |
| curl `/api/mapedit/*` | `wz_map_open` / `wz_map_doc` / `wz_map_validate` |
| 掃地圖找怪的 | `wz_map_list` + `wz_map_cast` |
| 手寫 WebSocket CDP | `tools/probe/cdp-shot.mjs` 等 30+ 支 |
| 自己估算怪物動畫時鐘 | `wz_map_patrol_mob` |

`docs/CLI-AND-MCP.md`（2026-10-11）與 `工作室守則.md` 兩份文件都寫了這個 MCP。
**動手前先讀專案根目錄的目錄清單，是最省時間的單一動作。**

---

## 7. 動到的檔案

```
新增  MapleMcp/RenderTools.cs              2 個 MCP 工具
新增  MapleMcp/MapPainter.cs               地圖 PNG 繪製
新增  tools/maple_mcp.py                   Python 呼叫端（含兩個環境陷阱）
修改  MapleMcp/MapleMcp.csproj             + System.Drawing.Common 10.0.0
修改  tools/probe/mcp-probe.mjs            接受 <command> [args...]，可帶 runtime shim
修改  docs/CLI-AND-MCP.md                  52 工具、執行陷阱、render 章節
```

建置：`MapleMcp.csproj` 0 錯誤。**未 commit**（工作區另有 20+ 個本輪之前的改動）。