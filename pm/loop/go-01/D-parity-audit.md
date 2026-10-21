# D — 引用稽核（Parity citation audit）

稽核對象：`MapleBench/Services/Simulate/*.cs`（12 檔）+ `MapleBench/wwwroot/js/walkview.js`
權威基準：`C:\MUWORK\GAME\MPSH\clean-client\src`（598 檔 = 259 `.cpp` + 339 `.h`，**0 個 `.cs`**）
稽核日期：2026-10-18

本報告**唯讀**。沒有修改任何產品程式碼、沒有建置、沒有動伺服器。

---

## 0. 方法與口徑

1. 從 13 個檔案抓出所有 `檔名.cpp:行號` / `檔名.cpp:起-迄` 形式的註解引用，正則 `(?<fn>[A-Za-z0-9_]+\.(?:cpp|h)):\s*(?<rest>\d+(-\d+)?(,\d+(-\d+)?)*)`，逗號串（`Skill.cpp:486,517`）拆成多條。
2. 每條比對三件事：**檔案存在嗎** / **行號在範圍內嗎** / **那一行在做註解說的事嗎**。
3. 去重後得到 **259 條唯一引用**（400 個原始書寫點，因為同一個引用會在多個檔案重複出現）。
4. 分類總數由腳本驗證過：**228 + 16 + 15 + 0 = 259，未分類 0**。

**編碼**：`clean-client/src` 有 8 檔不是 UTF-8，其中只有 `SkillId.h` 被引用；其餘 590 檔為合法 UTF-8（內含少數 GBK 殘留的中文註解，但不影響 ASCII 程式碼行的比對）。

**範圍限制（重要）**：本報告第 1–5 節只涵蓋 `.cpp`/`.h` 引用。另有 **273 處 `.cs`/`.js` 引用**屬於不同族群，單獨放在第 6 節——因為它們在 `clean-client` 裡一個都不存在，混進主統計會扭曲總管要的數字。

---

## 1. 結論總表

| 類別 | 條數 | 佔比 |
|---|---:|---:|
| **正確** | **228** | 88.0% |
| **行號偏移** | **16** | 6.2% |
| **講錯** | **15** | 5.8% |
| **檔案不存在** | **0** | 0.0% |
| **合計** | **259** | 100% |

一句話：**檔名全部是真的，沒有一條引用到不存在的 C++ 檔**。問題出在行號與內容的對應，不是路徑。

---

## 2. 正確（228 條）

以下為完整清單。每個 `檔名` 都已確認存在於 `clean-client\src`，括號內為相對於 `clean-client\src` 的路徑與實際行數。

