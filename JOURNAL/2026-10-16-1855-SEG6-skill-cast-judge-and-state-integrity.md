# 2026-10-16 18:30–19:00 · SEG6 — 施法鏈、判官可信度、狀態完整性

> 結論先講：**本段完成三件事，全部有實測證據，gate 收在 `RED SUITES: 0`。**
> 其中兩件（判官缺陷、狀位元組 BOM）是「原來就壞、沒人知道」，
> 第三件（技能傷害公式）是使用者指定的開發目標。

接手者先讀這裡，再讀 `pm/STATE.md`，然後 `pm/work/W-061` `-062` `-063` 的 Result。

---

## 0. 本段為什麼會開始：一次中立導讀

使用者要求「以中立角色閱讀這個專案，接著繼續開發模擬器核心功能」。
導讀過程中讀了 `pm/` 全套控制層，並對 C# / JS 原始碼做了五路分派掃描
（模擬器核心 / 物理 / 怪的戰鬥 / 前端繪製 / 狀態與門檻）。

導讀本身沒有改任何檔案。它產生了三個後續動作的依據：

| 發現 | 影響 |
|---|---|
| `pm/PARITY.md:73` 宣稱 "Ledge grab / hang — verified" | 我用整字邊界搜尋 `\b(Ledge\|hang\|Grab)\b` 掃過 `Services/MapEditor`，**13 個命中全是註解裡的 "ledge"（走到邊緣），沒有任何狀態、方法或旗標代表懸掛**。`CharacterPhysics.cs:374` 的 `hang, do not fall` 指的是**站在梯子上沒按鍵**。**該條宣稱與程式碼不符，待查。** |
| `FallOutSlack = 60f`（`CharacterPhysics.cs:86`） | 周圍每個常數都標了 `MapleCharacter.cs:NNN` 出處，**只有這個沒有**。它夾住「掉出地圖」，而 W-007 / W-038 兩項正是 blocked 在「找不到會掉出世界的地圖」。**這條線不需要地圖 id，只需要讀碼。** |
| W-035 / W-036 / W-057 / W-060 的 acceptance 是空的 | `pm.py next` 印出來的「接下來三件事」有兩件照現況**無法收尾**。 |

使用者裁定的方向：**玩家施放技能**。並核准先跑一次 gate 建立基準。

---

## 1. Gate 基準（18:38 實跑）

依 `pm/GATE.md:12-17` 的順序跑完四步：

```
build-gate.ps1   -> exit=0
deploy.ps1       -> wwwroot 59 檔全等; avatar.js md5 = a1eb2eb47af42e07ee048762c2592456
open-archives.py -> all 16 archives open, failed: []
gate.ps1         -> RED SUITES: 1
```

唯一的紅燈是 `mob-attack-verify`，輸出：

```
(the 50-HP character died on that hit; the chase itself had already been proven by the gap)
NO DAMAGE over 400 frames -- player hp still 0/50
player ended at 1601, mob at 1621.8091 -- gap 20.8090999999999
>>> RED: no damage over the 400 frames -- the mob never connected
```

**這句結論本身是錯的**：玩家 0/50、怪貼到 gap 20.8，代表怪打中了而且打死了人。
腳本下一句就說「never connected」——自我矛盾。

**怎麼證明是判官壞而不是產品壞**：重跑 4 次全綠，並且拿到了成因證據。
腳本自己在檔頭寫的原因（`mob-attack-verify.ps1:5-12`）：貼身傷害不需要仇恨，
靠過去就掉血。而腳本**從不記錄 Phase A 的玩家血量**：

```
RUN 1:  retreated to gap 100.2, player hp 30/50   <- 進戰鬥前就掉了 20 血
RUN 2:  retreated to gap 100.6, player hp 50/50
RUN 3:  retreated to gap 101.7, player hp 30/50
```

