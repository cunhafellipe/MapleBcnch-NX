# C — 機制語意：plan 的每一個欄位讀對了嗎

支線 C，2026-10-18。寫入範圍只有 `MapleBench/Services/Simulate/SkillCastService.cs`。
權威對標 `C:\MUWORK\GAME\MPSH\clean-client\src`。

## ⚠ 先講清楚：這份報告裡「已修好」的 C# 改動**沒有被編譯、沒有被執行、沒有被量測**

我沒有建置權。`http://127.0.0.1:5591` 上跑的是 **W-089 之前的舊版**。
因此下面每一條都分成三欄：

| 標記 | 意思 |
| --- | --- |
| **已量測** | 我這次真的打過 HTTP / 讀過封存，數字貼在下面 |
| **已改、未驗** | C# 已改完，`lsp_diagnostics` 乾淨，但沒有編譯過，執行結果未知 |
| **不屬於這條線** | 發現了，但檔案不是我的，或修它等於猜 |

**沒有一條用「應該」結案。**

---

## C1 — MultiStrike 現在還代表什麼？

### 結論：**那三個魔術數字站不住，已刪掉；改成照參考推導。**

### 參考根本沒有 `MultiStrike`

`C:\MUWORK\GAME\MPSH\clean-client\src` 全文沒有 `MultiStrike` 這個符號（`grep -i multistrike` 零命中）。
參考有的是**兩個各自獨立的計數器**，都讀在 level 節點上：

```cpp
// Data/SkillData.cpp:68-70
uint8_t attackcount = (uint8_t)sub["attackCount"].get_integer(1);
uint8_t mobcount    = (uint8_t)sub["mobCount"].get_integer(1);
uint8_t bulletcount = (uint8_t)sub["bulletCount"].get_integer(1);

// Gameplay/Combat/Skill.cpp:394, 397-405
attack.mobcount = stats.mobcount;
switch (attack.type)
{
case Attack::RANGED: attack.hitcount = stats.bulletcount; break;
default:            attack.hitcount = stats.attackcount; break;
}
```

「打幾隻怪」（`mobcount`）和「每隻打幾次」（`hitcount`）是**兩個問題**。
我們把兩個問題塞進同一個欄位，而且塞的是 MapleNecrocer 的硬編碼。

### 那三個 id 的共同點是什麼？**沒有共同點，而且其中一個被封存直接否定。**

已量測（`GET /api/children?path=<entry>/level/1`，66 個可施放技能全掃）：

```
=== what the archive says, straight off level/1 ===
  id          attackCount (type)  bulletCount  ball   plan.mobCount
  1121008     2         Long      null         false  1
  1001005     (不在 castable 清單裡)
  1311012     (不在 castable 清單裡，且 SkillId.h 沒有這個常數)

  attackCount present on 10 of 66          → PASS
  every attackCount is a Long              → PASS
  1121008 carries attackCount = 2 (the guard called it SINGLE-strike)  → PASS
  bulletCount exists on exactly 1 skill and reads 0  (13101005=0)       → PASS
  no skill carries level/1/ball                                        → PASS
```

- `1121008` = `SkillId.h:70` `BRANDISH`。封存上 `level/1/attackCount = 2`（`Long`）。
  **參考會打它兩下，而舊守衛說它是單擊。**
- `1001005` = `SkillId.h:42` `SLASH_BLAST`，沒有 `hit` 節點，`Castable()` 永遠不會列出它。
- `1311012` 在參考的 `SkillId.h` 裡**根本沒有宣告**。
- `attackCount > 1` 的 10 個：`11111004=2`、`11111006=4`、`1121008=2`、`1311001=2`、
  `1311002=2`、`14101006=4`、`21120006=4`、`4211006=10`、`4221007=2`、`5121004=8`。
  **舊守衛把這 10 個全部叫做「多次擊」，其中 1121008 還被它反向叫成單擊。**
- `bulletCount` 全封存只有 `13101005 = 0` 一個。

### 而且 W-084 之後那個 bool 回答的根本是另一個問題

W-084 把 `MultiStrike` 改接成 `mobCount > 1`。量測：

