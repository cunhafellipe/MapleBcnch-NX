# B — 視覺線報告

支線 B，2026-10-18。寫入範圍：`MapleBench/wwwroot/js/walkview.js`、`MapleBench/tools/browser-verify.mjs`。
沒有動任何 C#，沒有建置、沒有部署、沒有重啟伺服器、沒有 commit。

---

## 0. 先講一件會影響所有量測的事：伺服器跑的不是我改的那份檔案

**量測全程對的是舊 build。** 這不是推論，是量出來的：

```
$ (Invoke-WebRequest http://127.0.0.1:5591/js/walkview.js).Content.Length
221018
$ (Get-Item MapleBench/wwwroot/js/walkview.js).Length
228457
src  has lastMobHitRect : True
served has lastMobHitRect: False
```

伺服器送的是 `MapleBench/bin/Release/net10.0-windows/wwwroot/js/walkview.js`
（221018 bytes，時間 2026-10-17 23:02:47），我改的是 `MapleBench/wwwroot/js/walkview.js`。
**我對 `bin/` 沒有寫入權限，而且把檔案複製進去就是部署**，兩件事總管都保留給你。
所以下面每一條結論都是「**舊 build 的行為**」。

我把判官寫成**兩邊都能量**：能用 `lastEffectRect` 就用，沒有就從 wire 推出來
（見 §B1 的「鏡射」與 §B2 的 z 序）。這樣部署之後同一支命令會自動改用新欄位，
而且現在就能給出結論。

**部署之後必須重跑一次。** 我在 `walkview.js` 裡動了三個 publish 點（§改動）。

---

## 1. 改動清單（逐字）

### 1.1 `MapleBench/wwwroot/js/walkview.js`

| 符號 | 舊 | 新 |
| --- | --- | --- |
| `drawSkillEffectPass` 迴圈 | `for (const fx of entries) {` | `for (let order = 0; order < entries.length; order++) {`<br>`const fx = entries[order];` |
| `drawSkillEffectPass` 貼花節點名 | （無） | 貼花 entry 多一個 `node: 'tile'` |
| 時鐘來源 | `const born = this.skillFxBorn ?? (this.skillFxBorn = performance.now());`<br>`const age = performance.now() - born;` | `const now = performance.now();`<br>`const castSeq = this.live?.state?.skillCastSeq ?? 0;`<br>`const clocks = this.skillFxClock ??= new Map();`<br>`… if (clock.born == null) { … if (!ready) return; clock.born = now; }`<br>`const age = now - clock.born;` |
| 圖片來源 | 迴圈內 `cache.get(container)` / `new Image()` | 迴圈**前**先算好 `const art = entries.map(...)`，迴圈內 `const imgs = art[order];` |
| `this.lastEffectRect` | `{imagePath, frame, origin, mirrored, left, top, right, bottom, iw, ih, anchorX, anchorY, charX, charY}` | 同上 **＋** `pass: behind ? 'behind' : 'front'`、`order`、`node`、`z` |
| `drawMobHitEffectPass` 迴圈 | `for (const fx of list) {` | `for (let order = 0; order < list.length; order++) {`<br>`const fx = list[order];` |
| `drawMobHitEffectPass` publish | **（完全沒有）** | 新增 `this.lastMobHitRect = { mobId, imagePath, frame, origin, mirrored, left, top, right, bottom, iw, ih, anchorX, anchorY, mobX, mobY, castSeq, z, shiftX, shiftY, pass, order }` |
| `drawAvatar` publish | **（完全沒有）** | 新增 `this.lastAvatarPose = { state, stance, frame, flipped, dir, skillStance, parts, x, y }` |

### 1.2 `MapleBench/tools/browser-verify.mjs`

從「單一情境」改成「分節」。`node MapleBench/tools/browser-verify.mjs` 跑 core；
`--all` 跑全部；可單獨跑 `--shapes` / `--fx5` / `--actions` / `--tiles` / `--hits`。
`--limit=N` 縮短掃描（不影響挑哪幾個形狀）。原有 core 的每一條斷言**一字未改**。

**判官自己修掉的 bug（都是「檢查寫錯了」，不是「放寬檢查」）：**

1. **`prewarm` 沒有 timeout。** `Promise.all` 裡只要有一張圖既不 onload 也不 onerror，
   整支判官就永久停住。實測：33 幀的技能讓掃描十分鐘沒有任何一行輸出，
   「慢」和「卡死」長得一樣。→ 每張圖加 8 秒 timeout，未回應的路徑報成 `never answered`。
