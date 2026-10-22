# HANDOFF — MapleBench 模擬器 vs MapleNecrocer 對齊

> **給從未接觸過這個專案的 agent。** 讀完這份就能開始做，不用重走任何一輪弯路。
>
> 對應工作階段：2026-10-14 使用者測試回報 6 個缺陷 + 2 個要補的功能，已修 4 項。

---

## 1. 這是什麼

`MapleBenchX` 是一個 **WZ/NX 檔案瀏覽與地圖模擬器**（ASP.NET Minimal API + 原生 JS）。
其中「模擬器」這一段的目標是：**逐項對齊 `MapleNecrocer` 的角色/怪物行為**。

原則只有一句：**「它有什麼，你一個都不能少；它沒有的，你就不用有」**。
所以每個功能都要能在對標原始碼找到對應行號，找不到就是自己發明的，要嘛刪掉要嘛明說。

---

## 2. 絕對不要做的事（違反會毀掉別人的工作）

| 禁忌 | 原因 |
|---|---|
| **不要 kill / restart `MapleBenchX` 的 pid 3740（port 5480）** | 那是主線正在用的實例。它**持有 `bin/Release/net10.0-windows/MapleLib.dll` 的鎖**，殺掉它會讓 `bin/` 建置失敗 |
| **不要 port 5480、5590** | 別人正在用。要測試就開新 port（5591 是我這輪用的） |
| **不要 kill 所有 dotnet** | 一律用 `Where-Object {$_.Id -ne 3740}` 排除 |
| **不要開/關任何瀏覽器** | ~~使用者自己開自己關~~ **2026-10-14 使用者推翻此條**：改為「每次完整修改後主動開一次編輯器」。見下方 §11 |
| **不要用 `dotnet`（PATH 上的）** | 那是 SDK 8，build 這個 net10.0 專案會失敗。真 SDK 在 `C:/MUWORK/app/scoop/apps/dotnet-sdk/10.0.401/dotnet.exe` |
| **不要跑 `MapleBenchX.exe`** | 會去系統 runtime 解析 → `You must install .NET 10`。**一律 `dotnet.exe MapleBenchX.dll --port N --allow-multiple --no-browser`** |
| **不要加 `-p:BaseIntermediateOutputPath=...`** | 會產生兩份 AssemblyInfo，報 22 個 `CS0579` |
| **不要用合成截圖當證據** | 見第 6 節，這專案已經被這件事坑過好幾輪 |

---

## 3. 環境事實

| 項目 | 值 |
|---|---|
| 專案根 | `C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX-audit\` |
| 程式碼 | `MapleBench\`（csproj = `MapleBenchX.csproj`） |
| **服務的是 bin 不是原始檔** | 伺服器提供 `MapleBench\bin\Release\net10.0-windows\wwwroot\`。**改 `wwwroot\` 不會生效**，要複製過去 |
| 測試建置輸出 | `C:\Users\e7896\AppData\Local\hermes\cache\scratch\build-check\` |
| 暫存腳本 | `C:\Users\e7896\AppData\Local\hermes\cache\scratch\` |
| WZ 資料 | `C:\MUWORK\GAME\MPSHS\`（**注意是 MPSHS 不是 MPSH**），`.nx` 檔 |
| **對標 C#** | `C:\MUWORK\GAME\_necrocer_src\MapleNecrocer\` |
| **對標 C++（最終權威）** | `C:\MUWORK\GAME\MPSH\clean-client\src\` |
| git 分支 | `map-generation-baseline`，**265 個未提交檔案**（不要 reset、不要 clean） |
| 開機 port | 5480 = 主線；5591 = 本輪測試實例 |

### 開一個可用的測試實例

```bash
powershell -NoProfile -Command "Get-Process -Name 'dotnet','MapleBenchX' -EA SilentlyContinue | Where-Object {\$_.Id -ne 3740} | ForEach-Object { try { \$_.Kill() } catch {} }"
export DOTNET_ROOT="/c/MUWORK/app/scoop/apps/dotnet-sdk/10.0.401"
cd "C:/MUWORK/GAME/MAPLESOTRY/wz工具/MapleBcnch-NX-audit/MapleBench"
dotnet.exe build MapleBenchX.csproj -c Release \
  -o "C:/Users/e7896/AppData/Local/hermes/cache/scratch/build-check" \
  -p:UseSharedCompilation=false --nologo -v q
cp -r wwwroot/. "C:/Users/e7896/AppData/Local/hermes/cache/scratch/build-check/wwwroot/"

