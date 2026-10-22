# MapleBench 檢察官報告

服務：`http://127.0.0.1:5590`，Map.nx 已開，測試地圖 `f6/Map/Map0/000000002.img`。
所有數字都是實跑出來的；每條都有可貼進終端機的重現。

---

## F1  `Swing()` 的攻擊框永遠長在角色「背對」的那一邊 —— 鏡像邏輯整個反了

- **檔案:行號**: `Services/Simulate/CombatService.cs:707-711`（`AttackBox.At`），配合 `CombatService.cs:673-676`（`Box.MirrorX`）
- **類型**: 與對標不符 / 邏輯錯誤
- **對標**: `C:\MUWORK\GAME\_necrocer_src\MapleNecrocer\Client\AfterImage.cs:59-72`

### 證據

對標 `AfterImage.cs` 的算式（我自己開檔逐行比對過，不是照註解抄的）：

```csharp
LT = .../0/{Action}/lt;   // -64, -33
RB = .../0/{Action}/rb;   // -27, -18
switch (Game.Player.FlipX)
{
    case true:   Right = (int)X - LT.X + 18;  Left = (int)X - RB.X;   break;
    case false:  Left  = (int)X + LT.X;       Right = (int)X + RB.X;  break;
}
```

而 `FlipX == true` 的意思是**面向右**（`MapleCharacter.cs:1649-1653`：`Input.Right → FlipX = true`）。
所以：

| 面對方向 | 對標算出來的框 | MapleBench 算出來的框 |
|---|---|---|
| 右 (`FlipX=true`) | `X+27 .. X+82` —— **前面** | `X-64 .. X-27` —— **後面** |
| 左 (`FlipX=false`) | `X-64 .. X-27` —— **前面** | `X+27 .. X+64` —— **後面** |

兩個分支都落在背後，方向對調了。根因是 `Candidates()` 的鏡像條件寫反：
`local = facingLeft ? Box.MirrorX() : Box`（CombatService.cs:710）。
`barehands.img/0/stabO1` 的 `lt`/`rb` 本身是**負值**（-64 / -27），代表「面對右時，框在身體前方偏左 27~64px」。
正確的規則是：**不鏡像＝面右，鏡像＝面左**—— 跟現有程式正好相反。

`Shift` 本身沒問題，問題只在選錯了鏡像那一支。

### 實測（決定性實驗）

走進一隻 `9409001`（x=-553, y=88）並逐幀揮擊，同時用我自己的幾何獨立計算重疊：

```
n=53 px=-549.0 face=+1 mobDX= -4.0  pBox=(-613,58,-576,73) mBox=(-571,39,-535,89) overlap=False dmg=False hp=8
n=54 px=-546.5 face=+1 mobDX= -6.5  pBox=(-611,58,-574,73) mBox=(-571,39,-535,89) overlap=False dmg=False hp=8
n=55 px=-544.0 face=+1 mobDX= -9.0  pBox=(-608,58,-571,73) mBox=(-571,39,-535,89) overlap=False dmg=True  hp=6   <-- 打中
n=56 px=-541.5 face=+1 mobDX=-11.5  pBox=(-606,58,-569,73) mBox=(-571,39,-535,89) overlap=True  dmg=True  hp=3
```

**n=55 那一幀，伺服器判定命中，而我依程式碼算出來的框完全沒有重疊。**
此時玩家面向 **右**(+1)，怪物在玩家的**左邊**（mobDX = -9）。
也就是說：**揮擊打中的，是角色背對的那一側的怪。**

反向驗證（從怪物右側接近、面對左走過去）：同樣貼到 3px 內揮擊 70 幀，**一擊都沒有**。

```
player approached from RIGHT, walking LEFT. Hits landing:
  NONE
```

### 為什麼會被誤判成「攻擊從不生效」

這個 bug 的症狀是**單向可見性**：只有「從怪的左邊走過去、面朝右」才會打中。
從怪的右邊走過去（最自然的走法）永遠打不中。所以大部分測試姿势下 `damage` 恆為 `[]`，
看起來像整條命中判定死掉了。

