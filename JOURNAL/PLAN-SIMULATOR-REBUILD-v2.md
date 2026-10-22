# 計畫：讓互動模擬器真的存在（2026-10-13 重建版）

取代 `PLAN-SIMULATOR-REBUILD.md` 舊版。舊版是分階段宣告完成的計畫，而其中 S1/S2/MCP
三階段的成果並不存在於從零重建的程式碼裡 —— 詳見本文「盤點結果」。

## 為什麼要重寫這份

今天有兩件事同時壞掉，而且互相掩蓋：

1. **互動迴圈沒有真的接上。** `SimulatorEndpoints.cs` 與 HEAD 零差異，session 端點從來沒留在檔案裡。
2. **我一直在測幽靈。** 舊行程（PID 33652，3.1 GB 記憶體）還載著當時記憶體裡的 DLL，所以 HTTP 一直回 200、sessionId 一直有值、MCP 一直握手成功。DLL 重建後端點消失，舊行程卻還活著 —— 於是「驗證通過」持續產生，而對應的程式碼不存在。

第 2 點比第 1 點嚴重。它不是缺功能，是**驗證機制本身失效**：唯一能證明程式碼存在的證據（跑得起來）被一個已經不存在的行程頂替了。

因此第一條規則不是功能，是**誠實**：

> 每一步都必須對著「從零重建的程式碼」驗證。不得信任任何在重建之前就啟動的行程。

## 盤點結果（乾淨重建後實測）

| 項目 | 宣稱 | 實況 |
|---|---|---|
| session 端點 | 完成 | 路由表 12 條，全無 session |
| singleton 註冊 | 完成 | `Program.cs` 沒有 |
| 出生點照抄 SpawnNew | 完成 | 邏輯在檔案內，但無路由可達 |
| `livewalk.js` | 完成 | 檔案存在，`simulator.js` 沒有 import |
| MCP 三工具 | 驗證通過 | 工具在 `SessionTools.cs`，端點不存在→測不到 |
| 互動 73 幀實測 | 驗證通過 | 測在幽靈行程上 |
| 物理 `FindWall` / `Angle256` | 完成 | 驗收 28/29，**真的** |

只有物理修正與驗收是真的。

## 依賴現況（已確認存在，可直接用）

| 需要的能力 | 本地既有 | 出處 |
|---|---|---|
| portal 解析 | `FootholdPhysics._portals` | `FootholdPhysics.cs:114` |
| 出生 portal 判斷 | `Portal.IsSpawn => Type == 0` | `FootholdPhysics.cs:502` |
| 找腳下地面 | `FootholdPhysics.FindBelow(int,int,bool)` | `FootholdPhysics.cs:330` |
| 編輯器鎖 | `MapEditorService.EditorGate` | `SimAccess.cs:27` |
| 讀地圖 | `LoadForReadingShared(path)` | `SimAccess.cs:32` |

**本專案不需要自創任何出生點邏輯**，只需照抄 MapleNecrocer。

## 參考專案對照表（照抄來源）

`MapleCharacter.cs:162-176`：

```csharp
int PX = 0, PY = 0;
foreach (var Portals in MapPortal.PortalList)
    if (Portals.Type == 0) { PX = Portals.X; PY = Portals.Y; break; }
Vector2 Below = FootholdTree.Instance.FindBelow(new Vector2(PX, PY - 2), ref BelowFH);
```

兩個容易被自創搞錯的細節：

- 預設是 **(0, 0)**，是地圖原點，**不是**最左邊 foothold
- 探針是 `FindBelow(PX, PY - 2)`，在 portal **上方 2 像素**

## 階段

### T1 端點真正進檔案
- [x] `SimulatorEndpoints.cs` 加 4 條 session 路由（二進位寫入，已驗證路由表 16 條）
- [ ] `MapSimSessionStore.Peek(string id)` —— 端點引用了但 store 沒有此方法
- [ ] `Program.cs` 加 `AddSingleton<Services.Simulate.MapSimSessionStore>()`
- [ ] 確認 `MapSimulator` 路由有被 `Map()` 呼叫
- [ ] **驗收**：`grep` 路由表確認 session 在裡面，然後 `dotnet build` 0 error

### T2 從零建置 + 掃路由表
- [ ] `taskkill` 所有 MapleBenchX / dotnet 殘留
- [ ] `rm -rf` 輸出目錄後全新建置
- [ ] 啟動伺服器
- [ ] **驗收**：逐條 POST 測試 session 端點，確認不是 404

### T3 出生點實測
- [ ] 不給座標開 session（走 portal-first 路徑）
- [ ] 對照：參考專案同一張圖的出生 portal 座標
- [ ] 確認 `sessionId` 非空且有 foothold
- [ ] **驗收**：10 張地圖批量開啟，記錄成功率與失敗原因

### T4 真互動
- [ ] `simulator.js` 加 `import { LiveWalk }` 並在載入地圖時 `live.start()`
- [ ] **驗收**：瀏覽器按 ArrowRight / ArrowLeft，x 必須真的變化；canvas 必須有地圖美術（非空地圖）

### T5 MCP 端到端
- [ ] `WZ_SERVER` 指向新伺服器
- [ ] `wz_map_session_open` → `step` ×N → `close`
- [ ] **驗收**：方向會反轉、速度會換號、跳躍會起跳

### T6 日誌
- [ ] 記錄實際數字，不是「應該」

## 硬規則（本次學到的）

1. **建置後必須殺掉舊行程再啟動。** 否則測的是舊 DLL。
2. **驗收前先掃路由表。** HTTP 200 可能是幽靈行程回的。
3. **用 git diff 確認改動真的留在檔案裡。** `patch` 報成功不代表寫進去了。
4. **沒有結果不等於不存在。** 第一次 grep 失敗就宣稱「沒有建構子」是錯的。
5. **有參考專案就照抄。** 不要自創等價物再自己驗證它。

## 環境坑（會再踩）

| 坑 | 症狀 | 解法 |
|---|---|---|
| `maple-mcp.exe` 綁系統 dotnet（僅 6/8） | `You must install .NET` | 用 scoop 的 `dotnet.exe maple-mcp.dll` |
| 後端位址環境變數 | 直覺猜錯 | 實際是 `WZ_SERVER` |
| 從 scratch 啟動伺服器 | `mapCount: 0` | 需先 `POST /api/files/open-many` 開封存 |
| `--simdata` | 無效參數 | 專案沒實作此參數 |
| `acceptance.mjs` | `stdin is not a tty` | 需要 TTY 才能跑 |