# Mob Special-Attack Port Spec — `clean-client/src` (C++ v83 client)

Source tree: `C:\MUWORK\GAME\MPSH\clean-client\src\`
Read-only investigation. No files in the target tree were modified.

---

## 0. Encoding caveat (read this first)

`Mob.cpp`, `Mob.h`, `Summon.cpp`, `EffectLayer.h` are byte-valid **UTF-8**, but their Chinese
comments are **already destroyed in the source file** — they contain literal U+FFFD replacement
characters, not a mis-decoded byte stream. Proof: byte scan shows `invalid=0` for UTF-8 on all four
files, yet round-tripping UTF-8 → CP936 yields `锟斤拷` (the classic double-mojibake marker),
proving the loss is baked in and **irrecoverable**.

| File | Chinese comments |
|---|---|
| `Gameplay/MapleMap/Objects/Mob.cpp` | destroyed (e.g. lines 130, 309, 447, 983, 1007, 1015, 1022, 1036, 511) |
| `Gameplay/MapleMap/Objects/Mob.h` | destroyed (lines 58–126, 187–192, 504–512) |
| `Graphics/EffectLayer.h` | destroyed (line 42) |
| `Gameplay/MapleMap/Objects/Summon.cpp` | **intact** (UTF-8, renders correctly) |
| `MobAttack.cpp`, `MobAttackGeometry.h`, `Bullet.cpp`, `Attack.h` | no Chinese, fully readable |

Meaning was still inferable where it mattered, e.g. `Mob.cpp:447` decodes to
"状态下不能移动" (cannot move in this state). **All conclusions below rest on code, never on comments.**

---

## 1. Complete `info/` + `attackN/info/` node inventory

### 1a. `Mob.img/<id>.img` → `info/`

Constructor: `Mob.cpp:36`–`318`. `info = src["info"]` at `Mob.cpp:41`.

| archive node | ref | fills | notes |
|---|---|---|---|
| `info/link` | `Mob.cpp:50-57` | `anisrc` | **MISSING from the known list.** Animation source override: reads `link.get_integer()`, loads that mob's `.img`, and uses it for stand/move/jump/hit1/die1/attack/skill animations **if** it has `stand` or `fly`. `info/*` stats are still read from `src`, never from the link. |
| `info/level` | `Mob.cpp:59` | `level` | |
| `info/PADamage` | `Mob.cpp:60` | `watk` | |
| `info/MADamage` | `Mob.cpp:61` | `matk` | |
| `info/PDDamage` | `Mob.cpp:62` | `wdef` | |
| `info/MDDamage` | `Mob.cpp:63` | `mdef` | |
| `info/acc` | `Mob.cpp:64` | `accuracy` | **MISSING from the known list.** |
| `info/eva` | `Mob.cpp:65` | `avoid` | **MISSING from the known list.** Stored; consumed by `calculate_hitchance` region. |
| `info/pushed` | `Mob.cpp:66` | `knockback` | Threshold: a hit must be `>= knockback` to stagger the mob. |
| `info/speed` | `Mob.cpp:67` | `speed` | then shoe-style `+=100; *= 0.01` at `Mob.cpp:237-238` |
| `info/flySpeed` | `Mob.cpp:68` | `flyspeed` | then `+=100; *= 0.01` at `Mob.cpp:240-241` |
| `info/bodyAttack` | `Mob.cpp:69` | `touchdamage` | |
| `info/undead` | `Mob.cpp:70` | `undead` | |
| `info/cannotEvade` | `Mob.cpp:71` | `cannot_evade` | **MISSING from the known list.** Passed into every `MobAttack`. |
| `info/boss` | `Mob.cpp:72` | `boss` | |
| `info/noFlip` | `Mob.cpp:73` | `noflip` | **MISSING from the known list.** Suppresses facing flips throughout. |
| `info/notAttack` | `Mob.cpp:74` | `notattack` | |
| `info/invincible` | `Mob.cpp:75` | `invincible` | |
| `info/onlyNormalAttack` | `Mob.cpp:76` | `only_normal_attack` | |
| `info/damagedBySelectedSkill` | `Mob.cpp:77-78` | `damaged_by_selected_skills` | **MISSING from the known list.** Iterated node; every child integer pushed. |
| `info/firstAttack` | `Mob.cpp:79` | `first_attack` | |
| `info/maxMP` | `Mob.cpp:80` | `mp` | Node is **`maxMP`**, not `mp`. Seed value only; server overwrites via `set_move_response`. |
| `info/elemAttr` | `Mob.cpp:85-113` | `elements` map | 2-char pairs: letter = element, digit = `1`IMMUNE/`2`RESISTANT/`3`WEAK. Letters F,I,L,S,H,D,P. |
| `info/skill` | `Mob.cpp:180-234` | `mob_skills` vector | **MISSING from the known list — see §2.** |

### 1b. Nodes the known list claims are read but are **NOT**

Grep-verified absent from `Mob.cpp` entirely:

`dcLeft`, `dcTop`, `dcRight`, `dcBottom`, `hp`, `exp`, `allyMob`, `damagedByMob`,
`changeableMob`, `fixedDamage`, `ignoreDamage`, `fixedBodyAttackDamageR`, `noDebuff`,
`ignoreMoveImpact`, `ignoreMovable`, `category`, `removeAfter`, `hideName`, `MapleTV`,
`talkMouseOnly`, `script`, `shop`, `onlyHittedByCommonAttack`.

Consequences worth porting:
- **No hitbox.** Mob width is derived from animation bounds, half-width fallback 30:
  `Mob.cpp:2166-2172` (pull) and `Mob.cpp:2208-2216` (rush_push):
  ```cpp
  int16_t half_width = 30;
  if (animations.count(stance))
  {
      Rectangle<int16_t> bounds = animations.at(stance).get_bounds();
      if (bounds.width() > 0)
          half_width = bounds.width() / 2;
  }
  ```
- `maxHP` **is** read, but not by the constructor — only inside the Bamboo Rain path,
  `Mob.cpp:1635-1636`:
  ```cpp
  const auto src = nl::nx::mob[string_format::extend_id(id, 7) + ".img"]["info"];
  const int64_t hp = src["maxHP"].get_integer();
  ```
- `canjump` / `canfly` / `canmove` are **derived from animation presence**, not archive flags:
  `Mob.cpp:81-83`
  ```cpp
  canjump = anisrc["jump"].size() > 0;
  canfly = anisrc["fly"].size() > 0;
  canmove = anisrc["move"].size() > 0 || canfly;
  ```

### 1c. Non-`info/` reads in the constructor

| path | ref | fills |
|---|---|---|
| `String/Mob.img/<id>/name` | `Mob.cpp:135` | `name` |
| `Sound/Mob.img/<id>/Damage` | `Mob.cpp:139` | `hitsound` |
| `Sound/Mob.img/<id>/Die` | `Mob.cpp:140` | `diesound` |
| `Sound/Mob.img/<id>/Attack<N>` | `Mob.cpp:168` | `AttackData::sound` |
| `Effect/Summon.img/<spawneffect>` | `Mob.cpp:280-289` | spawn effect; `spawneffect==15` hardcodes `spawndelay = 3000`, else `node["delay"]` |
| `Skill/231.img/skill/2311005/mob` + `mob0` | `Mob.cpp:310,313` | `doom_animation`, `doom_effect` |
| `Skill/MobSkill.img/123/level/1/affected` | `Mob.cpp:312` | `stun_effect` |
| `Skill/211.img/skill/2111004/mob` | `Mob.cpp:314` | `seal_effect` |
| `Skill/MobSkill.img/125/level/1/affected` | `Mob.cpp:316` | `poison_effect` |

Animation roots read from `anisrc`: `stand`, `move`, `jump`, `hit1`, `die1`, `fly`,
`attack1..attack8`, `skill1..skill9` — `Mob.cpp:117-128`, `145`, `190`.

Note `Mob.cpp:131-133`: FROZEN reuses `hit1`, STUNNED reuses `stand` — there is no
`frozen`/`stunned` animation read.

### 1d. `attackN/info/` — the full list

Loop `Mob.cpp:142-178`, `N = 0..7` → `"attack" + std::to_string(N+1)`.

```cpp
std::string action = "attack" + std::to_string(index + 1);
nl::node node = anisrc[action];
nl::node data = src[action]["info"];
if (!data) data = node["info"];
if (!node["0"] || !data) continue;
```

Two consequences: (a) the **animation may come from `link`** while **info always comes from `src`**;
(b) **an attack is silently dropped if its animation or its info is missing** — no log, no fallback.

| archive node | ref | fills |
|---|---|---|
| `attackN` (whole) | `Mob.cpp:150` | `AttackData::animation` |
| `attackN/info/attackAfter` | `Mob.cpp:153` | `delay` (ms, clamped ≥0) |
| `attackN/info/effectAfter` | `Mob.cpp:154` | `effect_delay` (ms, clamped ≥0) |
| `attackN/info/conMP` | `Mob.cpp:155` | `conmp` |
| `attackN/info/magic` | `Mob.cpp:156` | `magic` |
| `attackN/info/knockback` | `Mob.cpp:157` | `strong_knockback` |
| `attackN/info/rush` | `Mob.cpp:158` | `rush` |
| `attackN/info/inactive` | `Mob.cpp:159` | `inactive` — permanently excluded from selection |
| `attackN/info/MADamage` | `Mob.cpp:160` | `power` (magic) |
| `attackN/info/PADamage` | `Mob.cpp:160` | `power` (physical) |
| `attackN/info/bulletSpeed` | `Mob.cpp:161` | `bullet_speed`, default 100, min 1 |
| `attackN/info/effect/0` | `Mob.cpp:162` | `has_effect` |
| `attackN/info/effect` | `Mob.cpp:164` | `AttackData::effect` |
| `attackN/info/ball/0` | `Mob.cpp:163` | `has_ball` |
| `attackN/info/ball` | `Mob.cpp:165` | `AttackData::ball` |
| `attackN/info/areaWarning` | `Mob.cpp:166` | `warning` |
| `attackN/info/hit` | `Mob.cpp:167` | `hit` |
| `attackN/info/type` | `Mob.cpp:169` | `MobAttackGeometry::type` |
| `attackN/info/range` | `Mob.cpp:170-171` | `MobAttackGeometry::range` |
| `attackN/info/range/sp` | `Mob.cpp:172` | `MobAttackGeometry::source` |
| `attackN/info/range/r` | `Mob.cpp:173` | `MobAttackGeometry::radius` |
| `attackN/info/range/start` | `Mob.cpp:174` | `MobAttackGeometry::area_start` |
| `attackN/info/range/areaCount` | `Mob.cpp:175` | `MobAttackGeometry::area_count` |
| `attackN/info/range/attackCount` | `Mob.cpp:176` | `MobAttackGeometry::attack_count` |

**Nothing else is read from `attackN/info`.** No `dcLeft/dcTop/dcRight/dcBottom`, no hit count, no
per-cell damage table, no `skill`-style payload.

---

## 2. `info/skill` — the mob-skill system

**Yes, there is a real mob-skill system, and the SERVER chooses it; the client never picks.**

### 2a. Load — `Mob.cpp:180-234`

```cpp
for (nl::node entry : info["skill"])
{
    MobSkillData skill;
    skill.id = static_cast<uint8_t>(entry["skill"].get_integer());
    skill.level = static_cast<uint8_t>(entry["level"].get_integer());
    skill.action = static_cast<uint8_t>(entry["action"].get_integer());
    if (skill.action < 1 || skill.action > 9)
        skill.action = 1;

    std::string skill_action = "skill" + std::to_string(skill.action);
    nl::node animation = anisrc[skill_action];
    ...
    nl::node definition = nl::nx::skill["MobSkill.img"][std::to_string(skill.id)]["level"][std::to_string(skill.level)];
    skill.mp_cost = std::max<int32_t>(0, definition["mpCon"].get_integer());
    skill.cooldown = std::max<int32_t>(0, definition["interval"].get_integer()) * 1000;
    skill.effect_delay = std::max<int32_t>(0, entry["effectAfter"].get_integer());
    nl::node effect = definition["effect"];
    ...
    nl::node icon = definition["mob"];
    skill.has_icon = bool(icon["0"]) && icon["icon"].get_integer() != 0;
    ...
    if (!skill.has_icon)
    {
        nl::node aura = icon;
        if (!aura["0"])
            aura = definition["mob0"];
        skill.has_aura = bool(aura["0"]);
        ...
        skill.aura_pos = static_cast<uint8_t>(aura["pos"].get_integer());
    }
    skill.duration = std::max(skill.duration, skill.effect_delay);
    if (skill.duration == 0)
        skill.duration = 500;
```

`info/skill` is a **list**; each entry reads `skill`, `level`, `action` (clamped 1..9),
`effectAfter`. Cost/cooldown come from **`Skill/MobSkill.img/<id>/level/<level>/`**
(`mpCon`, `interval` ×1000 → ms), plus `effect`, `mob` (icon if `icon!=0`, else aura),
`mob0` (fallback aura), and `mob/pos`.

### 2b. Choice — the server decides

`Mob.h:149`:
```cpp
// What the server answered the last movement with: the mob's current
// mp, whether it may act, and the skill it should perform next. The
// server picks the skill; this client only has to carry it back on
// the following movement packet.
void set_move_response(int16_t mp, bool may_use_skills, uint8_t skill_id, uint8_t skill_level);
```

Implementation `Mob.cpp:1204-1211` stores `next_skill_id` / `next_skill_level`.
Firing `Mob.cpp:1369-1391`:
```cpp
if (!control || active_attack >= 0 || next_skill_id == 0 || next_skill_level == 0 || stance == Stance::HIT || pulling)
    return false;

auto found = std::find_if(mob_skills.begin(), mob_skills.end(), [&](const MobSkillData& skill)
{
    return skill.id == next_skill_id && skill.level == next_skill_level;
});
...
begin_mob_skill(static_cast<size_t>(std::distance(mob_skills.begin(), found)), facing_left, true);
```

Gates: control, no active attack, non-zero id+level, not HIT, not pulling.
`cooldown_remaining` decrements every tick at `Mob.cpp:1313-1314` and is set at `Mob.cpp:1286` —
but **note it is never consulted as a gate**; it is bookkeeping only.

### 2c. Fire — `begin_mob_skill`, `Mob.cpp:1272-1309`

```cpp
active_mob_skill = static_cast<int>(index);
mob_skill_elapsed = 0;
mob_skill_effect_played = false;
flip = flipped;
set_stance(Stance::STAND);
counter = 0;
phobj.inputx = phobj.inputy = 0;
phobj.hspeed = phobj.vspeed = phobj.vx = 0.0;
skill.cooldown_remaining = skill.cooldown;

if (skill.has_animation)
    skill.animation.reset();
if (send_to_server)
{
    int action = std::clamp<int>(skill.action, 1, 9);
    outgoing_skill = true;
    outgoing_skill_action = static_cast<uint8_t>(42 + (action - 1) * 2 + (flipped ? 0 : 1));
    ...
    mp = std::max<int32_t>(0, mp - skill.mp_cost);
    update_movement();
}
```

Wire encoding `Mob.cpp:1294`: `42 + (action-1)*2 + (flipped ? 0 : 1)` — actions **42..59**.
Non-controlling clients replay it via `receive_skill` (`Mob.cpp:1213-1221`, accepts 42..59)
routed in `MapMobs.cpp:174-176`.

Timing `Mob.cpp:1352-1367`: animation advances, effect fires at the `effectAfter` crossing
(`Mob.cpp:1359-1363`), returns `true` (suppresses `update_attack`) while `mob_skill_elapsed < duration`.

**A mob skill has NO damage payload.** It is animation + effect + aura + icon + mp/cooldown only.
There is no projectile, no hitbox, no damage line anywhere in the skill path.

### 2d. Aura vs icon

`Mob.cpp:1043-1065` (aura, drawn over the mob) and `Mob.cpp:1091-1114` (icon, 4-per-row × 16 px
above the head). Aura anchoring by `aura_pos` — `Mob.cpp:1061-1062`:
```cpp
if (found->aura_pos != 1 && found->aura_pos != 5)
    anchor = Point<int16_t>(absp.x(), static_cast<int16_t>((absp.y() + headpos.y()) / 2));
```
Auras loop for as long as the status lasts (`Mob.cpp:1262-1269`).

---

## 3. CHARGE / RUSH / DASH — **NOT FOUND** on the mob

**There is no mob-side charge, rush, or dash mechanic in this client.** Plainly: the mob never
accelerates toward the player before an attack, and no mob move is a telegraphed dash.

Searches run (all over `clean-client\src`, case-insensitive):

| pattern | hits | where |
|---|---|---|
| `\b(charge\|charging\|charged)\b` | 45 total, 0 in Mob files | `Character/Char.cpp`, `Combat/Combat.cpp`, `Combat/Attack.h` (player charge gauge), `IO/UITypes/Hud/UIEnergyBar.*`, `Net/Handlers/PlayerHandlers.cpp`, `Net/Packets/AttackAndSkillPackets.h` |
| `\b(dash\|noDash\|dashSpeed)\b` | same set | `Character/Player.h`, `Character/SkillId.h:196` (`DASH = 5001005` skill id), `Character/Look/Stance.cpp:165` (anim name string), `Combat/Combat.cpp:1505,1560` |
| `\brush(ing)?\b` | 76 (incl. knockback) | see below |
| `\blunge\b`, `\bflinch\b`, `wind[_-]?up` | **0 hits** | — |

Every `rush`/`charge`/`dash` occurrence belongs to the **player** or to a **skill id / animation
name string**. Two apparent mob-side hits are red herrings:

1. `Mob.cpp:158` reads `attackN/info/rush` — but it sets a flag **on the attack**, applied to the
   **player**, not the mob:
   ```cpp
   attack.rush = data["rush"].get_bool();
   ```
   forwarded `MobAttack.cpp:221`:
   ```cpp
   attack.rush = data.rush;
   ```
   consumed on the victim side at `Character/Player.cpp:1691`:
   ```cpp
   const bool forced_impact = attack.strong_knockback || attack.rush;
   ```
   So `rush` means *"this hit forces the player's knockback impulse"* — it is a knockback
   strength modifier, not a mob charge.

2. `Mob::rush_push` (`Mob.cpp:2198`) **slides the mob AWAY** from the player — the push
   counterpart of `pull`, never a mob-initiated attack.

Closest real mob movements, all reaction-only:
- `Stance::HIT` knockback slide, `Mob.cpp:595-598`:
  ```cpp
  else if (canmove && stance_entered)
  {
      phobj.vx = flip ? -KBSPEED : KBSPEED;
  }
  ```
  with `Mob.h:467` `static constexpr double KBSPEED = 130.0;` and the note that the other v95
  move types (300..1400) are chosen by the attack, which this fork never learns.
- `Mob::pull` (Monster Magnet), `Mob.cpp:2160-2196`.
- `Mob::rush_push`, `Mob.cpp:2198-2235`.

---

## 4. `attackN/info/hit` and `attackN/info/ball`

### `hit` — `Mob.cpp:167` → `AttackData::hit`

**Purely cosmetic.** It is an `Animation` added to `attack_effects` at the instant a hit lands,
`MobAttack.cpp:224`:
```cpp
attack_effects.add(data.hit, DrawArgument(target_position));
```
Note the anchor: `target_position` — the **player's** position, not the mob's. It carries no
damage, no timing, and no state. A server port should drop it entirely.

### `ball` — `Mob.cpp:163,165` → `AttackData::ball`, `has_ball`

**Cosmetic projectile sprite.** It is drawn only for `type == 2`, at
`Mob.cpp:1118-1126`:
```cpp
for (const auto& entry : attack_entries)
{
    const auto& data = attacks.at(entry.index);
    if (data.geometry.type != 2 || !data.has_ball || entry.elapsed < entry.delay) continue;
    double t = std::min(1.0, double(entry.elapsed - entry.delay) / entry.lifetime);
    Point<int16_t> point{ ... };
    data.ball.draw(DrawArgument(point, entry.flipped), alpha);
}
```
Every frame's `AttackEntry::ball` `Animation` object is also ticked at `MobAttack.cpp:36`
(`for (auto& data : attacks) data.second.ball.update();`) so the frame advances in step with
flight. **`data.ball` itself is never added to any EffectLayer** — it is drawn directly here.

**Critically: `Bullet.cpp` / `Bullet.h` are NOT used by Mob.** `Mob.h:29` includes
`../../Combat/Bullet.h`, but grepping the tree shows `Bullet` is constructed only by
`Combat/SkillBullet.cpp` (player skills). Mob projectiles are pure geometry + an interpolated
sprite; there is no `Bullet` object, no `settarget`, no per-tick `update(target)`.

---

## 5. `MobAttackGeometry::launch` and `source`

`MobAttackGeometry.h:52-55`:
```cpp
Point<int32_t> launch(Point<int16_t> origin, bool flipped) const
{
    return { origin.x() + (flipped ? -source.x() : source.x()), origin.y() + source.y() };
}
```
`source` comes from `info/range/sp` (`Mob.cpp:172`). So the muzzle point is the mob origin
offset by the archive-declared `sp`, mirrored on X when flipped.

**What the client actually performs for a projectile** — `MobAttack.cpp:148-158`, at attack start:
```cpp
if (data.geometry.type == 2)
{
    entry.start = data.geometry.launch(origin, flipped && !noflip);
    double dx = destination.x() - entry.start.x(), dy = destination.y() - 25 - entry.start.y();
    double distance = std::hypot(dx, dy);
    if (distance == 0) { dx = flipped ? 1 : -1; distance = 1; }
    entry.finish = { static_cast<int32_t>(entry.start.x() + dx * data.geometry.radius / distance),
        static_cast<int32_t>(entry.start.y() + dy * data.geometry.radius / distance) };
    entry.previous = entry.start;
    entry.lifetime = std::max(1, data.geometry.radius * 600 / data.bullet_speed);
}
```

The projectile is **a straight line of fixed length `range/r`, aimed at the destination the
server supplied, advanced at constant speed, and it dies at the end.** Key points for a port:

- Aimed at `destination.y() - 25` (the ~25 px head offset).
- `radius` is the **flight distance**, not a hit radius.
- Speed is derived, not authored: `lifetime = radius * 600 / bullet_speed` ms, so
  **px/sec = radius / (lifetime/1000) = bullet_speed / 0.6 ≈ 1.667 × bullet_speed**.
- Zero-distance degenerate case falls back to a unit direction.
- There is **no travel-time targeting lock** — the aim vector is frozen at cast time.

Per-tick hit test, `MobAttack.cpp:202-211`:
```cpp
if (data.geometry.type == 2)
{
    double t = std::min(1.0, double(entry->elapsed - entry->delay) / entry->lifetime);
    Point<int32_t> current{ ... };
    Rectangle<int32_t> expanded{ target.left() - 8, target.right() + 8, target.top() - 8, target.bottom() + 8 };
    hit = MobAttackGeometry::crosses(entry->previous, current, expanded);
    entry->previous = current;
    expired = t >= 1.0;
}
```
Swept segment-vs-rect via Liang–Barsky (`MobAttackGeometry.h:30-44`), with an 8 px fattening of
the player box. The segment is consumed on hit or on expiry:
`MobAttack.cpp:226` — `if (hit || expired) entry = attack_entries.erase(entry); else ++entry;`

---

## 6. `Mob::aggressive` / `aggro`

There is **no `Mob::aggressive` member** — the member is `aggro` (`Mob.h:484`), a plain `bool`.
(`Mob.h:143` comment calls control mode 2 "aggro".)

Four writers:

| site | ref | effect |
|---|---|---|
| `set_control(mode)` | `Mob.cpp:1134` | `aggro = mode == 2;` — the authoritative setter |
| `set_move_response` | `Mob.cpp:1208` | `aggro = skills_allowed;` — **every movement response re-derives aggro from the server's skills flag** |
| `apply_damage` | `Mob.cpp:1802` | `if (damage > 0 && is_alive()) aggro = true;` |
| `update_attack` | `MobAttack.cpp:81` | acquisition, below |

`set_control` — `Mob.cpp:1130-1134`:
```cpp
bool had_control = control;
control = mode > 0;
aggro = mode == 2;
```

**Yes, `aggro` is a separate flag from `firstAttack`.** `firstAttack` (`Mob.cpp:79`) is a static
per-template trait; `aggro` is dynamic state. They are combined, never substituted —
`MobAttack.cpp:79-82`:
```cpp
// CMob::TryFirstAttack: priority distance abs(dx)/10 + abs(dy)/3 <= 40,
// or an available attack's reach. A hit/controller mode 2 also acquires.
if (first_attack && (std::abs(dx) / 10 + std::abs(dy) / 3 <= 40 || !available.empty())) aggro = true;
if (!aggro) return;
```
and again in flight at `Mob.cpp:671` — `const bool chasing = target_alive && !target_hidden && (first_attack || aggro);`

Aggro drop, `MobAttack.cpp:83-87`:
```cpp
if ((std::abs(dx) > 1000 || std::abs(dy) > 600) && available.empty())
{
    aggro = false;
    return;
}
```

---

## 7. `Mob::update_movement()` and when it is called

`Mob.cpp:797-805` — it is **not** a movement integrator at all; it is a "speak now" flush:
```cpp
void Mob::update_movement()
{
    // Only the controller speaks for a mob's position
    if (!control)
        return;

    record_path();
    flush_path(true);
}
```

Callers, all in `Mob.cpp` unless noted:

| line | context |
|---|---|
| `MobAttack.cpp:116` | an attack was chosen this tick — send action + target with the position |
| `Mob.cpp:574` | Monster Magnet pull finished (stayed / timed out) |
| `Mob.cpp:591` | Monster Magnet pull reached the target |
| `Mob.cpp:1301` | `begin_mob_skill`, when `send_to_server` |
| `Mob.cpp:1611`, `1627`, `1640`, `1817` | inside `calculate_damage` early-outs and `apply_damage` — the server answered, so report position |

It is **never** called from `update()`'s steady-state tick. The per-frame path is
`Mob.cpp:641-653`: accumulate elapsed ms, `record_path()` on action/fh change or every
`PATH_SAMPLE` (250 ms, `Mob.h:420`), then `flush_path(false)`.

`flush_path` (`Mob.cpp:830-883`) sends `MoveMobPacket` only if the batch changed, or was forced, or
carries an attack/skill. Non-forced flushes wait for `PATH_FLUSH` (1000 ms, `Mob.h:422`) **and**
require at least one point with `fh > 0` — `Mob.cpp:842-851`:
```cpp
if (!canfly)
{
    bool grounded = std::any_of(
            outpath.begin(), outpath.end(),
            [](const Movement& m) { return m.fh > 0; }
    );

    if (!grounded)
        return;
}
```

---

## 8. `attack_effects`

Declared `Mob.h:366`:
```cpp
EffectLayer effects;
EffectLayer attack_effects;
```
Two layers, deliberately separate. `effects` is mob-anchored (relative `DrawArgument` offset,
drawn at `absp`); `attack_effects` is **world-anchored** and drawn against the raw view origin.

**When it fires** — three producers only:

1. **Area warning**, `MobAttack.cpp:174`, at cast time, one per selected cell, in `begin_attack`:
   ```cpp
   attack_effects.add(data.warning, DrawArgument(Point<int16_t>{ static_cast<int16_t>(x), y }));
   ```
   Uses `areaWarning`, drawn at the absolute world point of each attacked cell. Note the cell is
   placed on the ground (foothold y for `type == 3`, `MobAttack.cpp:166-167`), so the warning
   appears where the cell actually is, not at mob height.

2. **Hit spark**, `MobAttack.cpp:224`, per landed hit:
   ```cpp
   attack_effects.add(data.hit, DrawArgument(target_position));
   ```

3. Nothing else. `attackN/info/effect` goes to `effects`, **not** `attack_effects` —
   `MobAttack.cpp:45` and `MobAttack.cpp:140` both use `effects.add(attack.effect, ...)`.

**When it updates** — `MobAttack.cpp:35`, inside `update_attack`, once per tick:
```cpp
attack_effects.update();
```

**What draws it** — `Mob.cpp:1117`, single call, after the mob body and status layers:
```cpp
attack_effects.drawabove({ static_cast<int16_t>(viewx), static_cast<int16_t>(viewy) }, alpha);
```
Only `drawabove` is ever called — `attack_effects` never draws below. Default `z` from the
2-arg `add` is `0` (`EffectLayer.cpp:73-76`), so every entry here is on the above side anyway.

Lifetime: one-shot. `EffectLayer::update` (`EffectLayer.cpp:44-54`) removes any effect whose
`update()` returns true (animation finished). `add_persistent`/`clear_persistent`
(`EffectLayer.cpp:83-90`) are **never used by Mob** — `persistent` is only used by other systems.

---

## 9. Port difficulty per mechanic

| # | Mechanic | Ref | Server-side tick port |
|---|---|---|---|
| 1 | Attack selection + aggro | `MobAttack.cpp:50-127` | **Easy.** 400 ms decision tick, pure geometry. Drop `noflip`/facing subtleties first. |
| 2 | Type 0 rect hit | `MobAttackGeometry.h:100-101`, `MobAttack.cpp:212` | **Trivial.** `world(range,origin,flipped).overlaps(target)`. |
| 3 | Type 1 single-target | `MobAttack.cpp:212` | **Trivial.** `entry->local_target && target_alive`. |
| 4 | Type 2 projectile | `MobAttack.cpp:148-158, 202-211` | **Medium.** Straight-line travel; must re-derive speed as `bullet_speed/0.6`, swept segment test, 8 px fattening. Aim frozen at cast. |
| 5 | Type 3/4 ground cells | `MobAttack.cpp:159-177` | **Medium-hard.** Needs `FootholdTree::get_foothold_range` (server needs a foothold query). Cell mask is `int32` bitfield, capped at 31 cells. |
| 6 | `attack_after` / delay | `MobAttack.cpp:146,199` | **Trivial.** `entry.delay` gates `take_attacks`. |
| 7 | Cooldown | `MobAttack.cpp:38,138` | **Trivial.** `max(duration, delay) + 400` ms. |
| 8 | MP gate | `MobAttack.cpp:65,113` | **Trivial**, but `mp` is server-owned — already is. |
| 9 | Mob skill cast | `Mob.cpp:1204-1211, 1272-1391` | **Already server-driven.** Client only replays. Nothing to port. |
| 10 | Aura/icon | `Mob.cpp:1043-1114` | **Cosmetic — do not port.** |
| 11 | `hit` / `ball` / `areaWarning` / `effect` | `MobAttack.cpp:174,224`, `Mob.cpp:1118-1126` | **Cosmetic — do not port.** |
| 12 | Knockback slide | `Mob.cpp:595-598` | **Easy.** One-shot impulse, `KBSPEED = 130.0`. |
| 13 | Monster Magnet pull | `Mob.cpp:2160-2196, 562-593` | **Medium.** 1000 px/s, stop at `half_width + 45`, 800 ms cap. |
| 14 | `rush_push` | `Mob.cpp:2198-2235` | **Easy.** `half_width + reach + rand(-10,20) + 45`. |
| 15 | Charge / rush / dash | — | **NOT FOUND.** Nothing to port; if the design needs it, it is new work. |

### Where the damage actually goes

`take_attacks` returns `MobAttack` structs; `MapMobs::take_attacks` (`MapMobs.cpp:91-111`) fans them
out; `Stage.cpp:792-798` converts each into a `TakeDamagePacket`:
```cpp
for (const auto& attack : mobs.take_attacks({ target.x() - 10, target.x() + 10, target.y() - 50, target.y() }))
{
    bool immune = attack.type == Attack::Type::MAGIC ? player.Char::is_invincible() || ... : player.is_invincible();
    if (!immune && ...)
        TakeDamagePacket(player.damage(attack)).dispatch();
}
```
Puppets are hit in place via `puppet->receive_damage(attack)` (`MapMobs.cpp:102`).
Touch damage is separate: `create_touch_attack` (`Mob.cpp:1848-1859`) → `MapMobs.cpp:309` →
`Stage.cpp:809-818`, keyed off body-bounds collision, not the attack table.

**So the mob→player damage path is already packet-driven and server-authoritative in shape.**
Porting means moving `take_attacks`/`MobAttackGeometry` to the server tick; the *decision* logic
(`update_attack`) is the only part with real AI in it.

---

## 10. Search log (for the NOT FOUND claims)

1. `grep -rniE '\b(charge|charging|charged|dash|noDash|dashSpeed|lunge|flinch|windup)\b'` over
   `clean-client\src` → 45 matches, **0** in `Mob.cpp` / `Mob.h` / `MobAttack.cpp`.
2. `grep -rniE 'rush|knockback|strong_knockback'` over `clean-client\src` → 76 matches; every
   mob-side hit traced and classified in §3.
3. `grep -n '^\s*...\s*Mob::\w+'` over `Mob.cpp` → full function index (49 functions) — confirms no
   unexamined entry point exists.
4. `grep -n 'skill|charge|rush|dash|knock|pull|launch|ball|bullet|effect'` over `Mob.cpp` → 250
   matches, all classified in §2/§3/§8.
5. `grep -rniE '\bball\b|\bbullet\b|\bprojectile\b'` → `Bullet` used only by `Combat/SkillBullet.cpp`
   (player skills); **Mob never constructs a `Bullet`**.
6. Read `Mob.cpp:36-318` (constructor) in full — the `info/` and `attackN/info/` lists above are
   exhaustive, not sampled.
7. Read `MobAttack.cpp:1-230` in full (file is 230 lines, not 222).
8. `glob`/`Get-ChildItem` on `Gameplay/**` → **no `MobAttack.h` exists**; **no `BulletData` directory
   or file exists** in this tree.
9. Byte-level encoding scan on all seven read files (see §0).
10. `Summon.cpp` mechanics sweep → the only mechanic `Mob` lacks is the **ground tile spread**
    (`Summon.cpp:880-905`, `skill/<id>.img/tile` + `tile/effectDistance`), which is a summon-only
    cosmetic; `Summon` has **no** mob-style special-attack mechanic `Mob` lacks.

### File inventory corrections
- `MobAttack.cpp` is **230 lines** (brief said 222).
- `MobAttack.h` — **does not exist**.
- `Gameplay/Combat/BulletData` — **does not exist**.
- `Mob.cpp` is 2245 lines, `Mob.h` is 514 lines (both as briefed).