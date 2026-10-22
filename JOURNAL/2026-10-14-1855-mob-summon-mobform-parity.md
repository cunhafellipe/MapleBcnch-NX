# 2026-10-14 18:55 — 怪物召喚：補上對標有、我沒有的功能

## 缺口盤點（先量，再做）

```
rg "spawn|drop|召喚|放置" MapleBench/wwwroot/js/mob.js   →  0 筆
MobService 的公開方法 → List / Detail / WriteFields / Bulk  （瀏覽與編輯，沒有召喚）
```

**瀏覽器裡有 1273 行的怪物管理 UI，卻沒有任何召喚功能。** 對標有：
`MobForm.cs:107-124` 一個 spawn 按鈕 + 一個數量輸入框，`Mob.cs:102-175` 是 `Spawn` 本體。

## 對標的召喚語意（逐行讀出來，沒有猜）

| 位置 | 行為 |
|---|---|
| `MobForm.cs:109` | `Mob/{id}.img` 不存在就 `return`（靜默） |
| `MobForm.cs:111` | 每次點擊 `new Random()` |
| `MobForm.cs:112-114` | 數量框當整數解析，非數字 → 一隻也不召喚 |
| `MobForm.cs:116` | `Random.Next(playerX - 100, playerX + 100)`（上界**不含**） |
| `MobForm.cs:117` | `if (Range > Map.Left && Range < Map.Right)` 才召喚，否則**靜默丟棄** |
| `MobForm.cs:119` | `Mob.Spawn(id, x, playerY - 100, left, right)` |
| `Mob.cs:110-122` | `info/link` → `Mob.ID` 改成 link 目標，`LocalID` 留原 id |
| `Mob.cs:123` | **解析後**的 id 必須有 `stand/0` 或 `fly/0`，否則 return |
| `Mob.cs:148-155` | `MoveSpeed = (1 + info/speed/100) * 2`，預設 2 |
| `Mob.cs:158` | `HP = 2000000` 寫死（註解掉的 `maxHP` 說明作者自己也棄用了） |
| `Mob.cs:166-175` | 無 `move` → 速度 0 + Stand；`jump` → Jump；`fly` → Fly（後者優先） |
| `MobForm.cs:126-140` | Remove 按鈕殺掉 `SummonedList` 裡所有 id 的怪 |

## 一處刻意不照抄的地方

**追蹤用索引，不用 id。** 對標的 `Mob.SummonedList` 是 `List<string>`，Remove 按鈕掃全場
比對 `LocalID`。一張地圖的 `life` 可以命名同一個 id（十二隻怪共用一個 id 是常事），
照抄會讓「移除召喚怪」連帶殺掉地圖自己的。

這裡的 `Mobs` **只增不減** —— 死掉的怪保留槽位、回報 `Alive == false`
（見打擊迴圈的 `if (!mobPatrol.Alive) continue;`），所以索引是穩定身分。
`MapSimSession.SummonedIndexes` 記錄索引。

## 改動

**`Services/Simulate/MapSimSessionStore.cs`**
- `MapSimSession.SummonedIndexes : HashSet<int>`
- `private SummonResult Summon(MapSimSession, string, int, string?)`
- `private int DespawnSummoned(MapSimSession)`
- `public SummonResult? Summon(string sessionId, ...)` / `public int? DespawnSummoned(string sessionId)`（HTTP 包裝）
- 淨新增 229 行，**零刪除零修改**（difflib 驗證：舊 893 行的每一行都還在新檔裡）

**`Api/SimulatorEndpoints.cs`**
- `POST /api/simulate/session/{id}/summon`
- `POST /api/simulate/session/{id}/despawn`
- `MapSimSummonRequest { MobId, Count, Kind }`

**宣告 own-design 的地方**：`SummonResult.Rejected`。對標沒有 session，
所以它無法回報「5 隻裡 3 隻落在地圖外被丟掉」—— `MobForm.cs:117` 是個沒有 `else` 的
`if`。回報被丟棄的每一隻，是讓呼叫端能分辨「召喚失敗」與「召喚成功但位置不好」的唯一方式。