2. **B2 的 z 序斷言恆為真。** `halves` 用 `imagePath` 當 key，查詢卻用 `containerPath`，
   每次查不到 → `undefined` → `if (undefined)` 為假 → 斷言永遠通過。
   MEASURED 舊輸出：`effect(z=0)->undefined effect0(z=0)->undefined …`。
   → key 改成 container，兩邊一致。
3. **B3 讀到上一個技能的姿勢。** `lastAvatarPose` 每幀 publish 一次、留到下一幀，
   姿勢結束後才取樣就會拿到上一個技能的。實測 11111004（actions = brandish1/brandish2）
   被報成 playing `bamboo`（那是 10001009 的姿勢）。→ 只接受
   `action === 'skill' && live.state.skillId === 本技能` 的幀。
4. **B5 拿 exact path 比對動畫幀。** wire 抓到的是 `hit/0/0`，blit 是 `hit/0/1`，
   同一個動畫的下一幀被報成「不是 wire 上的特效」，180 次裡錯 170 次。→ 改成比 container。
5. **`--hits` 單獨跑時 `plans` 是空的**，B5 iterate 空集合、一行都不印。
   → plan 一律載入。
6. **B4 只按一次鍵、也不確認有沒有施放**，於是穩定產出 5 張貼花的技能
   被報成 `0 decal(s)` — 而 0 正好是 fallback 該給的數字，兩者因此無法區分。
   → 改成重試並先確認 `action === 'skill'`；量到沒有貼花的施放不算一次量測。
7. **B4 的「每個 foothold 都走不到」斷言用 `endsWith('/0')` 比對
   `12111003 0/76` 這種字串**，永遠不成立，所以它在印出五個 0 的同時報紅。
   → 改成加總計數。**這一條是判官自己的斷言寫錯，不是產品缺陷。**
8. **B5 的峰值只取第一個有資料的施放就停**，所以「peak 1」有可能只是沒等到。
   → 六次施放全跑，取最大值。

---

## 2. 逐條缺口結論

### B1 逐幀落位按形狀分組 — **落位正確；動畫本身有真缺陷**

**分組是程式自己推出來的，不是手抄的。** 判官讀 `/api/skill-cast/castable` 加全部
66 份 `/plan`，自己挑代表：

```
-- derived shape matrix -- 66 plans,
   frames: 1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,20,21,22,23,24,25,26,30
   nodes:  1,2,3,5
```

> 任務書列的幀數是 1/2/4/5/…/26，**漏了 3 和 30**（`1311001` 的 effect0..3 是 3 幀、
> `5221003` 的第二個 node 是 30 幀）。以量測為準，兩種都納入。

**結果：`shapes: 37/37 face-runs clean; mirrored branches exercised: left,right`。**
每一個形狀、兩個朝向，逐幀比對「這一幀自己的 origin」全過（完整 `--all` 一輪 37 個朝向 run）。

逐幀規則（`DrawArgument.h:131-157`，`pos == center == 角色位置`）：

```
facing right (xscale +1):  left = charX - origin.x
facing left  (xscale -1):  left = charX + origin.x
兩邊都是:                   top  = charY - origin.y
```

鏡射用**blit 本身**判，不信單一欄位：未鏡射是 `drawImage(img, px(left), …)`，
鏡射是 `translate(px(right),0); scale(-1,1); drawImage(img, 0, …)`，
所以 `a[0] === 0` 就是鏡射分支的簽名。判官兩個都讀，不一致會報出來（`mirrorAgrees`）。

### B1 附帶發現（真缺陷，在舊 build 上）

**首次施放時特效不會播完，通常只畫第 0 幀。**

同一輪 run、同一個 build：

```
... 10001009 2fx/20+13f z=0,0
    cast[R] seen=true seq=[2] animFrames=[0]        ← 20 幀的動畫只出現第 0 幀
    ok  ... 94 draws; frames seen effect 1/20, screen 1/13
... 10001020 2fx/14+22f z=-2,0
    cast[R] seen=true seq=[4] animFrames=[4,5,6,8,9]   ← 這個會動
    cast[L] seen=true seq=[5] animFrames=[4,6,7,8,9,10]
```

`castSeq` 在一次施放內是穩定的（`seq=[2]` 全程只有 2），所以不是序列在跳。

兩個可驗證的成因，都量過：