### 重現

```bash
# 1) 從怪物左側接近、面朝右走過去 —— 會打中（怪物在 x=-553）
curl -s -X POST http://127.0.0.1:5590/api/simulate/session -H "Content-Type: application/json" \
  -d '{"path":"f6/Map/Map0/000000002.img","weapon":"1302000.img","startX":-650,"startY":60}'
# 記下 sessionId，然後：
curl -s -X POST http://127.0.0.1:5590/api/simulate/session/<ID>/frames \
  -H "Content-Type: application/json" -d '{"count":60,"first":{"right":true,"attack":true}}'
# -> damage 有數字

# 2) 從怪物右側接近、面朝左走過去 —— 一擊不中
curl -s -X POST http://127.0.0.1:5590/api/simulate/session -H "Content-Type: application/json" \
  -d '{"path":"f6/Map/Map0/000000002.img","weapon":"1302000.img","startX":-450,"startY":60}'
curl -s -X POST http://127.0.0.1:5590/api/simulate/session/<ID>/frames \
  -H "Content-Type: application/json" -d '{"count":60,"first":{"left":true,"attack":true}}'
# -> damage: []
```

### 影響

玩家面對右邊打，左邊的怪會受傷；面對左邊打，右邊的怪會受傷。
畫面上揮擊特效在身前，傷害數字卻在背後的怪身上跳出來。方向鍵左右都會打錯邊。

---

## F2  武器名稱帶了 `.img` 就會拼成 `xxx.img.img`，任何武器都靜默退回徒手

- **檔案:行號**: `Services/Simulate/CombatService.cs:139`（`Candidates`，`yield return ($"{w}.img", ...)`）
- **類型**: 錯誤前提 / 沈默失敗
- **證據**:

`Candidates()` 無條件把 `.img` 接在呼叫者給的字串後面。
`AttackBox("axe.img")` 因此去查 `f2/Afterimage/axe.img.img`：

```
$ curl "http://127.0.0.1:5590/api/node?path=f2/Afterimage/axe.img.img"
404 "'axe.img.img' not found under 'f2/Afterimage'."

$ curl "http://127.0.0.1:5590/api/node?path=f2/Afterimage/axe.img/0/swingT1/lt"
200 {..., "value": "-99, -90", "extra": {"x": -99, "y": -90}}
```

檔案**確實存在**，節點**確實讀得到**，但查詢字串多了 `.img`。
走完 `Candidates` 的全部五個備援 stance 都落空，最後掉進 barehands 迴路。

實測對照 —— 帶不帶副檔名，結果差一個整級武器：

```
axe       -> source=axe.img/stabO1       resolved=True box=(-90,-36,-9,-6)    <-- 正確
axe.img   -> source=barehands.img/stabO1 resolved=True box=(-64,-33,-27,-18) <-- 退回徒手
```

`/debug/attack-box` 對每一種寫法都回報 `resolved: true`，但 `source` 欄位才看得出真相。

### 重現

```bash
curl -s "http://127.0.0.1:5590/api/simulate/debug/attack-box?weapon=axe.img"     # -> barehands, (-64,-33,-27,-18)
curl -s "http://127.0.0.1:5590/api/simulate/debug/attack-box?weapon=axe"         # -> axe.img,  (-90,-36,-9,-6)
curl -s "http://127.0.0.1:5590/api/node?path=f2/Afterimage/axe.img/0/swingT1/lt" # -> 200, -99,-90（節點存在）
```

### 影響

前端選武器時傳 `"1302000.img"`（帶副檔名，INSPECTOR-TASK.md 的範例就是這樣寫的），
實際拿到的是**全角色最小**的徒手攻擊框 37×15。
斧頭應該是 48×108。攻擊距離與傷害範圍整個縮水，而且 UI 上看不出來。

