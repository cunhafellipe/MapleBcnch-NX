# 交接文件

**交接日期**：2026-10-11
**專案位置**：本倉庫根目錄
**分支**：`map-generation-baseline`（最新 `1da3f35`，工作區乾淨）
**上游**：`https://github.com/xkiro-dev/MapleBench`

> **⚠️ 2026-10-13 起先讀 [`工作室守則.md`](工作室守則.md)。**
> 那份記錄團隊分工規範、踩過的坑（合成截圖不算證據、資料量不等於驗證、
> 先確認服務的是哪一份建置），以及模擬器目前的真實狀態。
> 本文件早於那份，未更新。

---

## 一、這份文件怎麼用

**你是接手這個專案的 agent 或工程師。** 這裡回答四個問題：

1. 這個專案在做什麼？（§1）
2. 做到哪一步了？（§2）
3. 接下來要做什麼？為什麼是這件？（§3）
4. 有哪些坑是我踩過、你不用再踩的？（§4）

**如果只有 10 分鐘**：讀 §1、§2.3、§3.1、§4.1。

**不要跳過 §4**。這個專案最大的特徵是「錯誤的結論看起來很有說服力」，
上一任 agent（包括我）都犯過，文件裡留著兩次實例。

---

## 1. 專案在做什麼

MapleStory 的 WZ/NX 檔案編輯器。`.wz` 是 Maplesoft 的私有格式，
`NX` 是後來的反向工程格式（沒有官方結構定義，靠實測摸出來）。

**使用者的最終目標**：與 AI agent 溝通，讓它依提示詞、
用現成的 WZ/NX 素材，生成指定風格的地圖。

地圖是橫向卷軸遊戲的地圖——一條可走的路，兩旁是地形。
**一張能看的地圖不等於能玩的地圖**，這是整個專案的核心張力，
下面的每一個技術決定都圍繞它。

---

## 2. 現在做到哪裡了

### 2.1 這一輪（2026-10-11）做的事

| # | 做什麼 | 憑什麼 | 驗收證據 |
|---|---|---|---|
| P0-1 | `PaletteSets` 回報素材真的有像素（`drawableEntries`） | AI 選素材需要誠實的可用性 | 109/109 set 有像素、有縮圖 |
| P0-2 | `/validate` 端點：地圖規則檢查 | 斷鏈在畫面上看不出來 | 故意切斷→抓到→undo 修復；10/10 抽樣地圖與人工分析一致 |
| P1-1 | `/batch` 端點：多次編輯 = 單一 undo | undo 堆疊只有 200 格 | 300 操作→歷史只加一筆→單次 undo 全撤 |
| P2-2 | **`MapleMcp`：10 個地圖工具的 MCP 伺服器** | 舊 MCP 有 18 工具但 0 個能操作地圖 | 只經 MCP 跑完整流程 ALL PASSED |
| — | 地圖結構規格（10 張地圖解剖） | 後續所有工作的依據 | `docs/MAP-STRUCTURE.md` |

### 2.2 上一輪（2026-10-11 稍早）做的事

修好了兩個 P0：放置圖塊後存檔會殺行程（連續兩次存檔 + 冷啟動重讀驗證通過）、
i18n 工具在說謊（回報「已翻譯」的其實是從沒渲染過的字串碎片）。

### 2.3 現在 agent 能做到什麼

```
✅ 列舉地圖（5366 張，含中文名稱）
✅ 讀取地圖結構（層、圖塊、foothold、傳送門、怪物）
✅ 選素材（109 個 tile set，含 drawableEntries）
✅ 放置圖塊/物件/foothold
✅ 批量鋪地面（單一 undo）
✅ 自我檢查（斷鏈、無效引用、link stub）
✅ 存檔（會驗證）
❌ 生成可玩的 foothold ← 缺這個
```

**最後一項是唯一的缺口**，也是下一輪的工作。

---