| 檔案 | 路徑 | 行數 | 已驗證的引用行 |
|---|---|---:|---|
| `Afterimage.cpp` | `Character\Look\Afterimage.cpp` | 84 | 39 |
| `Animation.cpp` | `Graphics\Animation.cpp` | 432 | 64-67, 166-193, 169-176, 177-190, 283-333 |
| `Attack.h` | `Gameplay\Combat\Attack.h` | 177 | 167-176, 172 |
| `BodyDrawInfo.cpp` | `Character\Look\BodyDrawInfo.cpp` | 248 | 56-61 |
| `Char.cpp` | `Character\Char.cpp` | 1210 | 95, 160, 240, 449-452, 607-612, 615-622, 645, 653-658, 657, 950 |
| `CharLook.cpp` | `Character\Look\CharLook.cpp` | 897 | 161-204, 175-179, 594, 648, 682-696, 706 |
| `CharStats.cpp` | `Character\Stats\CharStats.cpp` | 595 | 57, 59, 73, 101-103, 115-119, 118, 170-176, 186-192, 371, 391-415, 435, 468 |
| `ChatBalloon.cpp` | `IO\Components\Display\ChatBalloon.cpp` | 264 | 61-82, 71-77, 100-110 |
| `Combat.cpp` | `Gameplay\Combat\Combat.cpp` | 2190 | 660, 662-679, 1044, 1226-1272, 1730, 1794 |
| `DamageNumber.cpp` | `Gameplay\Combat\DamageNumber.cpp` | 118 | 61 |
| `DrawArgument.h` | `Graphics\DrawArgument.h` | 168 | 40, 131-157, 143 |
| `EffectLayer.cpp` | `Graphics\EffectLayer.cpp` | 92 | 22-42, 24, 35, 68-71, 70 |
| `EffectLayer.h` | `Graphics\EffectLayer.h` | 80 | 59-61 |
| `ElementDamage.h` | `Gameplay\Combat\ElementDamage.h` | 59 | 9-15 |
| `EquipData.cpp` | `Data\EquipData.cpp` | 192 | 43-57 |
| `EquipQuality.cpp` | `Character\Inventory\EquipQuality.cpp` | 49 | 34 |
| `EquipSlot.h` | `Character\Look\EquipSlot.h` | 81 | 27-60 |
| `EquipTooltip.cpp` | `IO\Components\Tooltips\EquipTooltip.cpp` | 980 | 356, 407, 593 |
| `Inventory.cpp` | `Character\Inventory\Inventory.cpp` | 497 | 47-48 |
| `ItemData.cpp` | `Data\ItemData.cpp` | 322 | 124-151 |
| `ItemParser.cpp` | `Net\Handlers\Helpers\ItemParser.cpp` | 148 | 87-90 |
| `Keyboard.cpp` | `IO\Input\Keyboard.cpp` | 251 | 78-81 |
| `MapMobs.cpp` | `Gameplay\MapleMap\MapMobs.cpp` | 343 | 245-253, 255-268, 306-312 |
| `Mob.cpp` | `Gameplay\MapleMap\Objects\Mob.cpp` | 2245 | 62, 62-63, 69, 73, 73-74, 79, 81-83, 82, 83, 114-133, 115-124, 142-148, 142-177, 146, 146-147, 151-152, 153, 155, 160, 161, 169-175, 595-598, 597, 671, 967, 1118-1126, 1121, 1121-1125, 1460-1468, 1536-1559, 1540-1553, 1544-1547, 1546, 1546-1548, 1558, 1577-1601, 1590-1601, 1649-1652, 1770, 1770-1790, 1848, 1848-1858, 1850, 1853, 1856, 1875-1884, 2122-2128 |
| `Mob.h` | `Gameplay\MapleMap\Objects\Mob.h` | 514 | 467 |
| `MobAttack.cpp` | `Gameplay\MapleMap\Objects\MobAttack.cpp` | 230 | 50, 50-127, 57-58, 69, 81, 88, 93-94, 119, 147, 193-217, 199-210, 205-209 |
| `MobAttackGeometry.h` | `Gameplay\Combat\MobAttackGeometry.h` | 119 | 30-44, 45-49, 45-51, 56-59, 60-96, 98-117, 104-105, 107-115 |
| `Npc.cpp` | `Gameplay\MapleMap\Objects\Npc.cpp` | 611 | 60, 113-133, 123, 123-124, 127-128, 198-203, 203, 276-284, 457-469, 457-484, 461-467, 478, 478-484 |
| `Npc.h` | `Gameplay\MapleMap\Objects\Npc.h` | 178 | 94-95, 127, 131-132, 132, 136 |
| `PassiveBuffs.cpp` | `Character\Buffs\PassiveBuffs.cpp` | 245 | 98, 114 |
| `Player.cpp` | `Character\Player.cpp` | 2267 | 210-251, 247, 299-360, 309-313, 869-871, 1199-1212, 1214-1218, 1250-1253, 1252, 1429, 1665-1668 |
| `PlayerStates.cpp` | `Character\PlayerStates.cpp` | 501 | 52-63, 96, 112, 150, 174, 219, 364 |
| `Rectangle.h` | `Template\Rectangle.h` | 121 | 30 |
| `RegularAttack.cpp` | `Gameplay\Combat\RegularAttack.cpp` | 94 | 33, 49 |
| `Skill.cpp` | `Gameplay\Combat\Skill.cpp` | 677 | 94-98, 219-226, 394, 433, 441-456, 459-461, 486, 517, 524, 524-525 |
| `SkillData.cpp` | `Data\SkillData.cpp` | 491 | 58, 61-64, 65, 66, 69, 77, 79-83 |
| `SkillHitEffect.cpp` | `Gameplay\Combat\SkillHitEffect.cpp` | 117 | 24, 28, 31-41 |
| `SkillHitEffect.h` | `Gameplay\Combat\SkillHitEffect.h` | 164 | 47, 85-90 |
| `SkillId.h` | `Character\SkillId.h` | 218 | 172 |
| `SkillUseEffect.cpp` | `Gameplay\Combat\SkillUseEffect.cpp` | 268 | 162-201, 168-173, 173, 178-184, 187-199, 197-199, 203-212, 214-267, 224-232, 226-229, 243, 245-258, 254 |
| `SkillUseEffect.h` | `Gameplay\Combat\SkillUseEffect.h` | 172 | 50 |
| `Sprite.cpp` | `Graphics\Sprite.cpp` | 81 | 27-31 |
| `Stage.cpp` | `Gameplay\Stage.cpp` | 1719 | 315-318, 497-508, 809, 809-818 |
| `UIStateGame.cpp` | `IO\UIStates\UIStateGame.cpp` | 1694 | 548-556 |
| `UIStatsInfo.cpp` | `IO\UITypes\Character\UIStatsInfo.cpp` | 618 | 343 |
| `WeaponData.cpp` | `Data\WeaponData.cpp` | 134 | 28, 28-29, 32-35, 52 |

### 抽查內容比對（代表例）

以下四條是這批「正確」裡最關鍵的逐字比對，證明比對方法確實有在讀內容而非只對行號：

**`CombatService.cs:71` — `WeaponData.cpp:52`**
> 註解：`/// <c>WeaponData.cpp:52</c> reads <c>info/afterImage</c> off the weapon image,`

實際 `Data\WeaponData.cpp`：
```
   32|		nl::node src = nl::nx::character["Weapon"]["0" + std::to_string(equipid) + ".img"]["info"];
   ...
   52|		afterimage = src["afterImage"];
```
→ 檔、行、敘述三者全對。

**`PlayerStatus.cs:57` — `CharStats.cpp:468`**
> 註解：`/// <c>CharStats.cpp:468</c> <c>get_range() { return Rectangle&lt;int16_t&gt;(-projectilerange, -5, -75, 25); }</c>`

實際 `Character\Stats\CharStats.cpp`：
```
  468|		return Rectangle<int16_t>(-projectilerange, -5, -75, 25);
```
→ 逐字相符。

**`MapSimSessionStore.cs:719` / `walkview.js:1116` — `WeaponData.cpp:35`**
> 註解：`// (WeaponData.cpp:35 -> CharLook.cpp:648, table :682-696), which only the`

實際 `Data\WeaponData.cpp:35` = `attack = static_cast<uint8_t>(src["attack"]);`
→ `attack` 確實是餵給 `CharLook::getattackstance(attack)` 的索引（第 648 行），表在 682-696。→ **正確**。
（同一行號被 `CombatService.cs:255` 用在另一個講錯的敘述上，見第 4 節 W-15。）

**`MobAnimation.cs:158` — `Mob.cpp:114-133`**
> 註解：`/// The action is chosen the way <c>Mob.cpp:114-133</c> names them:`

