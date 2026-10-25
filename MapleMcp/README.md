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

## 工具家族（2026-10-17：52 → 92）

伺服器名稱 `maplebench-assets`。後端今天有 **227 條不同的路由路徑**、**237 個
(method, path) 註冊**；下面是 92 個工具，每個家族用 `kind`／`action` 參數分流，
而不是一條路由一個工具 —— 那麼多路由會把 agent 淹死，比 52 個更難用。

**這兩個數是量出來的，不是手寫的**（2026-10-24，branch `map-generation-baseline`，
commit `a83181cd63ead4875da514a5942e3230885e086a`；`git status --porcelain --
MapleBench/Api/ MapleMcp/` 是空的，所以工作樹的路由原始碼與那個 commit 位元組相同，
數字對兩者都成立）：

- **237 個 (method, path) 註冊** —— 母體：一個 HTTP 方法算一個註冊，也就是原始碼裡
  每一個 `MapGet/MapPost/MapPut/MapDelete` 呼叫。

      (Select-String -Path MapleBench/Api/*.cs -Pattern 'Map(Get|Post|Put|Delete|Patch)\s*\(\s*"([^"]*)"' -AllMatches).Matches.Count
        237

- **227 條不同的路由路徑** —— 母體：一條路徑算一條，方法再多也只算一條（路徑要套上
  route group 的前綴，例如 `/api`）。這一欄需要解 group 前綴，用這支指令：

      python C:\mbtest2\routes\extract-routes.py
        registrations (method, path) : 237
        distinct full paths : 227
        distinct raw literals : 218

  （這三行分別印在該指令輸出的 E1、E2、E3 三節裡。）
  指令稿在 `C:\mbtest2\routes\extract-routes.py`，原始輸出在
  `C:\mbtest2\routes\extract-routes.txt`；**兩者都還沒進版控**，要把這個數變成可
  重跑的東西，得先把這支指令稿收進 repo。第三個母體是 **218 個原始字面值**（不套
  group 前綴，也就是 `grep` 原始碼會看到的數字）：

      (Select-String -Path MapleBench/Api/*.cs -Pattern 'Map(Get|Post|Put|Delete|Patch)\s*\(\s*"([^"]*)"' -AllMatches).Matches | ForEach-Object { $_.Groups[2].Value } | Sort-Object -Unique | Measure-Object | Select-Object -ExpandProperty Count
        218

- **舊的「234 條路由」**：那是 (method, path) 那一欄在 **2026-10-16/17** 那棵樹上的
  數字，已經過期（今天 237）。而且那棵樹**從未進版控** —— git 歷史在 2026-10-12 之後
  直接跳到 2026-10-19 —— 所以 234 沒有任何指令能重跑出來，只能靠上面那三個母體的
  曲線去框住它。

**工具數字由 `node tools/probe/mcp-tool-census.mjs` 量出來**（掃原始碼，並對執行中的
伺服器做 `tools/list` 握手，兩邊必須一致），**不要手寫**。家族清單用同一支工具的
`--markdown` 產生。

家族與數量（下面這段是工具自己的輸出，勿手寫）：

```
**92 tools** are auto-discovered once connected, in 9 families:
  `wz_map_*` (22), `wz_asset_*` (24), `wz_sim_*` (12), `wz_archive_*` (8),
  `wz_data_*` (6), `wz_job_*` (6), `wz_render_*` (6), `wz_diag_*` (5),
  `wz_save_*` (3).
  (source scan of `MapleMcp/**/*.cs`: 17 file(s) read, truncated=False)
```

### 地圖 `wz_map_*` — 22 個

| 工具 | 作用 |
|---|---|
| `wz_map_capabilities` | 後端現在能做什麼、開了哪些封存 |
| `wz_map_list` | 列出封存內的地圖（可搜尋） |
| `wz_map_open` | 開啟地圖，回傳完整結構 |
| `wz_map_create` | 建立新地圖：從量測出的最小骨架，或**在封裝內建立**（.wz 與 NX 皆可） |
| `wz_map_doc` | 已開啟地圖的結構摘要 |
| `wz_map_node` | 讀取單一節點，**找出可寫的葉節點** |
| `wz_map_palette` | 列出素材 set 與 `drawableEntries` |
| `wz_map_place` | 放置圖塊／物件／foothold／傳送門 |
| `wz_map_batch` | 多次編輯 = **單一 undo** |
| `wz_map_validate` | 依規則檢查一張開著的地圖，一次回報所有問題 |
| `wz_map_save` | 寫回磁碟 |
| `wz_map_render` | 把一張地圖算成 PNG（圖塊層、foothold 線、角色的行走軌跡）；數字對但看不出來時用 |
| `wz_map_render_mob` | 把一隻怪／NPC 某個 stance 的每一幀輸出成 PNG，並回報每一幀的 origin、delay、畫布大小 |
| `wz_map_session_open` | 在地圖上放一個活角色，之後用 `wz_map_session_step` 一幀一幀走（自己試路線，不是問一次性的答案） |
| `wz_map_session_step` | 活角色前進一幀（客戶端 60Hz）；方向、速度、跳躍狀態在呼叫之間保留 |
| `wz_map_session_close` | 丟掉活角色、釋放它佔著的記憶體（閒置兩分鐘後也會自動丟） |
| `wz_map_simulate` | 用客戶端的物理走一次地圖：走不走得過去、停在哪、為什麼停 |
| `wz_map_simulate_batch` | 一次跑多張地圖，只列出走不過去的那些 |
| `wz_map_cast` | 畫一張地圖所需的一切：角色的路徑、每一座梯子、每一個可用的傳送門、每個出生點下面有沒有地面 |
| `wz_map_advise` | 角色停在哪就說哪裡不對：哪個 foothold 斷了鏈、link 是缺了還是撞牆、要延伸多遠 |
| `wz_map_ground` | 問某個座標下面是什麼：哪些 foothold 蓋住它、多高、哪些是牆、有沒有能站的地面 |
| `wz_map_patrol_mob` | 跑一次怪／NPC 的巡邏，回報會不會離開地圖；同一個 seed 重播一模一樣 |

### 封裝拆分 `wz_archive_*` — 8 個

| 工具 | 作用 |
|---|---|
| `wz_archive_capabilities` | 後端在線嗎、已經開了哪些封存 |
| `wz_archive_scan` | 列出某個資料夾下的 .wz/.ms，並標出哪些是 PKG1 指標檔 |
| `wz_archive_open` | 開一個 .wz 或 .ms，回傳 session id、偵測到的格式與版本 |
| `wz_archive_close` | 依 session id 關掉一個封存，釋放記憶體 |
| `wz_archive_children` | 列出某個節點的子節點：名稱、種類、節點路徑 |
| `wz_archive_node` | 讀一個節點：型別，若是影像還有屬性名稱 |
| `wz_archive_export_img` | 把一個 image 節點用 MapleLib 的 writer 序列化成獨立的 .img 檔，並回報寫了多少位元組 |
| `wz_archive_split` | 把封裝拆成一個資源一個 .img，寫到磁碟 |

把 split client 的 67 GB `.wz` 封裝拆成一個資源一個 `.img`，
用的是後端自己的序列化器，所以位元組與編輯器存檔一致。
`_Canvas` 預設不拆（見 `Program.cs` 的說明），磁碟剩餘 < 8 GB 會停。

### 通用資產 `wz_asset_*` — 24 個

| 工具 | 作用 |
|---|---|
| `wz_asset_files` | 列出已載入的 WZ/IMG/NX 封存（id 要重用，不要猜路徑） |
| `wz_asset_open` | 開一個 WZ、獨立 IMG 或 NX |
| `wz_asset_open_source` | 開一個信任的來源別名（可選其中的 IMG 檔名） |
| `wz_asset_sources` | 列出 `MAPLEBENCH_SOURCES` 設定的信任來源別名 |
| `wz_asset_close` | 只卸載指定的封存（關掉前先看 `wz_asset_changes`） |
| `wz_asset_save_as` | 存到指定的候選路徑，會留備份 |
| `wz_asset_add` | 在記憶體裡新增一個子節點 |
| `wz_asset_delete` | 在記憶體裡刪掉明確指定的節點 |
| `wz_asset_rename` | 在記憶體裡改名（改名前先看參照） |
| `wz_asset_transfer` | 在已載入的封存之間複製或搬移明確的子樹 |
| `wz_asset_tree` | 把節點樹匯出成 JSON（深度有上限） |
| `wz_asset_search` | 有上限的搜尋：名稱或值 |
| `wz_asset_node` | 讀節點的中繼資料（型別、子節點） |
| `wz_asset_children` | 列出已載入節點的子節點 |
| `wz_asset_set_value` | 改一個既有的純量葉節點（型別必須相容） |
| `wz_asset_set_values` | 用後端的批次操作，把同一個純量值設到 1–300 個葉節點 |
| `wz_asset_undo` | 復原最近一次 session 操作（不一定是你這次做的） |
| `wz_asset_history` | 讀後端的 undo/redo 紀錄（可能有別人的編輯） |
| `wz_asset_changes` | 存檔或關閉前先看未存的變更 |
| `wz_asset_export_img` | 把一個 image 子樹匯出成獨立的候選 IMG |
| `wz_asset_export_canvas` | 把一個 canvas 解碼後匯出成候選 PNG |
| `wz_asset_replace_canvas` | 把本機圖片塞進既有的 canvas（只在記憶體裡） |
| `wz_asset_create_empty` | 造一個全新的空白 .wz 或 .nx 容器 |
| `wz_asset_merge_nx` | 把獨立的 .img 檔合併進既有的 NX 封裝（見下面專章） |

不依賴「目前選中的地圖」，直接操作任意 WZ/IMG/NX 節點。
`wz_asset_sources` 需要設定 `MAPLEBENCH_SOURCES`；未設定會明確報錯，
不會假裝成功。

### 模擬器（`wz_map_*` 的一部分）— 6 個（地圖物理，不是本專案的模擬器）

| 工具 | 作用 |
|---|---|
| `wz_map_simulate` | 用客戶端的物理走一次地圖：走不走得過去、停在哪、為什麼停 |
| `wz_map_simulate_batch` | 一次跑多張地圖，只列出走不過去的那些 |
| `wz_map_patrol_mob` | 跑一次怪／NPC 的巡邏，回報會不會離開地圖 |
| `wz_map_cast` | 畫一張地圖所需的一切：角色的路徑、梯子、傳送門、出生點 |
| `wz_map_advise` | 角色停在哪就說哪裡不對、該怎麼修 |
| `wz_map_ground` | 問某個座標下面是什麼 |

**與 `wz_map_session_*` 一起看**：這六個走的是**地圖編輯器的物理**，
回答「這張地圖角色走不走得過去」。要回答「施放技能之後怪剩幾滴血」，
用的是下面的 `wz_sim_*`。兩者名字相近、用途完全不同，是本專案最容易誤用的一組。

### 現場模擬器 `wz_sim_*` — 12 個（2026-10-17 新增）

| 工具 | 作用 |
|---|---|
| `wz_sim_open` | 放一個持續存在的現場角色，後續呼叫都用它回傳的 `sessionId` |
| `wz_sim_step` | 讓現場角色前進 N 幀（預設 1，最多 60），並回報期間命中的揮擊 |
| `wz_sim_cast` | 只要求下一幀施放某技能：最小的一次呼叫就回答「那次施放有沒有事」 |
| `wz_sim_state` | 不推進地讀現場：角色在哪、hp/maxHp、現場的怪、熱鍵、腳下是什麼 |
| `wz_sim_summon` | 在現場生出 N 隻怪（x 落在角色的 100px 內，落在地圖可走範圍外就掉到那裡） |
| `wz_sim_despawn` | 收掉這個 session 召喚的全部怪，回報收掉幾隻 |
| `wz_sim_skills` | 整組取代（或清空）現場的熱鍵表 |
| `wz_sim_equip` | 換現場角色手上的東西 |
| `wz_sim_control` | 切換現場的開關：無敵／復活／把輸入按住 |
| `wz_sim_catalog` | 唯讀探索：可模擬的地圖、可召喚的怪、真正能施放的技能、外觀槽位、怪的資料 |
| `wz_sim_probe` | 某個點下面是什麼 foothold、哪裡是牆、有沒有能站的地面 |
| `wz_sim_close` | 丟掉現場 session、釋放它佔著的記憶體，並回報它本來在不在 |

**檔案 id 不穩定**：路徑開頭的 `fN` 是封存的**工作階段 id**，依開啟順序發號。
實測同一個 `Map.nx` 在一次執行是 `f4`，下一次是 `f25`。寫死會得到
`No open file with id 'f4'`，看起來像地圖不見，其實不是。
**先呼叫 `wz_asset_files` 或 `wz_sim_catalog kind=maps`，用它回傳的 id。**

這些工具**不帶 `commit`**：現場模擬器在記憶體裡、依 id 存放、關掉就沒了，
和其他家族「寫進封存」的風險完全不同。這個例外寫在類別的 remarks 裡。

### 遊戲資料域 `wz_data_*` — 6 個（2026-10-17 新增）

| 工具 | 作用 |
|---|---|
| `wz_data_capabilities` | 一次回報五個網域現在能做什麼（Skill.wz／Mob.wz／Npc.wz／String.wz／db） |
| `wz_data_list` | 瀏覽一個網域（以 `kind` 分流，見下表） |
| `wz_data_detail` | 讀一筆的完整內容（以 `kind` 分流，見下表） |
| `wz_data_write` | 改一筆並提交（以 `kind` 分流，見下表） |
| `wz_data_bulk` | 對很多筆套同一個操作，**先預覽**（以 `kind` 分流，見下表） |
| `wz_data_search` | 一次搜尋客戶端知道的所有 id 與名稱（跨物品、怪、技能、NPC、地圖） |

**四個分流工具的 `kind` 各自不同，四組都不一樣**（來源：`MapleMcp/DataDomainTools.cs`
的常數 `ListKinds`／`DetailKinds`／`WriteKinds`／`BulkKinds`）：

| 接受的 `kind` | 工具 | 來源 |
|---|---|---|
| `skill` `mob` `npc` `string` `books` `cashshop` | `wz_data_list` | `MapleMcp/DataDomainTools.cs:65`（`ListKinds`） |
| `skill` `mob` `npc` `string` `item` | `wz_data_detail` | `MapleMcp/DataDomainTools.cs:66`（`DetailKinds`） |
| `skill` `npc` `string` | `wz_data_write` | `MapleMcp/DataDomainTools.cs:67`（`WriteKinds`） |
| `skill` `npc` `string` | `wz_data_bulk` | `MapleMcp/DataDomainTools.cs:68`（`BulkKinds`） |

另外兩個不靠這組 `kind` 分流：`wz_data_capabilities` 不收參數；
`wz_data_search` 的 `kind` 是 **String.wz 的種類**（`item` `mob` `skill` `npc` `map`，
用來縮小範圍），不是上面那組分流種類。

**為什麼不直接用 `wz_asset_*`**：這六個域有自己的欄位驗證、自己的批次語義、
自己的 DTO。繞過 `/api/node/value` 直寫，會寫出該域根本載不進去的值。

### 長時間工作 `wz_job_*` — 6 個（2026-10-17 新增）

| 工具 | 作用 |
|---|---|
| `wz_job_capabilities` | 這台伺服器能驅動的工作家族、八種 `kind`、每個階段背後的路由、每個 start 路由接受的選項 |
| `wz_job_start` | 以 `kind` 開始八個工作家族之一 |
| `wz_job_progress` | 以 `kind` 輪詢一個工作中的工作 |
| `wz_job_result` | 以 `kind` 讀一個已完成工作的結果 |
| `wz_job_report` | 以 `kind` 讀另一個終態讀取：audit/dump 的發現報告、compose 的帳本、repair 的掃描判詞、repair.genchoice 的逐項衝突報告 |
| `wz_job_cancel` | 要求任何 `kind` 的工作停下來 |

以 `kind` 分流八個工作家族：`audit` `dump` `compose` `import`
`repair.canvas-format` `repair.canvas-dir-link` `repair.donor-restore`
`repair.genchoice`。

這八個家族合計 **55 個 (method, path) 註冊 / 52 條路徑**，形狀完全一樣（開始／進度／取消／結果）。
（2026-10-24 量，branch `map-generation-baseline` commit `a83181cd`；母體與指令見上面
「後端今天有 227 條不同的路由路徑」那一段。這一組的前綴是 `/api/audit`、`/api/dump`、
`/api/compose`、`/api/import`、`/api/repair`：

    (Select-String -Path MapleBench/Api/*.cs -Pattern 'Map(Get|Post|Put|Delete|Patch)\s*\(\s*"/(audit|dump|compose|import|repair)' -AllMatches).Matches.Count
      55

52 條路徑那一欄由同一支指令稿解 group 前綴後得到；**舊的「56 條路由」不是任何母體的
數** —— 這八個家族在 repo 裡每一個存在的 commit（2026-10-12 到今天）都是 55 / 52，
所以那個 56 沒有產生器，也不該留著。）
`repair.*` 會寫封存，所以 `start` 帶 `commit`。
某個 kind 沒有某一階段時**拋錯並列出有的**，絕不偷偷換一條路由。

### 算圖與輸出 `wz_render_*` — 6 個（2026-10-17 新增）

| 工具 | 作用 |
|---|---|
| `wz_render_view` | 把**一個** canvas 節點算成 PNG 內嵌回傳（base64），寬高從 PNG 檔頭讀 |
| `wz_render_preview` | 一次列出某個節點底下的所有 canvas：影格、尺寸、路徑 |
| `wz_render_thumb` | 任何節點取一張代表用的 PNG（自己的 canvas，或底下找到的第一個） |
| `wz_render_animation` | 檢視一個動畫節點（影格、延遲、原點） |
| `wz_render_dump` | 把一個 dump 產物下載成檔案（`canvas`／`sound`／`link`／`csv`） |
| `wz_render_export` | 把一個子樹匯出成檔案（`img`／`node-img`／`json`／`xml`／`xml-zip`／`images`） |

**上限句**：算出一張圖只證明**這個節點解得出來**，
不證明像素跟對標一致，也不證明這是對的圖。

### 診斷與存檔 `wz_diag_*` `wz_save_*` — 8 個（2026-10-17 新增）

| 工具 | 作用 |
|---|---|
| `wz_diag_health` | 三個便宜的健康檢查一次回報：版本與授權、模擬器自己的診斷、行程佔了多少記憶體 |
| `wz_diag_clientlog` | 讀／清／追加瀏覽器頁面自己的 console 報告 |
| `wz_diag_sim` | 模擬器與動畫稽核自己的 `/debug` 路由（以 `kind` 分流） |
| `wz_diag_facts` | 後端對**單一節點**能斷言的事實 |
| `wz_diag_audit` | 客戶端完整性稽核的報告（`plan=true` 時是它會看什麼） |
| `wz_save_preflight` | 後端自己對一個封存的拒絕預覽 |
| `wz_save_commit` | 就地存檔（`POST /api/files/save`） |
| `wz_save_memory` | 行程手上資源的四個把手：現在有什麼、釋放它、記憶體壓力下檢查、切換唯讀／可編輯 |

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