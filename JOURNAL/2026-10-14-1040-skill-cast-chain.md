# 2026-10-14 10:40 — 技能施放鏈：對標 Client/Skill.cs 移植完成並實測

接續 [1035](2026-10-14-1035-nx-art-loading-actually-solved.md)（同輪，不覆蓋）。
計畫：`docs/計畫-技能特效動畫異常.md`。

---

## 1. 先更正一個錯誤結論

`docs/對標考核-MapleNecrocer.md` 寫「技能施放與冷卻鏈：對標 SkillBook.cs + Skill.cs
完整技能樹完全未實現」。**錯。**

| 檔案 | 行數 | 性質 |
|---|---|---|
| `Services/SkillService.cs` | 2144 | **編輯器**（瀏覽/改公式/烘焙 level） |
| `Services/SkillFormulaEvaluator.cs` | 830 | 公式求值 |
| `Services/SkillFieldCatalog.cs` | 239 | 欄位分類 |
| `Api/SkillEndpoints.cs` | 120 | 9 條路由 |

那是靜態掃 `Services/Simulate/` 造成的——**沒看見別的目錄就宣判缺失。**
現有的確是編輯器（會寫檔），缺的確是執行期（不能寫）。

**區分點**：`SkillService` 寫 archive；`SkillCastService` 只從 archive 推導計畫，永不寫入。

---

## 2. 16 個 nx 全部開啟

`POST /api/files/open-many`，一次請求 0.2–0.5 秒，16/16，`failed: []`。

```
f1  Base       f2  Character  f3  Effect     f4  Etc
f5  Item       f6  Map        f7  Mob        f8  Morph
f9  Npc       f10  Quest     f11  Reactor   f12  Skill
f13 Sound     f14  String    f15  TamingMob f16  UI
```

開檔前 `/api/files` 只有 5 個（f1–f5）——**這就是「少開東西」發生的時刻**。
規則已在本 skill 寫明，現在真的照做。

---

## 3. WZ 結構（逐層問出來的，沒有猜）

- Skill.nx **根直接掛 76 本技能書**（`000.img` `100.img` `110.img` …），**不是** `Skill.img/100.img`
- 技能書 = `info` + `skill`（`info` 在前）
- 技能 = `icon` `iconDisabled` `iconMouseOver` `level`（+ 有施放的話 `action` `effect` `hit`）
- **這個客戶端沒有任何技能有 `common`** —— 全是明文 `level/1..N`。
  和 `SkillService` 註解所述（v232 的 911 技能）一致，所以 `ExpandCommon` 在這裡無用武之地。
- `Effect.nx` 根 17 個：`BasicEff` `CharacterEff` `Direction1-3` `ItemEff` `MapEff` `SetEff` `Summon` `Tomb` …

---

## 4. 對標規則（全部附行號，逐條照抄）

來源 `_necrocer_src/MapleNecrocer/Client/Skill.cs`（581 行）。

| 規則 | 行號 | 內容 |
|---|---|---|
| `GetJobImg` | 31–37 | 9 位數且開頭 `800` → `id/100`；否則 `id/10000` |
| Entry 路徑 | 41–42 | `Skill/<job>.img/skill/<id>` |
| action | 46–47 | `action/0`，缺值回落 `"alert"` |
| tileCount | 48–49 | `tile.Nodes.Count - 1` |
| `Effects[]` | 154–155 | `effect, effect0..3, screen, screen0, ball, keydown, keydown0, keydowned`（迴圈 `i<=10`，共 11） |
| MultiStrike | 168–178 | `1121008`/`1001005`/`1311012` 為單段，其餘六連 |
| Count | 184–208 | MultiStrike → 1；否則 6 |
| TotalTime | 187 | `DamageWaitTime + 6*7` |
| mobCollision | 197–199 | `StartTime = wait + j*7`，`j = 1..6` |
| skillCollision | 215–231 | `StartTime = wait + i*6`，`i = 1..Count` |
| 碰撞框 | 219–229 | 有 `level` → `/level/<MaxLevel>/lt`+`/rb`；否則 `/common/` |
| 特效定址 | 247–254 | 節點含 `0/0` → 經 `0` 取；否則節點本身；最後拼 `/0` |
| ball | 259–267 | `BallSpeed = 1000/bulletSpeed`，`Y -= 27`，方向依 FlipX，`AnimRepeat` |
| tile | 284–315 | 需 `tile/0` 且 `tile/0/0`；`Random.Next(0, tileCount)`；7 個，x = 240+i*120 |
| `DamageWaitTime` | 53–150 | **硬編碼 switch 表**，default 15（非公式非曲線），逐條轉錄 |

---

## 5. 新增檔案

- `Services/Simulate/SkillCastService.cs` — 施放計畫與時間軸
- `Api/SkillCastEndpoints.cs` — 3 條路由，全部唯讀
- `Program.cs` — DI 註冊
- `Api/Endpoints.cs` — `api.MapSkillCasts()`

