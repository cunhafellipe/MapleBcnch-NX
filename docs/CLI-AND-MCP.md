# CLI 與 MCP 的現況與缺口

**實測日期**：2026-10-11
**實測方式**：`wz.exe --help`、`wz.exe files`、`tilefit`、`ls`，以及一個
`tools/probe/mcp-probe.mjs` 以 JSON-RPC 走 stdio 真的握一次手。

---

## 一句話結論

**CLI 有、MCP 有，兩者都在舊版 repo（`追風WZ編輯器/agent-wz`），
新版 `MapleBcnch-NX` 裡沒有，而且兩者都不會操作地圖。**

---

## CLI — 可用，但地圖支援停在舊格式

`agent-wz/dist/WzCli/wz.exe`（217 KB C# 原始碼，41 個命令）

```json
{ "tool": "wz", "backend": "http://127.0.0.1:5100", "commands": [ ... ] }
```

**對接新版的實測結果**：

| 命令 | 結果 |
|---|---|
| `wz files` | ✅ `ok: true`，讀到開啟中的 `Map.nx` |
| `wz ls f1/Map/Map0/000000001.img` | ✅ 回傳節點樹 |
| `wz tilefit f1/Map/Map0/000000001.img` | ❌ `No 'back/*/bS' entries` |

**通用命令對 NX 完全可用**：`files` `open` `close` `browse` `scan` `ls` `get`
`tree` `set` `add` `rm` `rename` `dup` `mv` `cp` `compute` `find` `replace`
`canvas` `export` `diff` `hash` `save` `undo` `redo` `history` …

**地圖專用命令有 4 個**：`tilefit` `tileplace` `tileswap` `mappreview`。
它們走的是舊的 `back/*/bS` 結構，**對 NX 的 `.img` 不適用**。

> 換句話說：CLI 能讀寫 NX 地圖的**節點**，但沒有「鋪地面」「生成 foothold」
> 這種理解地圖語意的命令。那部分能力目前只存在於新版 HTTP API。

---

## MCP — 兩個伺服器，通用能力已重疊

### `maplebench-assets`（本專案內）— ✅ 41 個工具，三個家族

`MapleMcp/`，用官方 `ModelContextProtocol` SDK 2.2.0。
專案內路徑：`MapleMcp/bin/Release/net10.0-windows/maple-mcp.exe`

握手實測（2026-10-11，合併 `wz_asset_*` 與 `wz_archive_*` 後）：

```
server  : maplebench-assets v1.4.0
protocol: 2024-11-05
tools   : 41
  wz_map_*     11  wz_map_capabilities  wz_map_list      wz_map_open
                   wz_map_doc           wz_map_node      wz_map_palette
                   wz_map_place         wz_map_batch     wz_map_validate
                   wz_map_save          wz_map_create
  wz_archive_*  8  wz_archive_capabilities  wz_archive_scan
                   wz_archive_open        wz_archive_close
                   wz_archive_children    wz_archive_node
                   wz_archive_export_img  wz_archive_split
  wz_asset_*   22  wz_asset_files / open / open_source / close / save_as
                   add / delete / rename / transfer / tree / search
                   node / children / set_value / set_values / undo / history
                   changes / export_img / export_canvas / replace_canvas
                   sources
```

> 伺服器名稱自 1.3.0 起為 `maplebench-assets`；1.0.0～1.2.0 曾用
> `maplebench-maps`。若 agent 端有寫死的名稱，請一併更新。
> 舊的 `maplebench-wz`（另一個 repo，18 個通用工具）仍可同時註冊，
> 它與本伺服器的 `wz_asset_*` 功能重疊，見下方註記。

**完整流程實測通過**（只經 MCP，不碰 HTTP）：
從 `capabilities` → `list` → `open` → `palette` → `place` → `validate`
→ `batch`（300 操作、歷史只加一筆）→ `save`，全部 PASS。
詳見 `MapleMcp/README.md`。

### `maplebench-wz`（舊版，另一個 repo）— 18 個通用工具