實際 `Gameplay\MapleMap\Objects\Mob.cpp`：
```
  117|			animations[Stance::STAND] = anisrc["fly"];
  122|			animations[Stance::STAND] = anisrc["stand"];
  126|		animations[Stance::JUMP] = anisrc["jump"];
  127|		animations[Stance::HIT] = anisrc["hit1"];
  128|		animations[Stance::DIE] = anisrc["die1"];
  131|		animations[Stance::FROZEN] = anisrc["hit1"];
  133|		animations[Stance::STUNNED] = anisrc["stand"];
```
→ 正是「動作名稱由這裡命名」的清單。→ **正確**。

---

## 3. 行號偏移（16 條）

檔案對、事情對，**行號漂了**。其中多數只差 1–5 行，指向的區塊確實涵蓋註解說的內容，只是沒落在最精確的那一行。

### O-01 · `Afterimage.cpp:12-40`
- **引用逐字**（`CombatService.cs:96`）：`/// (Afterimage.cpp:12-40) treats a missing node as "no attack box at all",`
- **實際檔案**：`C:\MUWORK\GAME\MPSH\clean-client\src\Character\Look\Afterimage.cpp`（84 行）
- **該處實際內容**：第 12–17 行是 GNU AGPL 授權樣式開頭（`//	MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE...`），第 28 行才是建構子，第 38–39 行是 fallback，真正決定「節點不存在」的 `range = src;` 在**第 41 行**，落在引用範圍之外。
- **應該是**：`28-41`。

### O-02 · `CharStats.cpp:172`
- **引用逐字**（`PlayerStatus.cs:295-296`）：`/// The attacker's min damage: CharStats.cpp:172,` / `/// <c>max(1, (int)(calc_max * mastery + 0.5))</c>.`
- **實際檔案**：`C:\MUWORK\GAME\MPSH\clean-client\src\Character\Stats\CharStats.cpp`（595 行）
- **該處實際內容**：
```
  170|		if (calc_max > 0.0)
  171|		{
  172|			// Apply damagepercent if present
  173|			if (damagepercent > 0.0f)
  174|				calc_max *= (1.0 + damagepercent);
  175|
  176|			maxdamage = static_cast<int32_t>(calc_max + 0.5);
  177|			mindamage = std::max<int32_t>(1, static_cast<int32_t>(calc_max * mastery + 0.5));
```
→ 第 172 行是註解行，`mindamage =` 在**第 177 行**。區塊對，行號早 5 行。
- **應該是**：`177`。
- 同一個行號也出現在 `walkview.js:1166`（`* CharStats.cpp:186-192, the damage window from :118 and :172.`）與 `DamageRoll.cs:30`（`(CharStats.cpp:118, :172)`），同樣偏移。

### O-03 · `EffectLayer.cpp:66-69`
- **引用逐字**（`MapSimSessionStore.cs:211` / `:246` / `:2381` / `:2433`，共 4 處）：`/// (<c>EffectLayer.cpp:66-69</c>), whose speed is <c>1.0f</c>.`
- **實際檔案**：`C:\MUWORK\GAME\MPSH\clean-client\src\Graphics\EffectLayer.cpp`（92 行）
- **該處實際內容**：
```
   66|	}
   67|
   68|	void EffectLayer::add(const Animation& animation, const DrawArgument& args, int8_t z)
   69|	{
   70|		add(animation, args, z, 1.0f);
   71|	}
```
→ `1.0f` 在**第 70 行**，剛好落在引用範圍外一行。
- **應該是**：`68-71`。
- **佐證**：同一個 overload 在 `walkview.js:4042` 被引成 `EffectLayer.cpp:68-71`，那條**是正確的**。所以本條不是內容錯，是同一件事被寫成兩個行號。

### O-04 · `EffectLayer.cpp:31-44`
- **引用逐字**（`MapSimSessionStore.cs:1964`）：`// \`EffectLayer::update\` (EffectLayer.cpp:31-44) removes an effect when its`
- **實際檔案**：`C:\MUWORK\GAME\MPSH\clean-client\src\Graphics\EffectLayer.cpp`
- **該處實際內容**：
```
   31|	}                      <- drawbelow 結尾
   33|	void EffectLayer::drawabove(...)
   ...
   42|	}                      <- drawabove 結尾
   44|	void EffectLayer::update()
   45|	{
   46|		for (auto& effectlist : effects)
   48|			effectlist.second.remove_if(     <- 真正「移除」的動作
```
→ 範圍 31–44 前 13 行是 `drawabove`，只停在 `update()` 的簽章，真正做 remove_if 的 46–54 在外面。
- **應該是**：`44-61`。

### O-05 · `Mob.cpp:1577-1600`
- **引用逐字**（`DamageRoll.cs:9`）：`/// <c>calculate_maxdamage</c> (Mob.cpp:1577-1600):`
- **實際檔案**：`C:\MUWORK\GAME\MPSH\clean-client\src\Gameplay\MapleMap\Objects\Mob.cpp`（2245 行）
- **該處實際內容**：
```
  1577|	double Mob::calculate_mindamage(...) const     <- 這是 MIN 不是 MAX
  ...
  1588|	}
  1590|	double Mob::calculate_maxdamage(...) const     <- 註解要指的函式
  1600|		return maxdamage < 1.0 ? 1.0 : maxdamage;
```
→ 範圍起點早 13 行，把 `calculate_mindamage` 整段包進來了。
- **應該是**：`1590-1601`。
- 對照：`CombatService.cs:1610` 引的 `Mob.cpp:1577-1601`（敘述是「min 扣 0.6、max 扣 0.5」，需要兩個函式都在範圍內）是**正確**的。

### O-06 · `Mob.cpp:1104-1125`
- **引用逐字**（`MapSimSessionStore.cs:473` + `walkview.js:4133`）：
  - `/// delay (<c>Mob.cpp:1104-1125</c>), so this list is the faithful shape.`
  - `// though it was in the archive the whole time. \`Mob.cpp:1104-1125\` draws one`