```
"mobCountGt1": 56,        ← mobCount > 1 的技能數
"oldBoolTrue": 56,        ← multiStrike == true 的技能數
"distinctAxes": 56        ← (attackCount > 1) !== (mobCount > 1) 的技能數
```

**66 個裡 56 個不一致。**欄位叫「多次擊」，內容是「打幾隻怪」。

### 改動（逐字）

**刪除**（`SkillCastService.cs`）：

```csharp
// 舊
/// <summary>
/// Whether the skill hits once or six times.
/// </summary>
/// <remarks>
/// <c>Skill.cs:168-178</c>. Three ids are single-strike; the rest combo six
/// times. Default is <c>true</c>, matching the reference.
/// </remarks>
public static bool MultiStrike(string skillId) =>
    skillId is not ("1121008" or "1001005" or "1311012");
```

**新增**（取代它）：

```csharp
/// <param name="levelNode">The <c>level/&lt;n&gt;</c> node the rest of the plan read, or null.</param>
/// <param name="projectile">Whether the skill carries a <c>ball</c> node — <c>Skill.cpp:201-216</c>.</param>
/// <param name="node">Which node the number came from, "" when both are absent.</param>
public int HitCountOf(WzObject? levelNode, bool projectile, out string node)
```

**record 欄位**：`bool MultiStrike` **移除**，換成

```csharp
int HitCount,
bool Projectile,
```
以及尾端選用參數 `string HitCountNode = ""`。

**plan 端**：`wait, collisionCount, totalTime, mobCount, hitCount, projectile, hRange, maxLevel, ...`

**回報沒寫但你會想知道的事**：
- 舊靜態方法**沒有任何呼叫者**。全 repo `grep 'MultiStrike\s*\(|\.MultiStrike'` 只命中定義本身和
  `MapSimSessionStore.cs:2127` 的一段**文件註解**（該檔案 2129 行提到的 `if (plan.MultiStrike) return;`
  在 W-083 已經刪了）。所以刪掉不會編譯壞別人的檔案。
- `plan.MultiStrike` 這個 wire 欄位也**沒有前端消費者**（`grep multiStrike MapleBench/wwwroot` 零命中）。
  刪掉是乾淨的，沒有 shim。

### 殘餘，明寫不藏

參考的分岔條件是 `attack.type == RANGED`，而 `RANGED` 來自
`Skill.cpp:373` `if (projectile || SkillData::get(skillid).is_ranged())`。
`is_ranged()` 是 `SkillData.cpp:139-333` 一張約 150 個 id 的**硬編碼表**。
我只抄了封存可推的那一半（`projectile` = 有沒有 `ball` 節點）。

**量測後這張表今天對答案沒有影響**：66 個裡 5 個有 `ball`（12111006、2121003、2221003、3111003、5221008），
而**這 5 個沒有一個有 `bulletCount`**，所以兩個分支今天都回 1。
抄 150 個 id 來讓一個數字一樣，是這個檔案不做的事。

---

## C2 — pos 1–4 的語意

### 結論：**我們讀對了，不需要改一行行為。** 但語意要寫清楚，因為原來的註解寫錯了方向。

### 參考的 `pos` 是**兩值**欄位，不是五值

`Gameplay/MapleMap/Objects/Mob.cpp:1543-1556` 全文抄錄：

```cpp
void Mob::show_effect(const Animation& animation, int8_t pos, int8_t z, bool f)
{
    if (!active) return;
    Point<int16_t> shift;
    switch (pos)
    {
    case 0: shift = get_head_position(Point<int16_t>()); break;
    case 1: break;
    case 2: break;
    case 3: break;
    case 4: break;
    }
    effects.add(animation, DrawArgument(shift, f), z);
}
```

`shift` 是預設構造的 `Point<int16_t>` = (0,0)。**只有 `case 0` 有賦值。**
所以 `case 1`..`case 4` 是「什麼都不做」的四種寫法，switch 沒列到的值也一樣。

- `pos == 0` → `Mob.cpp:1460-1468` `get_head_position()`：怪的**頭**，
  而且 X 會被 `(flip && !noflip)` 鏡射。
