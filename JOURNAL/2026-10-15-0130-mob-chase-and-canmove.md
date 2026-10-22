# 2026-10-15 01:30 — firstAttack 看得見了：補上 chase 那一半，外加三個閘門

## 0. 這一輪修的東西

上一輪把 `firstAttack` 的**取得**（acquisition）接上去並校準過，但它是**死的**：
取得不等於打得到，而且 `MobPatrol.Step` 只在 `HitsTaken > 0`（被敲過）時才轉身走向玩家。
這輪補上 C++ 裡讓取得有意義的那一半，四個部分全部有驗證證據。

| 項目 | 對標 | 之前 | 現在 |
|---|---|---|---|
| chase（走向目標） | `MobAttack.cpp:119-126` | 無 | `Step` 內 `Aggro` 分支 |
| aggro 放掉 | `MobAttack.cpp:83-87` | 無（單向鎖死） | `|dx|>1000 \|\| |dy|>600` → 解除 |
| `canmove` | `Mob.cs:166-170` / `Mob.cpp:83` | 每隻怪都能走，2px | 讀存檔 |
| `noFlip` | `Mob.cpp:73` / `MobAttack.cpp:119` | 無 | 讀存檔 |
| `notAttack` | `Mob.cpp:74` / `MobAttack.cpp:50` | 無 | 讀存檔 |
| 走路速度 | `Mob.cs:147-151` | 寫死 2px | `(1 + info/speed/100) * 2` |
| 命中判定 | `MobAttack.cpp:57-58, 69` | `characterY == _y` 整數相等 | 身體矩形重疊 |

## 1. chase：`MobAttack.cpp:119-126`

```cpp
bool move_facing = noflip ? flip : dx > 0;
if (canmove && std::abs(dx) > 20) { flip = move_facing; set_stance(Stance::MOVE); ... }
else set_stance(Stance::STAND);
```

`Aggro` 就是 C++ 測的那個旗標，而它有兩條路徑會置位：`Mob.cpp:1802`（受到傷害）
與 `MobAttack.cpp:81`（firstAttack 取得）。兩條路徑現在都驅動怪走向玩家。

**MEASURED**（`chase-verify.py`，f4/Map/Map1/100000004.img，出生點 1710,-25）：

```
=== 1. firstAttack 怪，未被敲擊，出生在攻擊範圍外 ===
  出生 x=1641，玩家 1710（距離 69）→ x=1671，最近距離 39
  PASS the chase closed the gap  gap 69 -> 39
  PASS it closed to inside its reach band (<=41px)  closest 39
  PASS and then it hit without being struck  frame 25, 50
=== 2. 對照：沒有 firstAttack，未被敲擊 ===
  出生 x=1648，距離 62 → x=1648，最近距離 62
  PASS 0 damage        PASS it did not home in  gap 62 -> 62
=== 3. 對照：info/notAttack=1（9409000）===
  出生 x=1623，距離 87 → x=1623
  PASS 0 damage over 400 frames
=== 4. 被敲擊的怪：另一條通往 chase 的路 ===
  swing landed on frame 0: mob hp 317/350, gap 86
  出生 x=1796，距離 86 → 最近 40
  PASS it closed the gap after being struck   PASS and hit back  frame 42, 50
FAILURES: none
```

修正前同一個測試的第 1 部分：400 幀 0 傷害。修正後：第 25 幀命中。

## 2. 三個我自己量錯、把結論當證據用的地方

### 2.1 `info/attack1` 是 0/2053 —— **錯的，而且我沿用了三輪**

前幾輪（`2026-10-14-2300`、`2026-10-15-0030`、`2026-10-15-0125`）都寫
「`info/attack1` 在這個存檔是 0/2053，所以 `available` 恆空，那個分支永遠不會 fire」。

量的是 `f5/<id>.img/info/attack1`。但 `Mob.cpp:146` 讀的是

```cpp
nl::node data = src[action]["info"];   // src["attack1"]["info"]
if (!data) data = node["info"];
```

兩個寫法解析到同一個節點：**攻擊動作自己的 info**，不是怪的 info。

**重測（兩個互不相交的切片，守則 §7.5）**：

```
slice A（206 張圖）: 87 隻怪帶 attack data，143 個 attack 槽
slice B（205 張圖，偏移 3）: 88 隻，152 個槽
```

**約 43% 的怪帶攻擊資料。** 所以 `MobAttack.cpp:81` 的 `|| !available.empty()`
對這些怪是活的，`MobAttack.cpp:88-118` 的「挑一個攻擊、播動畫、扣 MP、這一幀不追」
也是活的。這是**真的缺口，不是「做出來等於沒做」**。