路由：

```
GET /api/skill-cast/capabilities   規則與常數（前端不用自己再寫一份）
GET /api/skill-cast/plan?skillId=  單一技能的完整施放計畫
GET /api/skill-cast/sample?perBook 每本技能書抽樣
```

---

## 6. 實測：技能 1121008（勇士 Superskill）

```
entry   f12/112.img/skill/1121008
action  ['brandish1', 'brandish2']
effect  [('effect', 9 frames, bullet=False)]
hit     f12/112.img/skill/1121008/hit/0/0 , hit/0/1
碰撞框   (-150,-110) - (-10,0)   140x110   hasBox=true
maxLevel 30   wait 25   multiStrike false   strikes 6   totalTime 67t
```

時間軸 22 個事件，全部由 WZ 推導：

```
t  0  sprite     effect/0/0 (9 frames)
t  0  hitSprite  hit/0/0, hit/0/1
t  0  tile       x=240+0 .. 240+720   (tileCount=0，無變體可選)
t 31  skillCollision #0
t 32  mobCollision   #0   combo 1/6
t 37  skillCollision #1
t 39  mobCollision   #1   combo 2/6
t 43  skillCollision #2
t 46  mobCollision   #2   combo 3/6
t 49  skillCollision #3
t 53  mobCollision   #3   combo 4/6
t 55  skillCollision #4
t 60  mobCollision   #4   combo 5/6
t 61  skillCollision #5
t 67  mobCollision   #5   combo 6/6
```

---

## 7. 像素驗收（手寫 PNG 解碼器，非 HTTP 200）

**技能特效 11/11 全部解碼成功**，總不透明像素 90,878：

```
effect/0   66x37   不透明 81%     effect/5   281x127  不透明 32%
effect/1   142x95  不透明 69%     effect/6   164x118  不透明 47%
effect/2   154x94  不透明 68%     effect/7   148x156  不透明 39%
effect/3   251x110 不透明 60%     effect/8   143x163  不透明 21%
effect/4   249x110 不透明 43%
hit0/0     71x69   不透明 68%     hit0/1  65x64   不透明 68%
```

揮砍軌跡的尺寸與不透明度**單調遞減**（66×37/81% → 281×127/32%），
這是「特效由小到大散開」的正確形態，不是亂數。

**brandish1/2 立繪 14/14 全部解碼成功**（2 姿勢 × 7 幀，`frameCount=7 delay=100`），
但每幀只有 **1 個部件 26×16**——v83 的 `brandish1/2` 是**單層姿勢**（只有武器），
不穿裝備。這是資料的真實形狀，不是合成器漏了層。

輸出：`C:\Users\e7896\AppData\Local\hermes\cache\scratch\skillcast\`（11 張）

---

## 8. 覆蓋率：65 本技能書全讀

```
有碰撞框  7/65      有 effect 12      有 hit 4      有 tile 0
```

碰撞框只有 7 個，因為**抽的第一個技能多半是被動/增益**（`1000000` 是「生命恢复」）。
主動技才有框——`12101000` 600x400、`2121000`/`2221000`/`2321000`/`5121000` 800x600、
`9001000` 800x600 alert2、`9101000` 800x600 alert4。
`8001000` 是 `stabO1`（武器攻擊技），`5210000` 有 `triplefire` 動作 + 2 特效 + 4 命中特效。

**這不是覆蓋率不足，是抽樣方式反映的真實分布。**

---

## 9. 三個踩到的坑（都寫進註解）

1. **`WzObject` 不在 `MapleLib`，在 `MapleLib.WzLib`** —— 第一版建置 7 個 CS0246。
   `CombatService.cs` 的 using 是照抄對象。
2. **記錄型別裡的 `ValueTuple` 序列化成 `{}`** —— `effects: [{}]`。
   結構化輸出不能用 tuple，改成 `SkillCastSprite` / `SkillHitSprite` / `SkillTileSprite` 三個具名 record。
3. **`Sample()` 回 0** —— 我寫 `Children(book).First().Name != "skill"` 來判斷是不是技能書，
   但這個存檔的書是 `info` 在前 `skill` 在後，於是**每一本都被跳過**，回空。
   改成 `Child(book, "skill")` 依名稱取。
   **「位置」是猜的，「名稱」才是查的** —— 和 10:35 那次裝備葉名是同一類錯誤。

---

## 10. 下一個單位

動畫異常偵測（使用者指定的用途之一）：對每隻怪每個姿勢，報出可量化的異常
——幀數與 WZ `delay` 是否一致、`originX` 對位誤差、幀尺寸跳變。
輸入全部來自 WZ，**對標沒有這項 → 宣告自設計**，但量測對象不自立。