- `pos != 0` → **原點／腳底**。

### 地面貼花用 `pos=1` 是刻意的嗎？——**是註解在替一個「不做任何事」的分支命名**

`Gameplay/Combat/SkillUseEffect.cpp:203-212`：

```cpp
// Inferno's tile belongs to the hit target's ground, not the caster.
// Keep the tile canvas origin; pos=1 is the mob's foot anchor.
const size_t variant = static_cast<size_t>(rand()) % tile_animations.size();
target.show_effect(tile_animations[variant], 1, 4, false);
```

註解寫「pos=1 is the mob's foot anchor」是**真的**，但 `1` 不是一個特別的腳底值——
它是四個什麼都不做的 case 裡的第一個。寫 `2`、`3`、`4` 或 `7`，畫出來**完全一樣**。

### 所以讀取器本來就對

`HitSprites()` 用 `ReadByte(group, "pos")` **原值透傳**，而參考自己就把 1..4 收斂掉了。
我們沒有開分支，是對的。開了反而是發明。

### 已量測：全 66 個 plan 的 pos 分佈

```
pos=0   409 sprite(s)   mob HEAD   e.g. 10001009 hit/0
pos=1    99 sprite(s)   mob FEET (no shift) -- reference treats 1..4 identically   e.g. 11111006 hit/0
pos=2    48 sprite(s)   mob FEET (no shift) -- reference treats 1..4 identically   e.g. 15111001 hit/0
pos=3    78 sprite(s)   mob FEET (no shift) -- reference treats 1..4 identically   e.g. 12101006 hit/0
pos=4     3 sprite(s)   mob FEET (no shift) -- reference treats 1..4 identically   e.g. 5121005 hit/0
```

最後四類**全部落在腳底**（照上面那個 switch）。
`5121005`（Snatch）那 3 張是全 66 裡唯一 `pos=4` 的命中特效——參考也把它畫在腳上。

### 改動：只有註解

`SkillCastSprite`… 不對，是 `SkillHitSprite` 的 `<param name="Pos">` 與 `<remarks>`，
以及 `ReadByte()` 的 `<remarks>`，補上上面這段 switch 與量測。
**沒有行為改動。**

### 一個附帶的正確性確認（也寫進註解了）

`SkillHitEffect.h:43` `pos = src["pos"]` 是把 `nl::node` 賦給 `int8_t`，
**節點不存在時 `nl::node` 給 0**，而 `Mob.cpp:2242` 自己也傳字面 0。
所以「plan 報 pos=0」和「封存沒寫 pos」是同一件事，都是頭。

---

## C3 — SkillCastService 裡還有沒有第二個「只認單一節點名」的讀取器

### 結論：**有。而且找到 4 個真的會畫錯的形狀，全部已改（未驗）。**

W-087 的那個 `damage` 是「讀一個名字」；這幾個是「讀對了名字，但沒讀**節點的深度**」——
同類病，病灶不同。

### 逐欄位核對（參考讀幾個節點名 / 我們讀幾個）

| 欄位 | 參考 | 我們（改前） | 判定 |
| --- | --- | --- | --- |
| `effectDistance` | 1 個名字：`tile/effectDistance`（`SkillUseEffect.cpp:176`） | 1 個，寬度全支援 | ✅ 對 |
| `mobCount` | 1 個名字：`level/<n>/mobCount`（`SkillData.cpp:69`） | 1 個，寬度全支援 | ✅ 對 |
| `range`→HRange | 1 個名字：`level/<n>/range`（`SkillData.cpp:77`） | 1 個，但**用較窄的 switch**，沒有 String 分支 | ⚠ 已改 |
| `maxLevel` | `stats.size()` = level 節點**個數**（`SkillData.cpp:117`） | level 名字的**最大值** | ⚠ 量測後兩者全等，保留並註明 |
| `lt`/`rb` | `level/<技能等級>/lt` + `rb`（`SkillData.cpp:79-83` → `Skill.cpp:459-461`） | `level/<**最大等級**>/lt` + `rb` | ❌ **已改** |
| `action` | `action/0`、`action/1`、`prepare/action`、`level/1/action`（`Skill.cpp:158-199`） | 只有 `action/*` | ❌ **已改** |
| `effect` | `CharLevel/<n>/effect` **取代** `effect`/`effect0..N`（`Skill.cpp:46-52`） | 只有 `effect`/`effect0..3`/`screen`/`ball`/`keydown*` | ❌ **已改** |
| `hit` | `hit/0`｜`hit/0`+`hit/1`｜`CharLevel/<n>/hit`｜`level/<n>/hit`（`Skill.cpp:113-156`） | 只有 `entry/hit/<g>` 全部 | ❌ **已改** |
| `prepare` | `prepare` 且 `prepare/0` 是 bitmap（`Skill.cpp:86-92`） | 完全沒讀 | ⚠ 已加，這個封存上無命中 |
| `affected` | `affected` 且 `affected/0` 是 bitmap（`Skill.cpp:107-110`） | 完全沒讀 | ❌ **已改** |