量到的 `range` 結構（證明幾何可讀、缺口可補）：

| 怪 | type | range | 其它 |
|---|---|---|---|
| 3300007 | 0 | `lt=-250, -80` `rb=0, 0` | PADamage=125, conMP=2, attackAfter=1200 |
| 2400049 | （無 → 0） | `lt=-275, -175` `rb=0, 0` | attackAfter=1560 |
| 4230101 | 1（扇形） | `r=200` `sp=-14, -29` | PADamage=135 |
| 4220000 | 2（扇形） | `r=160` `sp=-93, -44` | PADamage=110 |

程式碼與 XML doc 裡五處「0 of 2,053」已全部改正。

### 2.2 `characterY == _y` 擋掉了斜坡上的命中

`MobAttack.cpp:57-58` 用的是**身體矩形**：

```cpp
// CAvatar::GetBodyRect: standard player body bounds [-22, 22, -65, 0]
Rectangle<int32_t> target{ x - 22, x + 22, y - 65, y };
if (data.geometry.reaches(origin, current_facing && !noflip, target)) ...
```

本專案寫的是整數相等。**MEASURED**：被 chase 的怪沿著上升的 foothold 從 1655 走到 1691，
y 從 -25.0 漂到 -24.1，`(int)(-24.1) != -25`，每一幀的揮擊都被靜默取消 ——
acquisition 和 chase 都正常，傷害是 0。

換成同一個 `AttackReach` 讀成方塊：玩家身體 ±22，`AttackReach - PlayerHalfWidth = 18`
（`0100100` 的攻擊框量到 -18..+19）。

### 2.3 命中框本身也不是方塊

原本是 `gap = dx - AttackReach`，接受 `±AttackReach`，等於 dx ∈ [0, 80]：
**打不到**站在原點後方 22px 的玩家（身體寬 44，是伸過去的），
卻**打得到** 80px 外的人 —— 是宣稱值的一半。**MEASURED**：一隻 2220000 出生在 98px 外，
轉身第一幀就命中，讓底下的 chase 看起來沒必要。

改成 `MobAttackGeometry::world(range, origin, flipped).overlaps(target)` 的形狀：
怪方框只伸向它面向的那一側 ±18，玩家 ±22。

## 3. `canmove`：約 40% 的怪本來不該走

`Mob.cs:166-170` 與 `Mob.cpp:83` 兩個對標一致：沒有 `move` 節點（且沒有 `fly`）→
不會走。

**MEASURED（兩個互不相交的 158 隻切片）**：`move` 節點分別在 89 隻與 101 隻上 —— 約 40% 的怪沒有。
`SpawnMobs` 之前對每一隻都寫死 `MobMoveType.Move` 與 `2f`，而且建構子還把
`moveSpeed <= 0` 拉回 2，把 `Mob.cs:168` 的 `MoveSpeed = 0` 整個抵消掉。

**兩面否證**（`canmove-falsify.py`）：

```
A. 沒有 move 節點的怪，4 個 session × 90 幀
   4220000: max path=0.0px  frames-with-Direction=0
   6300004: max path=0.0px  frames-with-Direction=32
   8220014: max path=0.0px  frames-with-Direction=0
   8510000: max path=0.0px  frames-with-Direction=0
   VERDICT: PASS -- 沒有一隻動過
```

6300004 那 32 幀 `Direction != 0` 但 path = 0 不是 bug：它有 `fly` 節點（6 幀）沒有 `move`，
所以 `canmove = move || fly = true`（`Mob.cpp:83`），但 `Mob.cs:168` 把 MoveSpeed 設成 0，
方向有值而位移為零。兩個對標在這種怪上不一致（C++ 用 `info/flySpeed` 飛，C# 用 MoveSpeed=0），
本專案照 C#，並且本來就沒有飛行控制器 —— 這是**已宣告**的限制。

**速度**（`Mob.cs:147-151`）：`info/speed` 量到 -40 / 5 / -20 / -60（2220000 / 2220100 /
2110200 / 9409000），也就是 1.2 / 2.1 / 1.6 / 0.8 px/幀 —— 四隻沒一隻是本來寫死的 2。

## 4. 全門檻

