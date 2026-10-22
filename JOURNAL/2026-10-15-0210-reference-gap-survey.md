# 2026-10-15 — 對標差距盤點：哪些「參標有、我們沒有」其實不是缺口

目標第 (4) 項：系統性比對兩個對標，找出模擬器未移植的行為。

**方法修正**：先列「參標的 `Client/` 有哪些檔、模擬器沒有對應物」，
再對每一個去問**封存裡有沒有那份資料**。資料不存在的，參標有程式碼也不等於是缺口。

---

## 盤點結果

參標 `MapleNecrocer/Client/` 共 27 個檔。已移植：
`MapleCharacter`、`Mob`、`Skill`、`MapleMap`、`Footholds`、`MapPortal`、
`DamageNumber`、`AfterImage`、`LadderRopes`、`MapTile`。

### 不是缺口（資料不存在）

| 參標 | 行數 | 為什麼不做 |
|---|---|---|
| **`MapBack.cs`** | 625 | **抽樣 100 張地圖（Map0–Map3 各 25 張），`info/bg` 可達 0 張、存在 0 張。** 這個存檔根本沒有地圖背景層，畫了也沒有東西可畫 |
| **`NameTag.cs`** | 399 | 它是**玩家自己**的名牌（角色創造那套）與勳章/暱稱，**不是怪頭上的名字**。做「怪名牌」是自設計 |
| **`MapParticle.cs`** | 116 | 整套 3D 粒子系統（Spine + MeshBatcher + RenderEnv）。模擬器是 canvas 繪製，沒有對應物，移植等於重寫一個渲染器 |
| **`ChatBalloon.cs`** | 293 | 對話氣球需要聊天內容；模擬器沒有聊天系統，氣泡永遠是空的 |
| `ItemDrop.cs` | 181 | 掉落物。`任務計畫.md` P3 明確寫「不要搬掉落」，是先前有意識的範圍裁決 |

### 附帶/系統類（對模擬器沒有對應物）

`Pet` 557、`Familiar` 444、`TamingMob` 378、`Android` 364、`Morph` 181、
`MapleChair` 322、`MapleTV` 87、`Reactor` 88、`MapleEffect` 619、
`MapObj` 354（模擬器已畫 `obj` 圖層）、`Sound` 155、`ToolTip` 146、`MouseEx` 90。

---

## 真的缺口：`NpcText` —— NPC 名牌

`Npc.cs:246-298` 的 `NpcText` 畫三樣東西，模擬器**一樣都沒有**：

| 元素 | 位置 | 顏色 | 條件 |
|---|---|---|---|
| **ID** | `WY + 36` | 青色 `Cyan` | `Map.ShowID` |
| **名稱** | `WY + 3` | 黃色 `Yellow` | `Map.ShowNpcName` 且 `!HideName` |
| **功能** | `WY + 21` | 黃色 | `HasFunc` |

每個元素底下是 `FillRoundRect(..., new Color(0,0,0,150))` 的半透明黑底，
文字以 `WX - Width/2` 置中；GMS 區域的 `OffsetY = 0`
（KMS/TMS 為 1、JMS 為 2，見 `:282-287`）。

**資料確認存在**：`String/Npc.img` 有上千筆條目。
名稱依 `Npc.cs:121` 走 `String/Npc.img/<id>`，
訊息是該節點下**兩字元的區域鍵**（`Npc.cs:122-138`，GMS 取原字串，其他區域先去掉空白再 `ReString`）。

### 還沒查清的一件事

`NpcText` 的 `NpcName` / `NpcFunc` / `HasFunc` / `HideName` 四個欄位，
`Npc.cs` 前半段沒有直接賦值 —— 需要再讀 `Npc.Create()`（`:35-64`）確認它們
分別從 `String/Npc.img/<id>` 的哪個子節點來。**沒讀到之前不要寫。**
`NpcFunc` 很可能是 `String/Npc.img/<id>/func`，但那是推測。

---

## 這一輪的真正收穫

不是補了功能，是**排除了九個看起來像缺口的東西**，而且其中兩個
（`MapBack`、`NameTag`）如果照著「參標有程式碼」去做，會做出**永遠畫不出東西**的功能：
一個因為存檔沒有背景層，一個因為參標畫的是玩家不是怪。

**判準：先問資料在不在，再問參標有沒有。順序反了會做出一堆空殼。**

## 回歸

本輪沒有改任何程式，樹停在上一輪已部署、回歸全過、三處 md5 一致的狀態。