- **實際檔案**：`C:\MUWORK\GAME\MPSH\clean-client\src\Gameplay\MapleMap\Objects\Mob.cpp`
- **該處實際內容**：
```
  1104|			for (size_t index = 0; index < icons.size(); ++index)   <- 狀態圖示格線，與球無關
  ...
  1113|				icons[index]->draw(DrawArgument(icon_pos), alpha);
  1117|		attack_effects.drawabove(...)
  1118|		for (const auto& entry : attack_entries)                   <- 真正要指的迴圈
  1121|			if (data.geometry.type != 2 || !data.has_ball || entry.elapsed < entry.delay) continue;
  1125|			data.ball.draw(DrawArgument(point, entry.flipped), alpha);
```
→ 前 14 行（1104–1117）是**狀態效果圖示**的排版，跟註解說的「每個 attack entry 畫一顆球」無關。
- **應該是**：`1118-1126`。
- **佐證**：`walkview.js:4106` 引的 `Mob.cpp:1118-1126` 正是這個範圍，那條**是正確的**。

### O-07 · `Mob.cpp:1866-1870`
- **引用逐字**（`MobAnimation.cs:183`）：`// (Mob.cpp:1866-1870, apply_death) and only afterwards free the sprite`
- **實際檔案**：`C:\MUWORK\GAME\MPSH\clean-client\src\Gameplay\MapleMap\Objects\Mob.cpp`
- **該處實際內容**：
```
  1861|	void Mob::apply_death()
  1862|	{
  1863|		active_attack = -1;
  1864|		attack_entries.clear();
  1865|		set_stance(Stance::DIE);
  1866|		diesound.play();
  1867|		dying = true;
  1868|	}
  1869|
  1870|	bool Mob::is_alive() const
```
→ 引用範圍少了函式簽章所在的 1861–1865，卻多含了下一個函式的開頭 1870。
- **應該是**：`1861-1868`。

### O-08 · `Mob.cpp:1544`
- **引用逐字**（`SkillCastService.cs:1635`）：``// `src` is the node Effect was built from. Absent is 0, which Mob.cpp:1544``
- **實際檔案**：`C:\MUWORK\GAME\MPSH\clean-client\src\Gameplay\MapleMap\Objects\Mob.cpp`
- **該處實際內容**：
```
  1543|		switch (pos)
  1544|		{                      <- 只有一個左大括號
  1545|		case 0:
  1546|			shift = get_head_position(Point<int16_t>());
```
→ `case 0:` 在 **1545**，1544 只是 switch 的開括號。
- **應該是**：`1545`。

### O-09 · `SkillHitEffect.h:37-40`
- **引用逐字**（`SkillCastService.cs:113`）：`/// <c>SkillHitEffect.h:37-40</c> reads <c>pos</c> and <c>z</c> off the EFFECT node`
- **實際檔案**：`C:\MUWORK\GAME\MPSH\clean-client\src\Gameplay\Combat\SkillHitEffect.h`（164 行）
- **該處實際內容**：
```
   37|		class Effect
   38|		{
   39|		public:
   40|			Effect(nl::node src)
   41|			{
   42|				animation = src;
   43|				pos = src["pos"];
   44|				z = src["z"];
```
→ 範圍停在建構子簽章，兩個 `pos` / `z` 讀取在 43–44。
- **應該是**：`40-45`。

### O-10 · `SkillHitEffect.h:39-40`
- **引用逐字**（`SkillCastService.cs:1749`）：`/// <c>SkillHitEffect.h:39-40</c> assigns both <c>pos</c> and <c>z</c> from an <c>nl::node</c> into an <c>int8_t</c>.`
- **實際內容**：同上，43–44 才是指派處。
- **應該是**：`40-45`（或 43-44）。

### O-11 · `SkillUseEffect.cpp:255`
- **引用逐字**（`MapSimSessionStore.cs:236`）：`/// (<c>SkillUseEffect.cpp:255</c>), which is`
- **引用逐字**（`walkview.js:3423` / `:3433`）：`// A tile is z = 4 on both of its paths (SkillUseEffect.cpp:209,255), so it`
- **實際檔案**：`C:\MUWORK\GAME\MPSH\clean-client\src\Gameplay\Combat\SkillUseEffect.cpp`（268 行）
- **該處實際內容**：
```
  255|                                   <- 空白行
  256|				Point<int16_t> rel_pos(x - user_pos.x(), (ground_y + jitter_y) - user_pos.y());
  257|				target.show_attack_effect_at(tile_animations[anim_idx], 4, rel_pos);
```
→ `z` 的字面值 `4` 在**第 257 行**。
- **應該是**：`257`。

### O-12 · `SkillUseEffect.cpp:209`
- **引用逐字**（`MapSimSessionStore.cs:243` / `:297` / `walkview.js:3433`）：`/// (<c>SkillUseEffect.cpp:209</c>). <c>pos == 1</c> leaves <c>shift</c> at`
- **該處實際內容**：
```
  208|		// Inferno's tile belongs to the hit target's ground, not the caster.
  209|		// Keep the tile canvas origin; pos=1 is the mob's foot anchor.
  210|		const size_t variant = static_cast<size_t>(rand()) % tile_animations.size();
  211|		target.show_effect(tile_animations[variant], 1, 4, false);
```
→ 第 209 行確實在講 `pos=1`（註解列），但註解宣稱的「z 是字面值 4」在 **211 行**。
- **應該是**：`211`（若指 `pos=1` 的說明則 209 可保留）。

