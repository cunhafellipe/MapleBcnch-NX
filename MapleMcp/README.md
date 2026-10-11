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

---

## 41 個工具，三個家族

伺服器名稱 `maplebench-assets`。2026-10-11 合併前只有 10 個 `wz_map_*`；
之後併入 `wz_archive_*`（.wz 封裝拆成 .img 檔）與 `wz_asset_*`
（通用 WZ/IMG/NX 節點操作）。

### 地圖 `wz_map_*` — 11 個

| 工具 | 作用 |
|---|---|
| `wz_map_capabilities` | 後端現在能做什麼、開了哪些封存 |
| `wz_map_list` | 列出封存內的地圖（可搜尋） |
| `wz_map_open` | 開啟地圖，回傳完整結構 |
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

### 安全設計

**所有會改動的工具都帶 `commit`，預設 `false`。**
`commit=false` 只回報「會做什麼」，不動任何東西。

```
agent 選錯目標 → 帶 commit=false 重跑 → 讀結果 → 確認後才 commit=true
```

沿用舊版 `WzMcp` 已驗證的模式。磁碟變動只發生在
`wz_map_save(commit=true)`，而存檔會重寫整個封存（會留時間戳備份）。

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