補充：`sword.img` / `club.img` / `bow.img` 在這個 Character.nx 裡**本來就不存在**，
那幾個退回徒手是正確行為 —— 這也是為什麼這個 bug 容易被誤判成「只有斧頭壞掉」。
`Resolved` 旗標在 fallback 那條路徑上沒有被設成 false（CombatService.cs:101-105），
所以「沒查到武器」與「查到武器」回報的 `resolved` 都是 `true`。

---

## F3  `Start()` 的 `direction` 參數被忽略；回傳的 facing 由「出生點 X 的正負號」決定

- **檔案:行號**: `Services/Simulate/MapSimSessionStore.cs:1394`（`session.LastX` 未初始化）搭配 `MapSimSessionStore.cs:1646-1651`（`Describe`）
- **類型**: 邏輯錯誤
- **證據**:

`Start()` 從不給 `session.LastX` 賦值，所以 `LastX` 停在預設 `0`。
`Describe()` 的第一幀因此算 `dx = b.X - session.LastX = -683 - 0 = -683`，
於是 `facing = dx > 0 ? 1 : -1` 把「位移方向」判成了「面向方向」。
角色出生在 x=-683 → facing 被強制成 **-1**，不管呼叫者要求什麼。

`CharacterPhysics` 的建構子**確實**收到並使用了 `direction`（`CharacterPhysics.cs:144-145`
決定 `_x = start.X1` 還是 `start.X2`），但回報給前端的那個 facing 被 `LastX` 蓋掉了。

實測六次，`direction` 參數完全沒有影響力：

```
requested direction=+1 startX= -553 -> returned facing=-1 at x=-683
requested direction=+1 startX=  267 -> returned facing=+1 at x=107
requested direction=+1 startX=  900 -> returned facing=+1 at x=497
requested direction=-1 startX= -553 -> returned facing=-1 at x=-476
requested direction=-1 startX=  267 -> returned facing=+1 at x=275   <-- 要求 -1 卻回 +1
requested direction=-1 startX=  900 -> returned facing=+1 at x=1392  <-- 要求 -1 卻回 +1
```

三次要求 -1、三次要求 +1，回傳的 facing 完全由出生 X 的正負號決定。

### 重現

```bash
for d in 1 -1; do for x in 267 900; do
curl -s -X POST http://127.0.0.1:5590/api/simulate/session -H "Content-Type: application/json" \
  -d "{\"path\":\"f6/Map/Map0/000000002.img\",\"weapon\":\"1302000.img\",\"startX\":$x,\"startY\":60,\"direction\":$d}"
done; done | grep -o '"direction":[0-9]*\|"x":[-0-9.]*'
# 每一次回傳的 facing 都一樣，與 direction 無關
```

### 影響

任何指定初始面向的呼叫都拿不到預期結果。
這會直接放大 F1：既然 swing 框長在背後，起始面向錯了就是**第一幀就站錯邊**。

---

## F4  「無輸入漂移」不是 bug，是動能遞減 —— 但它讓 `action` 有 10 幀回報 `walk`

- **檔案:行號**: `Services/MapEditor/CharacterPhysics.cs:207-208`（遞減）與 `CharacterPhysics.cs:227`（`LastAction`）
- **類型**: 與描述不符（你回報的現象我重現不出來，但換成另一個真問題）
- **證據**:

**先澄清你已知的那一項**：全新 session 送 `{}`，只有**第一幀**跳 +20px，之後完全靜止：

```
fresh spawn x -3252 speed 0 action ''
  frame1: x=-3232.000 dx=+20.000 speed=0 action='stand'
  frame2: x=-3232.000 dx=+0.000  speed=0 action='stand'
  ... 15 幀全部 dx=0
```

+20 是 `_x = start.X1` 落在 foothold 左端、第一幀才被正規化到可行走區段的位移，不是持續漂移。

**但真正在漂移的是「放開鍵之後」**，而這是**設計如此**，符合對標
（`MapleCharacter.cs:718/735` 的 `SpeedL -= 0.25f`）：

