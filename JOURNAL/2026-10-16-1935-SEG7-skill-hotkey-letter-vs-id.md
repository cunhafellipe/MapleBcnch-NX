# 2026-10-16 19:03–19:40 · SEG7 — 施放技能按鍵：一個字元的缺陷

> 結論先講：**使用者回報「施放技能沒反應」是真的，而且原因是一個字元。**
> 修好之後 gate 收在 `RED SUITES: 0`。過程中另外抓到兩個判官缺陷。

---

## 0. 使用者說的對，而且正好是我上一段沒碰的地方

上一段（W-061）我所有的驗證都走 API：
`POST /session/{id}/frames` 帶 `skillId: "1121008"`。
**那條路徑完全繞過了「技能設定按鍵 → 綁定 → 按鍵施放」的客戶端邏輯。**

`W-050` 的 Result 裡其實早就寫明了：

> **Not proven here:** that pressing a bound key now casts. The client half of
> that is `skillFor`, which reads a Map this server cannot see, and a browser
> keystroke is not something an HTTP suite can send.

**上一輪就是明知道沒驗證就關掉了。** 使用者這一輪碰到的是同一個洞。

---

## 1. 缺陷本體（已 close，W-065）

### 一個字元

```javascript
// MapleBench/wwwroot/js/livewalk.js:911  (原樣)
this.skillDown = skill.key;
```

`skillFor()` 一直回傳**兩個**值：

```javascript
// livewalk.js:1007-1009
const skillId = this.skillKeys.get(key);
if (!skillId) return null;
return { key, skillId };          // skillId 算出來了，然後從來沒被任何東西讀過
```

而 `skillDown` 是幀迴圈唯一攜帶施法目標的欄位（`:1158-1173`），
所以客戶端送出的是**字母**：

```javascript
const skill = this.skillDown && !this.skillLatch && !this.castBusy() ? this.skillDown : '';
const held = { ..., ...(skill ? { skillId: skill } : {}) };   // skillId = "A"
```

伺服器端 `Plan()` 第一件事是 `int.TryParse`（`SkillCastService.cs:231`），
`CastSkill` 在 `if (plan is null) return;`（`MapSimSessionStore.cs:1479`）丟棄。

### 量測證明（同一個端點、同一幀、同一個 session）

```
{"skillId": "A"}        -> action='stand'  skillId=''              沒有施放
{"skillId": "1121008"}  -> action='skill'  skillStance='brandish1' 施放

POST /session/{id}/skills  bindings=[{skillId:1121008, key:A}]
  rejected: []            <- 面板回報綁定成功，沒有任何抱怨
```

**綁定成功、伺服器接受、按下去什麼都沒有、畫面上沒有任何錯誤。**
這就是使用者描述的症狀，一字不差。

### 為什麼所有既有測試都沒抓到

每一個既有測試都**直接送數字 id**（`skill-test.ps1`、`skillkey-verify.py`），
從來不經過 `onKeyDown`。而 `sim-skills-check.mjs` 斷言的是
「`skillFor` 回傳正確的 `{key, skillId}` 對」——**那是對的**——
它從沒檢查呼叫端拿這個對做了什麼。

### 修法與回歸測試

`livewalk.js:932` 改成 `this.skillDown = skill.skillId;`

`sim-skills-check.mjs` 加 6 條 Node 斷言（27 → 33），不需要瀏覽器按鍵。
**並且驗證它抓得到原本的錯**：

```
改回 skill.key :  FAIL  got "K"  want "1121008"   30 passed, 3 failed, >>> RED
還原           :  33 passed, 0 failed
```

> 這個洞現在**不需要真按鍵就關上了**，因為缺陷在資料流而不在輸入事件。

---

## 2. 連帶抓到的判官缺陷一（已 close，W-064 knockback-verify）

部署後第一次 gate 跑出 `knockback BAD=1`。**不是我的改動造成的**——我只動了客戶端 JS。

拿到完整輸出：

```
=== calibration: an UNHIT mob must not drift ===
  mob x=2221.00, largest single-frame drift over 40 idle frames: 194.850px
  >>> the control moved 194.850px, so 'it moved' would prove nothing
```

反彈三條斷言全過（`+2.167px`、`knockbackFacing=-1`、恰好 1 次脈衝），紅的是對照組。

**194.85px 在一幀內不可能是移動**——`MaxWalkSpeed` 是 2.5px/幀。
量了這張地圖上 2220100 的實例：

```
frame 0: 11 instance(s) of 2220100 ->
  (2091,-25) (2221,-25) (2767,-805) (2553,-145) (2681,-145)
  (2847,-145) (3043,-145) (2006,-535) (2210,-535) (2451,-535) (2435,-805)
```

`x=2221` 是**第二隻**。腳本 setup 用「離玩家最近」挑了一隻，
量測時卻用 `[x for x in mobs if x.mobId == MOB][0]`——**清單裡第一隻，x=2091**。
**它量的是兩隻不同怪的距離。** 走位決定紅綠，所以單跑 3 次全綠、在 gate 裡紅。