## 3. 下一步要做什麼

### 3.1 foothold 生成器 🔴

**為什麼是這件**：實測確認 `/auto-foothold` 依賴素材自帶的 Convex 幾何，
而真實的 `Map.nx` 裡**沒有任何素材帶這個東西**：

```
掃 25 個 Tile set → 帶 foothold 幾何的：0
掃 12 個 Obj set  → 帶 foothold 幾何的：0
```

（呼叫時系統的回應很棒：「寧可什麼都不做，也不猜。」）

所以 agent 鋪完地面之後**沒有東西讓角色站住**。地圖看起來對，
一走就掉出去。這是「能看」與「能玩」的差別。

**要做什麼**：從已鋪的圖塊輪廓推導 foothold 線段集合。

**必須遵守的三條規則**（都來自 `docs/MAP-STRUCTURE.md` 第 5 節）：

1. **foothold 的 layer 跟著視覺圖層走**，不是固定值。
   實測：東部岩山 `L0=21 L1=70 L2=14 L3=40`，深海峽谷 `L4=156`。
   （我最初以為固定 layer 4，抽到第二張地圖就推翻了。）
2. **`prev`/`next` 雙向鏈零斷裂**。鏈是角色的行走路徑。
   實測所有既有地圖的斷鏈數都是 0。
3. **斜坡是主流**。深海峽谷 142/156 是斜坡，東部岩山 81/145。
   這是橫向卷軸地圖的本質——連續傾斜面，不是階梯。

**意外的好消息**：因為 foothold 就是「線段 + 鏈」，
**反向重力（天花板行走）= 鏈的 y 翻轉，磁力牆 = 鏈的 x 翻轉**。
使用者的太空／特殊重力場景需求，現有架構天然支持，不必重寫。

**驗收**：鋪一片地面 → 自動產生 foothold → `/validate` 回報
`brokenChains=0` → 存檔 → 冷啟動重讀 → `/validate` 仍為 0。

### 3.2 其他（順序隨意）

| # | 項目 | 狀態 |
|---|---|---|
| 範本機制 | 從現有地圖提取結構與風格當模板 | 原料已齊：5366 張地圖、109 個 tile set、一張地圖的 doc 回傳 name/bgm/bounds/layers/footholds |
| CLI 地圖命令 | `tilefit` 等 4 個仍走舊的 `back/*/bS`，對 NX 不適用 | 需要改寫或明確標示只支援 .wz |
| `Mob.nx` 壓測 | 818 MB，未測 | 見 §4.4 的記憶體警告 |
| 188 條無用翻譯條目 | 字典有、沒人用 | 不影響執行 |

---

## 4. 踩過的坑（別再踩）

### 4.1 ⚠️ 「API 在說謊」——其實是我測錯了

我曾寫進規格文件：`ancientForest` 是無效素材，`sets` 端點給了縮圖
但實際沒有視覺 canvas，放置會寫入看不見的空引用。

**這個結論是錯的，錯了兩層：**

1. 我用 `bsc/10` 測試——**那個編號本來就不存在**。
   系統回應完全正確：精確指出是哪個參數解析不到。
2. 我把 `/palette/entries` 的 `path` 傳成**地圖路徑**，
   得到「這張地圖用到的 9 筆」而非「這個 set 的全部 18 筆」。

真相：`ancientForest` 有 18 個 90×60 的真實素材，`bsc/0` 存在。

**教訓**：懷疑程式碼之前，先確認自己的測試有沒有做對。
這個專案裡 API 的錯誤訊息品質很高——**要讀它**。

（更正保留在 `docs/MAP-STRUCTURE.md` 第 6.1 節，
連同我的錯誤原文一起留著。）

### 4.2 ⚠️ 會靜默跳過的測試比沒有測試更危險

`tools/probe/acceptance.mjs` 第一次跑報「11/11 通過」，
但 batch 那三項**根本沒執行**——它找可編輯節點沒找到就跳過了。

