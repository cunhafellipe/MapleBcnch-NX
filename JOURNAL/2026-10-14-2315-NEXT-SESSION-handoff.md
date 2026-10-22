# 下一輪接續說明（2026-10-14 23:15 寫給 session 重開後的第一個 agent）

> **你沒有看過前面的對話。** 這份檔案與 `JOURNAL/` 裡的其它日誌就是你的全部上下文。
> 先讀這份，再讀 `HANDOFF-2026-10-14-simulator-parity.md`（專案環境事實在那裡）。

---

## 0. 現在的狀態

**已完成、建置部署、回歸全過、三處 md5 一致。** 測試實例在 port 5591（pid 4868）跑著。

工作樹乾淨 —— 剩下兩項**只做過讀碼與量測，沒有寫任何程式**。

---

## 1. 第一件事：先把「列出 Mob.nx 內容」修好

**這是唯一真正的阻塞點。** 剩下的兩項都卡在「要先能正確列出怪物清單」。

### 現在的症狀（都是實測，不是猜的）

```
GET /api/children?path=f5&limit=3000        -> 只回 1 筆
GET /api/children?path=f5&limit=1000&offset=0/1000/2000 -> 合起來只有 3 筆
POST /api/search {"path":"firstAttack"}      -> {"hits":[],"scanned":0}
POST /api/search {"path":"attack1"}          -> {"hits":[],"scanned":0}
```

而**同一個端點帶小數字時是好的**：

```
GET /api/children?path=f5&limit=40   -> 完整回 40 筆（00020000.img, 00020001.img, …）
GET /api/children?path=f1/Face&limit=40 -> 540 個 Face 項目，前 14 筆正常
```

### 已知的坑

- **不要把上面那幾個失敗的結果當成分佈。** 前一輪的 agent 就是這樣把
  「只取到 3 筆」寫成「這個存檔幾乎沒有攻擊資料」的（雖然它後來自己發現了）。
  `scanned=0` 的意思是「根本沒掃到東西」，不是「掃了但沒找到」。
- `f5` 是 **Mob.nx**（`f1`=Character, `f2`=Base, `f3`=String, `f4`=Map, `f5`=Mob,
  `f6`=Etc, `f7`=Item, `f8`=Morph …）。檔案代號取決於 `open-many` 的開啟順序，
  **每次 session 都要重新確認**，不要沿用本文件寫的代號。
- 有效載入時 `/api/node` 與 `/api/children` 對 canvas 以外的節點都正常；
  `WzVectorProperty`（`lt`/`rb`/`origin`）**沒有子節點**，要讀值得用
  `/api/inspect?path=...`（它回 `{node:{value:"13, 8", extra:{x:13,y:8}}}`）。

### 修好之後要量的兩件事

1. **Mob.nx 裡有幾隻怪帶 `info/attack1`？有幾隻帶 `info/firstAttack`？**
   這決定第 7 項走 A 還是 B（見下）。
2. **Mob.nx 一共幾隻怪、id 分佈** —— 第 8 項的選單需要能列出全部並分頁/搜尋。

---

## 2. 第 7 項：怪物攻擊

### 基礎設施已經完整存在，不要重寫

`Services/MapEditor/MobPatrol.cs:365` 的 `AttackPlayer(int characterX, int characterY)`：
有範圍判定、60 幀冷卻（`AttackCooldown`）、傷害 `MobDamage = MaxHp / 8`。
`MapSimSessionStore.Advance` 每幀對每一隻怪呼叫它，並把傷害餵給 `session.Status.Hurt()`。

**所以怪物「已經在攻擊」了 —— 只是沒被打就不打。**

```csharp
public int AttackPlayer(int characterX, int characterY) {
    if (!Alive || !Aggro) return 0;      // ← 問題在 Aggro
```

`Aggro` 只在被玩家擊中後才設（`MobPatrol.cs:489`，註解引 `MapleNecrocer/Client/Mob.cs:434-460`
「客戶端沒有仇恨半徑，怪只有挨打才會反應」）。

### 對標

| 來源 | 事實 |
|---|---|
| C# `MapleNecrocer/Client/Mob.cs:434-460` | 沒有仇恨半徑，只有挨打才反應 |
| **C++ `clean-client/src/Gameplay/MapleMap/Objects/Mob.cpp:79`** | `first_attack = info["firstAttack"].get_bool()` |
| **C++ `Mob.cpp:671`** | `chasing = target_alive && !target_hidden && (first_attack \|\| aggro)` |
| **C++ `MobAttack.cpp:81`** | `if (first_attack && (std::abs(dx)/10 + std::abs(dy)/3 <= 40 \|\| !available.empty())) aggro = true;` |
| **C++ `Mob.cpp:144-177`** | 從 `info/attack1..N` 讀 `PADamage`/`MADamage`/`range`(lt/rb/sp/r)/`attackAfter`/`conMP` |
| **C++ `Mob.cpp:69`** | `touchdamage = info["bodyAttack"].get_bool()` |

守則 §7.6：**C# 與 C++ 衝突時 C++ 優先。**

### 關鍵資料事實（實測，`f5/2220100.img`）

```
info 的完整子節點：MADamage, MDDamage, PADamage, PDDamage, acc, bodyAttack,
                   eva, exp, fs, level, maxHP, maxMP, mobType, pushed, speed,
                   summonType, undead
info/attack1        -> 404
info/firstAttack    -> 404
info/icon           -> 404
```