1. **時鐘在圖還沒解碼時就開始跑。** `drawSkillEffectPass` 的註解寫
   「so the cast simply starts when its own art is ready」，但程式碼沒有實作這句：
   `born` 就是施放的瞬間，圖則是同一瞬間才開始抓。MEASURED：`10001009` 的 33 張幀圖
   在 `Cache-Control: private, max-age=3600` 下仍要 22–356 ms，
   期間 `age` 已經走過去大半，`found` 直接落到最後一幀。
2. **我自己先寫錯過一次，順手抓到的第二個 bug：** tick 上**沒有** `skillCastSeq`。
   `livewalk.js:1401` 組出來的 tick 只有 `skillStance / dir / step / skillEffects /
   tileEffects`。所以 `cur.skillCastSeq ?? 0` 永遠是 0，整個 session 只有一個時鐘。
   我第一版修正就是這樣寫的，跑出來每個技能都只畫最後一幀——**修正本身是錯的**，
   現在改成讀 `this.live?.state?.skillCastSeq`。

**所以 core 的 `the effect animation advances through its frames` 是紅的，這是對的，
我沒有放寬它。** 它斷言的是 renderer 的性質；我另外加了一段 `prewarm`
把「圖還沒到」這個輸入固定下來，並把冷 cache 的數字印出來，而不是把它藏起來。

**沒修好在哪：** 修好了一半。`walkview.js` 裡的時鐘改成了「圖解碼了才起跑、
且以 live 的 cast 序列為鍵」，這兩點我驗證過程式邏輯與語法，
但**沒有在瀏覽器裡驗證過修好之後的動畫**——因為舊 build 還是線上的那份。
**部署之後必須重跑 `--all` 才能確認動畫有播完。** 這一條我不簽「修好了」。

### B2 fx = 5 張 — **5 張都畫了，z 側也對**

```
ok  1311001 (5 nodes) every effect node reached the canvas   5 distinct archive paths drawn
ok  1311001 each effect node was drawn on the side its z asks for
    body drew at order 0..1767; effect(z=0)->front effect0(z=0)->front effect1(z=0)->front
                                              effect2(z=0)->front effect3(z=0)->front
ok  1311002 / 15111003 / 5111006 同樣
ok  5221003 (3 nodes) every effect node reached the canvas   3 distinct archive paths drawn
```

四個 5 張技能（`1311001` 槍連擊、`1311002` 矛連擊、`15111003` 碎石亂擊、`5111006` 碎石亂擊）
**每一張都出現在畫布上**，用 `/api/canvas?path=` 的 distinct 出現次數確認。

z 側是從**繪製順序**推出來的，不是讀欄位：角色的 blit（`f1/…`）是分界線，
前面的是 behind 後半的是 front。這是 W-076 的規則本身
（`EffectLayer.cpp:24,35` / `Char.cpp:95,240`）。

> ⚠️ 這一段的 z 側斷言**在第一版是恆真的**（key 用錯），修好之後第一次跑出真值。
> 修好前的輸出是 `effect(z=0)->undefined …`，那個「全過」不算數。

**沒驗到的：** 這四個技能所有 node 的 z 都是 0，所以**只驗到 front 側**。
有負 z 的技能（`10001020` z=-2、`1111008` z=-1、`2221007` z=-1、`2311004` z=-1、
`2321008` 有一個 z=-1）沒有被 B2 這一節涵蓋。判官已經寫好「z 有正有負時必須兩側都跑到」
的斷言，但**目前沒有任何一個 5 張技能有負 z，所以那條斷言還沒有被執行過**。
需要一個同時有 5 個 node 且其中一個 z<0 的技能才驗得到；封存裡沒有。

### B3 actions 1 張 / 2 張 — **2 張的永遠只播第 0 張。這是 C# 的線，不是我的。**

規則在參考端（權威）：

```
SkillAction.cpp:59-63   TwoHandedAction(src)
                           actions[false] = src["action"]["0"];
                           actions[true]  = src["action"]["1"];
SkillAction.cpp:65-71   apply(target,):
                           bool twohanded = target.is_twohanded();
                           target.attack(actions[twohanded]);
SkillAction.cpp:158-167 action/0 與 action/1 都是字串 -> TwoHandedAction
```

**所以兩張不是「接著播」，是「單手 / 雙手各一張」，由武器決定播哪一張。**
`Char::is_twohanded()` → `CharEquips::is_twohanded()` → `WeaponData::is_twohanded()`
（`Char.cpp:1036` / `CharEquips.cpp:178`）。同一個規則也用在特效本身上
（`SkillUseEffect.cpp:90-96` 的 `TwoHandedUseEffect`）。

