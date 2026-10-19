# MapleMcp — 讓 agent 操作地圖

MCP（Model Context Protocol）伺服器，把地圖編輯的能力暴露給 agent。

**存在的理由**：舊版的 `wz-mcp` 有 18 個工具，但只認
`api/files`、`api/node`、`api/canvas`、`api/children` 四個端點。
agent 透過它連線時**連地圖清單都列不出來**。這個專案補上那條鏈。

---

## 快速開始

```bash
# 1. 建置
dotnet build MapleMcp/MapleMcp.csproj -c Release

# 2. 啟動後端並開啟地圖封存
#    （後端預設 http://127.0.0.1:5100）

# 3. 註冊到 MCP client
#    命令： wz-mcp-map.cmd
#    或直接： MapleMcp/bin/Release/net10.0-windows/maple-mcp.exe
#    環境變數 WZ_SERVER 可改後端位址
```

註冊範例（VS Code 已內建 `.vscode/mcp.json`；Claude Desktop / Cursor 用同一個 `mcpServers` 區塊）：

```jsonc
{
  "mcpServers": {
    "maplebench": {
      "command": "<repo>/wz-mcp-map.cmd",
      "env": { "WZ_SERVER": "http://127.0.0.1:5100" }
    }
  }
}
```

> **給 AI agent**：repo 根目錄有 [`AGENTS.md`](../AGENTS.md)——開後端、連 MCP、
> 工具清單、不可違反的規則都在那裡。連上 MCP 後工具是自動探索的，不必先讀文件。

---

## 47 個工具，四個家族

伺服器名稱 `maplebench-assets`。2026-10-11 合併後有 41 個工具；
2026-10-12 模擬器完成，再加 6 個 `wz_map_simulate*` 與
`wz_map_patrol_mob` / `wz_map_cast` / `wz_map_advise` / `wz_map_ground`。

### 地圖 `wz_map_*` — 11 個

| 工具 | 作用 |
|---|---|
| `wz_map_capabilities` | 後端現在能做什麼、開了哪些封存 |
| `wz_map_list` | 列出封存內的地圖（可搜尋） |
| `wz_map_open` | 開啟地圖，回傳完整結構 |
| `wz_map_create` | 從量測出的最小骨架建立新地圖 |
| `wz_map_doc` | 已開啟地圖的結構摘要 |
| `wz_map_node` | 讀取單一節點，**找出可寫的葉節點** |
| `wz_map_palette` | 列出素材 set 與 `drawableEntries` |
| `wz_map_place` | 放置圖塊／物件／foothold／傳送門 |
| `wz_map_batch` | 多次編輯 = **單一 undo** |
| `wz_map_validate` | 檢查地圖規則（斷鏈等） |
| `wz_map_create` | **在封裝內建立新地圖**（.wz 與 NX 皆可） |
| `wz_map_save` | 寫回磁碟 |

### 封裝拆分 `wz_archive_*` — 8 個

`capabilities` `scan` `open` `close` `children` `node` `export_img` `split`

把 split client 的 67 GB `.wz` 封裝拆成一個資源一個 `.img`，
用的是後端自己的序列化器，所以位元組與編輯器存檔一致。
`_Canvas` 預設不拆（見 `Program.cs` 的說明），磁碟剩餘 < 8 GB 會停。

### 通用資產 `wz_asset_*` — 22 個

`files` `open` `open_source` `close` `save_as` `add` `delete` `rename`
`transfer` `tree` `search` `node` `children` `set_value` `set_values`
`undo` `history` `changes` `export_img` `export_canvas`
`replace_canvas` `sources`

不依賴「目前選中的地圖」，直接操作任意 WZ/IMG/NX 節點。
`wz_asset_sources` 需要設定 `MAPLEBENCH_SOURCES`；未設定會明確報錯，
不會假裝成功。

### 模擬器 `wz_map_simulate*` — 6 個

| 工具 | 作用 |
|---|---|
| `wz_map_simulate` | 用客戶端物理走一張地圖，回報走到哪、卡在哪 |
| `wz_map_simulate_batch` | 一次跑多張，只列走不過去的 |
| `wz_map_patrol_mob` | 讓怪物巡邏一段，種子固定可重現 |
| `wz_map_cast` | 取得繪製所需：角色軌跡、梯子、門、每個生成點 |
| `wz_map_advise` | 給定卡住的位置，說明該延伸哪個 foothold 多遠 |
| `wz_map_ground` | 查某個點下面有什麼 foothold、哪裡是牆 |

**與 `wz_map_validate` 的差別**：validate 檢查規則，
這些檢查**角色實際會遇到什麼**。鏈完整但地面中斷的地圖，
validate 說沒問題，這裡會說走不過去——那才是缺口。

**為什麼種子是參數**：同種子必然重播同一次巡邏。
一個每次答案都不同的問題不是問題。

### 安全設計