**這隻怪沒有 `attack1`、沒有 `firstAttack`、沒有 `icon`。**
它有頂層的 `PADamage` / `MADamage` / `bodyAttack` / `acc` / `eva`。

### 路線（使用者已選 A，但 A 的前提還沒驗完）

| 路線 | 內容 | 狀態 |
|---|---|---|
| **A** | 只做對標有的：讀 `info/attack1`，有的怪會打、沒有的不會 | 使用者選的。**若全庫都沒有 attack1，做出來等於沒做** |
| **A′** | 先修好列舉、量出全庫分佈 → 有的話走 A，沒的再回來問 | **建議先做這個** |
| **B** | 宣告自設計：用頂層 `PADamage` + 自己定的範圍與仇恨半徑 | 保證看得到怪物打人，但範圍與時機無對標可抄 |

**不要在 A′ 完成之前就動手寫第 7 項。**

---

## 3. 第 8 項：召喚怪物的圖示選單

### 前提（實測）

- 怪**沒有 `info/icon` 節點**（上面已列）。所以「跟裝備一樣的圖示」沒有同一份資料來源。
- 使用者已確認：**用怪的 `stand/0` 當縮圖**。誠實的替代，不是 icon。
- 對標的 `MobForm.cs:107-140` 本來就是 **DataGridView 清單**，不是方格牆；
  裝備那種 21 格 `ImageListView` 是 `AvatarForm.cs:107`。
  **「跟裝備一樣的視窗形狀」是超出對標的改動，必須宣告。**

### 已有可用的

後端 summon/despawn 已完成並實測（怪 24 → 召 5 → 29 → 移除 5 → 24，守門三種輸入正確）。
`GET /api/simulate/mob-sprite?mobId=<id>` 回完整動作與每一幀的
`path/width/height/originX/originY/delay` —— 縮圖直接用它，不需要新端點。

### 注意

Mob.nx 的清單規模很大（Character 的 Face 有 540、Hair 有 1518，Mob 估計上千），
選單必須能搜尋/分頁，否則不可用。而這正是第 1 節要修的東西。

---

## 4. 這一輪之前已完成並驗證的九項

| 項目 | 內容 | 日誌 |
|---|---|---|
| 6.1 | 爬梯掉出地圖：`climb` 符號寫反 + 頂端出口搬 X | `2026-10-14-2200-6.1-...` |
| 6.2 | 攻擊姿勢：`AttackStancesFor` 查錯封存（f2 應為 f1） | `2026-10-14-2135-...` |
| 6.3 | 召喚/移除按鈕 + 修好 live 模式下不存在的控制列 | `2026-10-14-2245-...` |
| 6.4 | 技能施放設定 A/B/C/D | `2026-10-14-2135-...` |
| 臉 | 臉的 canvas `map/brow` 偏移被丟掉，高 12px | `2026-10-14-2210-...` |
| 1 | 地圖輔助線（foothold/牆/梯子/傳送門/邊界框）預設關閉 | 未寫日誌 |
| 2 | 骨架人：保留上一個已畫的幀，不為載入新姿勢退回 fallback | 未寫日誌 |
| 3 | 玩家被怪物覆蓋：平手規則 + null 深度不再排到最前 | 未寫日誌 |
| 4 | 傷害數字拉高 `DMG_HEAD_LIFT = 42` | 未寫日誌 |
| 5 | 怪物 HP 條（**宣告自設計**，對標沒有） | 未寫日誌 |
| 6 | 怪物死亡動畫：`MobAnimation` 用 `Dying` 而非 `Dead` | 未寫日誌 |
| 9 | 技能特效骨架：伺服器送 `effects`、前端 `drawSkillEffects` | 未寫日誌 |

**1–6、9 還沒寫日誌。** 若還有餘裕就補上 —— 這個專案靠 JOURNAL 當交接介面。

---

## 5. 環境速查

```
測試實例    http://127.0.0.1:5591     （不要碰 5480；pid 3740 早已不存在）
建置        C:/MUWORK/app/scoop/apps/dotnet-sdk/10.0.401/dotnet.exe
            不要用 PATH 上的 dotnet（那是 SDK 8）
            dotnet build MapleBenchX.csproj -c Release -o "<scratch>/build-check" -p:UseSharedCompilation=false
部署        Copy-Item wwwroot\* 到 bin\Release\net10.0-windows\wwwroot\ 和 <scratch>/build-check\wwwroot\
開檔        POST /api/files/open-many  {"paths":[.../MPSHS/{Character,Base,String,Map,Mob,Npc,Item,Skill,Quest}.nx]}
WZ 資料     C:\MUWORK\GAME\MPSHS\     （注意是 MPSHS 不是 MPSH）
對標 C#     C:\MUWORK\GAME\_necrocer_src\MapleNecrocer\
對標 C++    C:\MUWORK\GAME\MPSH\clean-client\src\      （衝突時優先）
量測腳本    C:\Users\e7896\AppData\Local\hermes\cache\scratch\{ladder-all,attack-test,skill-test}.ps1
JS 邏輯測試 MapleBench\tools\sim-skills-check.mjs      （node --check 之外的真執行，24 項）
```

**量測工具必須先校準**（工作室守則 §7.1），而且校準條件自己也可能錯 ——
本專案已經有四次「量測工具自己壞掉、偽造出結論」的記錄，
其中兩次是我自己寫的（`@($null).Count` 在 PowerShell 回 1；`$input` 是自動變數）。
