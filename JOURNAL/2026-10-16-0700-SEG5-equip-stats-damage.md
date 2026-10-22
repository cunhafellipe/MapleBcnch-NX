# 段 5a/5b — 裝備 → 屬性 → 傷害鏈，以及一個藏了很久的缺陷

日期：2026-10-16
狀態：完成，門檻全綠（10 個 suite）。

## 量測先行

兩個 subagent 讀完兩份參考的傷害鏈，得到三個決定性事實：

1. **兩份參考在「傷害套用層」上不一致**：C# 那份 fork（`_necrocer_src` 是
   Kagamia WzComparerR2 的剝枝版）**根本沒有** `close_totalstats` /
   `get_attack` / `Mob.calculate_damage`；只有統計聚合與武器倍率表。
   傷害公式只能來自 C++（守則 §7.6 本來就是這樣）。
2. **兩份參考的屬性節點名完全一致**：`incSTR incDEX incINT incLUK incMHP
   incMMP incPAD incMAD incPDD incMDD incACC incEVA`（C++ `EquipData.cpp:43-57`，
   C# `Gear.cs:840-854` 把 `info` 子節點名當 `GearPropType` 枚舉成員）。
3. **C++ 傷害鏈根本不从 WZ 讀裝備屬性**：`ItemParser.cpp:87-90` 直接讀伺服器封包的
   15 個 short；`EquipData::defstats` 只給 tooltip 與品質分級用。
   本專案沒有伺服器封包，所以只能讀 archive —— 但那正是參考自己的 tooltip 讀的
   同一批數字。這點在 `EquipStatsService` 的類註解裡宣告，不留暗門。

### archive 端實測（840 件，12 個屬性類別各抽 70 件，只走 `/api/inspect`）

| 節點 | 次數 | 節點 | 次數 |
|---|---|---|---|
| incPDD | 326 | incEVA | 42 |
| incDEX | 134 | incMHP | 39 |
| incMDD | 129 | incACC | 31 |
| incSTR | 123 | incSpeed | 27 |
| incLUK | 105 | incMAD | 18 |
| incINT | 70 | incJump | 15 |
| incPAD | 69 | incMMP | 62 |

量測抓到三件靠節點名稱永遠抓不到的事：

- **archive 寫的是 `incSpeed` / `incJump`，不是 C++ 參考的 `incSPEED` / `incJUMP`**
  （27 / 15 次）。大小寫完全比對會兩個都讀成 0。
- **`Glove/01082143.img` 把 LUK 節點拼成 `incLUk`**（小寫 k）。一個大小寫敏感的
  讀取器會安靜地丟掉它的 LUK。所以所有讀取一律 `OrdinalIgnoreCase`。
- **`Weapon/01372038.img` 帶著 `incRMAI` / `incRMAL`**，兩份參考的表都沒有。
  讀了就丟，不映射成任何屬性。

840 件裡 **0 個** `r` 形式的百分比節點、**0 個**合併鍵、**0 個** `incHANDS` ——
後者因為兩份參考都是從道具 id 推.hand 數，不是從節點。

## 修掉的東西

### 1. 角色基礎屬性本來就不對（量測缺陷）

`PlayerStatus` 是 STR/DEX/INT/LUK = 4/4/4/4、MaxMP = 0。
`MapleNecrocer/CharaSim/Character.cs:14-21` 寫的是 MaxHP 50、MaxMP 10、
**STR 12、DEX 5**、INT 4、LUK 4。直接照抄。

### 2. `AttackBox` 從來沒用過武器 —— **每一次揮擊都打在空氣上**

`CombatService.AttackBox` 用**武器 id**組路徑，問 `Afterimage/01302000.img`。
那張圖不存在：`Afterimage` 底下是 15 個**武器類別**
（axe barehands blank bow crossBow gun hit knuckle mace poleArm spear
swordOL swordOS swordTL swordTS）。所以每次查詢都掉到 fallback，
`/api/simulate/debug/attack-box?weapon=01302000.img` 回的是
`barehands.img/stabO1`，box (-64,-33)-(-27,-18) —— 全遊戲最小的 37x15，
而且整個落在角色**背面**。角色貼著怪（差 9px）兩個 box 還是不重疊。