### O-13 · `SkillUseEffect.cpp:253`
- **引用逐字**（`MapSimSessionStore.cs:266`）：`/// <c>SkillUseEffect.cpp:253</c>, or <c>(0,0)</c> for the fallback at`
- **該處實際內容**：
```
  253|				int16_t jitter_y = static_cast<int16_t>((rand() % 5) - 2);
  254|				size_t anim_idx = static_cast<size_t>(rand()) % tile_animations.size();
  255|
  256|				Point<int16_t> rel_pos(x - user_pos.x(), (ground_y + jitter_y) - user_pos.y());
```
→ 註解描述的 `rel_pos` 算式在 **256 行**。
- **應該是**：`256`。
- 對照：同段落的 fallback `(0,0)` 在 **265 行**；該註解寫的 `:262` 指到 fallback 的說明註解列，算可接受。

### O-14 · `SkillUseEffect.cpp:224`
- **引用逐字**（`SkillCastService.cs:650`）：`/// SkillUseEffect.cpp:224   if (tile_skill_id == 5121001) { re-read lt/rb; mirror when facing right; }`
- **該處實際內容**：
```
  222|		if (tile_skill_id == 5121001)
  223|		{
  224|			// Dragon Strike's ground effects must follow this cast's level
```
→ `if` 在 **222 行**。
- **應該是**：`222`。
- 對照：`SkillCastService.cs:448` 引的 `SkillUseEffect.cpp:224-232`（涵蓋 222 起的整塊）是**正確**的。

### O-15 · `SkillUseEffect.cpp:198`
- **引用逐字**（`SkillCastService.cs:665`）：`/// <c>SkillUseEffect.cpp:198</c> ??<c>affected_area =`
- **該處實際內容**：
```
  197|		else
  198|		{
  199|			affected_area = Rectangle<int16_t>(-400, 400, -350, 250);
```
→ 指派在 **199 行**。
- **應該是**：`199`。

### O-16 · `UIRevive.cpp:143-158` — **唯一一條超出檔案長度**
- **引用逐字**（`PlayerStatus.cs:385`）：`/// buttons (<c>UIRevive.cpp:143-158</c>) or the free wheel`
- **實際檔案**：`C:\MUWORK\GAME\MPSH\clean-client\src\IO\UITypes\System\UIRevive.cpp`（**155 行**）
- **該處實際內容**：
```
  143|	void UIRevive::revive(bool use_wheel)
  144|	{
  ...
  151|		ChangeMapPacket(true, 0, "", use_wheel).dispatch();
  153|		deactivate();
  154|	}
  155|}                        <- 檔案結束
```
→ 引用到 158 行，**檔案只有 155 行，超出 3 行**。
- **應該是**：`143-154`。

---

## 4. 講錯（15 條）

引用落在一段**內容不符**的程式碼上。這批是本次稽核最該修的。

### W-01 · `UIStateGame.cpp:794` → 應該是 `Stage.cpp:794`（**整個檔案搞錯**）
- **引用逐字**（`MapSimSessionStore.cs:2842` + `walkview.js:1366`，共 2 處）：
  - `/// and <c>UIStateGame.cpp:794</c>, <c>player.is_invincible()</c> — the client has`
  - `// \`player.is_invincible()\` — UIStateGame.cpp:794 for the damage path,`
- **實際檔案**：`C:\MUWORK\GAME\MPSH\clean-client\src\IO\UIStates\UIStateGame.cpp`（1694 行）
- **該處實際內容**：
```
  790|		case KeyAction::Id::CASHSHOP:
  791|		{
  792|			if (Stage::get().has_field_limit(MapInfo::FieldLimit::MIGRATE))
  793|				UI::get().emplace<UIOk>(u8"...", [](bool) {});
  794|			else
  795|				EnterCashShopPacket().dispatch();
```
→ 第 794 行是**進入現金商店按鍵處理器的一個 `else`**，跟無敵無關。
- **正確位置**：`C:\MUWORK\GAME\MPSH\clean-client\src\Gameplay\Stage.cpp`：
```
  794|		bool immune = attack.type == Attack::Type::MAGIC ? player.Char::is_invincible() || ... : player.is_invincible();
  799|		if (!player.Char::is_invincible() && !player.is_gm_hidden() &&
  806|		if (player.is_invincible())
```
→ **`Stage.cpp:794` 才是對的，檔名寫成了 `UIStateGame.cpp`。**

### W-02 · `Player.cpp:911` → 應該是 `Char.cpp:911`（**整個檔案搞錯**）
- **引用逐字**（`MapSimSessionStore.cs:1593`）：`//   Player.cpp:911     Char::attack(action) sets \`attacking = true\``
- **實際檔案**：`C:\MUWORK\GAME\MPSH\clean-client\src\Character\Player.cpp`（2267 行）
- **該處實際內容**：
```
  910|	void Player::send_pet_movements()
  911|	{                       <- 只有一個左大括號
  912|		// Only our own pets are ours to sync; ...
```
→ 第 911 行是寵物同步函式的開頭，與 `attacking` 無關。
- **正確位置**：`C:\MUWORK\GAME\MPSH\clean-client\src\Character\Char.cpp:911` = `attacking = true;`（另有 923、935 兩處同樣的賦值）。
- 註解自己都寫了 `Char::attack(action)`——**行號是對的，檔名寫錯了**。

### W-03 · `MobAttack.cpp:163` → 應該是 `Mob.cpp:163-165`（**整個檔案搞錯**）
- **引用逐字**（`CombatService.cs:1200` / `:1454` / `:1486` / `:1516` + `MapSimSessionStore.cs:452`，共 5 處）：
  - `/// when it has none — <c>MobAttack.cpp:163</c>, <c>attack.ball =`
  - `// MobAttack.cpp:163 -- \`attack.ball = data["ball"]\``
  - `/// has a frame 0 — <c>MobAttack.cpp:163</c>, <c>bool(data["ball"]["0"])</c>.`
