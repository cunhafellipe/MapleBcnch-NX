# 2026-10-17 01:25–01:40 · SEG14 — 裝備在別名姿勢下消失、特效從腳長出來

> 結論先講：**兩個都是「別名解析只做了一半」的後果。**
> 技能特效現在會播了（上一段），但起點在腳上；施放時裝備全部消失只剩眼睛。

---

## 1. 使用者的報告

> 「技能特效有撥放了 但是 起始點 座標不對 裝備 包括 頭髮 上衣 褲子 套服 手套 全部 動作必須匹配
> 目前放技能 全身就會脫光 只有人物 眼睛 技能是從腳開始出現的」

---

## 2. 裝備消失：我漏掉了第四條讀取路徑

上一段（W-074）我在 `ComposeCore` 最上游解析別名，套用到**三個**地方。
實際上有**四個**。

```csharp
ReadPose(body,   readPose, readFrame, ...);   ✅ 改到
ReadPose(head,   readPose, readFrame, ...);   ✅ 改到
CollectBody(..., readPose, readFrame, ...);  ✅ 改到
CollectEquip(..., stance,       frame, ...);  ❌ 漏了  ← 裝備在這裡
```

所以身體有姿勢、裝備沒有 → **角色裸體**。

### 決定性的量測

```
f1/Cap/01000000.img     stances=36  有 stabOF, stand1, swingOF, swingT1, swingT3, swingTF
f1/Coat/01040000.img    stances=34  同上
f1/Pants/01060000.img   stances=34  同上
f1/Shoes/01070000.img   stances=34  同上
f1/Glove/01080000.img   stances=34  同上
f1/Cape/01102000.img    stances=34  同上
f1/Weapon/01302000.img  stances=16  有 stabOF, stand1, swingOF
f1/Hair/00030000.img    stances=36  同上
f1/Face/00020000.img    stances=25  一個都沒有
```

**裝備表是以「別名的目標名」為 key 的，而且完全沒有 `brandish1` 這個名字。**
所以用原始的 `stance` 去查，裝備查不到任何東西 → 什麼都不畫。

（`Face` 25 個姿勢裡沒有任何一個目標姿勢，所以臉部在任何技能姿勢下都不會畫——
這可能是使用者說「只剩眼睛」的一部分。）

---

## 3. 技能特效從腳長出來

封存自帶每一幀的 origin：

```
f8/112.img/skill/1121008/effect/0/0   canvas  66 x 37   origin 30, 23
f8/112.img/skill/1121008/effect/0/1   canvas 142 x 95   origin 49, 81
```

**每一幀的 origin 都不同。**而客戶端用的是自己編的：

```javascript
ctx.drawImage(img, px(cur.x) - w / 2, py(cur.y) - h * 0.72, w, h);
```

`cur.y` 是角色的**腳**，再加上固定 `-0.72 × 高` 的偏置。**這就是「從腳開始出現」。**

### 修法

1. `SkillCastSprite` 多帶一個 `Origins`（每幀一個，`PointDto` 列表）。
2. `SkillCastService.Plan` 在已經握著幀節點的那個迴圈裡順便讀 origin
   （不多走一次樹）。
3. 客戶端改成 `px(cur.x) - origin.x*s, py(cur.y) - origin.y*s`——
   **和身體圖層同一條規則：畫布用自己的 origin 對齊錨點，而不是用它的角。**

實測送出的資料：

```
node=effect frames=9
origins=[{30,23},{49,81},{52,81},{150,82},{145,83},{172,88},{152,85},{137,…}]
```

---

## 4. 上一段漏掉特效的原因（和這次漏掉裝備是同一個錯）

`livewalk.paint()` 交給檢視器的是 `v.mobs`、`v.sheet`、`v.damageNumbers`——
**從來沒有交 `v.skillEffects`**。所以 `drawSkillEffects` 讀 `cur?.skillEffects`
永遠是 undefined，第一行就 return。

**兩次的錯都一樣：沿著一條資料路徑追，卻沒有列出這條路徑上的所有消費者。**
別名有三個讀取點我以為是三個，實際四個；特效有一個消費者我以為有，實際 paint 沒接。

---

## 5. 狀態

```
build   exit=0, 0 errors 0 warnings
deploy  59 wwwroot 檔全等；avatar.js md5 未變
sim-skills-check  35 passed, 0 failed
check   59 items, 0 problem(s)
gate    RED SUITES: 1   ← 仍是 ball-art（W-070，未結）
editor  pid 17024，16 個封存
```

---

## 6. 還沒證明的

**畫面依然沒被看過。**這兩條都是渲染層的修正——
`origins` 有送到、`CollectEquip` 有讀到正確的姿勢，
但「裝備有沒有真的畫出來、特效有沒有畫在角色身上」只有畫面能回答。

使用者已經連續三次提供畫面回報，這是唯一有效的驗證途徑。

---

## 7. 給下一個 agent 的一句話

**追一條資料路徑之前，先把那條路徑上所有讀同一個值的點列出來，不要靠記憶。**
別名解析漏了一個 `CollectEquip`，特效漏了一個 `paint`——
兩次的症狀都長得像「渲染壞了」，實際都是「值沒被送到會用它的最後一站」。