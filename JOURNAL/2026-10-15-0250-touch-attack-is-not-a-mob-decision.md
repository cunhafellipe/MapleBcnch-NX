# 2026-10-15 02:50 — 接觸傷害本來就不是怪的決策：整條攻擊鏈重寫

> **本篇在 08:40 之後繼續，被使用者反饋推著加了四項。見 §7。**

## 0. 這一輪推翻了上一輪的架構

上一輪把「怪物攻擊玩家」做成：**被 aggro 的怪 → 面向 → 冷卻 → 判定方框 → 傷害**。
一路都有引用，所以看起來像對標。實際上**整條鏈的架構就是錯���**，
引用只證明「這些數字有出處」，沒有證明「它們被接在一起的地方是對的」。

出處在 `Stage.cpp:809-818`：

```cpp
if (int32_t oid_id = mobs.find_colliding(player.get_phobj(), player.get_touch_bounds()))
{
    if (MobAttack attack = mobs.create_attack(oid_id); attack && ...)
    {
        player.damage(attack);
        TakeDamagePacket(result, TakeDamagePacket::From::TOUCH).dispatch();
    }
}
```

**接觸傷害完全不是怪的決策。** 它是「角色身上那個矩形和哪一隻怪的動畫框重疊」。
沒有 aggro、沒有面向、沒有冷卻、沒有 `firstAttack`、沒有 y 帶。

### 之前錯在哪

| 之前 | `Stage.cpp:809` 實際上是 |
|---|---|
| 只有 aggro 的怪會打 | 任何被碰到的活著的怪 |
| 每隻怪各自判定 | `std::find_if`，**第一隻**重疊的（`MapMobs.cpp:255-268`） |
| 面向 + `AttackReach` 方框 | 角色**掃過**的身體矩形 vs 怪的**當前動作**動畫框（`Mob.cpp:1875-1884`） |
| `characterY == _y` | `Char.cpp:449-452`：`{-22, 22, -65, 0}` |
| 有 `AttackCooldown` | **完全沒有節流**，每一幀都打 |

**後果**：一隻普通怪站在玩家旁邊是**完全不痛**的。那不是遊戲。

## 1. 改動

`MobPatrol.AttackPlayer` 拆成兩個對標裡分開的函式：

| 新 | 對標 | 負責 |
|---|---|---|
| `MobPatrol.Engage(x, y)` | `MobAttack.cpp:20-127` | 取得、放下、追。**不打任何傷害** |
| `MobPatrol.ContactDamage()` | `Mob.cpp:1848-1858` `create_touch_attack` | 一個接觸傷害：權限 + 數字 + 冷卻 |

`MapSimSessionStore.Advance` 新增 `MapSimSession.PrevX / PrevY`（`MapMobs.cpp:245-253` 的掃過矩形），
並依 map 順序找**第一隻**活著、當前動作框與掃過矩形重疊的怪。

`firstAttack` 現在只管**追不追**，不管打不打——這正是它原本的意義。

## 2. 驗證

```
touch-probe   2/2   aggro=False on the toucher — 那就是要修的那件事
                     9409000（notAttack=1）仍然會碰你：該旗標只擋 update_attack
mob-attack    provoked at 68px → 退到 101px → 怪自己走回 26px，+83/+144 幀各打一下
chase-verify  FAILURES: none
nomove        8/8 命中，走動 0.0px
facing-rule   16/16
first-attack  2 PASS    ladder 5/5    attack    pass    skill  pass
npc 2/6, 0 誤標          node 24/24    建置 0 錯誤 0 警告
```

## 3. 被正確行為打破的測試（改的是**前提**，不是門檻）

| 測試 | 舊斷言 | 為什麼錯 | 現在斷言 |
|---|---|---|---|
| `first-attack-verify` §1 | 「普通怪 0 傷害」 | 那**就是**錯的行為本身 | 怪有沒有**走**過去 |
| `chase-verify` §1 | 「收斂到 ≤41px」 | 舊的 `AttackReach` 虛構方框。`move` 幀從 82px 就碰得到，同一隻怪站 19px 反而碰不到 | 「來到臂長距離」 |
| `chase-verify` §2 | 「它沒有移動」 | 巡邏本來就會動怪（`Mob.cs:354-375`，2% 命中率） | 6 次裡最多 2 次收斂 |
| `mob-attack-verify` | 走到怪旁邊 | 90 傷害 / 50 血，走過去就死 | 改用 `9500302`（10 傷害）+ 後退 |

## 4. 這一輪又把量測工具騙了自己兩次

1. **`mob-attack-verify` 連續揮擊** —— 揮擊姿勢 48 幀且擋住走動，角色 400 幀只走 1.5px。
2. **同一支腳本先判 `status.dead` 再讀 `playerDamage`** —— 殺死玩家的那一幀兩者同時成立，
   先判 dead 就把那一擊丟掉。**守則 §2.9，第二次踩到。**

`attack-test.ps1` 長期報「8 damage entries」是幻象：`@($null).Count` 在 PowerShell 是 **1**。

---

# §7. 2026-10-15 08:40 — 使用者反饋後追加的四項

## 7.1 放開上鍵會掉下來（已修，實測）

```
修正前：爬到 y=-325，放開所有鍵 → action=fall，掉回 y=-265
修正後：爬到 y=-325，放開所有鍵 → y=-325 不動，action=climb
```