| 測試 | 結果 |
|---|---|
| `ladder-all.ps1` | passed=5 failed=0 |
| `attack-test.ps1` | 8 次揮擊的 stance 全在宣告集合內 |
| `skill-test.ps1` | MultiStrike=false 有傷害、true 無傷害、施法鎖、梯子上拒絕 —— 全過 |
| `mob-attack-verify.ps1` | +19 幀，50 傷害（與修正前相同，無退步） |
| `first-attack-verify.ps1` | **PASS: it attacked without being struck**（修正前 FAIL） |
| `npc-label-check.ps1 000000002.img` | 2/6 有名、0 隻怪被誤標 |
| `sim-skills-check.mjs` | 24 passed, 0 failed |
| `chase-verify.py` | 4/4 段 |
| `nomove-acquire.py` | 8/8 命中，走動 0.0px |
| `facing-rule.py` | 31/32（一次未解釋的漏接，見 §6） |
| 建置 | 0 錯誤 0 警告；兩份輸出 + 三方 md5 一致；`avatar.js` md5 `a1eb2eb4…` 未變 |

## 5. 下一個缺口：`MobAttack.cpp:88-118`

```cpp
if (!available.empty() && (canfly || phobj.onground)) {
    auto [selected, attack_facing] = available[randomizer.next_int(available.size())];
    flip = attack_facing; set_stance(Stance::STAND);
    phobj.inputx = phobj.inputy = 0; phobj.hspeed = phobj.vx = 0;
    ...
    begin_attack(selected, flip, target_info, origin, &physics, target_position);
    mp -= attacks.at(selected).conmp;
}
```

需要：讀 `<img>/attackN/info`（`type` / `PADamage` / `MADamage` / `conMP` /
`attackAfter` / `inactive` / `magic` / `range`）、`MobAttackGeometry::reaches`
（type 0 = 方塊、type 1/2 = 扇形 `check_cone`、type 3/4 = 格狀區域）、
`data.cooldown = max(duration, delay) + 400`、以及 MP（那個我問過兩次還沒答的問題）。

**未驗證、不宣稱**：MP 扣除。C++ 有（`Mob.cpp:200` 讀 `mpCon`，`:1300` 扣），
C# 對標沒有，`§7.6` 說 C++ 贏但這個專案其餘都跟 C#。仍然等使用者裁決。

## 6. 補記：又一個量測工具騙了我（off-by-one）

追查「為什麼貼身 spawn 的怪不打」時，我先後給了兩個**錯誤**結論。

### 6.1 `/api/simulate/debug/mob-swing` 讀錯 session

端點用 `sessions.Any()`，回傳字典裡的**第一個** session。前面幾支探針漏了沒刪的
session，`Any()` 就一直回答那個從沒跑過一幀的 session —— 它的
`MobSwingLog` 永遠是空的。

**MEASURED**：一隻站在玩家旁邊 5px、`aggro` 已置位的怪，「swing-log 一行都沒有」。

已修：端點接受 `?sessionId=`，`MapSimSessionStore.Find(id)` 是 `Any()` 的對應版本。

### 6.2 差一幀 —— 「瞬間命中」看起來像「從沒命中」

玩家 maxHp=50，而 `info/PADamage` 也是 50。**貼身 spawn 的怪在第 0 幀就命中並殺死玩家**，
之後每一幀 `playerDamage` 都是 0。

我的觀察迴圈從「準備那一幀之後」才開始讀，於是**打得最快的怪看起來像從不打人**。
這個 off-by-one 讓我一度以為：

- 怪不會轉身面對玩家（`MobAttack.cpp:120` 的 flip 在 `canmove && |dx|>20` 裡面）；
- 不會走的怪（4220000）永遠打不到人。

兩個都錯。修好之後：

| 測試 | 結果 |
|---|---|
| `nomove-acquire.py`（4220000，無 move 節點） | **8/8 命中**，最大走動 **0.0px** |
| `facing-rule.py`（2220000，無 attack 資料） | 兩輪合計 **31/32** |

### 6.3 命中與面向無關（量測推翻了我的預測）

我原本預測「出生面向決定打不打」（`Mob.cs:199-200` 的硬幣）。
量測反了：**面向朝向玩家 7/11 次命中、背向 9/4 次命中** —— 兩邊都打。

這正是 `MobAttackGeometry::world()`（`MobAttackGeometry.h:45-50`）會給的結果：
玩家的身體 44 寬（±22），怪的攻擊框只有 18，在這個區間裡面向不決定兩者是否重疊。

**一次未解釋的漏接**：32 次取樣中有 1 次（dx=+12，面向朝玩家）沒有命中。
不在邊界上，我沒有查出原因，**記為未解，不當成雜訊**。