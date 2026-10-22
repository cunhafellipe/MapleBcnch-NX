# 2026-10-15 — NPC 名牌（`NpcText`）：盤點找到的第一個真缺口

上一輪的盤點排除了九個「對標有、模擬器沒有」看起來像缺口的東西，
留下 `Npc.cs:246-298` 的 `NpcText`。這一輪把它做出來。

---

## 對標規格（`Npc.cs:171-186` + `:261-298`）

在 **spawn 時**解析一次，不是每幀：

```csharp
Npc.cs:67   npcStringPath = "String/Npc.img/" + ID.IntID();
Npc.cs:172  NpcText.NpcName = ...GetStr("name");
Npc.cs:173  NpcText.NpcFunc = ...GetStr("func");
Npc.cs:174-175  if (NpcFunc != "") HasFunc = true;   // 判斷「字串非空」，不是「節點存在」
Npc.cs:178  NpcText.Z = 1000000 + 100000;            // 在整張地圖之上
Npc.cs:185  NpcText.HideName = GetBool("Npc/" + ID + ".img/info/hideName");
```

`DoDraw` 的三行（`Npc.cs:261-298`）：

| 元素 | 顏色 | 黑底 | 文字 | 條件 |
|---|---|---|---|---|
| id | 青 `Cyan` | `(x-2, WY+36, w+5, 15)` | `WY+37` | `Map.ShowID` |
| 名稱 | 黃 | `(x-2, WY+3, w+5, 16)` | `WY+4` | `ShowNpcName` 且 **`!HideName`** |
| 功能 | 黃 | `(x-2, WY+21, w+5, 16)` | `WY+22` | `HasFunc` |

底色 `Color(0,0,0,150)`，置中 `X = WX - width/2`，GMS 的 `OffsetY = 0`（`:282-287`）。

**兩個容易做錯的細節**：
① `hideName` 只擋名稱，**id 已經先畫了**（`:279-280` 的 early return 在 id 之後）——
藏起名字的 NPC 仍然看得到 id。
② `HasFunc` 判斷字串非空，所以有 `func` 節點但內容為空時**不畫**功能行。

---

## 這輪又抓到一次自己的量測壞掉

先前寫「抽樣 34 張地圖，0 張有 NPC」—— **那是假陰性**。
`/api/children` 回的節點描述**只有 name 和 type，沒有 `value` 欄位**，
我讀 `hits[0]["value"]` 於是每個查詢都回 `None`，每張地圖看起來都空。

改用 `/api/inspect`（它回 `{node:{value,...}}`）重跑：

```
probing 80 maps
maps with a readable life : 44
maps carrying NPCs        : 29
total mob  entries seen   : 503
total NPC entries seen    : 95
```

**這是這個專案第五次「量測工具偽造結論」，而且前兩份文件裡都寫了它。**
規則要寫成：讀 WZ 節點的值一律走 `/api/inspect`，`/api/children` 只給清單。

## 一張有 NPC 的地圖上的實測（`f4/Map/Map0/000000002.img`）

```
mobs=12  npcs=6
  with name   : 2 / 6
  with func   : 0
  hideName    : 0
  mobs wrongly carrying a name: 0
    id=9101003 name='彼特'  func='' hide=False
    id=0002101 name=''      func='' hide=False
    id=0002000 name=''      func='' hide=False
```

**4 隻沒名稱是正確答案，不是失敗** —— 逐一查過：
`String/Npc.img/0002101/name` 根本不存在（`value=None`），而
`Npc/0002101.img` 存在。對標在 `Npc.cs:68-69` 遇到沒有字串節點的 NPC
**整隻 return、不生成**；我們照樣生成只是不畫名牌，資訊量更大。

`func` 全部為空 → 依 `:174-175` 的規則確實不畫功能行。

**怪物身上帶到名牌的有 0 隻** —— 名牌只掛在 `kind=="n"` 上（`:43`、`:51`）。

---

## 改動

**後端**：`CombatService.NpcText(id)` 讀三樣東西；`MapSimSession.NpcLabels`
**在 spawn 時解析一次**（不是每幀問封存）；`MapSimMobFrame` 多三個欄位。

**前端**：`drawNpcLabels()` 逐行照 `NpcText.DoDraw`，
`this.showNpcId` 預設開（對標的 `Map.ShowID` 是 ViewForm 的核取方塊，
這裡沒有，就留成旗標而不刪掉，因為名稱那一行真正的閘是封存的 `hideName`）。

## 回歸

建置部署完成，三處 md5 一致，`avatar.js` 全程未動。
爬梯 5/5、6.2、6.4、怪物反擊（+20 幀 50 傷害）、JS 邏輯 24/24 全過；
NPC 名牌 2/6 有名、0 隻怪誤帶。

**沒有桌面截圖。** 名牌的排版、字體、半透明底都是照數值畫的，看起來對不對仍需你看。
