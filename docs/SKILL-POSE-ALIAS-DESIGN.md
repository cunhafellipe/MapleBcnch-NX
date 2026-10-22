# 技能姿勢是「別名」，合成器不會跟隨 — 設計文件

> 寫於 2026-10-17 01:20，實作依此進行。
> 報告來源：使用者實際畫面——「技能動畫不會播放，玩家整個人物會變透明，剩眼睛顯示在腳的位置」。

---

## 1. 症狀與實測

### 使用者看到的
- 施放技能時**技能動畫不播放**
- **玩家整個人物變成透明，只剩眼睛，而且畫在腳的位置**

### 逐幀掃描 `/api/simulate/look`（fileId=f1）

| stance | frameCount | bodyAnchor | faceAnchor | bounds |
|---|---|---|---|---|
| `stand1` | 3 | `{-8,-21}` | `{-8,-52}` | `{-23,-64,16,0}` |
| `swingT1` | 3 | `{-8,-21}` | `{-8,-52}` | `{-23,-64,16,0}` |
| `walk1` | 4 | ok | ok | ok |
| `jump` | 1 | ok | ok | ok |
| **`brandish1`** | **7** | **`{0,0}`** | **`{0,0}`** | **`{0,0,0,0}`** |
| **`brandish2`** | **7** | **`{0,0}`** | **`{0,0}`** | **`{0,0,0,0}`** |

七幀全部為零。錨點為 0 = 所有圖層畫在角色原點 = **腳的位置**，
而且沒有圖層可畫 = **人物透明**。症狀與量測完全吻合。

### 封存裡的東西

```
f1/00002000.img/stand1/0/     -> [arm, body, delay, face]
f1/00002000.img/swingT1/0/    -> [arm, armOverHair, body, delay, face]
f1/00002000.img/brandish1/0/  -> [action, delay, frame]      <- 沒有 body
f1/00002000.img/brandish2/0/  -> [action, delay, frame]
```

**`brandish1` / `brandish2` 在角色表裡不是姿勢，是動作別名。**逐幀內容：

```
brandish1/0..3  action='swingOF'  frame=0..3
brandish1/4..6  action='stabOF'   frame=0..2
brandish2/0..2  action='swingT3'  frame=0..2
brandish2/3..6  action='swingTF'  frame=0..3
```

`swingOF` / `stabOF` / `swingT3` / `swingTF` **全部存在於同一張角色表的 158 個姿勢裡**。
資料是完整的；缺的是「跟隨別名」這一步。

### 為什麼技能一定踩到

技能 `1121008` 的 `action` 節點回傳 `['brandish1','brandish2']`
（`Skill.cs:46-47` 的 fallback 是 `alert`，但這個技能自己指定了這兩個）。
而 `MapSimSessionStore.CastSkill` 把 `plan.Actions[0]` 設為 `SkillStance`
（`MapSimSessionStore.cs:1492`），客戶端 `walkview.skillPoseTick` 再把它改寫到
攻擊路徑上（`action='attack', attackStance='brandish1'`）。
所以**每一次技能施放都必定走進這個零錨點的姿勢**。

---

## 2. 成因

`CharLookComposer` 讀每幀時假設 `<stance>/<frame>/body` 存在。
別名幀沒有 `body`，讀不到 → 五個錨點（navel / bodyNeck-headNeck / brow / hand /
arm）全部退化成 0 → bounds 也全 0。

**這是一個只影響別名姿勢的缺陷，所有一般姿勢都正常**——所以它躲過了
`sim-skills-check.mjs` 的 33 條斷言：那一條只斷言
`brandish1 is a real pose in the open archive`（姿勢**存在**），從來沒斷言
**它能合成出東西**。

---

## 3. 修法

在合成器解析每幀時加入別名跟隨：

```
node = <stance>/<frame>
if node has `body`            -> use it (the normal case, unchanged)
else if node has `action`     -> resolve <action>/<frame.value>
                                  and use THAT frame
else                          -> unchanged: zero anchors, bounds 0
```

**為什麼判定放在這裡**：錨點與 bounds 全部由合成器從它實際讀到的那個
`body` / `face` / `arm` 節點推出來，所以只要讀對節點，錨點自然就對，
不需要另外修任何繪製程式碼。

**不做的事**：
- 不在客戶端硬寫一張 `brandish1 -> swingOF` 的對照表。資料在封存裡，
  客戶端不該知道封存的別名規則。
- 不改 `SkillCastService` 讓它避開別名。參照就是把 `action` 節點的名字
  原樣交給角色表解析（`MapleCharacter.cs:1591-1600`），別名是角色表自己的事。

---

## 4. 驗收

```
# 1. 別名姿勢現在合成得出東西，而且錨點與一般姿勢同形
GET /api/simulate/look?fileId=f1&stance=brandish1&frame=0
# EXPECT: bodyAnchor != {0,0}, bounds != {0,0,0,0}

# 2. 七幀全部如此
# EXPECT: brandish1 與 brandish2 的每一幀 bodyAnchor 都非零

# 3. 沒有回歸：所有一般姿勢與改動前逐幀相同
GET /api/simulate/look?fileId=f1&stance=<每個其他姿勢>&frame=0..N
# EXPECT: 錨點與改動前完全一致

# 4. 合成出來的圖層對得上別名指向的那一幀
#    brandish1 frame 0 == swingOF frame 0
# EXPECT: 兩者 bodyAnchor / bounds / 圖層數相同

# 5. 整個 gate 不變紅
python tools/pm.py gate
# EXPECT: 不新增紅燈
```

## 5. 風險

| 風險 | 緩解 |
|---|---|
| 別名鏈（別名指向別名）造成無限迴圈 | 跟隨一次為限，並在程式的常量表裡寫明上限與理由 |
| 一般姿勢的節點順序改變 | 只在 `body` 缺席時才走新分支；其他路徑逐行不動 |
| 別名指向不存在的姿勢 | 退回原本的零錨點行為，並記錄原因，不拋例外 |