伺服器端：

```
MapSimSessionStore.cs:1876-1882
    if (plan.Actions.Count > 0)
    {
        // The reference picks among them at random per cast ... Index 0 is
        // the first and is what the reference's own resolution reaches ...
        session.SkillStance = plan.Actions[0];
    }
```

**`Actions[0]` 是寫死的，武器從來沒有被查。** 量測：

```
ok    10001009 a pose name reached the avatar   actions: [bamboo]              seen: [bamboo]
ok    11111004 a pose name reached the avatar   actions: [brandish1,brandish2] seen: [brandish1]
FAIL  11111004 every action the archive declares was reachable
        -- NEVER played: [brandish2] over 1 cast(s)
FAIL  1121008 every action the archive declares was reachable
        -- NEVER played: [brandish2] over 2 cast(s)
```

**結論：沒修好，而且不屬於這條線。** 修法在
`MapleBench/Services/Simulate/MapSimSessionStore.cs`（A/C 支線的檔案，我不能動）：

> `session.SkillStance = plan.Actions[0];` 應改成依 `is_twohanded()` 選
> `Actions[twohanded ? 1 : 0]`。武器狀態在這個檔案裡已經有了
> （`session.EquippedWeapon`，`SpawnSkillTiles` 就在同一個類裡用
> `_combat.WeaponAttackSpeed(session.EquippedWeapon)`）。
> 同樣的判斷在 `SkillCastService` 產生 `plan.Actions` 的地方也可能要動，
> 因為 **plan 本身** 現在就把 2 張都送出來，客戶端無法知道該選哪張。

我留了一個 NOTE 說明這個分支現在拿不到：`[brandish2]` 只有在**雙手武器**下才會出現，
而模擬器目前沒有雙手武器，所以 `[brandish1]` 是單手分支的正確答案。
**換句話說：現在這兩條 FAIL 是「模擬器沒有雙手武器」，不是「選錯了」。**
真正的缺陷是「就算有了雙手武器也不會切」。

「畫了幾張姿勢動畫幀」量到的是 `poseFrames: []`（舊 build 沒有 `lastAvatarPose`，
tick 上也沒有幀索引）。**這一問我沒有量到答案**，見 §未驗到。

### B4 地面貼花 fallback — **分支存在，但在任何普通地圖上都走不到**

規則（權威）：

```
SkillUseEffect.cpp:248-266
    for (int16_t x = w_left; x <= w_right; x += step)
        for (int16_t ground_y : fht.get_foothold_range(x, w_top, w_bottom))
            show_attack_effect_at(tile[rand()], 4, {x - user.x, ground_y - user.y});
    // Fallback: if no footholds found in area (e.g. floating void map),
    // show at user feet
    if (spawned_count == 0)                                        // :263
        show_attack_effect_at(tile[0], 4, Point<int16_t>(0, 0));   // :265
```

C# 端**有**實作（`MapSimSessionStore.cs:2051-2067`），簽章是
`ShiftX = 0, ShiftY = 0, Z = 4, Sprite = plan.Tiles[0]`。
所以**這條不是「沒做」**。

走不到的原因是可以算的：七個有 tile 的技能，
`tileAreaBottom` 全部 **> 0**（20 / 30 / 250），所以 `w_bottom >= 施法者的 Y`，
而施法者腳下就有自己的 foothold 面 —— 只要迴圈的任何一**列**落在那條 foothold 的
X 範圍內，`get_foothold_range` 就一定有東西。

判官把這件事斷成兩條：

```
ok  12111003 the ground decal is visible  5 decal(s) on the wire; 9 tile node(s) in the archive
ok  12111005 the ground decal is visible  5 decal(s); 1 tile node(s)
ok  2121007 / 2221007 / 2321008 / 5121001 同樣 5 decal(s)
ok  every tile area reaches at or below the caster, so a grounded cast always finds a foothold
    area bottoms: 20,30,250 (all >= 0 ...)
ok  on f4/Map/Map1/100000004.img the fallback is unreachable at EVERY foothold
    76 non-wall foothold(s) checked; would-fire counts:
    [12111003 0/76; 12111005 0/76; 2121007 0/76; 2221007 0/76; 2321008 0/76]
    (5121001 skipped: its box is re-read per level and mirrored by facing)
  fallback branch executed by: NOBODY — see the assertion above
```

貼花的實際落點（六個技能在測試圖上**完全一致**）：

```
[[-40,1], [140,0], [140,-239], [320,0], [320,-298]]
```