# 背景啟動（用 background=true，不要用 nohup）
cd "C:/MUWORK/GAME/MPSHS"
dotnet.exe ".../build-check/MapleBenchX.dll" --port 5591 --allow-multiple --no-browser
```

啟動後要 **載入檔案**才會有資料：

```python
POST /api/files/open-many
{"paths":[".../Character.nx",".../Base.nx",".../String.nx",".../Map.nx",
          ".../Mob.nx",".../Npc.nx",".../Item.nx",".../Skill.nx",".../Quest.nx"]}
```

### 必做的「你測的是哪一份」檢查

改完 JS 後，**md5 三處必須一致**，否則你在測舊檔：

```bash
md5sum MapleBench/wwwroot/js/walkview.js
md5sum MapleBench/bin/Release/net10.0-windows/wwwroot/js/walkview.js
curl -s http://127.0.0.1:5591/js/walkview.js | md5sum
```

這專案已經浪費掉一整輪在「改了沒生效」上，原因是根本沒建置。

---

## 4. 驗證 API 的正確用法（我自己錯過兩次）

**輸入要放在 `/frames` 的 body 裡，不是 `/input`。**

```python
POST /api/simulate/session          {"path": "f4/Map/Map1/100000004.img"}
                                   → {"sessionId": "...", "x":..., "y":..., ...}
POST /api/simulate/session/{id}/frames
     {"count": 1, "first": {"right": true}, "then": {"right": true}}
