# 2026-10-17 08:20–09:05 · SEG15 — 傷害簡化、一個能看見 canvas 的判官、revive 站在半空

> 結論先講：**使用者那句「放得出來 看的到 位置對 有效果 就這樣」是對的，而我們花了
> 一整天在量一個沒人會去量的數字。** 改成「一擊 100、怪的 HP 一律 1000」之後，
> ADR-0005 的四條驗收標準第一次全部可以用一行斷言寫出來，而且是在瀏覽器裡斷的。

---

## 1. 使用者的指示

> 「簡化一下好了 玩家預設傷害都是100 怪物血量都預設1000 方便測試 你也方便模擬
> 不用扯太多傷害計算公式 就是 放得出來 看的到 位置對 有效果 就這樣」

這跟 ADR-0005 是同一個方向：模擬器是測試架，不是客戶端複製品。傷害公式
（`DamageRoll.Roll`：等級差、怪的 `info/PDDamage` 減傷、兩個 1.0 下限）是對標專案的
做法，但它讓「這一擊是多少」變成一個沒有人能用眼睛驗算的數字。

---

## 2. ADR-0006：一擊 100，怪的 HP 一律 1000

新增 `MapleBench/Services/Simulate/SimTuning.cs`：

| 常數 | 值 | 用在哪 |
|---|---|---|
| `PlayerDamage` | 100 | `MapSimSessionStore.ApplySkillHits`、一般揮擊 |
| `MobHp` | 1000 | 刷怪、召喚、`MobPatrol.DefaultMaxHp`、`CombatService.MobMaxHp` 的 fallback |

**沒有刪掉的東西**（這很重要）：

- `DamageRoll.Roll` 還在，還是對標的公式，只是不再是模擬器報出來的數字。
- 封存的 `info/maxHP` 還在讀（編輯器與圖鑑用），模擬器在 spawn 時覆蓋它。
- **技能有沒有打到** 完全沒變：`ApplySkillHits` 的判定框、`MultiStrike` /
  `HasCollisionBox` 守衛、以及 W-061「沒有 damage 節點就不傷害」。只有數字被固定。

### 量到的證據

地圖上 25 隻怪，全部 `maxHp: 1000`：

```
mobs before: [{"id":"9400101","hp":1000,"maxHp":1000,...}]
mobs after : [{"id":"9400101","hp": 900,"maxHp":1000,...}]
```

---

## 3. `browser-verify.mjs`：第一個看得到 canvas 的判官

> **2026-10-17 修正**：本節原本記錄「零相依 + 手寫 CDP」，那是錯的。
> 見 §7。判官現在用 `playwright-core`（DSH 自帶），12 條斷言。

零相依（Node 24 內建 `WebSocket` + 機器上的 Chrome），走 CDP，13 條斷言。

### 3.1 先講量測工具本身壞在哪（這是本段最貴的教訓）

第一版手動 `await live.tick()` 灌幀。症狀是**同一台伺服器、同一份 build，綠一次紅一次**，
紅的時候 `trail` 從來沒前進過——`drawImage` 是 0，`ticks` 是空的。

診斷欄位裡的 `busy: true` 說明了原因：

> `--headless=new` **會**跑 `requestAnimationFrame`。頁面自己的 loop 和手動的
> `await live.tick()` 在搶同一個 `LiveWalk`，輸的那次看到 `this.busy` 已經是 true，
> 直接 return，什麼都沒做。

試過的每一個「聰明」做法都不對：手動 `cancelAnimationFrame`（有競態）、改 `gen`
（`tick()` 在開頭就把它 capture 了，擋不住重排）。**唯一乾淨的做法是不 pump**：
用真實時間等待，讓頁面自己跑。

```js
const waitFor = async (pred, ms = 12000) => {
  const until = performance.now() + ms;
  while (performance.now() < until) {
    if (pred()) return true;
    await new Promise(r => setTimeout(r, 50));
  }
  return pred();
};
```

連續 4 次全綠。

### 3.2 另外兩個「量測壞掉」而不是「產品壞掉」的例子

| 症狀 | 真相 |
|---|---|
| `fxArt` 報 `404` | 我 fetch 的是封存裸路徑 `f8/112.img/...`，相對於頁面根變成 `/f8/...`。viewer 用的是 `/api/canvas?path=`（walkview.js:3264）。**測試抓錯網址，圖沒有不見。** |
| 60 次按鍵全部無效 | `keydown` 與 `keyup` 在同一個 turn，`tick()` 還沒讀到 `skillDown` 就被清掉了。改成按住不放、每回合重複 `keydown`（瀏覽器自動重播本來就是這樣），才對。 |