- **實際檔案**：`C:\MUWORK\GAME\MPSH\clean-client\src\Gameplay\MapleMap\Objects\MobAttack.cpp`（230 行）
- **該處實際內容**：
```
  163|				if ((target_info & (1 << cell)) == 0) continue;
```
→ 這是**目標格位遮罩**的過濾，跟 `ball` 節點無關。
- **正確位置**：`Mob.cpp`：
```
  162|			attack.has_effect = bool(data["effect"]["0"]);
  163|			attack.has_ball = bool(data["ball"]["0"]);
  165|			attack.ball = data["ball"];
```
→ 這是本次最嚴重的一條：**同一個行號 163 被 5 處註解當成 `ball` 的來源，但它在 `MobAttack.cpp` 裡講的是別件事**。

### W-04 · `MobAttack.cpp:141`
- **引用逐字**（`MapSimSessionStore.cs:1694`）：``// `MobAttack.cpp:141` retires it when its duration runs out.``
- **該處實際內容**：
```
  141|		AttackEntry entry;
```
→ 這是 `begin_attack` 裡的**entry 建構宣告**，不是「 retire」。
- **正確位置**：退場判定在 `MobAttack.cpp:199-210`（`if (entry->elapsed < entry->delay) { ++entry; continue; }` 與 `expired = t >= 1.0;`），該範圍已被 `MapSimSessionStore.cs:431` 正確引用為 `MobAttack.cpp:199-210`。

### W-05 · `Mob.cpp:1127`
- **引用逐字**（`MapSimSessionStore.cs:2316`）：``// retires it when the animation ends (Mob.cpp:1127, the layer's own``
- **該處實際內容**：
```
  1127|		effects.drawabove(absp, alpha);
```
→ 這是 `Mob::draw` 結尾的**繪製**呼叫，不是 retire。
- **正確位置**：退場在 `EffectLayer.cpp:44-61` 的 `EffectLayer::update()`（`effectlist.second.remove_if(...)`），那個函式目前被 `MapSimSessionStore.cs:1964` 引成偏移的 `31-44`（見 O-04）。

### W-06 · `Mob.cpp:972`
- **引用逐字**（`walkview.js:3897`）：`//   Mob.cpp:972  effects.drawabove(absp)   AFTER it`
- **該處實際內容**：
```
  963|		if (spawndelay > 0)
  964|		{
  967|			effects.drawbelow(absp, alpha);
  968|			effects.drawabove(absp, alpha);
  969|			return;
  970|		}
  971|
  972|		effects.drawbelow(absp, alpha);
```
→ 第 972 行是 **drawbelow**，不是 drawabove。怪物本體的 drawabove 在 **1127 行**。
- **對照**：同一段落的 `Mob.cpp:967`（`walkview.js:3896`）引的是 drawbelow，**那條是正確的**——所以 3896/3897 兩行的其中一行寫反了。

### W-07 · `Mob.cpp:177-186`
- **引用逐字**（`DamageRoll.cs:87`）：``// Mob.cpp:177-186: leveldelta = max(0, mobLevel - playerLevel). A mob BELOW``
- **該處實際內容**：
```
  177|			attacks.emplace(index, std::move(attack));     <- 攻擊資料迴圈結尾
  178|		}
  180|		for (nl::node entry : info["skill"])
  181|		{
  183|			skill.id = static_cast<uint8_t>(entry["skill"].get_integer());
  184|			skill.level = static_cast<uint8_t>(entry["level"].get_integer());
  185|			skill.action = static_cast<uint8_t>(entry["action"].get_integer());
```
→ 這是**怪物技能表建構**，沒有 leveldelta。
- **正確位置**：`Mob.cpp:1649-1652`（已被 `DamageRoll.cs:89` 正確引用）：
```
  1649|		int16_t leveldelta = level - attack.playerlevel;
  1651|		if (leveldelta < 0)
  1652|			leveldelta = 0;
```
→ 同一個檔案裡，`:89` 引對了、`:87` 引錯了。

### W-08 · `Mob.cpp:1776`
- **引用逐字**（`DamageRoll.cs:100`）：``// Mob.cpp:1776 picks next_real(min, max); :1790 truncates to int32.``
- **該處實際內容**：
```
  1772|		double damage = randomizer.next_real(mindamage, maxdamage);   <- next_real 在這
  1775|		if (iscritical)
  1776|		{                                                        <- 1776 只是左大括號
  ...
  1789|		else if (damage > DAMAGECAP)
  1790|			damage = DAMAGECAP;                                    <- 1790 是上限截斷
  1792|		auto intdamage = static_cast<int32_t>(damage);             <- int32 轉型在這
```
→ 兩個子敘述都不準：`next_real` 在 **1772**（差 4 行）；`:1790` 是 `DAMAGECAP` 上限，不是 int32 截斷（轉型在 **1792**）。

### W-09 · `Skill.cpp:221`
- **引用逐字**（`SkillCastService.cs:648`）：`/// Skill.cpp:221             if (tileeffect && skillid != 3111003) tileeffect->apply(user);`
- **引用逐字**（`MapSimSessionStore.cs:1942`）：``// Skill.cpp:221 — the caster-side half is exactly "every skill with a tile``
- **該處實際內容**：
```
  219|	void Skill::apply_useeffects(Char& user) const
  220|	{
  221|		if (skillid == 5101007)
  222|			return; // CUser::SetMorphed owns the barrel entry effect.
  223|		if (tileeffect && skillid != 3111003)
  224|		{
  225|			tileeffect->apply(user);
```
→ 第 221 行是 **5101007 變形技能的提早返回**，跟 tile 無關；註解引的那個條件在 **223 行**。
- **對照**：`Skill.cpp:219-226`（`MapSimSessionStore.cs:1919` / `SkillCastService.cs:431`）涵蓋整塊，**是正確的**。