### 特別點名的「只列 Int/Short/Long 的 switch」——找到一個，已刪

`ReadRealNode(WzObject?, string, double)` 是這個檔案裡**第二個**數值讀取器：

```csharp
// 已刪除
private double ReadRealNode(WzObject? parent, string name, double fallback)
{
    if (parent is null) return fallback;
    return Child(parent, name) switch
    {
        WzFloatProperty f => f.Value,
        WzDoubleProperty d => d.Value,
        WzIntProperty i => i.Value,
        WzShortProperty sh => sh.Value,
        WzLongProperty l => l.Value,
        _ => fallback,          // ← String 在這裡被當成「節點不存在」
    };
}
```

`range` 讀的就是它。`range` 是**會改變命中判定**的欄位
（`MapSimSessionStore.cs:2192` `int near = haveBox ? (int)Math.Round(left * plan.HRange) : left;`）。
現在改走 W-087 寫的那一個 `TryReadReal`（多 String 分支）。

已量測：全 66 個的 `range` 目前都是 `Long`，所以**今天沒有差別**。但這正是 W-087 的形狀。

### 四個真的會畫錯的形狀（全部已量測）

**① `CharLevel` — 2 個技能讀錯了節點的深度**

`Skill.cpp:46` `bool byleveleffect = src["CharLevel"]["10"]["effect"].size() > 0;`
走這條分支時 `ByLevelUseEffect`（`SkillUseEffect.cpp:118-125`）**只**讀 `sub["effect"]`，
`skill/<id>/effect` 與 `effect0..N` 一次都不會被 spawn。

```
  CharLevel (ByLevel use-effect + hit)             2 / 66  5001001 5001002
      CharLevel/10  ["effect(SubProperty)","hit(SubProperty)"]
      /10/effect   ["0(Canvas)","1","2","3","4"]      5001001
      /10/effect   ["0(Canvas)".."6(Canvas)"]          5001002
      /10/hit/0    ["0(48 x 55)","1(65 x 63)","2(59 x 63)","3(56 x 58)"]
      /25/hit/0    ["0(54 x 61)","1(70 x 69)","2(65 x 69)","3(60 x 64)"]   ← 不一樣
```

兩個技能**同時也有** entry 級的 `effect` 和 `hit`，所以讀錯節點不會報錯，只會畫錯拳頭。

參考的選擇規則（`SkillUseEffect.cpp:129-139` / `SkillHitEffect.cpp:60-72`，同一段十行）
**不是「就近不超過」**，是「先走到第一個 ≥ level 的 key，再退一步」：

| 角色等級 | 選到 |
| --- | --- |
| 1–10 | `10`（迴圈沒走，就沒得退） |
| 15 | `10`（停在 15，退到 10） |
| 20 | `15` |
| 25 | `20` |
| 26+ | `25`（走出尾端，從 `end()` 退一步） |

已實作為 `CharLevelPick()`，逐字照抄。

**② `level/<n>/action` — 2 個技能姿勢讀錯**

`Skill.cpp:158-199` 的階梯是：`action/0`+`action/1` → `action/0` → `prepare/action` →
`is_attack()` 時的 `level/1/action`。我們只讀第一階。