`MapleCharacter.cs:541-585` 只在 Up / Down 分支裡改 `Y`，放開鍵兩個分支都不跑，
`InLadder` 保持 true——角色掛著。本專案原本在 `climb == 0` 時 `Fall()`。
加了兩處：`StepCore` 的掛住分支，和 `ClimbOn` 的同一條規則。

## 7.2 爬梯時眼睛長在後腦勺（已修，存檔自己寫明了）

```
f1/00002000.img/ladder/0, ladder/1, rope/0, rope/1  body z = "backBody"
f1/00002000.img/walk1/0,  stand1/0                   body z = "body"
```

參考同時把 `FaceDir` 設成 `None`（`:546/:563/:573`）。**兩邊都說：背面沒有臉。**
改成讀姿勢自己的 `z`，`backBody` 就不收臉部件。

驗證（前後都有臉，才不算把臉整個弄丟）：

```
walk1  [body, arm, head, face]  face=1   PASS
stand1 [body, arm, head, face]  face=1   PASS
jump   [body, head, face, ...]  face=1   PASS
ladder [body, head]             face=0   PASS
rope   [body, head]             face=0   PASS
```

## 7.3 圖層接縫：`backBody` 那一類深度被丟棄（已修）

使用者說「從每一個圖塊下去判斷，平地跟方塊疊起來的高地，前接觸這邊」。

**第一層掃描（層與層的頻寬）：5366 張地圖抽 60 張，57 張一致、3 張反轉** ——
層頻寬不是普遍問題，我的測試圖屬於正常的 57 張。

**真正的原因在層內**：

```csharp
// MapEditorService.cs:2537  原本
int z = (canvas["z"] as WzIntProperty)?.Value ?? 0;
```

`MapTile.cs:33` 讀的是圖檔節點自己的 z，實測 `f4/Tile/grassySoil.img/edU/1` = **-4**。
窄型別轉換對其他整數型別回傳 null，`?? 0` 把它吞掉。**於是同層所有地磚共用一個 z**，
`scene.sort` 穩定排序退回文件順序——平地蓋住高牆。

```
修正前：253 塊地磚 → artZ 全部 0
修正後：253 塊地磚 → {-4:38, -3:100, -2:18, 0:97}
```

## 7.4 無敵開關（自設計，已標註）

session 請求新增 `invulnerable: true`。理由：接觸傷害每幀都打，角色第一下就死，
後面的揮擊動畫根本看不到。**只擋 HP 扣減，傷害回報照舊**——擋掉回報等於把要測的東西藏起來。

## 7.5 「可以爬任何東西」—— 沒重現

```
離所有梯子 335px 的地面按上 60 幀 → actions=['stand'], dy=0, onLadder=False
```

`FindLadder` 的判定和 `LadderRopes.cs:19` 完全一致。這張圖的梯子全部懸空
（y −533..−271、−1343..−1082），地面在 y=−25，距離上千像素，本來就該拒絕。
**需要使用者指認地圖與位置。**

## 7.6 攻擊清單（資料層已通，執行層未接）

`CombatService.MobAttacks` + `MobAttackSpec.Reaches`（`MobAttackGeometry.h:98-117` 四種 type 全移植）。

讀到的值與手動量測完全一致：

| 怪 | 手動量測 | 伺服器讀到 |
|---|---|---|
| 3300007 | type=0, PADamage=125, conMP=2, after=1200ms, lt=−250,−80 rb=0,0 | 完全相同 |
| 4230101 | type=1, PADamage=135, after=850ms, r=200 | type=1, power=135, after=850 |
| 4220000 | type=2, PADamage=110, after=1320ms, r=160 | type=2, power=110, after=1320 |
| 2400049 | type 缺省, after=1560ms, lt=−275,−175 | type=0, after=1560 |

**中途抓到一個自己的 bug**：`ReadInt` 沒處理 `WzLongProperty`，NX 的整數就是這種型別，
於是 `type`/`PADamage`/`conMP`/`attackAfter`/每幀 `delay` 全讀成 0。
而 `range` 走的是 `WzVectorProperty` 所以正常——**抽查幾何會過，抽查傷害與時序不會**。

**尚未接上**：`Engage` 還沒用這份清單算 `available`、沒挑攻擊、沒播 `attackN` 姿勢、
沒在 `attackAfter` 結算傷害、沒設 `max(duration, delay) + 400` 的冷卻。

**MP 仍未決**：C++ 有（`Mob.cpp:155` 讀 `conMP`、`:113` 扣），C# 對標沒有，
`§7.6` 說 C++ 贏但這個專案其餘都跟 C#。數值有讀進來，**沒有扣**，等裁決。

## 7.8 這一段的工具失誤記錄

| 假結論 | 真正原因 |
|---|---|
| 「按上在任何地方都跳起來」 | 探針在 `?jump` 造成的 `rise` 狀態下才量 `?up`（§2.9 的前置幀污染） |
| 「每個姿勢都沒有臉」 | GET `/look` 沒有 slot map，本來就組不出臉 |
| 同上 | POST 的 slot 名是 `Face-1` 不是 `Face`，而且要**裸 id** `00020000` 不是路徑 |
| 「攻擊資料全為 0」 | `WzLongProperty` 沒處理（見 §7.6） |
| C: 磁碟 0 GB free | 建置寫檔的瞬間量到，事後自行恢復到 32 GB |

## 7.9 門檻（全綠）

```
first-attack 0 · mob-attack 0 · ladder 5/5 · attack 0 · skill 0 · npc 0 · node 24/24
建置 0 錯誤 0 警告，wwwroot 三方 md5 一致
```