### 3.3 四條驗收標準現在都有斷言

```
ok    the client put the numeric id on the wire  ["1121008"]
ok    the frame says the body is casting  skill
ok    the pose is the one the archive names  brandish1
ok    the effect sprite is published  1 sprite(s)
ok    the pose holds across the animation  after 24 frames: skill
ok    the viewer painted  25042 drawImage calls
ok    every mob on the map spawns with the test hit points  maxHp values on this map: [1000]
ok    the cast took exactly one test hit off the mob it was in range of  1000 -> 900
ok    the page reported no errors of its own  200 entries, 0 of them errors
ok    the effect has a real size on screen  66x37
ok    the effect sits where the archive says it sits  drawn (-30, -23) vs anchor-origin at scale 1 = (-30, -23)
ok    the effect is drawn at its own size, not stretched  66x37 on screen vs 66x37 in the archive
ok    the effect art the viewer drew really came from the archive  HTTP 200, 1479 bytes
VERDICT: browser-verify 13 assertions, 0 failed
```

**位置準確**這一條以前沒有任何視覺證據，現在是數字：畫在 `(-30, -23)`，封存的
`origin` 是 `(30, 23)`，`rlt = pos - center - origin`（`DrawArgument.h:143`）。尺寸也
對得上封存的 66x37，沒被拉伸。

---

## 4. 這一輪 gate 抓到的真 bug：revive 站在半空

改 HP 之後 gate 紅了 6 個。兩個是測試自己過期（`sheet-verify` 的「揮擊不得超過
角色面板上限 11」在 ADR-0006 之後已經不是規則），一個是真的產品 bug。

`revive-verify` 報「復活後走不動」。量測：

```
session spawn:  x=0    y=95
after revive:   x=0    y=-2      onGround=True
20 frames 右:   x 0 -> 0
```

同一張地圖、同一段程式碼，`Start` 站在 y=95，`Revive` 站在 y=-2。

原因：`CharacterPhysics` 的**建構式**從 foothold 的端點取 Y
（`_y = start.Y1 / start.Y2`，:147-148），而 `Respawn(foot, x, y)` **照單全收**
(:202-207)。`Revive` 傳的是 spawn portal 的 Y，於是身體被放在半空中，
`OnGround = true` 而 foothold 在 97 px 下面——每一步的 foothold 測試都判失敗。

修法（`MapSimSessionStore.Revive`）：X 保留 portal 的位置，Y 取自 foothold。

```csharp
FootholdPhysics.Foot f = foot.Value;
int placeX = x < f.X1 ? f.X1 : x > f.X2 ? f.X2 : x;
int placeY = placeX <= (f.X1 + f.X2) / 2 ? f.Y1 : f.Y2;
session.Body.Respawn(f, placeX, placeY);
```

修完：`after revive: x=0 y=95`，20 幀右移 `x 0 -> 49`。

### 4.1 `revive-verify` 自己也有問題

它在 `step({"skillId": ...})` 之後**立刻**測走路。技能姿勢會鎖住移動（這是對標的行為，
`Skill.cs:321` 的 `Skill.PlayEnded`），而 10001009 的姿勢比它走的 20 幀還長。
它給揮擊 24 幀settle 卻沒給施法 settle。補上 40 幀——**斷言本身沒有放寬**。

---

## 5. `SimAuditService` 也要跟著改

`playerCanFinishFight` 原本用角色面板的 `MaxDamage` 經過
`Mob.cpp:1590-1601` 的公式算「要幾下打死」，現在改成用 `SimTuning.PlayerDamage`。
不改的話它會說一個武裝角色打不完任何一場戰鬥，而那個角色正在一下一下把 1000 HP 的
怪打死。

---

## 6. 這一輪的方法論

1. **量測工具壞掉時先懷疑量測。** headless rAF 競態與錯誤網址，兩個都曾經看起來
   像產品缺陷。
2. **判官要能報紅，也要能重複。** 連續 4 次全綠才算數；一次綠是運氣。
3. **規則改了，斷言要跟著改，但要說清楚為什麼。** `sheet-verify` 不是被放寬，是
   ADR-0006 明寫的新規則取代了舊斷言的前提，而且新斷言更嚴（全都要剛好 100）。
4. **HTTP 測試碰不到 canvas 與焦點。** `browser-verify` 是第一個能回答「位置對不對」
   的東西。

---

## 7. 補記（同日稍後）：我用了專案自帶的 MCP 嗎？沒有

使用者指出「這個專案有 MCP CLI」。查證結果：