這就是 fallback 的否定證據：fallback 的簽章是**剛好 1 張、shift 剛好 (0,0)**。
量到的是 5 張、shift 全都不是 (0,0)。
`+140` 和 `+320` 各出現兩次，是因為同一列有**兩層不同的地面**，
`get_foothold_range` 會把兩層都回來（`FoetholdPhysics.cs:330-344`，8px 內去重）——
跟參考一致。欄位只跑了 `-40 / +140 / +320`，`-400 / -220` 兩列落在地圖外。

**而且我直接用幾何算過，不是只用推論。** 上面那條 `unreachable at EVERY foothold`
就是：取 `/api/simulate/geometry` 的 `f4/Map/Map1/100000004.img`
（114 條 foothold，去掉 `isWall` 後 **76** 條），對每一條 foothold 的中點跑一遍
參考自己的迴圈（`SkillUseEffect.cpp:248-259`）與 `GetFootholdRange` 的語義。
最寬的非牆 foothold 只有 **90 px**，而框寬 800 px、步長 150–200 px，
所以「某一列完全落空」不足以讓 `spawned == 0` —— 別的層會補上。

**結論：沒修好（分支是死碼），但不是缺陷，是對照組沒有對應的圖。**
要走到的條件是：施法者周圍整條框在 `y ∈ [施法者-350, 施法者+250]` 裡
一個 foothold 都沒有，也就是真正的浮空 / 斷層圖。
兩個選項都不屬於我：
(a) 找一張浮空圖當測試 fixture；
(b) 讓 `Start()` 接受一個站在空氣中的起點（目前 `Start` 一定把角色放上 foothold，
見 `MapSimSessionStore.cs:3152-3166`，`foot` 找不到就直接回 error）。

### B5 MultiStrike 命中特效疊成一團 — **量到的峰值是 1，沒有疊**

判官在**頁面裡**用 `requestAnimationFrame` 錄（不是從 Node 每 40 ms 輪詢）。
這一點是關鍵：hit effect 只活一幀，Node 輪詢一次都抓不到 ——
實測 6 次 1121008 施放、每次都確實命中（1000 → 900），觀察值 0 個。

量到的（完整 `--all` 一輪）：

```
ok  5121001 a mob published its hit effects on the wire  1 effect(s) on mob 2220100
ok  5121001 the struck mob's hit effects reached the canvas  1 on the wire, 176 blit(s)
ok  5121001 hit effects never pile: peak concurrent per mob (reported)
    peak 1 on one mob across 6 cast(s); 1 animation; 1 anchor [2,-54]; 20 distinct offsets
ok  1121008  peak 1 across 6 cast(s); 1 animation; anchor [2,-54]; 2 distinct offsets; 31 blit(s)
FAIL 4211004 a mob published its hit effects on the wire -- nothing observed in 6 casts
```

**答案：不會疊成一團，因為它們本來就沒有同時掛在同一隻怪身上。**
`5121001` 的 plan 有 **39 個** hit sprite，但 timeline 把它們攤在不同 tick，
任一幀最多一隻。而單一動畫本身有多幀（`5121001` 21 幀 / 20 個不同 offset），
所以視覺上是「一個閃動走完一輪」，不是「多個閃動疊在一起」。

**`4211004` 那條 FAIL 是量測失敗，不是結論。** 6 次施放都沒有打到怪
（怪在巡邏），所以這個技能沒有峰值資料。B5 的結論只建立在
`5121001` 與 `1121008` 這兩個**有資料**的技能上。

另外確認了一件對照組的事：**同一隻怪身上的所有 hit effect 共用同一個 anchor**
（`Mob.cpp:1536-1559`，`shift = get_head_position(...)`，一個 shift 給全部），
所以就算同時有兩個，它們也是疊在頭上，再各自用自己的 origin 分開 ——
這正是 `DrawArgument.h:131-157` 的行為，跟參考一致。**共用 anchor 本身不是缺陷**；
真正的缺陷會是「N 個完全相同的矩形」，而量到的 offset 都是不同的。

**沒驗到的：** peak 1 是在測試圖、單一施法者、怪在面前的情況下量的。
multi-hit 的 `mobCount > 1` 情境（A2 的線）會不會讓同一幀疊多個，**我沒有量**。

---

## 3. 附帶量到的東西

**封存裡有六個特效的最後一幀沒有圖。** 判官逐一印出路徑：