映射一直都在手邊：`Weapon/01302000.img/info` 帶 `afterImage = "swordOL"`，
參考正是讀它（`WeaponData.cpp:52` → `Char.cpp:950` → `Afterimage.cpp:39` →
`RegularAttack.cpp:49` 拿 afterimage 的 range 當攻擊距離）。

修正後：`swordOL.img/stabO1`，box (-84,-30)-(-20,-15)，64x15。

### 3. `AttackStancesFor` 只認呼叫者給的 id 拼法

`1302000` 解析成 `f1/Weapon/1302000.img` → 不存在 → **回空陣列** → 沒有揮擊姿勢
→ 沒有姿勢鎖定 → 傷害從不結算。HTTP 200，一切看起來正常。
`WeaponAttackSpeed` 早就記載過這個坑並且試過所有寬度，這裡沒有跟。
現在三處（`WeaponAttackSpeed` / `AttackStancesFor` / `AfterimageName`）一致。

### 4. `SetEquippedWeapon` 重建角色表時弄丟 `Invulnerable`

角色表換新是對的（`Player::recalc_stats`），但 `Invulnerable` 是 session 設定
不是推導值，兩份參考都不會重算它。裝備一次就讓角色變脆弱。

### 5. `DamageRoll` 換成參考公式

- 攻擊者傷害 = `PlayerStatus.MinDamage/MaxDamage`
  （`CharStats.cpp:118` 新手分支 `((4*STR+DEX)/100)*(WATK*1.2)`，
  `:172` `max(1,(int)(max*0.20+0.5))`）
- 怪的防禦 = `info/PDDamage`（`Mob.cpp:62`），min 減 0.6、max 減 0.5
- 舊註解說「archive 沒帶防禦節點 `info/wdef` 所以拿不到」—— **這個 archive 上
  根本沒有 `wdef` 節點**，真實節點是 `PDDamage`，342/343 隻都有。
  錯誤的節點名讓防禦項被當成「資料缺席」丟掉，而它一直都在。

實測：`01302000`（incPAD=17）→ max=11、min=2，實測傷害落在 1..3
（怪的 PDDamage=10 把區間壓下來），怪 HP 350→340。

## 這輪的工具教訓（都是真的假綠燈）

1. **`@($null).Count == 1`。** `attack-test.ps1` 讀 `@($r.damage)`，
   frame 沒打中時根本沒有 `damage` 鍵，`@($null)` 每次算 1 命中 ——
   它長期回報「8 次揮擊、8 個傷害」，實際上一次都沒中。已改成
   `@($r.damage | Where-Object { $_ })`。
2. **延遲結算的事件，量測時必須包含等待幀。** 命中在
   `swingDelayTicks`（實測 15–20 幀）之後才出現，而且**下一幀再按攻擊會
   把倒數重設**，所以「每幀揮一次」的迴圈永遠結算不出任何一擊。
   傷害數字出現在**閒置幀**上，不是按鍵那一幀。
3. **選怪只看 x。** `attack-test.ps1` 挑到 x=1867 y=-535 的怪（出生點上方 510px），
   走過去什麼都沒有。已改成同層挑選 + 從角色**當前**位置算 gap。
4. **正則不是門檻。** 三次嘗試用文字推論紅燈，三次都錯：$LASTEXITCODE 對
   PowerShell 腳本不設定（誤判 6 個綠燈）；`\d+ failed` 命中
   `passed=5 failed=0` 裡的「0 failed」（再誤判 2 個）。最後一版只看
   suite 自己印的明確標記，其他一律照印不判。

## 門檻

建置 0 error 0 warning · build-check 160 檔與 bin 全等 ·
`walkview.js` / `avatar.js` / `livewalk.js` src/bin/served 三方 md5 全等 ·
`avatar.js` 維持 `a1eb2eb47af42e07ee048762c2592456` ·
10 個 suite 全綠（ladder-all / attack-test / skill-test / mob-attack-verify /
first-attack-verify / npc-label-check / sim-skills-check / mobinfo-verify /
equipstats-verify / sheet-verify）· 編輯器已開。