**兩個假設先被量掉、沒有被採信**（R-2）：

- 「怪會巡邏」——量了 40 幀閒置位移 = **0.000px**，有沒有接觸都一樣。否決。
- 「接觸傷害設 aggro 所以怪追過來」——走到接觸距離再量，仍是 **0.000px**，
  `knockbackPx` 全程 0。`invulnerable=False` 時玩家 0/50，接觸傷害確實發生，
  但它不讓這隻怪動。否決。

修法：改用**清單索引**釘住那一隻——這個專案裡 `mob-attack-verify.ps1`
早就在用 `indexes[0]`，從來沒有這個問題。

驗收：單跑 3 次 `BAD=0`，在原本會紅的 gate 上下文裡也 `BAD=0`。

---

## 3. 連帶抓到的判官缺陷二（補記在 W-062）

修完 knockback 後重跑完整迴圈，`skill-test` 自己報紅：

```
VERDICT: skill-test 19 assertions, 1 failed
FAIL  2.2 a MultiStrike=true cast leaves the mob untouched -- hp 350 ->
```

**紅的是我上一段自己寫的那條斷言。** 它同樣用位置釘住怪：

```powershell
Where-Object { $_.mobId -eq $mob.mobId -and [Math]::Abs($_.x - $mob.x) -lt 3 }
```

同一張地圖、同一種錯誤。怪走幾像素 → 查找回空 → `[int]$null` = 0 → 失敗。

而且那條斷言**問錯了問題**：它宣稱「施放沒造成傷害」，實際量的是「怪沒有走動」。
走動的怪一樣沒被傷害到。改成 `Nearest` 輔助函式（先比 id 再比離身體的距離），
於是「不在畫面裡」只可能代表**被摧毀**，而無傷害的施放不可能摧毀任何東西。

修後 3 次：`19 assertions, 0 failed`，血量穩定 350→247（受傷）/ 350→350（沒受傷）。

> **這張地圖的重複 mob id 已經造成三個缺陷，其中兩個在判官而不在產品。**

---

## 4. 順帶記錄：一個 gate 迴圈裡的假成功

中途我單跑 `deploy.ps1`，它印：

```
started pid 22356 on 5591
up after 2s with 16 archives
```

但實際上 5591 上活著的只有 18:56 那一個（pid 44420）。
**它啟的那個因為埠被占用而死，舊的回答了，deploy 卻報成功。**
因為舊行程本來就開著 16 個封存，「16 archives」看起來完全正常。

`deploy.ps1` **不會**停掉舊行程，只有 `build-gate.ps1` 會。
所以正確順序必須是 build-gate → deploy → open-archives → gate（`pm/GATE.md:12-17`），
**跳過第一步就會得到一個假的成功訊息。** 這和 T-2 是同一族：
量測工具自己回報的結論不可信。

---

## 5. 最終狀態

```
build                exit=0, 0 errors 0 warnings
deploy               59 wwwroot files identical
avatar.js md5        a1eb2eb47af42e07ee048762c2592456
archives             all 16 open（pid 42800，唯一一個程序）
gate                 RED SUITES: 0   （25 個 suite）
sim-skills-check     33 passed, 0 failed  （原 27）
skill-test           19 assertions, 0 failed
pm.py check          53 items, 0 problem(s)
```

本次關閉：**W-064、W-065**（皆帶 `evidence=`）。

---

## 6. 這一頁沒做的事（不要誤以為做過）

- **沒有真的用瀏覽器按鍵驗證過。** 一次 `script.evaluate` 被使用者拒絕後我就沒有再嘗試。
  因此「在 walk view 按 K，角色會播 brandish1」這句話，靠的是程式碼路徑與 Node 斷言，
  **不是**一次真實 keystroke。W-065 的 Result 裡也明寫了這一點。
- 沒有動 MP、冷卻、技能等級門檻（使用者這次沒勾）。
- 沒有動 MultiStrike 的 0 傷害行為。
- 沒有為「同一張地圖 11 隻同 id 的怪」建立一個共用工具——
  三次踩到同一個坑，目前是逐個 suite 各自修。
- 沒有 commit。

---

## 7. 給下一個 agent 的兩句話

1. **驗證一個功能不等於驗證它通往使用者的最後一段。**
   W-061 用 API 證明了伺服器會施放；W-065 證明了按鍵會送出正確的 id。
   兩者之間還差「面板綁定的 key 有沒有真的進到 `skillKeys`」——
   那段在 `bindSkills()` 的回應寫入（`livewalk.js:855`），本段沒有端到端驗過。
2. **任何「量測對象是清單裡的某一個」的探針，先確認那個清單裡有幾個同值的元素。**
   這張地圖有 11 隻 2220100，已經讓兩個 suite 在同一個下午各紅一次。
   `mob-attack-verify.ps1` 的 `indexes[0]` 是目前唯一正確的寫法。