```
  1311001  entry/action = []          plan.actions = ["alert"]
  1311002  entry/action = []          plan.actions = ["alert"]
  level/*/action: 1=burster1 ... 15=burster1 16=burster2 ... 30=burster2
```

**entry 的 `action` 是空的子節點**（不是 null，是空容器），所以舊邏輯直接落到 `"alert"`。
參考會播 `burster1`。兩個 id 在 `SkillData.cpp:146-147` 都有 ATTACK 旗標，
所以 `ByLevelAction` 那條分支在參考裡是通的。

**③ `affected` — 1 個技能少了一段動畫**

```
  2301002 affected ["0(Canvas=45 x 19)","1(46 x 31)","2(48 x 47)","3","4","5","6"]
      affected/0 ["delay(Long)","origin(Vector)"]
```

`Skill.cpp:107-108` 讀，`Skill::apply_affectedeffects`（`Skill.cpp:276-281`）在命中**之後**播。
所以我把它放成**獨立的 `Affected` 欄位**，不放進 `effects`——
`EffectMs`（施放鎖定時間）只由施放當下的精靈決定，參考沒有任何一條路徑讓 `affected` 延長鎖定。

**④ `lt`/`rb` — 24 個技能的命中框讀在錯誤的等級**

`Skill.cpp:317-319` 每個等級拿一份 `Stats`，`:459-461` 用的是**角色所在等級**那份。

```
24 of 66 skills carry a different box at level 1 than at the top level.
  1111006 / 2211002   level/1 = -110,-82,110,83    level/30 = -400,-350,400,250
  4211006             level/1 = -200,-100,0,50     level/30 = -300,-120,0,50
  4121008             level/1 = -100,-70,100,30    level/30 = -200,-70,200,30
```

`1111006` / `2211002` 的等級 1 框比等級 30 的**小四倍**。
我們讀的是等級 30 的框，等於用滿級的揮擊範圍去做一級角色的命中判定。

⚠ **這一條會改變 A 支線的命中結果**：`MapSimSessionStore.cs:2183-2198` 用 `plan.Ltx/Lty/Rbx/Rby`。
角色等級 1 時 24 個技能的判定框會**縮小**。這是參考的答案，不是我的選擇，
但合流後 A 支線的 `ApplySkillHits` 會看到不同的數字。

### 沒有改、但量過了的（避免以後有人重來一遍）

| 形狀 | 量測結果 | 為什麼不用改 |
| --- | --- | --- |
| `maxLevel` | `plan.maxLevel != level 個數`：**0 / 66**；`level 最大名 != level 個數`：**0 / 66** | 參考算 `stats.size()`，我們算最大名。這個封存上兩者全等，分不出來。保留最大名（也是夾限需要的）。 |
| entry 根的 `lt`+`rb` | **0 / 66** | `box ??= Corners(entry)` 這個 fallback 從來沒觸發過。留著（`Sample()` 會走全部書），註明它不會跑。 |
| entry 根的 `z` | **0 / 66** | `AddCastSprite` 的 z 查找鏈本來還包含 `entryPath`，我拿掉了（參考只讀 effect 節點上的 z，`SkillUseEffect.h:50`）。量測確認沒有技能靠那個 fallback。 |
| `prepare` | 2 / 66（15101003、5101004） | `prepare = {action:"dash", time:0}`，**沒有** `prepare/0` → 參考的 `data_type() == bitmap` 判斷為假 → 不是精靈。而兩者 `action/0 = "screw"` 存在，`Skill.cpp:169` 那條分支根本走不到。**舊答案是對的。** |
| `special` | 6 / 66 | `Skill.cpp:100-104` 只對 `EXPLOSION=2111002`、`Genesis_212=2121001`、`Genesis_222=2221001`、`Genesis_232=2321001` 讀。**這四個都不在 66 裡。** 沒有缺口。 |
| `level/<n>/hit` | **0 / 66** | `BySkillLevelHitEffect` 照抄了，但在這個封存上不可能觸發。 |
| `level/<n>/ball` | **0 / 66** | 同上。 |
| `effect4..` | 最深只到 `effect3` | `MultiUseEffect` 是無上限走訪（`SkillUseEffect.cpp:98-110`）。我加了延伸到 `MaxMultiEffectIndex=64`，今天加不到東西，但規則是對的。 |