**所有會改動的工具都帶 `commit`，預設 `false`。**
`commit=false` 只回報「會做什麼」，不動任何東西。

```
agent 選錯目標 → 帶 commit=false 重跑 → 讀結果 → 確認後才 commit=true
```

沿用舊版 `WzMcp` 已驗證的模式。磁碟變動只發生在
`wz_map_save(commit=true)`，而存檔會重寫整個封存（會留時間戳備份）。

---

## NX 合併工具（`wz_asset_merge_nx`）

生成好的候選 `.img` 要進到客戶端會開的封存，用這一個工具：

| 參數 | 意義 |
|---|---|
| `targetNx` | 要改寫的 `.nx` 絕對路徑 |
| `entries` | `[{ source, target }]`，`source` 是鬆散的 `.img`，`target` 是封存內目錄（如 `Map/Map9`、`Obj`）；同名節點會被取代 |
| `commit` | 預設 `false`：只開檔檢查、回報會新增或取代什麼；讀過再帶 `true` 重跑 |
| `backup` | 預設 `true`：改寫前把原檔留成時間戳 `.bak` |

一次呼叫就是一次**全封存改寫**，所以它自己做了交易：
先寫到旁邊的暫存檔 → 重新讀回驗證節點數與每個目標路徑 → 把原檔改名備份 → 才換上新檔。
任一步失敗都不會動到目的檔。

**封存不能已經開在這個 session**（會被拒絕）：在活的映射底下換檔，
會讓 session 讀到已經不存在的位元組。合併是獨立於 session 之外的檔案操作。

SAO 第一層的實例：

```json
{
  "targetNx": "E:/SAO Client/wz/Map.nx",
  "entries": [
    { "source": "E:/SAO Client/content/floor01/generated-field-v001/img/Map/Map9/910870003.img", "target": "Map/Map9" },
    { "source": "E:/SAO Client/content/floor01/generated-field-v001/img/Obj/saoGeneratedField910870003.img", "target": "Obj" }
  ],
  "commit": true
}
```

> 實測：先在 `Map.nx` 的複本上跑（8,400,281 → 8,400,592 節點、31 秒），
> 再對實機 `Map.nx` 跑 `commit=true`（27.6 秒、`verified: true`）；
> 用獨立 NX 解析器確認兩張 IMG 都在、舊的城鎮地圖未被動到。

---

## 實測驗證

```bash
# 只經 MCP 跑完整流程，不碰 HTTP API
node tools/probe/mcp-flow.mjs \
     MapleMcp/bin/Release/net10.0-windows/maple-mcp.exe \
     "<Map.nx 的絕對路徑>"
```

**2026-10-11 實測：ALL PASSED**

```
PASS  reachable                    — http://127.0.0.1:5340
PASS  maps available               — 5366 maps
PASS  map list returns rows        — 5 of 5366
PASS  a row carries a usable path  — f1/Map/Map0/000000000.img
PASS  map opens                    — 8 layers
PASS  no link-stub warning         — none
PASS  tile sets listed             — 109 sets
PASS  every set reports entries    — 0 empty, 109 usable
PASS  preview is a preview         — commit=false 沒有寫入
PASS  tile placed                  — [0,2,4]
PASS  validate returns a verdict   — ok=true errors=0 footholds=13
PASS  batch preview does not write — 300 ops queued
PASS  batch applied                — applied=300/300
PASS  history collapsed            — 10 -> 11 for 300 ops
PASS  save preview is a preview    — 沒有寫入
```

---

## 兩個實測發現（寫進說明裡了）

### 1. 只有葉節點能編輯

`wz_map_batch` 的 addr 必須指向**葉節點**：

```
"'0' is a SubProperty and its value cannot be edited here.
 Containers, vectors and binary payloads are not scalar edits."
```

指向容器會被拒絕。所以 `wz_map_node` 是必要的——
它讓 agent 先讀出子節點，找到真正有值的葉節點。

### 2. 型別不能改

```
"'y' is an Int and 'bsc' is not a number, so writing it here would have
 to retype the node."
```

寫入的值必須和原來型別相容。這兩條都寫進
`wz_map_batch` 的 description 與 `ServerInstructions`，
因為 agent 會先讀說明再動手。

---

## 為什麼不移植舊的 WzCli

舊 `WzMcp` 依賴 `WzCli`（217 KB，41 個通用命令）。
新專案只需要打 `/api/mapedit/*`，所以 `MapClient` 只有約 100 行。

好處是**不會有第二套路徑格式的假設**與實際端點不一致。
壞處是通用 WZ 操作（`wz_get`/`wz_search` 等）要另外用舊的 server。
兩個可以同時註冊給 agent。

---

## 相依

```
ModelContextProtocol 2.2.0        （與舊版 WzMcp 同版本）
Microsoft.Extensions.Hosting 10.0.0
```

沒有專案相依，獨立於 `MapleBench`——它透過 HTTP 說話，
所以後端可以從任何地方跑。