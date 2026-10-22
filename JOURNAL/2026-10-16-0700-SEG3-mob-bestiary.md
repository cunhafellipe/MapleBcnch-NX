# 段 3 — 怪物圖鑑面板（MobTooltipRenderer 對標）

日期：2026-10-16
狀態：完成，門檻全綠。

## 為什麼先量後寫

`MobTooltipRenderer.cs` 有 378 行，其中一半的欄位在這個 archive 上根本沒有值。
先量，才知道要寫的是「照抄參考」還是「照抄參考然後承認資料缺席」。

量測只用 `/api/inspect`（工作室守則 §2.7），343 隻怪抽樣（全部 2,053 中每 6 隻一隻）。

### `Mob.nx/<7 位 id>.img/info` 實際帶的欄位

| 出現率 | 欄位 |
|---|---|
| 343/343 | `level` `maxHP` `maxMP` `PADamage` `MADamage` `fs` |
| 342/343 | `PDDamage` `MDDamage` `acc` `eva` `pushed` |
| ~320 | `exp` `summonType` |
| ~310 | `speed` `bodyAttack` |
| ~290 | `undead` |
| 稀疏 | `mobType` 72、`boss` 63、`elemAttr` 60、`mpRecovery` 42、`firstAttack` 40、`hpRecovery` 35、`revive` 15、`removeAfter` 13、`invincible` 5、`partyBonusMob` 4、**`category` 14** |
| **一次都沒見到** | `icon`、`charismaEXP`、`senseEXP`、`insightEXP`、`willEXP`、`craftEXP`、`charmEXP`、`wp`、`fixedBodyAttackDamageR`、`onlyHittedByCommonAttack`、`allyMob`、`ignoreDamage`、`ignoreMovable`、`noDebuff`、`notAttack` |

型別不是只有 int：`maxHP` 有 `String`（`2400049` = `"300000000"`），
`mobType` 有 `String`（`"1N"` `"7N"` `"4H"`），`fs` 全部是 `Double`。

### `info/category` 只有 4%

343 隻裡 14 隻有 `category`，值只有 `0` `1` `7` `8`。所以
`GetMobCategoryName` 大多會印 "None" — 那是**資料的事實**，不是程式漏了。
面板在沒有這個節點時印破折號，而不是印 0/None：兩者是不同的句子。

### 元素

`info/elemAttr` 是一串「字母+數字」，例如 `"I2F3L2H2S2"` `"H3"`。

字母以 **C++ 為準**（工作室守則 §7.6），`Mob.cpp:2122-2128`：
`F`火 `I`冰 `L`雷 `S`毒 `H`神聖 `D`暗 `P`物理 — C# 的
`GetElemAttrString` 字母相同，只有順序不同，取 C++ 的順序。

數字由 `ElementDamage.h:9-15`（v95 `get_damage_adjusted_by_elemAttr` 0x7231e0）定死：
`0` 一般 `1` 免疫 `2` 抗性 `3` 弱。

### `String.nx/MonsterBook.img` — 真正的圖鑑頁

344 筆（333 個七位補零 id、11 個六位），每筆三個子節點：
`episode`（中文說明，含「等級：Lv.1」「形態：動物型」）、
`map`（地圖 id 清單）、`reward`（道具 id 清單）。

兩種 key 都存在，所以兩種都試；沒有任何單一 key 找得到全部 344 筆。

**陷阱**：`episode` 裡的斷行是**字面兩字元** `\n`，不是真的換行。
直接送出會在面板裡印成一整面 `\n`。在 server 端 `Unescape()` 轉回真換行。

## 改了什麼

- **新增** `MapleBench/Services/Simulate/MobBestiaryService.cs`
  欄位表照 `MobTooltipRenderer.Render()` 的行序；特徵標籤照 `:56-126` 的字串原文；
  分類表照 `:334-348`。所有數值欄位是 `long?`，archive 沒有就是 null。
- **`Program.cs`** 註冊 singleton。
- **`Api/SimulatorEndpoints.cs`** 新增 `GET /api/simulate/mob-info?mobId=&fileId=`，
  沒有 id 時回 200 + `hasInfo:false`（跟 `/mob-sprite` 同一個理由）。
- **`wwwroot/js/walkview.js`** 召喚選單變雙欄：左邊原來的格子，右邊圖鑑面板。

### 自設計（偏離參考，已在 JSDoc 宣告）

點格子**不再關閉對話框**，而是選取並填右邊面板；footer 的「Use this mob」才把 id 寫進框。
參考是在格子自己的 click 就提交 — 但一個沒讀就要關掉的圖鑑頁不是圖鑑頁。
提交的值與最終結果都不變（`onPick` 仍然填 id box，仍然由呼叫端送出）。
快速路徑也留著：搜尋框輸入 id 按 Enter 直接選中那隻。

## 門檻

- 建置 Release：0 error 0 warning
- `wwwroot` src/bin 59 檔 md5 全等；`walkview.js` src/bin/**served** 三方 md5 全等
- `avatar.js` md5 維持 `a1eb2eb47af42e07ee048762c2592456`
- build-check 160 檔與 bin 全等（先清掉一個舊 publish 留下的 `MapleLib.deps.json`）
- ladder-all `passed=5 failed=0` / ALL LADDERS PASS
- attack-test attackStance 在線上；無武器控制組全空
- skill-test multiStrike damage=0、二次施放被拒、未知/空/梯子上被拒
- mob-attack-verify 怪自己從 gap 102.49 收斂到 62.75，兩次命中 50→30
- first-attack-verify `PASS: it attacked without being struck`
- npc-label-check 2/6 有名（與基線相同，是資料缺席不是回歸）
- `node MapleBench/tools/sim-skills-check.mjs` 24 passed 0 failed
- 新增 `mobinfo-verify.py`：12 個案例 BAD=0，episode 已無字面 `\n`

## 這輪的工具教訓

1. **無 BOM 的 UTF-8 .ps1 帶 CJK 路徑字面值會被主機用 ANSI code page 讀掉。**
   `build-gate.ps1` 與 `deploy.ps1` 都在 `Push-Location` 上炸掉一個明明存在的路徑。
   改成**用 glob 找**路徑，永遠不要在腳本裡打 CJK 路徑。
2. **`$LASTEXITCODE` 對 PowerShell 腳本不設定。** 拿它當門檻會把 6 個全綠的
   suite 判成失敗（第 7 次自我誤判）。門檻只能看**印出來的**結論行。
3. 我第一版的失敗正則 `(?im)[1-9]\d* failed` 會把 `passed=5 failed=0` 裡的
   「5 failed」當成失敗 —— 比沒有正則更壞。