```

- `MapSimInput` 的欄位是 **bool**：`left / right / up / down / jump / attack`。
  送 `{"right": 1}` 會 400。**跳躍是 `jump`，不是 `up`**（`up` 是往上爬梯）。
- **回應是扁平的 frame 本體**（`livewalk.js:253` 直接讀 `f.x` / `f.mobs`），
  不是 `{frame: {...}}` 包裝。改這個形狀會直接破壞瀏覽器。
- **`count` 靜默夾在 60。** 現在有 `ticksRequested / ticksRun / clamped` 三欄可辨識。
  要跑 600 tick 得分 10 批呼叫。
- **回應是在「最後一幀」拍的，不是逐幀。** 要逐幀追蹤必須 `count:1` 逐次呼叫。
- 常用端點：
  `GET /api/simulate/look/attack-stances?sessionId=&attack=`（0–4）
  `POST /api/simulate/session/{id}/weapon` `{"image":"01302000.img"}`
  `POST /api/simulate/session/{id}/summon` `{"mobId":"100100","count":5}`
  `POST /api/simulate/session/{id}/despawn`
  `POST /api/simulate/look`（整體外觀合成）

---

## 5. 這一輪完成的項目

### ✅ 5.1 `FindWall` 比較方向寫反 —— 每幀瞬移 90px、飛出地圖

**這是這一輪最大的 bug。** 使用者回報的「亂飛」和「掉到地圖外面」是同一件事。

`FootholdPhysics.FindWall` 問的是「整張地圖上下一个牆在哪」，
而且**沒有距離上限**。探針在 x=1716 時回傳了 x=3189 的地圖邊界牆（相距 1473px），
接著 `Walk` 的牆推擠 `_x = w2.X1 + 1` 把角色瞬間傳送到 3190，
再被 foothold 交接拉回 `step.X1 = 1800`，**每幀淨 +90px**（`MaxWalkSpeed` 只有 2.5）。

對標是相反的語意 —— `Footholds.cs:130`（右）/ `:162`（左）問的是
「**我剛走進去的牆**」：牆在探針當下或已越過的位置，tie-break 取最近者。

```csharp
// 錯                          // 對
f.X1 <  x   → continue         f.X1 >  x   → continue   // right
f.X1 <  best.X1                 f.X1 >  best.X1
```

量測（`f4/Map/Map1/100000004.img`，spawn x=1710，按右 200 幀）：

| | 修前 | 修後 |
|---|---|---|
| Δx | +1459.0 | **+487.5** |
| 空中幀數 | 194 / 200 | **0 / 200** |
| 末尾 onGround | False | **True** |
| 地圖內 | 否 | **是** |
| 最大單幀位移 | 90px | **2.50px** |

誌：`JOURNAL/2026-10-14-2140-findwall-inverted-90px-teleport-out-of-map.md`

### ✅ 5.2 怪物移動方向反了

`walkview.js` 的 `drawMob` 鏡像條件是 `facing < 0`。對標三處都說**右=翻轉、左=不翻轉**
（`Mob.cs:356-362`、`Morph.cs:106-109`、`Familiar.cs:124-131`，原圖預設朝左）。
改成 `facing > 0`，與角色 `avatar.js` 的 `flip: dir > 0` 一致。

### ✅ 5.3 玩家沒有臉

`avatar.js` 的 `DEFAULT_EQUIP` 缺 `Hair-1` / `Face-1`。
伺服器 `LookService.Compose` 只對 head/body **sheet** 有 `??=` 預設值，
頭髮和臉**沒有任何預設來源** —— 不給就真的沒有。實測 13 部件 0 臉 → 16 部件 1 臉 2 髮。

### ✅ 5.4 空中往錯方向飄

`DriftWhileFalling` 的 `_x += _speed * moveLeft` —— 但 `_speed` **本身帶符號**
（本 port 把對標的 `SpeedL`/`SpeedR` 合併成一個帶號值）。
`moveLeft = Math.Sign(_speed)` 使乘積恆為 `|_speed|`，**永遠往右**。
改成 `_x += _speed`。

---

## 6. 還沒做的（本輪的未完成項）

### ❌ 6.1 爬梯子會掉落到地圖外面 —— **未診斷**

目前的阻礙不是 bug，是**測不到**：實測時角色停在 x=1618，
梯子在 x=1581，超出 `FindLadderForClimb` 的 ±10 抓取框。
**第一步是找到一個梯子實際可達的出生點**，再量。

相關程式碼：`CharacterPhysics.ClimbOn` / `FindLadderForClimb`，
常數 `ClimbProbeUp=-3`、`ClimbReachY=12`、`ClimbSpeedPerStep=1.5f`。
對標：`MapleCharacter.cs:541`（Up/Down 分支在 walk 分支之前）。

### ❌ 6.2 攻擊怪物沒有攻擊動作 —— **後端有、前端沒接**

`GET /api/simulate/look/attack-stances?sessionId=&attack=0..4` 已可用，
裝劍後 `attack=1` 回 `["stabO1","stabO2","swingO1","swingO2","swingO3"]`（裸手空）。
對標：`CharLook.cpp:648` 的 `info/attack` 整數索引表、`:682-696` 的完整對照、`:594` 裸手無姿勢。

**但「打到怪物時播哪一個」的接線還沒做。**
注意 `AvatarForm.cs` / `CharLook.cpp:594`：**裸手沒有攻擊姿勢**。

### ❌ 6.3 怪物召喚按鈕（UI）

後端 `POST /session/{id}/summon` + `/despawn` 已完成並實測過
（召 5 隻全落在角色 ±100 內；守門：未知 id / 無圖 id / 空 id / 不存在的 session 全部正確拒絕；
`despawn` 只移除召喚的，地圖原生怪不動）。**UI 上還沒有按鈕。**

### ❌ 6.4 技能施放設定

完全沒開始。`JOURNAL/2026-10-14-1040-skill-cast-chain.md` 有相關記錄。

---

## 7. 這專案的方法論（最重要的一節）

### 7.1 「量測」也會是壞的

我因為量測方式不對，得出過兩個**完全錯誤的結論**：

- **「怪物不會動」** — 真相：怪物正常巡邏。`/frames` 的 `count` 被夾在 60，
  我送 600 實際只跑 60 幀（1 秒），量到的是錯的時間窗。
- **「怪物從生成就是死的」** — 真相：是我用 regex 插儀表時把
  `if (Dead) return;` 改成無條件 return，把 `Step()` 弄壞了。
  編譯器的 `CS0162`（不可達程式碼）警告**就在那裡**，我第一眼只看了「0 error」。

**規則：量測工具本身要先用一個已知答案校準過。**

### 7.2 從外部量不出來時，埋點

`FindWall` 那個 bug 我從 frame 的 x/y/speed/foothold 推了**六輪**，
每一輪假設都合理、每一輪都被推翻。真正終結它的是在 `Walk` 內部
寫一行 trace 到檔案 —— 一次建置、四十秒。

**規則：推導三輪以上還沒有結論，就埋點。** 埋點寫完**必須移除**
（本次已移除，`grep TEMP_DIAG` / `grep Trace` 應為 0）。

### 7.3 合成截圖不算證據

把 canvas 的點陣圖貼到頁面截圖上再宣稱「畫面正常」—— 這專案已經這樣自欺過好幾輪，
使用者一句「我怎麼沒看到」才逼出真相。
**任何「使用者看到什麼」的斷言只能用桌面截圖（`computer_use`）驗證。**
DOM 讀取、`canvas.toDataURL()`、合成圖一律不算。

### 7.4 探針回傳 undefined = 探針壞掉

跳脫字元在 template literal 裡被吃掉 → `Runtime.evaluate` 丟例外回 `undefined`，
主線卻把它當成結果，畫出了一輪自信的錯誤推論。**同一個坑踩了三次。**

### 7.5 掃描只能證偽，不能證成

同一批 5366 張地圖掃了三次，三個數字都不同（2117 / 886 / 5366），
差異全來自掃描器自己。**能���讀碼講清楚的問題，不要用跑幾千筆去「確認」。**

### 7.6 對標衝突時 C++ 優先

C# 與 C++ 衝突時以 `MPSH\clean-client\src` 的 C++ 為準。
本輪 `FindWall` 就是靠 `Footholds.cs:130` 定案的 —— 而 C# 的 `FindWall` 寫錯了。

---

## 8. 開工前先載入這些 skill

| skill | 用途 |
|---|---|
| `maplebench-sim-work` | 本專案專屬：建置/部署的坑、物理常數表、量測方法 |
| `game-sim-verification` | 模擬器驗證流程 |
| `studio-work-mode` | 工作室體制（分派、角色分工、禁止自證） |
| `cross-session-handoff` | 寫交接文件的格式 |

另外專案根目錄有 **`工作室守則.md`** —— 那是壓縮後的第一個要讀的東西，
規定四角色分工與六條不可動搖的規則。

---

## 9. 檔案地圖

```
MapleBench/
  Api/SimulatorEndpoints.cs                  模擬器所有端點（含 summon/frames/look）
  Services/MapEditor/CharacterPhysics.cs    角色物理（Walk/Jump/Fall/ClimbOn）
  Services/MapEditor/FootholdPhysics.cs     foothold 鏈、FindBelow、FindWall
  Services/MapEditor/MobPatrol.cs           怪物巡邏（勿加儀表，這裡曾被改壞過）
  Services/Simulate/MapSimSessionStore.cs   session 狀態、SpawnMobs、Summon
  Services/Simulate/CombatService.cs        傷害、AttackStances 對照表
  Services/Look/LookService.cs               外觀合成 Compose / SlotImages / RootImages
  Services/Look/EquipSlotTable.cs           槽位定義（含 Head/Body 的 RootPrefix）
  wwwroot/js/avatar.js                      角色合成、actionFor、DEFAULT_EQUIP
  wwwroot/js/walkview.js                    地圖渲染、drawMob、裝備挑選器
  wwwroot/js/livewalk.js                    前端物理迴圈（gen 世代守衛）