### W-10 · `Rectangle.h:82-87`
- **引用逐字**（`CombatService.cs:2017`）：`/// The client's own test. <c>Rectangle::overlaps</c> (Rectangle.h:82-87)`
- **實際檔案**：`C:\MUWORK\GAME\MPSH\clean-client\src\Template\Rectangle.h`（121 行）
- **該處實際內容**：
```
   81|		constexpr bool straight() const
   82|		{
   83|			return left_top == right_bottom;
   84|		}
   86|		constexpr bool empty() const
   87|		{
```
→ 這是 `straight()` 與 `empty()`，**不是 `overlaps`**。
- **正確位置**：`74-79`：
```
   74|		constexpr bool overlaps(const Rectangle<T>& ar) const
   75|		{
   76|			return
   77|				get_horizontal().overlaps(Range<T>(ar.left(), ar.right())) &&
   78|				get_vertical().overlaps(Range<T>(ar.top(), ar.bottom()));
   79|		}
```

### W-11 · `DrawArgument.h:37`
- **引用逐字**（`walkview.js:4060`）：`//   DrawArgument.h:37       DrawArgument(position, flip)`
- **該處實際內容**：
```
   34|		constexpr DrawArgument(Point<int16_t> position, bool flip) : DrawArgument(position, flip, 1.0f) {}    <- 註解要指的
   ...
   37|		constexpr DrawArgument(Point<int16_t> position, Color color) : DrawArgument(position, position, Point<int16_t>(0, 0), 1.0f, 1.0f, color, 0.0f) {}   <- 實際指向這個
```
→ 第 37 行是 **`(position, Color)`** 多載，不是 `(position, flip)`。
- **應該是**：`34`。

### W-12 · `SkillHitEffect.h:94-101`
- **引用逐字**（`MapSimSessionStore.cs:2468`）：``/// <item><c>ByHitHitEffect</c> (<c>SkillHitEffect.h:94-101</c>) —``
- **該處實際內容**：
```
   89|	// Arrow Bomb chooses hit/0 for the first target, hit/1 for splash targets.
   90|	class FirstTargetHitEffect : public SkillHitEffect
   93|		explicit FirstTargetHitEffect(nl::node src)
   94|			: effects(src["hit"]["0"], src["hit"]["1"]) {}
   95|		void apply(const AttackUser& user, Mob& target) const override
   97|			effects[user.splash_hit].apply(target, user.flip);
  101|	};
  103|	// Double Uppercut selects hit/0 and hit/1 by damage line, not weapon.
  104|	class ByHitHitEffect : public SkillHitEffect
```
→ 94–101 是 **`FirstTargetHitEffect`**（Arrow Bomb），`ByHitHitEffect`（Double Uppercut）在 **104-115**。
- **應該是**：`104-115`。
- **對照**：`SkillHitEffect.h:85-90`（`MapSimSessionStore.cs:2465` 引 `FirstTargetHitEffect`）**是正確的**，兩條剛好鄰接但指错了對象。

### W-13 · `SkillHitEffect.h:88`
- **引用逐字**（`MapSimSessionStore.cs:152` / `:2299`，共 2 處）：
  - `/// (<c>SkillHitEffect.cpp:28</c>, <c>SkillHitEffect.h:88</c>,`
  - `// (SkillHitEffect.cpp:28, SkillHitEffect.h:88), which is the`
- **該處實際內容**：
```
   87|	};
   88|                                   <- 空白行，什麼都沒有
   89|	// Arrow Bomb chooses hit/0 for the first target, hit/1 for splash targets.
```
→ 指向一個**空白行**。
- **正確位置**：同一個「呼叫端傳 `user.flip`」的證據在 **97**（`effects[user.splash_hit].apply(target, user.flip);`）與 **111**。

### W-14 · `SkillHitEffect.h:39`
- **引用逐字**（`SkillCastService.cs:1634`）：``// `pos` is the GROUP's, per SkillHitEffect.h:39 ??`pos = src["pos"]`, where``
- **該處實際內容**：
```
   39|		public:                            <- 只有存取修飾子
   40|			Effect(nl::node src)
   43|				pos = src["pos"];
```
→ 註解逐字引用的 `pos = src["pos"]` 在 **43 行**，39 行是 `public:`。
- **應該是**：`43`。

### W-15 · `WeaponData.cpp:35`
- **引用逐字**（`CombatService.cs:255`）：``/// Afterimage image name this class otherwise deals in. <c>WeaponData.cpp:35</c>``
- **該處實際內容**：
```
   34|		attackspeed = static_cast<uint8_t>(src["attackSpeed"]);
   35|		attack = static_cast<uint8_t>(src["attack"]);
   ...
   52|		afterimage = src["afterImage"];
```
→ 第 35 行是 **`attack`（攻擊型別）**，不是 afterimage 名稱。
- **正確位置**：`WeaponData.cpp:52`（`afterimage = src["afterImage"];`）與 `WeaponData.cpp:125`（`get_afterimage()`）。
- **註**：同一行號 35 被 `MapSimSessionStore.cs:719` / `walkview.js:1116` 用作「索引 stance 表」的引用，那是**正確的**（見第 2 節）。也就是同一行號承載了兩種敘述，其中一種指錯了目標。

---

## 5. 檔案不存在（0 條）

**C++ 引用族群裡沒有任何一條指向不存在的檔案。**