而且我的斷言也寫錯：該比對批次前後的深度差異，不是絕對值 1。

**後來連續三次測試失敗，前兩次都是測試本身錯。**

改成「找不到就報失敗」之後才暴露真相。

### 4.3 ⚠️ 只有葉節點能編輯

`setValue` 指向容器會被拒：

```
"'0' is a SubProperty and its value cannot be edited here."
```

型別也不能改：

```
"'y' is an Int and 'bsc' is not a number, so writing it here would have to retype the node."
```

這是為什麼有 `wz_map_node` 工具——讓 agent 能先讀子節點找到葉節點。
否則它只能靠送出錯誤的編輯、再從錯誤訊息裡學。

### 4.4 ⚠️ 記憶體

實測數據（`Map.nx` 679 MB）：

```
開檔              0.5 秒
存檔              10 秒
存檔時工作集峰值  14.76 GB
存檔後回落        3.06 GB
```

**`Mob.nx`（818 MB）沒有測過**，因為當時本機只剩 19.6 GB 閒置。
硬測只是把測試機推去 OOM，不能拿來判定程式對錯。

**要測之前先查 `Get-PSDrive C` 的可用空間。**

### 4.5 ⚠️ 路徑含中文時 Node 的 argv 會壞掉

截圖腳本用相對路徑或純 ASCII 輸出，否則會寫到磁碟機根目錄。
`agent-wz` → `MapleBcnch-NX` 搬家時有 29 個檔案有硬編碼舊路徑。

---

## 5. 怎麼跑起來

### 5.1 環境

```
.NET SDK 10.0（`dotnet` 需在 PATH，或設 `DOTNET_PATH`）
Node 24（驗證腳本用）
Chrome（視覺驗收用，headless + CDP）
```

### 5.2 建置與啟動

```bash
cd MapleBcnch-NX
dotnet build MapleBenchX.sln -c Release

# 開發模式（熱重載 + Debug 面板）
dotnet run --project MapleBench -- --port 5100
```

### 5.3 驗證（**改完東西就跑，不要只說「測試通過」**）

```bash
# 完整驗收：15 項
# 先啟後端並開啟 Map.nx，然後：
node tools/probe/acceptance.mjs http://127.0.0.1:5100 acceptance.json

# MCP 完整流程：只經 MCP，不碰 HTTP
WZ_SERVER=http://127.0.0.1:5100 \
node tools/probe/mcp-flow.mjs \
     MapleMcp/bin/Release/net10.0-windows/maple-mcp.exe \
     "<Map.nx 絕對路徑>"

# 翻譯完整性：必須 exit 0
node tools/probe/i18n/i18n-inventory.js

# 視覺驗收（需要 headless Chrome）
node tools/visual-check/editor.mjs http://127.0.0.1:5100 <mapPath> out.png
```

### 5.4 註冊 MCP 給 agent

```bash
# 命令： wz-mcp-map.cmd
# 或直接 MapleMcp/bin/Release/net10.0-windows/maple-mcp.exe
# 環境變數 WZ_SERVER 可改後端位址
```

---

## 6. 專案結構

```
MapleBcnch-NX/
├── MapleBench/          主程式（C# / ASP.NET Minimal API）
│   ├── Api/             HTTP 端點（31 個 mapedit）
│   ├── Services/
│   │   ├── MapEditor/   地圖編輯核心（Validate.cs 是本輪新增）
│   │   ├── MapModel/    WzNode / MapDocument —— 資料模型
│   │   └── Nx/          NX 格式處理
│   └── wwwroot/         前端（原生 JS，無框架）
├── MapleMcp/            MCP 伺服器（本輪新增）
├── dependencies/        MapleLib —— WZ 格式解析
├── docs/
│   ├── MAP-STRUCTURE.md ⭐ 地圖結構規格，所有規則的出處
│   └── CLI-AND-MCP.md   CLI/MCP 現況與缺口
├── JOURNAL/             開發日誌
├── tools/               驗證工具
└── scripts/             發佈腳本
```