血池在接近階段被抽乾，Phase B 中途 `status.dead` 觸發 `:137` 的 break，
那一幀 `playerDamage` 沒被回報 → `:141` 判定 `hits.Count -eq 0` → 印紅。

> **結論：怪的追擊與貼身傷害是正確的。這是 `工作室守則 §2.9` 的上層版本——
> 量測的前置階段消耗掉了量測本身依賴的資源。**

---

## 2. W-062 · 判官（已 close）

### 2.1 skill-test.ps1：105 行裡一個斷言都沒有

檔尾四行全是 `print`，包括 `<-- MUST be refused`。W-057 點名的兩個無斷言 suite
其中之一就是它。

**改成真斷言**：加了 `Check()` 機制，每個斷言失敗都印 `>>> RED`（`gate.ps1`
唯一允許讀的顯式標記），檔尾印 `VERDICT: skill-test N assertions, M failed`
—— `VERDICT` 是 `gate.ps1:20` 五個比對樣式之一，gate 才有東西可以顯示。

**新斷言第一次跑就抓到一個舊 suite 看不到的真問題**：

```
CAST -> action=walk skillId='' skillStance='' damage=0
FAIL  1.1 a MultiStrike=false cast deals damage -- damage entries=0
FAIL  1.3 the pose lasts more than one frame -- only 0 of 80 frames held the pose
```

用 `%TEMP%\skill-cast-probe.py` 原樣複製腳本序列去量：

```
walked to x=2057.4773 y=-25 facing=1 action=walk onLadder=False dead=True
```

**角色在施放之前就死了。** 目標怪 2220100 的 `info/PADamage = 90`，
新鮮角色 50 HP，而從 spawn 走到目標的 ~140 幀全在它的接觸框裡。
`CastSkill` 第一道門就是 `if (session.Status.Dead) return;`
（`MapSimSessionStore.cs:1472`）—— **產品是對的，腳本才是錯的。**
端點自己的註解早就寫了這件事（`SimulatorEndpoints.cs:305-307`：
"a level-1 character (50 HP) on a mob map is dead within seconds"）。

**更糟的是 section 2 的「MultiStrike 不造成傷害」通過的理由是錯的**：
屍體本來就沒人能打中。證明不了任何事的綠燈，正是這個項目存在的理由。

**修法**：`New-Session` 送 `{"value": true}` 開免傷
（端點收的是 `value` 不是 `invulnerable`，踩過），並加一條會抓到它的對照組
`1.0c CONTROL: the character survived the approach`。

### 2.2 mob-attack-verify.ps1：修 flaky

- 每次嘗試都開免傷，接近階段不再花掉 Phase B 要量的血池
- 每次嘗試都印 `player 50/50 after the approach`，紅燈那一局從來沒印過這行，
  所以它無法診斷自己
- **「死了」和「從沒打中」拆成兩個分支**。死亡且零命中 = INCONCLUSIVE，
  印自己的 `>>> RED: inconclusive`（`gate.ps1` 看不到 exit code，T-2，
  一個什麼都沒證明的 run 不該讀成 `ok`）。只有角色活著卻零命中才印
  `>>> RED: ... the mob never connected`。

**驗收：5 次連跑全綠**（原本 5 跑 1 紅）。怪的最近距離也從 50–69px 收斂到
20–27px——那是沒被花掉的 HP 買來的，怪現在真的走完整段路。

### 2.3 失敗路徑本身也驗過

`skill-test.ps1 -ForceFail` → `>>> RED` 有印、`exit 1`。
**一個宣稱能報紅的判官，必須證明它報得出紅。**

---

## 3. W-061 · 技能傷害從亂數改成真公式（已 close，使用者指定）

### 3.1 先量，再寫

`Skill.nx` 全普查（535 筆，透過 `/api/inspect`，不是從註解猜節點名——
W-041 條款二就是那樣失敗的）：

