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

## 工具家族（2026-10-17：52 → 90）

伺服器名稱 `maplebench-assets`。後端有 **234 條路由**；下面是 90 個工具，
每個家族用 `kind`／`action` 參數分流，而不是一條路由一個工具 —— 234 個工具會把
agent 淹死，比 52 個更難用。

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

### 模擬器 `wz_map_simulate*` — 6 個（地圖物理，不是本專案的模擬器）

`wz_map_simulate` `wz_map_simulate_batch` `wz_map_patrol_mob`
`wz_map_cast` `wz_map_advise` `wz_map_ground`

**與 `wz_map_session_*` 一起看**：這六個走的是**地圖編輯器的物理**，
回答「這張地圖角色走不走得過去」。要回答「施放技能之後怪剩幾滴血」，
用的是下面的 `wz_sim_*`。兩者名字相近、用途完全不同，是本專案最容易誤用的一組。

### 現場模擬器 `wz_sim_*` — 12 個（2026-10-17 新增）

| 工具 | 作用 |
|---|---|
| `wz_sim_open` | 開一個持續存在的現場角色，回傳 `sessionId` |
| `wz_sim_step` | 前進 N 幀，帶方向鍵／跳／普攻／施法 |
| `wz_sim_cast` | 只做一件事：下一幀要求施放某技能 |
| `wz_sim_state` | 讀整個現場：角色狀態、每隻怪的 hp/maxHp、技能特效 |
| `wz_sim_summon` / `wz_sim_despawn` | 放／收怪 |
| `wz_sim_skills` | 綁定／清除熱鍵 |
| `wz_sim_equip` | 換武器／整組裝備（**整組取代**，不是疊加） |
| `wz_sim_control` | 無敵／復活／輸入開關 |
| `wz_sim_catalog` | 唯讀探索：地圖清單、技能清單、技能計畫、外觀槽位、怪的資料 |
| `wz_sim_probe` | 某個點下面是什麼 foothold、哪裡是牆 |

**檔案 id 不穩定**：路徑開頭的 `fN` 是封存的**工作階段 id**，依開啟順序發號。
實測同一個 `Map.nx` 在一次執行是 `f4`，下一次是 `f25`。寫死會得到
`No open file with id 'f4'`，看起來像地圖不見，其實不是。
**先呼叫 `wz_asset_files` 或 `wz_sim_catalog kind=maps`，用它回傳的 id。**

這些工具**不帶 `commit`**：現場模擬器在記憶體裡、依 id 存放、關掉就沒了，
和其他家族「寫進封存」的風險完全不同。這個例外寫在類別的 remarks 裡。

### 遊戲資料域 `wz_data_*` — 6 個（2026-10-17 新增）

`capabilities` `list` `detail` `write` `bulk` `search`，
以 `kind` 分流：`skill` `mob` `npc` `string` `cashshop` `books`。

**為什麼不直接用 `wz_asset_*`**：這五個域有自己的欄位驗證、自己的批次語義、
自己的 DTO。繞過 `/api/node/value` 直寫，會寫出該域根本載不進去的值。

### 長時間工作 `wz_job_*` — 6 個（2026-10-17 新增）

`capabilities` `start` `progress` `result` `report` `cancel`，
以 `kind` 分流八個工作家族：`audit` `dump` `compose` `import`
`repair.canvas-format` `repair.canvas-dir-link` `repair.donor-restore`
`repair.genchoice`。

這八個家族合計 56 條路由，形狀完全一樣（開始／進度／取消／結果）。
`repair.*` 會寫封存，所以 `start` 帶 `commit`。
某個 kind 沒有某一階段時**拋錯並列出有的**，絕不偷偷換一條路由。

### 算圖與輸出 `wz_render_*` — 6 個（2026-10-17 新增）

`view` `preview` `thumb` `animation` `dump` `export`。

**上限句**：算出一張圖只證明**這個節點解得出來**，
不證明像素跟對標一致，也不證明這是對的圖。

### 診斷與存檔 `wz_diag_*` `wz_save_*` — 10 個（2026-10-17 新增）

`wz_diag_health` `wz_diag_clientlog` `wz_diag_sim` `wz_diag_facts` `wz_diag_audit`
`wz_save_preflight` `wz_save_commit` `wz_save_memory`

**存檔的真相**：封存一旦開啟就是 process 生命期的記憶體映射，
`preflight` 乾淨**不等於**存檔會成功；`commit=true` 是整個封裝重寫
（有時間戳備份，但仍是重寫）。

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
     "<Map.nx 的絕絕路徑>"
```

### 現場模擬器流程（2026-10-17，實測通過）

`mcp-probe.mjs` 可以真的呼叫工具，不必再寫一支 client：

```bash
# --script @file.json：多步驟流程，{{sessionId}} 會自動帶入前一步回報的 id
node tools/probe/mcp-probe.mjs \
     "C:/MUWORK/app/scoop/apps/dotnet-sdk/10.0.401/dotnet.exe" \
     "MapleMcp/bin/Release/net10.0-windows/maple-mcp.dll" \
     --script @flow.json
```

```json
[
 {"tool":"wz_sim_open",   "args":{"path":"f25/Map/Map1/100000004.img","invulnerable":true}},
 {"tool":"wz_sim_skills", "args":{"sessionId":"{{sessionId}}","bindings":["1121008:K"]}},
 {"tool":"wz_sim_summon", "args":{"sessionId":"{{sessionId}}","mobId":"9400101","count":1}},
 {"tool":"wz_sim_step",   "args":{"sessionId":"{{sessionId}}","count":40,"left":true}},
 {"tool":"wz_sim_cast",   "args":{"sessionId":"{{sessionId}}","skillId":"1121008"}},
 {"tool":"wz_sim_state",  "args":{"sessionId":"{{sessionId}}"}}
]
```

實測輸出：`action='skill'`、`skillStance='brandish1'`、
1 個技能特效（9 幀、origin `(30,23)`、z=0）、全場怪的 `maxHp=1000`。

> `maple-mcp.exe` 是 framework-dependent apphost，而系統 runtime 只有 .NET 6/8，
> 直接跑會報 "You must install or update .NET"。用 .NET 10 的 `dotnet.exe` 跑 DLL。

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