- 共引用 **47 個**不同的 `.cpp` / `.h`，全部在 `clean-client\src` 找到。
- 唯一一個行號越界的（`UIRevive.cpp:143-158`）檔案存在、長度已知，只是範圍超出，已歸入第 3 節 O-16。
- 需留意：`MobAttack.h` **不存在**（`MobAttack.cpp` 存在），但沒有任何一條引用它，所以不構成缺口。

---

## 6. 附帶發現：非 C++ 引用族群（273 處，獨立於上面的統計）

這些引用**不是** `Foo.cpp:NN` 形式，不在任務指定的抓取範圍內，但它們與「引用本身需要稽核」直接相關，所以單獨列出。

**關鍵事實**：`clean-client\src` 底下 **0 個 `.cs` 檔**（598 檔全是 `.cpp` / `.h`）。因此任何 `.cs` 引用都不可能指向權威客戶端。

| 分類 | 目標數 | 說明 |
|---|---:|---|
| MapleBench 內部交叉引用 | 16 | 合法。`MapSimSessionStore.cs`、`CombatService.cs`、`walkview.js`、`livewalk.js`、`avatar.js`、`mobsprite.js`、`LookService.cs`、`SimulatorEndpoints.cs`、`EquipSlotTable.cs`、`AnimationService.cs`、`CharacterPhysics.cs`、`FootholdPhysics.cs`、`MobPatrol.cs`、`media.js`、`pixeleditor.js`、`ui.js` — 都在本 repo 內找得到 |
| 指向 MapleNecrocer（次要參考）的 C# | 24 | **`Mob.cs`、`Npc.cs`、`Skill.cs`、`MapleCharacter.cs`、`Character.cs`、`SpriteEngine.cs`、`MobForm.cs`、`SkillForm.cs`、`AvatarForm.cs`、`Extension.cs`、`AfterImage.cs`、`Familiar.cs`、`Morph.cs`、`MapleMap.cs`、`MapleCharacterEx.cs`、`CharaProp.cs`、`Gear.cs`、`LadderRopes.cs`、`MapObj.cs`、`MapParticle.cs`、`MapPortal.cs`、`MapTile.cs`、`MobTooltipRenderer.cs`、`Minimap.cs`** — 在 `clean-client` 一個都不存在 |
| 完全解析不到 | 3 | **`Designer.cs`、`Foetholds.cs`、`view.js`** — `clean-client` 沒有、MapleBench 沒有、MapleNecrocer 也沒有 |

### 6.1 任務書給的次要參考路徑是錯的

任務書寫：
> 次要參考（不可當權威）：`C:\MUWORK\GAME\GAME_necrocer_src\MapleNecrocer`

實測該路徑**不存在**（`Test-Path` = False）。實際位置是：

> `C:\MUWORK\GAME\_necrocer_src\MapleNecrocer`（**單底線**，193 個 `.cs`）

上面 24 個 `.cs` 目標中的 23 個在正確路徑下找得到（`Minimap.cs` 實際檔名是 `MiniMap.cs`，大小寫不符）。

### 6.2 這正是專案曾經踩過的坑

任務書提到專案曾因「照抄 MapleNecrocer 的 `Skill.cs` 而不是參考的 `Combat.cpp`」而整段重寫。**這個族群至今仍在**：第 6 節表格第二列的 24 個 `.cs` 目標，全部是 MapleNecrocer 的 C# 檔名，且 `MapleNecrocer` 依任務書定義**不是權威**。

以 `MapSimSessionStore.cs` 為例，`.cs` 引用密度遠高於 `.cpp`：

| 檔案 | `.cs` 引用數 | `.cpp` 引用數 |
|---|---:|---:|
| `MapSimSessionStore.cs` | 61 | 79 |
| `SkillCastService.cs` | 26 | 41 |
| `walkview.js` | 96 | 44 |
| `CombatService.cs` | 19 | 62 |
| `PlayerStatus.cs` | 10 | 14 |

**這一節沒有做內容比對**——只比對了「檔案存不存在」。要確認這 273 處的敘述是否正確，需要對 MapleNecrocer 做同樣的行號+內容核對，那是另一份工作量。**本報告不對它們的正確性下結論。**

---

## 7. 沒有驗到的部分（明講）

1. **228 條「正確」只有完整清單 + 11 條抽查的逐字比對**，其餘 217 條是逐條讀過該行/該區段後判定一致，但報告正文沒有逐一附上摘錄。若總管要全量摘錄，需要再跑一輪輸出。
2. **沒有比對註解敘述的「語意正確性」**，只驗「該處在做註解說的事」。也就是說：若 C++ 本身的行為被 C# 誤解（而非引用指錯），本次稽核抓不到。`Mob.cpp:1794`（註解寫 `get_attackdelay(0)`、程式是 `get_attackdelay(index)`）我判為正確，因為指標對、敘述是簡化，但嚴格說字面不符。
3. **第 6 節的 273 處 `.cs` / `.js` 引用沒有做內容比對**（見上）。
4. **沒有確認這些行號是否對應到「權威」的那一份修訂**。`clean-client\src` 內含明顯的當地修改（例如 `SkillUseEffect.cpp:208-209` 的 `// Inferno's tile belongs to the hit target's ground`、`Combat.cpp` 第 1239 行的中文註解），這些是 clean-client 自己的版本差異，不是上游。審核基準是磁碟上這一份。
5. **編碼**：8 個非 UTF-8 檔中只有 `SkillId.h` 被引用，已用位元組層讀取確認第 172 行 = `GENESIS = 2321008,`。其餘中文註解的亂碼不影響 ASCII 程式碼行的比對，但**若某條引用要指向中文註解行本身，本次未逐字比對**。
6. **沒有動任何檔案**，沒有建置，沒有部署，沒有碰伺服器（`127.0.0.1:5591` 未被呼叫）。第 6 節的次要參考路徑差異是我在唯讀掃描中發現的，未做任何修正。