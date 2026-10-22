# 2026-10-14 — 第二輪：對標符合性審查，抓到一個 16 倍的施放鎖偏差

第一輪把 §6 四項都做出來了（見 [2135](2026-10-14-2135-6.4-skill-cast-settings-built.md)）。
第二輪沒有新功能，做的是目標條件要求的那件事：
**「改動不得超出對標 MapleNecrocer / clean-client 已有行為」** —— 把自己寫的東西逐一對回原始碼。

順帶補了一件一直沒驗的事：主線是從 `bin/` 跑的，而我前兩輪只跑 `build-check`。

---

## 0. `bin/` 那份確實能跑

```
bin dll 21:17 · wwwroot/js 81 個檔（src 只有 41，bin 裡有 40 個舊檔，Copy-Item 不會刪）
bin build boots on 5592: {"version":"1.1.0","license":"GPL-3.0"}
服務的 js md5 = src md5 ✓
castable 66/66 有名稱 · 綁定 POST 正常 · summon 正常 · 爬梯 60 幀 y −265 → −355 onLadder=True
```

---

## 1. 審查結果：動作姿式的選擇**沒有**偏離

我原本擔心 `plan.Actions[0]` 是自己挑的。對標：

```
Skill.cs:47   Equip.DataS.AddOrReplace(ID + "/action", Iter.GetStr("0", "alert"));
```

`GetStr("0", "alert")` 取的就是第 **0** 個，缺值回落 `"alert"`。
`SkillCastService` 已經套用了同一個回落。所以取 `[0]` 是對標行為，不是發明。**維持不變。**

---

## 2. 審查結果：施放鎖**偏離了，而且是數量級的偏離**

我第一版的鎖是 `SkillCastPlan.TotalTime`，理由寫成「它就是那段動畫的長度」。**那個理由是錯的。**

對標的鎖是：

```
Skill.cs:410   Skill.PlayEnded = AnimEnd;      ← 由 SkillSprite 自己的動畫結束設定
Skill.cs:321   if (Skill.PlayEnded && !Game.Player.InLadder)   ← 只有這個能開始下一次施放
MapleCharacter.cs:1472,1482,1509,1513,1522,1642                 ← PlayEnded 為 false 時禁止走/跳/趴/普攻
```

而 `TotalTime`（`Skill.cs:187`）是 **碰撞物件活多久**，是另一個量。

### 十二個技能的實測偏差

| 技能 | effect 動畫 | TotalTime（我原本鎖的） | 偏差 |
|---|---|---|---|
| 1121008 | 1080ms | 67 幀 (1116ms) | −2 幀 |
| 12111006 | 960ms | 57 幀 (950ms) | −4 幀 |
| 1311001 | 1130ms | 57 幀 (950ms) | −11 幀 |
| 10001009 | 4440ms | 57 幀 (950ms) | **−216 幀** |
| 12101006 / 12111003 / 12111005 / 12111006 / 11111006 | 60–90ms | 57 幀 (950ms) | **+50 幀** |

**兩個方向都錯**：短特效的技能我鎖得過長（約 16 倍），長特效的技能我鎖得不夠（3.6 秒變 0.95 秒）。

⚠️ **我第一個量到的樣本是 1121008，偏差只有 2 幀 —— 正好是十二個裡最接近的一個。**
若只量一個，結論會是「差不多，可以接受」。這是「資料量不等於驗證」的又一例，
和本專案記錄過的 5366 張地圖掃三次得到三個數字是同一類教訓。

---

## 3. 修法：重用專案既有的動畫服務，不寫第二套讀法

第一版修法是手寫 delay 讀取（`Children(holder)[f]` 逐格找 `delay`），**結果拿到 900ms 而不是 1080ms**。
原因寫在 `AnimationService.cs:258-263`：

> 容器自己的 `delay` 才是影格長度。它在容器上而不在任何內層 canvas 上 ——
> 對過位元組：`stand1/0/delay` = 500，而 `stand1/0/body` 底下沒有 `delay`。
> 對標也從同一層讀（`MapleCharacter.cs:395`, `Iter2.GetInt("delay")`）。

所以**我讀錯了層級**，拿到的是讀取器的 100ms 預設值而不是封存的真實時序。

與其修好第二套讀法，不如不要第二套。`MobSpriteService.cs:54-56` 已經是這個先例
（`MobSpriteService(WzSessionService, AnimationService)`），照它改成：

- `SkillCastService` 建構子多收 `AnimationService`
- `SkillCastSprite` 多帶一個 `ContainerPath`
- `effectMs = max(各效果節點的 _animation.Describe(container).TotalMs)`
- 鎖定幀數 = `round(effectMs / (1000/60))`，下限 1 幀

### 為什麼取「最長」而不是「最短」

對標的 `PlayEnded` 被每一個 SkillSprite 逐幀賦值，所以嚴格說是**依繪製順序**的、
本身就不乾淨。但「最長」是唯一一種讀法能讓**還在播放的精靈繼續鎖住角色**，
而那正是這個旗標存在的唯一理由（`Skill.cs:321`）。已在程式碼註解寫明。

### 驗證：`effectMs` 與 `/api/animation` 逐筆一致

```
1121008    plan.effectMs=1080  animation=1080  ✓
12111006   plan.effectMs=960   animation=960   ✓
10001009   plan.effectMs=4440  animation=4440  ✓
1311001    plan.effectMs=1130  effect/0 單獨看是 900
           → 但它有五個效果節點，effect3=1130 是最長的，取最大 = 1130 ✓
```

（`1311001` 一開始看起來不符，是**我的比法錯了**：拿最大值去比單一節點。）

### 實測鎖定幀數

```
id          effectMs  實測 action=skill 幀數
1121008     1080      64        (= floor(1080/16.667))
12111006    960       57        (= floor(960/16.667))
1311001     1130      67        (= floor(1130/16.667))
10001009    4440      265       (= floor(4440/16.667))
```

與對標一致到不超過一幀 —— 而對標本身也不到整數幀（1080ms = 64.8 幀）。

---

## 4. 這一輪沒有動到的

`Actions[0]`（對標 `GetStr("0")`）、碰撞框算式（`Skill.cs:441-458` 逐字）、
MultiStrike 的分支（已證明對標的 MultiStrike 技能不造成傷害）、
熱鍵 A–Z 限制（`SkillForm.Designer.cs:50`）。

`rejected` 回報仍然是**宣告式的自設計**：對標的 `MobForm.cs:117` 與
`SkillForm.cs:156-162` 都是無聲丟棄，沒有任何回報途徑。與既有的
`SummonResult.Rejected` 同一模式。

---

## 5. 本輪狀態

| 項目 | 狀態 |
|---|---|
| `bin/` 建置可啟動並服務新版 | ✅ 實測 |
| 施放鎖對齊對標動畫長度 | ✅ 四個技能實測 |
| 動作姿勢選擇對齊對標 | ✅ 讀碼確認，本來就對 |
| 6.1 / 6.2 / 6.3 / 6.4 回歸 | ✅ 全過 |
| `TEMP_DIAG` 殘留 | 0 |
| 診斷檔殘留 | 無 |
| 三處 md5 | 一致（`avatar.js` 仍為交接記錄的 `a1eb2eb4…`）|

**仍然沒有桌面截圖。** 面板與按鈕「看得到」這件事我依然不能宣稱。