```
after 30 right frames: x -3160.52 speed 2.5 action walk
release frame 1: x -3158.27 speed 2.25 action walk dx  2.25
  +9: x=-3149.27 speed 0.25 action walk dx  9.00   (累計 9.00px)
  +10: x=-3149.27 speed 0    action stand
```

**真正值得報的**：`LastAction` 用 `Math.Abs(_speed) > 0.01f` 判定，
所以放開鍵之後還有 **10 幀** `action="walk"`，期間角色其實在明顯位移（9px 總量）。
任何以 `action` 決定角色姿勢的客戶端，會看到角色停下來了卻還在踏步姿勢 10 幀。

### 重現

```bash
curl -s -X POST http://127.0.0.1:5590/api/simulate/session -H "Content-Type: application/json" \
  -d '{"path":"f6/Map/Map0/000000002.img","weapon":"1302000.img"}'
# 先 POST 30 幀 {"right":true}，再連續送 10 幀 {}
# action 會維持 "walk" 直到 speed 歸零
```

### 影響

`action=walk` 但 X 不再變的 10 幀，畫面上角色原地踏步。
對照 `CharacterPhysics.cs:224-227` 自己的註解，它引用的 `PlayerWalkState::update_state`
是判斷**殘餘速度**——而殘餘速度就是這 9px，所以這一段其實是對標行為，
但門檻 0.01 與 `Decceleration=0.25` 搭配起來讓尾段長達 10 幀（167ms）。

---

## F5  `TakePortal()` 之後 `session.Facing` 沒被保留，落地面向靠 `LastX` 猜

- **檔案:行號**: `Services/Simulate/MapSimSessionStore.cs:1138`
- **類型**: 邏輯錯誤（與 F3 同源）
- **證據**:

```csharp
session.Body = new CharacterPhysics(destMap, land.Value, session.Facing);
```

傳了 `session.Facing` 進建構子，但 `session.LastX` 仍然是**舊地圖的座標**。
下一幀 `Describe()` 算 `dx = 新地圖的 X − 舊地圖的 X`，跨越兩張地圖的座標差是任意大數字，
`facing` 會被這個垃圾值決定，直到第二幀才恢復正常。

### 影響

穿過傳送門之後的第一幀，面向是隨機的。緊接著 F1，會有一幀用錯邊的攻擊框。

---

## 沒找到的

- **`MobAnimation` 的 frame 計時**：逐幀核對過 `stand` 7 幀 / `move` 7 幀 與
  `debug/mob-frame` 回報的 `frameCount` 一致，delay 累加器
  （`MobAnimation.cs:205-215`）的 `while (_heldMs > _delays[FrameIndex])` 邏輯正確。**沒有問題。**
- **傷害結算與 EXP**：`9409001` maxHP=8，三擊 2/2/2 打死，第四幀 `dying=True`、`expGained=1`、
  `status.exp=1`。批次路由 `/frames` 與單幀 `/frame` 的 EXP 結果一致。**沒有問題。**
- **`MobFrame` 的 stand fallback**：`9300048` 沒有 `move` 動作時退回 `stand` 的邏輯（CombatService.cs:472）
  是對的，註解說的 node 內容我也用 `/api/node` 逐一驗過。
- **`catch {}` 吞例外**：在 `Services/Simulate/` 底下沒找到空的 catch 區塊。

---

## 建議的修補順序

1. **F1** — 把 `AttackBox.At` 的鏡像條件對調。這一條修好，「攻擊從不生效」就會變成
   「攻擊打錯邊」，而後者一眼看得出來。
2. **F3** — `Start()` 裡補 `LastX = body.X`（`TakePortal` 同樣要補）。
3. **F2** — `Candidates()` 先 `Path.GetFileNameWithoutExtension` 去掉既有副檔名再接 `.img`；
   同時讓 barehands 迴路回報 `Resolved = false`。