`追風WZ編輯器/agent-wz/dist/WzMcp/wz-mcp.exe`

```
tools: 18  (wz_status wz_browse wz_get wz_search wz_bulk_math
           wz_edit_node wz_replace wz_save wz_close wz_changes
           wz_undo wz_render wz_export wz_hash wz_diff
           wz_transfer_preview wz_inventory wz_verify wz_run_plan)
地圖相關工具: 0
```

`WzClient` 只認 `api/files`、`api/node`、`api/canvas`、`api/children`，
**一個 mapedit 端點都沒暴露**。

> **重疊狀況**：這句「互不重疊」只對 `wz_map_*` 成立。
> 2026-10-11 合併後，`maplebench-assets` 多了 22 個 `wz_asset_*` 工具，
> 覆蓋舊伺服器大部分的通用節點職責。**同時註冊兩個伺服器時，
> agent 會看到兩組功能相近的工具**；只註冊 `maplebench-assets` 即可涵蓋兩者，
> 舊的 `maplebench-wz` 可留作備援。

**這個缺口已於 2026-10-11 補上**，方式是新增 `MapleMcp`
而非修改舊專案——理由見 `JOURNAL/PLAN-MCP-MAPS.md`。

---

## 三者的實際關係

```
        追風WZ編輯器/agent-wz/          MapleBcnch-NX/
        （舊版 repo，未追蹤本次變更）
        ┌──────────────────┐          ┌────────────────────┐
        │ WzCli  41 命令    │──HTTP──▶ │                     │
        │ WzMcp  18 工具    │──HTTP──▶ │  31 個 mapedit 端點 │
        │ (0 個管地圖)      │          │  + validate/batch   │
        └──────────────────┘          │  ← 能力都在這裡      │
                                      └────────────────────┘
```

**新版後端不相容舊版 CLI 的所有假設嗎？** 不相容。
CLI 的 `WzClient` 走的 `api/node`、`api/files` 在新版**依然存在**
（實測 `ls` 成功），所以通用功能沒斷。
斷的是「地圖專用命令」與「MCP 完全沒接 mapedit」。

---

## 要補的（依優先順序）

| # | 項目 | 工作量 | 狀態 |
|---|---|---|---|
| 1 | **MCP 加地圖工具** | 中 | ✅ **已完成** — `MapleMcp`，10 個工具，實測通過 |
| 2 | foothold 生成器 | 大 | 🔴 下一個。素材不帶幾何（實測 25 Tile / 12 Obj set 全無），需從輪廓算鏈 |
| 3 | CLI 地圖命令改用 mapedit | 中 | ⏸ `tilefit` 等 4 個仍走舊的 `back/*/bS`，對 NX 不適用 |
| 4 | 把 CLI/MCP 搬進新版 repo | 大 | ⏸ 舊 `WzMcp` 仍在另一個 repo，但可與新版同時註冊使用 |

---

## 為什麼 CLI/MCP 不在新版 repo

實測發現新版 `MapleBcnch-NX` 從頭到尾沒有 CLI 或 MCP 的原始碼——
只有 `scripts/publish.ps1` 與 `scripts/version.ps1`（發佈用）。
CLI/MCP 的 7 個專案全在 `追風WZ編輯器/agent-wz/src/`，
連同它們的 git repo 一起，屬於**舊版架構**。

新版重寫了檔案格式處理與地圖編輯，但**沒有沿用 CLI/MCP**。
推測是重寫時聚焦在 GUI 與 HTTP API，agent 介面還沒接上。

> **這是推測，不是查證過的結論。** 若有其他原因，
> 修正這個段落。

---

## 重跑驗證

```bash
# CLI
wz.exe --help
wz.exe files                      # 需先啟動後端於 :5100
wz.exe ls f1/Map/Map0/000000001.img

# MCP
node tools/probe/mcp-probe.mjs <path-to>/wz-mcp.exe
```

`mcp-probe.mjs` 會真的握手並列出工具，
「地圖相關工具：N」那一行就是這份報告的依據。