```
art: 1 without art -- f8/1000.img/skill/10001009/effect/19
                   f8/1000.img/skill/10001020/effect/13
                   f8/111.img/skill/1111008/effect/17
                   f8/1211.img/skill/12111003/effect0/20
                   f8/222.img/skill/2221007/effect/25
                   f8/232.img/skill/2321008/effect0/22
art gaps: 6 path(s) the archive has no picture for
```

六個全部是某個 node 的**最後一幀**，而且 33 張圖在 22 ms 內就全部回完
（32 張 200、1 張 404），所以不是逾時，是封存裡真的沒有。
**每幀一張的節奏因此在最���一幀會停住** —— 這是檔案的事，不是渲染器的事。

`1311001` 的 `effect0/0`、`effect1/0`、`effect2/0`、`effect3/0` 回 **1×1 像素**，
那**不是**缺口：這幾個動畫的 `delays` 是 `[600,30,200]`、`[750,30,200]`，
第一幀本來就是給長的空白 lead-in。

**地圖貼花每個技能都穩定產出 5 張**，`z = 4`，符合
`SkillUseEffect.cpp:209,255`。

---

## 4. 我明確沒驗到的部分

1. **修好之後的動畫有沒有播完** — 舊 build 還是線上的那份，修正沒有被瀏覽器執行過。
2. **`lastMobHitRect` / `lastAvatarPose` / `lastEffectRect.pass` 這三個 publish
   沒有被任何量測讀到過**（舊 build 沒有）。B5、B3 的姿勢幀數是靠 wire 推的，
   不是靠這三個欄位。
3. **B3「姿勢動畫畫了幾幀」沒有答案。** 需要 `lastAvatarPose`（或 tick 上的幀索引）。
4. **B2 的 behind 側沒有被驗到**，因為沒有任何 5 張技能的 node 是負 z。
5. **B5 的 multi-mob 情境沒有被驗到**（`mobCount > 1` 是 A2 的線）。
   `4211004` 連打到怪都沒成功 6 次，所以 B5 只有兩個技能的資料。
6. **B4 的 `5121001` 被跳過**（它的框會依等級重讀並依朝向鏡射，
   我沒有重現那個 per-level 重讀），所以它的可達性沒有算。
7. **core 的 `the cast took exactly one test hit off the mob it was in range of`
   是 flaky 的。** 我在同一天看到它 `1000 → 900` 也看到 `1000 → 1000`，
   原因是怪會巡邏走開（這是原本 suite 就有的註解說明的已知抖動，不是新問題）。

---

## 5. 重跑方式與最終數字

```powershell
cd C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX-audit
node MapleBench\tools\browser-verify.mjs            # core
node MapleBench\tools\browser-verify.mjs --all      # 全部，約 8-12 分鐘
node MapleBench\tools\browser-verify.mjs --shapes --limit=5   # 快速抽樣
node MapleBench\tools\browser-verify.mjs --tiles             # B4，含幾何可達性
```

`--hits-casts=N` 控制 B5 每個技能施放幾次（預設 6）。

**完整 `--all` 一輪：`VERDICT: browser-verify 92 assertions, 9 failed`。**
9 條紅全部是**已知且已分類**的，不是未知：

| 紅的斷言 | 分類 |
| --- | --- |
| `the effect animation advances through its frames` | 舊 build 的真缺陷（§B1 附帶發現），修正待部署 |
| `11111004 / 1121008 every action the archive declares was reachable` | C# 的線，`Actions[0]` 寫死（§B3） |
| `12111003 / 12111005 / 2121007 the ground decal is visible`（0 decals） | 判官當時還沒加重試；修好後六個技能全部穩定 5 張（§B4） |
| `4211004 a mob published its hit effects on the wire` | 怪沒被打到，量測失敗不是結論（§B5） |

修好判官那三條之後，`--tiles` 單節是 **22 assertions, 1 failed**，
唯一那條紅就是上面表裡第一列。

## 6. 交給總管的事

1. **部署**：把 `MapleBench/wwwroot/js/walkview.js` 送到線上，然後重跑 `--all`。
   這之前，B1 的動畫結論、B3 的姿勢幀數、B5 的 publish 欄位都驗不到。
2. **C# 改動（B3）**：`MapSimSessionStore.cs:1882` 的 `plan.Actions[0]` 要依
   `is_twohanded()` 選 `Actions[twohanded ? 1 : 0]`。**不屬於我的線，我沒動。**
3. **C# 決定（B4）**：fallback 分支要有測試對象，要么加浮空圖 fixture，
   要么讓 `Start()` 接受一個站在空氣中的起點。**不屬於我的線，我沒動。**