JOURNAL/                                    每日誌，只追加不覆蓋
  2026-10-14-2140-findwall-inverted-90px-teleport-out-of-map.md   ← 本輪最大修復
  2026-10-14-2005-omp-review2-headbody-strip-was-a-noop.md
  2026-10-14-1940-mobs-do-move-it-was-my-measurement.md
  2026-10-14-1855-mob-summon-mobform-parity.md
  2026-10-14-1810-head-body-are-pickable-avatarform-proved-it.md
工作室守則.md                                 ← 先讀這個
任務計畫.md
```

---

## 10. 本輪產出的 md5（部署後線上核對過）

```
js/avatar.js    a1eb2eb47af42e07ee048762c2592456
js/walkview.js  a6b02fea1c78e1682fc8fa1e86eca764
```

C# 已建置到 `build-check` 並在 5591 實測；`bin/` 仍待主線自然重啟
（pid 3740 持有 DLL 鎖，**不要去殺它**）。

---

## 11. 2026-10-14 使用者的新規則（覆蓋 §2 的一條）

> **「你之後每次完整都打開一次編輯器」**

§2 那條「不要開/關任何瀏覽器，使用者自己開自己關」**已被推翻**。
舊規則的後果是實測出來的：agent 因此連嘗試驗證都不做，
連續三輪卡在同一件事 —— 沒有任何桌面截圖，而工作室守則 §2.1 規定
「任何『使用者看到什麼』的斷言只能用桌面截圖驗證」。

**新規則**

| 項目 | 內容 |
|---|---|
| 何時開 | 一項修改**完整**之後：建置部署到 `bin/Release`、回歸閱關全過、三處 md5 一致 |
| 不要開的時機 | 改到一半 |
| 怎麼開 | `browser_call` `target.open`，url 指向**該輪測試實例**的 port（本專案是 5591）|
| 絕對不做 | 不要碰 5480、不要關閉使用者自己開的分頁 |
| 開了之後**不能**做的事 | 不要用 DOM 讀取或 `canvas.toDataURL()` 當成「使用者看到什麼」的證據 —— §2.1 仍然有效 |

**「開啟」不等於「可以自己驗證」。** 開了是讓**使用者**看；
要看對不對，仍然是使用者的眼睛。這兩件事不要混。