| 事實 | 證據 |
|---|---|
| `MapleMcp/` 就在工作區裡，52 個工具 | `MapleMcp/README.md`、`Program.cs:42` `Name = "maplebench-assets"` |
| 它**從來沒有被註冊** | `C:\Users\e7896\.dsh\@wingsky-1\dsh-mcp-manager\mcp.json` 只有 8 個 entry，其中 4 個 description 明寫「registered manually 2026-10-14」 |
| 所以我這幾天手寫 HTTP suite 和 300 行 CDP client | 那是我呼叫不到 MapleMcp 的結果，不是選擇 |

### 已修

1. `mcp.json` 加入 `maplebench`（備份 `mcp.json.bak-20261017`）。
2. `maple-mcp.exe` 起不來：系統 runtime 只有 .NET 6/8，apphost 直接報
   "You must install or update .NET"。改成註冊 scoop 的 dotnet 跑 DLL。
3. 用專案自帶的 `tools/probe/mcp-probe.mjs` 驗證：

```
=== initialize ===
server: {"name":"maplebench-assets","version":"1.4.0"}
protocol: 2024-11-05
=== tools/list ===
tool count: 52
```

**本 session 還呼叫不到它**：dsh-mcp-manager 只在 server SET 變動時重新正規化，
新加的 server 要下一個 session 才掛上來。

### 連帶清掉的手寫 CDP

`browser-verify.mjs` 從 300 行手寫 CDP（WebSocket shim、attach 重試、rAF 競速急救）
改成 playwright-core —— **DSH 自己就帶著那份**（`playwright-core@1.63.0`，
`%DSH_PROFILE_DIR%\node_modules`），Playwright MCP 用的就是同一個 build。

好處不是行數：`page.keyboard.down/up` 是真的按鍵（`press('k')` 是零時長點擊，
會被「下一幀才讀 skillDown」的設計吃掉），`page.waitForFunction` 是真的等待，
兩者讓 §3.1 那個 rAF 競態**沒有地方存在**。

實測 **12 斷言連續 4 次全綠**，gate 內也是 12/0。

### 仍然存在的缺口（不隱瞞）

MapleMcp 52 個工具裡**沒有** live session 類的工具：沒有開 session、沒有 frames、
沒有 summon、沒有施放技能、沒有讀怪的 HP。`wz_map_session_open/step` 是**地圖編輯器的
物理行走**，不是模擬器那一套。所以：

- 走地圖 / 查封存 / 編輯地圖 → 用 MapleMcp（已註冊）
- 模擬器的 HTTP 端點 → 目前仍是 python suite 直接打
- canvas / 按鍵 / 焦點 → 用 playwright MCP 或 browser-verify（playwright-core）

補齊 MapleMcp 的模擬器工具是下一個明確的工程項目，記為 W-081。
---

## 8. 續：把 MapleMcp 補成「全域可達」（使用者：「補 mcp 我要全能」）

後端 **234 條路由**，MCP 原本只有 52 個工具，而且沒有任何一個碰到
現場模擬器。補完後 **90 個工具**。

### 設計原則：家族，不是路由

一條路由一個工具會把 agent 淹死 —— 234 個工具比 52 個更難用。
所以每個家族用 `kind` / `action` 分流：

| 家族 | 個數 | 覆蓋 |
|---|---|---|
| `wz_sim_*` | 12 | 現場模擬器：開場景、綁熱鍵、放怪、走位、施放、讀 hp |
| `wz_data_*` | 6 | skill / mob / npc / string / cash shop 的欄位驗證與批次語義 |
| `wz_job_*` | 6 | audit / dump / compose / import / repair×4，**56 條路由同一個形狀** |
| `wz_render_*` | 6 | canvas / animation / sound / preview / thumb / export |
| `wz_diag_*` `wz_save_*` | 8 | health / clientlog / debug / facts / preflight / commit / memory |

`.WithToolsFromAssembly()` 會自動註冊 `[McpServerToolType]`，所以加檔案就行。

### 實測：真的跑了一個完整流程

`mcp-probe.mjs` 擴充了 `--call` / `--script @file` / `{{sessionId}}`
（**擴充專案既有的 probe，不是另寫一支 client**）：

```
wz_sim_open -> wz_sim_skills -> wz_sim_summon -> wz_sim_step
-> wz_sim_cast -> wz_sim_state

action='skill'   skillStance='brandish1'
1 skillEffect (9 frames, origin (30,23), z=0)
every mob maxHp=1000
```

### 兩個值得記的坑

1. **`f4/Map/...` 的 `f4` 不穩定。** 它是封存的工作階段 id，依開啟順序發號。
   實測同一個 `Map.nx` 一次是 `f4`、下一次是 `f25`（gate 開了第二輪）。
   寫死會得到 `No open file with id 'f4'`，**看起來像地圖不見，其實不是**。
   已寫進 `wz_sim_open` 的說明、README 和 ServerInstructions。