## 驗證（獨立實例 :5591）

```
POST /session/{id}/summon {"mobId":"100100","count":5}
→ 200 {"mobId":"100100","resolvedId":"100100","requested":5,"spawned":5,
       "rejected":[],"indexes":[18,19,20,21,22]}
   怪物數 18 -> 23 (+5)
   角色 x=90；召喚怪 x = 120,118,34,32,6（差 +30,+28,-56,-58,-84）
   |dx| <= 100 : 5/5
```

守門：

| 輸入 | 結果 |
|---|---|
| `mobId: "99999999"` | `spawned:0, rejected:["99999999: not in any open Mob archive"]` |
| `mobId: "1010000"`（`hasArt=false`） | `spawned:0, rejected:[...]` |
| `mobId: ""` | `rejected:["no mob id given"]` |
| 不存在的 session | HTTP 404 `no live character with id nope` |

despawn：

```
POST /session/{id}/despawn → 200 {"removed":4}
怪物數 22 -> 18（4 隻召喚的 alive=false 且不再送出，地圖原生 18 隻不動）
```

## 一個必須說清楚的事實：怪物不動，而且不是這次改的

召喚的怪在 120 幀內位移 0px。**第一反應是 bug，但對照組證明不是**：

```
f5/Map/Map1/100000003.img  地圖原生 idx0-5：位移全 +0.0，stance 全 stand
f5/Map/Map1/100000004.img  24 隻，4 秒後位移>1px 的 = 0
f5/Map/Map0/000040001.img  26 隻，位移>1px 的 = 1
f5/Map/Map1/100000000.img  30 隻，位移>1px 的 = 0
f5/Map/Map0/000000002.img  12 隻，位移>1px 的 = 0
```

而且 `100100` **有** `move` 節點（5 幀），
所以不是 `Mob.cs:166-170` 的「無 move → 速度 0」規則生效 —— 那是對標行為，本該不動。

difflib 逐行比對證明本次改動是**純新增**：
`mobPatrol.Step` / `MobAnimation.For(session` / `private void SpawnMobs` /
`session.Animations` 這些行的**文字**在新舊檔完全相同，舊的每一行都還在檔內。

**結論：怪物巡邏不動是既有的獨立問題，不在本次召喚功能的範圍。**
skill 裡記載過一次成功測量（5 次 stance 變化、269px 位移），所以曾經能動 ——
需要單獨追。本誌不把它記成「已修」。

## 踩到的四個編譯/語意坑

1. **`MobMoveType` 是巢狀在 `MobPatrol` 裡的列舉**，不是 namespace 層級。
   寫 `MobMoveType.Fly` → CS0246，要寫 `MobPatrol.MobMoveType.Fly`。
2. **`Map.Left` / `Map.Right` 在對標的 `Map` 上，這裡沒有。**
   `FootholdPhysics` 的邊界是 `Bounds`（nullable 的 `(Left,Top,Right,Bottom)` tuple）；
   而 `Left`/`Right` 是 **`Foot`**（單一 foothold）的端點 —— 用錯會拿生成位置去比一個
   foothold 的跨度。`Bounds is null`（地圖沒有可用 foothold）時全部拒絕，這是誠實答案。
3. **兩個 public 方法吃 internal 的 `MapSimSession`** → CS0051。降為 private，
   對外的 public 包裝器本來就存在。
4. **`SummonRandom` 是我憑空寫的**，編譯前就抓到自己沒定義；改用
   `session.CombatRandom`（對標 `MobForm.cs:111` 每次點擊 `new Random()`，
   同一 tick 兩次點擊會拿到相同種子）。

## 部署狀態

- JS 無改動
- C# 仍是「bin 未含最新」：5590（pid 19300）持有 `bin/Release/net10.0-windows/MapleLib.dll`，
  約束是不打斷主線。所有驗證在 :5591 獨立實例完成。