# 任務規劃：讓 MCP 看得到地圖

**日期**：2026-10-11
**承接**：[PLAN-2026-10-11.md](PLAN-2026-10-11.md)、[2026-10-11-generation.md](2026-10-11-generation.md)
**依據**：[CLI-AND-MCP.md](../docs/CLI-AND-MCP.md)

---

## 為什麼這是下一個

使用者要的是「與 agent 溝通並讓它生成指定風格的地圖」。
現在的狀態：

| 層 | 現況 |
|---|---|
| HTTP API | ✅ 31 個 mapedit 端點，`/validate` `/batch` 已實測 |
| **MCP** | ❌ 18 個工具，**0 個能操作地圖** |
| foothold 生成 | ❌ 素材不帶幾何（實測 25 Tile / 12 Obj set 皆無） |

**MCP 是鏈條上唯一斷掉的一環**。agent 走 MCP 連線時，
連地圖清單都列不出來，更別說生成。

---

## 關鍵判斷：不移植 WzCli

舊版 `WzMcp` 依賴 `WzCli`（217 KB，41 個命令）。
新專案要的是**只打 mapedit 端點的薄 client**，不是通用 WZ 工具。

| 做法 | 評估 |
|---|---|
| 把 WzCli + WzMcp 搬進新版 repo | 重複 217 KB，且通用命令對新版不一定適用 |
| **新寫一個只懂 mapedit 的 client**（採用） | 小、專注、不帶入舊假設 |
| 改舊 WzMcp 加地圖工具 | 跨 repo，且新版的 `/batch` `/validate` 它完全不知道 |

**結論**：新建 `MapleMcp` 專案，HTTP 層自己寫，約 100 行就夠。

---

## 執行順序

### P0 — MCP 能看見地圖（本次主線）

| # | 任務 | 依據 | 驗收 |
|---|---|---|---|
| 1 | `MapleMcp` 專案骨架 + mapedit HTTP client | 舊 WzMcp 用 `WithToolsFromAssembly` + stdio | 握手成功，列得出工具 |
| 2 | `wz_map_capabilities` | `/capabilities` | 回報 archive 與地圖數 |
| 3 | `wz_map_list` + `wz_map_open` | agent 連地圖清單都做不到 | 列出前 N 張、開啟指定地圖 |
| 4 | `wz_map_doc` | 讀結構 | 回報層/圖塊/foothold/傳送門 |
| 5 | `wz_map_palette` | 選素材 | 列出 set 與 `drawableEntries` |
| 6 | `wz_map_place` | 放置 | 單一放置，回報實際位置 |
| 7 | `wz_map_batch` | 批量 | 一次多操作，單一 undo |
| 8 | `wz_map_validate` | 自我檢查 | 回報斷鏈等問題 |
| 9 | `wz_map_save` | 寫入 | 存檔並回報驗證結果 |

**安全設計**：所有變更性工具帶 `commit` 旗標，預設 `false` 只預覽。
沿用舊版 `WzMcp` 已驗證的模式。

### P1 — 讓 agent 真的能生成一張地圖

| # | 任務 | 驗收 |
|---|---|---|
| 10 | 用 MCP 工具跑一次完整流程 | 從列地圖到存檔，全程只經 MCP |
| 11 | 視覺確認 | 截圖確認畫面正確 |

---

## 不做的事

| 不做 | 原因 |
|---|---|
| 移植 WzCli 的 18 個通用工具 | 新版 repo 不含 WzCli，且通用能力不在使用情境的核心 |
| foothold 生成器 | 是 P2 的大工程，本次先把 MCP 鏈條接上 |
| 改舊 WzMcp | 跨 repo，只會造成兩套並存 |

---

## 風險

| 風險 | 緩解 |
|---|---|
| MCP SDK 離線不可用 | 已確認 2.2.0 在 nuget 快取 |
| 工具太多讓 agent 困惑 | 只做 9 個，每個對應 agent 的一個真實問題 |
| 路徑含中文導致建置失敗 | 專案本身不碰素材路徑，路徑是執行期參數 |

---

## 產出

- `MapleMcp/` — 新的 MCP 專案（獨立於舊 repo）
- `wz-mcp-map.cmd` — 啟動腳本
- `docs/CLI-AND-MCP.md` — 更新實測結果
- `JOURNAL/2026-10-11-mcp-maps.md` — 本輪日誌