### 必讀文件

| 文件 | 什麼時候看 |
|---|---|
| `docs/MAP-STRUCTURE.md` | **動地圖編輯之前必讀**。foothold、圖塊、背景、物件分類的實測規格。第 10 節是端點參數速查表（我踩過的坑） |
| `docs/CLI-AND-MCP.md` | 要動 agent 介面時 |
| `MapleMcp/README.md` | 要改 MCP 工具時 |
| `JOURNAL/README.md` | 從哪裡開始讀 |
| `JOURNAL/TODO.md` | 已知問題與優先順序 |

---

## 7. 寫程式碼時要遵守的

### 7.1 行尾

```
CRLF（工作區實體），.gitattributes 會在索引裡轉 LF
```

**不要用會轉換行尾的編輯方式**。上一任 agent 的
`edit-preserving-eol.js` 就是為了解這個問題而存在
（但它還有個已知缺陷：插入時會多出空行，見 TODO P3-5）。

### 7.2 註解要寫「為什麼」，不是「是什麼」

這個專案的註解密度很高，而且解釋的是**為什麼這樣做**。
新增程式碼要跟上這個標準，否則會變成噪音。

特別是：**不要寫「已驗證」除非你真的驗過。**
專案裡到處是宣稱已驗證的註解，其中不少是錯的——
**包括我自己寫的**（§4.1）。

### 7.3 日誌規範

`JOURNAL/README.md` 有兩條要求：

1. **只寫驗證過的事，並說明怎麼驗的。**
   「測試通過」等於沒寫。
2. **沒做的事要寫下來。**

---

## 8. 誠實聲明

以下是我不確定、沒有驗證過的事，交接時要記得：

| 事項 | 狀態 |
|---|---|
| 為什麼新版不沿用舊的 CLI/MCP | **推測**是重寫時聚焦 GUI 與 HTTP API，agent 介面還沒接上。沒查證 |
| 縱向地圖（比例 <1）的背景捲動是否不同 | **未驗證**。本輪樣本只落在 Map0–Map2 |
| `background.type`（層級）的確切語意 | **未驗證** |
| `background.inLayer` 為 false 時如何錨定 | **未驗證** |
| 中文地圖名搜尋為何回 0 筆 | **根因未查**。已知 `/maps?limit=10` 能拿到中文名 |
| `MemoryPressureService` 是否接上 NX 路徑 | **未確認** |

---

## 9. 給接手者的第一句建議

**先跑一次驗收，確認現況和你看到的一樣。**

```bash
dotnet build MapleBenchX.sln -c Release
node tools/probe/mcp-flow.mjs ...   # 應該 ALL PASSED
```

如果**不是** ALL PASSED，先查環境（後端有沒有開、Map.nx 路徑對不對），
再懷疑程式碼。

然後從 §3.1 開始做 foothold 生成器。動手前**先把
`docs/MAP-STRUCTURE.md` 第 5 節讀完**——那三條規則都是實測出來的，
猜的話會做出跟遊戲對不上的地圖。

---

## 10. 快速索引

| 想做的事 | 看哪裡 |
|---|---|
| 理解地圖資料怎麼長 | `docs/MAP-STRUCTURE.md` |
| 加 HTTP 端點 | `MapleBench/Api/MapEditorEndpoints.cs` |
| 改地圖編輯邏輯 | `MapleBench/Services/MapEditor/` |
| 加 agent 工具 | `MapleMcp/MapTools.cs` |
| 驗證 | `tools/probe/`、`tools/visual-check/` |
| 查已知問題 | `JOURNAL/TODO.md` |
| 了解為什麼是現在這樣 | `JOURNAL/2026-10-11-generation.md` |