---

## C4 — 其他形狀漏網

跑 `probe-C4-shape-sweep.mjs`：66 個 plan × 30 個欄位，取每個欄位的分佈。

### 全部 66 個都一樣（= 沒有資訊的欄位）

```
hasCollisionBox   1 distinct   66x true
skillLevel        1 distinct   66x 1        ← /api/skill-cast/plan 固定 characterLevel=1
effectMs          1 distinct   66x nonzero
planAffected      1 distinct   66x (none)   ← 舊版；新版應該 1 個是 2301002
```

### 只差一兩個的欄位（= 還沒被處理的形狀）

| 欄位 | 只差 1–3 的值 | 是誰 |
| --- | --- | --- |
| `effectZ` | `effect:-1` × 3、`effect:0+screen:0` × 2、**`effect:-2+screen:0` × 2** | 1111008/2221007/2311004；10001009/20001009；**10001020/20001020** |
| `hitGroups` | **9**（4211006）、**6**（15101003 / 4211004 / 5101004）、**3**（5121001）、**2**（4221007） | 見下 |
| `hitPos` | **4** × 1 | 5121005 |
| `hitZ` | `0+1` × 1 | 4211004 |
| `tileCount` | 1（12111005）、3（3111003）、4（5121001） | 既有處理，非缺口 |
| `tileStep` | **150**（2321008）、**20**（3111003）、**200**（5121001） | 既有處理，非缺口 |
| `hRange` | **1.5** × 1 | 4121008 忍者衝擊（→ A3） |
| `damagePresent` | absent × 1 | 2301002 群體治癒 |
| `damageNode` | `x` × 1、**`damagepc` × 4** | 4211006；10001009/10001020/20001009/20001020（W-087 已修） |
| `boxWidth` / `boxHeight` | 21 / 20 個相異值 | 全部命中框都不同，正常 |

### `hitGroups` 大於 1 的 6 個技能——參考只會畫其中 1 個

`Skill.cpp:113-156` 選**一個** subclass，每個 subclass 最多讀**兩個** group：

| 技能 | group 數 | 參考選到 |
| --- | --- | --- |
| 15101003、5101004、4221007 | 6 / 6 / 2 | `TwoHandedHitEffect`：`hit/0` 或 `hit/1`，**依武器** |
| 4211004 | 6 | `RandomHitEffect`：`rand() % size`，**隨機一個**（`SkillHitEffect.cpp:31-42`） |
| 4211006 | 9 | `SingleHitEffect`：**只有 `hit/0`** |
| 5121001 | 3 | `SingleHitEffect`：**只有 `hit/0`** |

**已加** `string HitSelector` 到 plan（診斷用），實測應該是
60 × `SingleHitEffect`、3 × `TwoHandedHitEffect`、1 × `RandomHitEffect`、2 × `ByLevelHitEffect`。

**`HitEffects` 清單本身沒改**——它仍然是封存上**所有** group 的圖檔庫，
而 group 0 在參考的**每一條**分支裡都合法，所以永遠畫 group 0 的客戶端畫得出來東西。

### `effect_ship` — 兩個參考都不讀

```
  5211004 effect_ship ["0".."14"(Canvas)]   effect ["0".."14"(Canvas)]   plan.effects ["effect"]
  5211005 effect_ship ["0".."12"(Canvas)]   effect ["0".."12"(Canvas)]   plan.effects ["effect"]
```

`clean-client` 和 MapleNecrocer 都沒有任何一行讀 `effect_ship`。
**不屬於這條線：我不會加它。** 加了就是憑空發明一個參考沒有��繪製規則，
而且這兩個技能會多畫一段參考不畫的東西。要不要畫是 B 支線的視覺判斷，不是我的。

### 附帶發現（同樣不屬於這條線）

