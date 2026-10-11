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

## MCP — 18 個工具，0 個能操作地圖

`agent-wz/dist/WzMcp/wz-mcp.exe`（38 KB C#，官方 `ModelContextProtocol` SDK）

握手結果：

```
server  : maplebench-wz v1.0.0
protocol: 2024-11-05
tools   : 18
```

工具清單：

```
wz_status      wz_browse      wz_get         wz_search
wz_bulk_math   wz_edit_node   wz_replace     wz_save
wz_close       wz_changes     wz_undo        wz_render
wz_export      wz_hash        wz_diff        wz_transfer_preview
wz_inventory   wz_verify      wz_run_plan
```

**沒有一個能操作地圖。**

`WzCli/WzClient.cs` 只認四個 API 前綴：

```
api/files   api/node   api/canvas   api/children
```

而新版有 **31 個地圖端點**（`/api/mapedit/*`），MCP 一個都沒暴露：

```
capabilities  maps  open  doc  close  edit  batch  undo  redo
node  save  validate  portal-icons  history  palette/preview
changes  palette/sets  palette/entries  palette/life
palette/reactors  thumb  place  foothold-chain  auto-foothold
set-layer-ts  minimap/plan  minimap/regenerate  create
```

> **對「讓 agent 生成地圖」這個目標，這是最大的缺口。**
> Agent 現在連地圖清單都列不出來，更別說生成。
> 上一輪做的 `/validate` `/batch` `/place` 都只有 HTTP 介面，
> MCP 呼叫方看不到。

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

| # | 項目 | 工作量 | 理由 |
|---|---|---|---|
| 1 | **MCP 加 5–6 個地圖工具** | 中 | `wz_map_list` `wz_map_open` `wz_map_place` `wz_map_batch` `wz_map_validate` `wz_map_save`。**最高優先**：沒有它，agent 只能用 HTTP 原始呼叫 |
| 2 | CLI 地圖命令改用 mapedit | 中 | `tilefit` 等 4 個改走新版端點；或明確標示只支援 .wz |
| 3 | 把 CLI/MCP 搬進新版 repo | 大 | 目前它們在另一個 git repo，等於兩套 codebase |

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