| 事實 | 值 |
|---|---|
| 技能條目總數 | 535 |
| 有 `level/1/damage` | **175** |
| 完全沒有 `level` 節點 | 0 |
| `level/1/damage` 值（n=175） | min 1 / p25 72 / 中位 104 / p90 265 / max 1210 |
| `f5/<mob>/info/PDDamage`（n=409/411） | min 0 / p25 40 / **中位 170 / p90 950 / p99 2000 / max 30000** |

**所以封存裡本來就有真正的傷害資料，只是沒有任何程式碼讀它。**

### 3.2 原本是什麼

`MapSimSessionStore.cs:1613`：

```csharp
int dealt = mobPatrol.TakeHit(50000 + session.CombatRandom.Next(700000));
```

硬編碼亂數。不吃角色、不吃技能、不吃怪的防禦。而 `DamageRoll.Roll`
（`Mob.cpp:1577-1600` 的參照公式）只走揮擊路徑——**同一隻怪，技能打它
和揮擊打它是兩套物理**。

### 3.3 改成什麼

1. `SkillCastPlan` 加 `SkillLevel` / `Damage`；`Plan` 多收 `characterLevel`
   （預設 1，所以唯讀端點行為不變），讀
   `level/<clamp(characterLevel,1,maxLevel)>/damage`，**OrdinalIgnoreCase**
   （T-10：`incLUk` 是本封存真實存在的小寫 k）
2. `CastSkill` 傳 `session.Status.Level`——和揮擊給 `DamageRoll` 的是同一個來源
3. `ApplySkillHits` 改呼叫 `DamageRoll.Roll(..., MobLevel, MobDefence, damage, damage)`，
   呼叫形狀和 `:1843` 的揮擊**完全相同**
4. 沒有 damage 節點的技能**不造成傷害**，回報 `null` 不是 `0`（R-5）
5. 同一檔案的 4 處 mojibake 修掉（`??six` / `??this` → em dash）

### 3.4 實測對照（同一張圖、同一隻怪、同一個技能）

| | 之前 | 之後 |
|---|---|---|
| `/api/skill-cast/plan?skillId=1121008` | 沒有 damage 欄位 | `skillLevel=1, damage=135` |
| 打在 2220100 上的傷害數字 | **90,801** | **103** |
| 該怪 hp 350 | 當場摧毀 | **247** |
| `?skillId=10001009`（MultiStrike=true） | 亂數，被 MultiStrike 分支擋掉 | `damage: null` |

那個 135 不是誰定的數字，是量出來的 `level/1/damage`。

### 3.5 兩條誠實的限制（寫在程式碼裡，不是等使用者發現）

- **觸底是參照自己的行為**，不是 bug（`Mob.cpp:1590-1601` 的 `max(1.0, …)`）。
  `PDDamage` 中位 170 / p90 950，等級 1 角色打高等怪會讀成 1 傷害。
  **揮擊本來就這樣**，技能只是第一次也走進同一條規則。
- `long` 傷害節點用 clamp 不用 cast——cast 過 `int.MaxValue` 會捲成**負數傷害**。

---

## 4. W-063 · 狀位元組自己漏掉一個項目（已 close）

關 STATE.md 時發現 status 印出一列**空白**的 `P99` todo，而且
「37 done」和磁碟上 38 個 `state: done` 對不上。

**根因**：`W-055-...md` 檔頭有 **UTF-8 BOM**。`pm.py` 用 `encoding="utf-8"` 讀，
BOM 會保留，於是 `text.startswith("---\n")` 為 false，
`parse_frontmatter` 回傳**空 dict**。不是報錯，是**整個項目靜默消失**：
不算 done、`check` 看不到它的驗收、status 上長得像一個空白佔位符。

**修在讀取端，不修檔案位元組**：一個 frontmatter 正確的檔案必須讀得到，
因為三個位元組就刪掉一個項目是更糟的失敗模式。
全庫只有這一個工作項目有 BOM（`tools/probe/nx/NX-TECHNICAL-ROUTE.md` 是第二個，
但它不是工作項目）。