2. **`wz_map_simulate*` 和 `wz_sim_*` 是兩個不同的模擬器。**
   前者是地圖編輯器的物理（「這張地圖走得過去嗎」），
   後者是畫面掛著的那個現場 session（「我按 K 為什麼沒反應」）。
   名字太像，選錯會浪費一小時。已寫進 ServerInstructions 的抬頭。

### 順帶修掉的三個編譯錯誤

`LiveSimTools.cs` 一個字串裡的單引號（`kind='lookslots'`）讓 CS1003；
`DiagTools.cs` 一個 `route` 遮蔽（CS0136）。都是 agent 自查後交出來的靜態檢查
沒抓到、真的編譯才現形的東西 —— **委派的產物必須由 parent 編譯一次**。
---

## 9. 使用者回報：「有怪 就不能放技能」「技能朝向沒有依照玩家朝向」

兩個都是真的，而且第一個**根本不是技能的問題**。

### 9.1 重現：有怪就不能施放，是角色死了

用昨天補的 MCP 工具量（`wz_sim_*`，不是手寫 HTTP）：

```
opened   x=1710  gap=15  tookHit=150  playerHp=0  dead=true
```

地圖上的怪是 **20 級、碰到傷害 150**；角色只有 **50 HP**，一下就死。
而 `CastSkill` 第一行就是 `if (session.Status.Dead) return;`。

**ADR-0006 只固定了怪物那一邊。** 同一個問題在角色這一邊：怪 1000 HP 是為了
「讀得懂」，角色 50 HP 卻讓「有怪就死了」。加 `SimTuning.PlayerHp = 1000`，
兩邊對稱。

修完同一個場景：

```
opened   playerHp=1000  dead=false
walked   gap=5   playerHp=850  dead=false      <- 挨了 150，還活著
after    action="skill" stance="brandish1" mobHp=900
```

### 9.2 朝向：跟按鍵走，不是跟位移走

對標：

- `PlayerStates.cpp:52-63` —— 在按鍵處理裡 `player.set_direction(false/true)`
- `Player.cpp:1214-1218` —— `if (!attacking) Char::set_direction(f)`，
  **整個函式只有這一句**：施放/揮擊中不改方向
- `Mob.cpp:731-732` —— 怪也一樣：`if (phobj.inputx != 0 && !noflip) flip = phobj.inputx > 0;`

原本的實作從**身體實際位移**推朝向，而且在 `Describe()` 裡**事後**才更新 ——
判定框在 `Advance()` 裡用的是上一幀的朝向。角色站著不動（被擋住、或施放中
`moveLeft` 被歸零）時永遠不會轉身，技能就朝上次走動的方向飛出去。

改成在 `Advance` 開頭、戰鬥結算之前由輸入決定。決定性量測 —— **同一幀**轉向並施放：

```
施放前   x=1734  facing=RIGHT   怪在 LEFT   怪 HP=1000   <- 背對著怪
一幀：按左 + 施放 1121008
施放後   x=1744  facing=LEFT    怪 HP=900              <- 判定框用這一幀的方向
```

### 9.3 順帶量到的第三個：屍體會走路

`Swing` 和 `CastSkill` 都有 `Status.Dead` 守衛，**走路沒有**。
量到 `playerDead: true, hp: 0` 的角色仍然走了 40 幀（1710 → 1614）。

### 9.4 連帶修正的兩個測試（不是放寬斷言）

角色 HP 從 50 變 1000，兩個 suite 的**前提**不再成立：

| suite | 原本壞在哪 | 怎麼修 |
|---|---|---|
| `sheet-verify` | 斷言 base HP 是 50 | 改成斷 `SimTuning.PlayerHp` = 1000，MP 仍是 10 |
| `revive-verify` | 400 幀打死角色；而且角色每幀揮擊，**把殺手打死了**（1000 HP 的怪 10 下就沒），於是沒有傷害來源 | 幀數按 `maxHp` 算；不揮擊只站著挨打；姿勢改**等它真的結束**而不是等固定幀數 |

`revive-verify` 還量出一個數字值得記：`10001009` 的 `bamboo` 姿勢長 **265 幀**（4.4 秒），
是封存身體動畫的逐幀延遲算出來的。這個 suite 原本等 40 幀就開始走路 —— 永遠不可能夠。

**Gate 回到紅 1 個**（`ball-art` / W-070，開著的舊問題）。
`browser-verify` 12 斷言 0 失敗。