- `2301002`（群體治癒）**沒有**任何傷害節點（只有 `hp`）。參考 `Skill.cpp:363`
  對 `stats.damage <= 0` 的技能給 `skill_damage_rate = 1.0`（武器基底傷害）。
  但 2301002 沒有 ATTACK 旗標，`Combat.cpp:711-721` 走的是治療分支，**不該有傷害**。
  plan 報 `damage: null` 是對的。66 個裡沒有「是攻擊技能卻沒傷害節點」的反例。
- `mob`、`info`、`afterimage`、`masterLevel`、`weapon`、`req`、`invisible`、`elemAttr`、
  `x`/`y`/`time`/`prop`/`cooltime`/`mpCon`/`itemCon` 這些 entry 節點 plan 都不讀。
  大部分是冷卻/MP（ADR-0005 明確不做）。**不是這一輪的缺口。**
- `screen` / `keydown` / `keydown0` / `keydowned` **不在權威參考裡**，
  是 MapleNecrocer `Skill.cs:154-155` 的清單。留著並在程式碼裡註明出處，
  因為封存上真的有（4 個技能有 `screen`、2 個有 `keydown`），
  畫一個參考不畫的精靈比丟掉封存寫的精靈更糟。

---

## 可重跑的檢查

```
node MapleBench/tools/probe-C1-multistrike.mjs     → C1
node MapleBench/tools/probe-C2-pos-and-shapes.mjs  → C2 + C3 的形狀實據
node MapleBench/tools/probe-C3-node-shapes.mjs     → C3
node MapleBench/tools/probe-C4-shape-sweep.mjs     → C4
```

四支都用 `BASE` 環境變數指向伺服器（預設 `http://127.0.0.1:5591`）。

**它們怎麼知道伺服器是舊版**：plan 裡沒有 `hitSelector` 欄位時，四支都會
印 `server not rebuilt` 並把**依賴新 plan 的斷言標成 SKIP 而不是 FAIL**。
架構量測（`/api/children` 直接讀封存）不依賴新 build，所以現在就有效。
合流重編後，同樣四支指令會把那些 SKIP 變成真的 PASS/FAIL。

本次執行結果（舊版伺服器）：

```
probe-C1-multistrike.mjs     exit=0    5 passed, 0 failed, 3 skipped
probe-C2-pos-and-shapes.mjs  exit=0
probe-C3-node-shapes.mjs      exit=0    0 passed, 0 failed, 4 skipped
probe-C4-shape-sweep.mjs      exit=0
```

---

## 我沒驗到的部分（逐條）

1. **W-089 的 C# 改動沒有編譯過。** `lsp_diagnostics` 對
   `SkillCastService.cs` 回 `No diagnostics reported`，
   對 `MapSimSessionStore.cs` 只回三個既有的 redundant-using Hint（CS8933/CS8019，改前就有）。
   語言伺服器不是編譯器——`dotnet build` 才是。
2. **`hitCount` / `hitCountNode` / `projectile` / `hitSelector` / `Affected`
   五個新欄位的實際值，一次都沒有被量到。** probe 會在重編後驗。
3. **`CharLevel` 分支、`level/<n>/action` fallback、`affected`、24 個技能的框縮小，
   這四項的執行結果一次都沒看過。** 它們是靜態讀取器的改動，行為一定會變，
   但變成什麼樣要重編後才知道。
4. **`CharLevelPick()` 的等級選擇規則我只在紙上對過 C++ 的迴圈，沒跑過。**
   probe-C3 有對應斷言（重編後才會跑）。
5. **`EffectNodes` 延伸到 `effect4..` 在這個封存上不會觸發**，所以「延伸是對的」
   這句話沒有任何執行證據。
6. **刪掉 `ReadRealNode` 沒有編譯證據**（它是 private、只在本檔用，但「只在本檔用」
   是 grep 出來的，不是編譯器確認的）。
7. **我沒有測過任何一個技能實際施放後的視覺。** ADR-0005 的四項
   （位置／機制／即時／可見）我這輪一個都沒有從客戶端角度驗過——
   `walkview.js` 不在我的寫入範圍，而且舊版伺服器也還在跑。