**效果**：`unparsed 1 → 0`、`check 51 items 0 problems`、
status **done 37 → 38**、空白列消失。

W-055 自己 Intent 裡的使用者中文報告是 mojibake。**記錄，不重建**——
T-11 明講不要為已被破壞的文字編預算，而且該行句尾的英文版承載了同樣的內容。

---

## 5. 最終狀態

```
build                exit=0, 0 errors 0 warnings
deploy               59 wwwroot files identical
avatar.js md5        a1eb2eb47af42e07ee048762c2592456  (與 GATE.md 記錄一致)
archives             all 16 open
gate                 RED SUITES: 0
pm.py check          51 items, 0 problem(s)
pm.py status         51 work items, 39 done, 12 open
editor               已開 http://127.0.0.1:5591 (R-19 三條件皆滿足)
```

本次關閉：**W-061、W-062、W-063**（皆帶 `evidence=`）。

### 5.1 一個必須記下來的 PowerShell 陷阱

本段有兩次 `pm.py status ... | Select-Object -First N` 回報了 exit 1。
實測是：

```
pm.py status direct                        -> exit 0
pm.py check  direct                        -> exit 0
pm.py status | Select-Object -First 1       -> exit -1
```

**`Select-Object -First N` 會提早終止管線，被截斷的那一端因此回非零。**
這跟 T-2 記的「`$LASTEXITCODE` 不是 PowerShell 腳本設的」是同一族：
管線之後的 exit code 不是那個程式的 exit code。**別把它當成失敗回報出去。**

---

## 5bis. 為什麼 `check: 0 problem(s)` 還會漏

`check` 在 BOM 那一局也回 `0 problem(s)`，因為它**看不到那個項目**。
所以真正的狀態完整性檢查是這一條：

```
python -c "import sys; sys.path.insert(0,'tools'); import pm; \
           print('unparsed:', len([i for i in pm.load_items() if not pm.field(i,'id')]))"
# 必須是 0
```

建議後續把這條併進 `pm.py check`，否則「0 problems」這個詞本身有盲點。

---

## 6. 這一頁沒做的事（不要誤以為做過）

- **沒有**動 `W-017` 的 MP 決策。技能依然**不檢查也不扣 MP**，
  `CastSkill` 的四道門只有：死亡 / 姿勢未完 / 在梯子上 / 查無此技能
  （`MapSimSessionStore.cs:1472-1479`）。使用者這次沒勾「恢復 MP 消耗」。
- **沒有**補冷卻時間、前置時間、技能等級需求。
- **沒有**處理 MultiStrike 技能造成 0 傷害。那是參照行為，註解在
  `MapSimSessionStore.cs:1556-1562`。
- **沒有**修 §0 提到的 ledge grab 宣稱落差，也沒有動 `FallOutSlack`。
  兩者都只是記錄，未經驗證。
- **沒有**處理 W-034 / W-035 / W-036 / W-057 / W-059。其中 W-035 / W-036 /
  W-057 / W-060 的 acceptance 仍是空的，照現況**無法收尾**。
- 沒有 commit。分支 `map-generation-baseline`，工作區在本段之前就已經是髒的。

---

## 7. 給下一個 agent 的三句話

1. **`gate.ps1` 的 `RED SUITES: 0` 現在比 18:38 之前可信**，因為
   `skill-test` 從 0 條斷言變成 19 條，而且它**自己**印 `VERDICT`。
   但它仍然看不到 exit code（T-2），所以每個 suite 必須自己講話這條規則還在。
2. **看到「量測結果不合理」先懷疑量測。** 本段三次都是判官錯：mob 的紅燈、
   skill-test 對屍體施放、pm.py 對 BOM 掉項目。
3. **`pm.py check` 回 0 problems 不等於狀態完整。** 它在 BOM 那一局也回 0——
   因為它根本看不到那個項目。真正的檢查是
   `unparsed: len([i for i in pm.load_items() if not pm.field(i,'id')]) == 0`。