---
id: W-085
title: A skill's ground decal (tile) was parsed but never published or drawn
state: done
priority: 98
owner:
opened: 2026-10-17
closed: 2026-10-17
depends: [W-084]
refs: SkillUseEffect.cpp TileFootHoldUseEffect; Skill.cpp apply_useeffects and apply_hiteffects; SkillCastService.cs TileSprites/Tiles/TileArea; MapSimSessionStore.cs SpawnTileEffects; walkview.js drawSkillEffectPass
blocked_on:
evidence: |
  before: 7 of the 66 castable skills carry a `tile` node and drew no decal at all
          -- the plan parsed the variants and nothing consumed them
  after:  all 7 spawn and draw; 12111003 66/81/78, 12111005 78, 2121007 71/87,
          2221007 75/78, 2321008 126/156/168, 5121001 59/84/93 blits per cast,
          3111003 28 blits on the struck mob
  plan:   2321008 tileStep 150 (SkillId::GENESIS), 3111003 tileAtTarget true,
          5121001 tileAreaFollowsFacing true, areas read off the archive
          (3111003 -150..150/-100..100, 5121001 -400..25/-100..20) with the
          literal (-400, 400, -350, 250) where level/30 and level/1 carry none
workstream: simulate
kind: gap
risk: med
---

## Intent

ADR-0005 requires 有即時效果 and 正常可見. Seven skills draw a ground effect --
a crater, a rune, a fire patch under the caster or the target -- and none of them
did. The skill hit, the mob moved, the number floated up, and the ground was bare.

## The reference

`Gameplay/Combat/SkillUseEffect.cpp:162-267`, `TileFootHoldUseEffect`. The rule
that decides which of the two shapes applies is in `Skill.cpp`:

```cpp
// Skill.cpp:96    any `tile` node except Phoenix/Frostprey
if (src["tile"] && skillid != 3121006 && skillid != 3221005)
    tileeffect = std::make_unique<TileFootHoldUseEffect>(src, skillid);

// Skill.cpp:223   on the CASTER
if (tileeffect && skillid != 3111003) tileeffect->apply(user);

// Skill.cpp:524   on the TARGET, and only 3111003
if (skillid == 3111003 && tileeffect) tileeffect->apply_at_target(target);
```

So 3111003 (烈火箭) is the only skill whose decal lands on the mob; every other
one paints a GRID on the ground around the caster:

```cpp
// SkillUseEffect.cpp:248-259
for (int16_t x = w_left; x <= w_right; x += step) {
    std::vector<int16_t> y_list = fht.get_foothold_range(x, w_top, w_bottom);
    for (int16_t ground_y : y_list) {
        int16_t jitter_y = (rand() % 5) - 2;
        size_t anim_idx   = rand() % tile_animations.size();
        Point<int16_t> rel_pos(x - user_pos.x(), (ground_y + jitter_y) - user_pos.y());
        target.show_attack_effect_at(tile_animations[anim_idx], 4, rel_pos);
    }
}
// :263-266  nothing underfoot -> one decal at the caster's own feet
```

Anchors, both from the same call sites:

- `Mob::show_effect(anim, pos=1, z=4, false)` (`Mob.cpp:1536-1559`) -- `pos == 1`
  leaves `shift` at `(0,0)`, so the decal sits on the mob's **feet**, not its
  head. `pos == 0` is the head, which is what a hit effect wants and a decal does
  not.
- `Char::show_attack_effect_at(anim, 4, rel_pos)` (`Char.cpp:653-658`) --
  `effects.add(anim, DrawArgument(position, facing_right), 4, get_real_attackspeed())`.
  The position is RELATIVE to the caster, it is mirrored by the caster's facing,
  and it runs at the caster's real attack speed rather than at 1.0.

Per-skill special cases, all three read from the archive rather than invented:

| skill | rule | source |
| --- | --- | --- |
| 2321008 圣光普照 | `effectDistance` default 150, not 180 | `SkillId::GENESIS` is 2321008 (`SkillId.h:172`) |
| 3111003 烈火箭 | the only `apply_at_target` skill | `Skill.cpp:524` |
| 5121001 潜龙出渊 | re-reads its area at the caster's own skill level, mirrored by facing | `SkillUseEffect.cpp:222-232` |

## What was built

- **`SkillCastService`** -- `SkillTileSprite` grew from `(Variant, ImagePath)` to
  also carry `ContainerPath`, `Frames`, `Origins` and `Delays`, because a variant
  is an *animation* (`SkillUseEffect.cpp:173` stores the whole node, and
  `Animation.cpp:166-193` reads the bitmaps off it), not a still. `SkillCastPlan`
  gained `TileArea`, `TileStep`, `TileAtTarget`, `TileAreaFollowsFacing`.
- **`MapSimSessionStore.SpawnTileEffects`** -- mirrors `ApplySkillHits`, including
  the `Math.Max(30, …)` hold floor from W-084, and reuses the map's own
  `GetFootholdRange` rather than a second foothold reader.
- **`walkview.js`** -- no new rendering path. The caster's decals are normalised
  into the existing `drawSkillEffectPass` entry shape (W-088); the mob's reuse
  `drawMobHitEffectPass`.

## Three defects found by measuring the port

A subagent wrote the first pass. All three of these were invisible in its report
and visible only in the numbers:

1. **`tiles` held one row per FRAME, not per variant.** `rand() % size` then rolled
   over 224 "variants" for a skill with 10 variant nodes, so every decal was ~22x
   more likely than the reference draws it. `SkillUseEffect.cpp:254` rolls over
   `tile_animations`, which is a list of variant NODES.
2. **`tileArea` serialised as `{}`.** System.Text.Json emits a `ValueTuple` as an
   empty object unless a tuple converter is registered, and none is -- so the one
   field that decides *where* a decal lands read as empty on the endpoint whose
   whole job is to be read. Now four named integers, matching the collision box's
   existing `Ltx`/`Lty`/`Rbx`/`Rby`.
3. **`Corners()` returned an unnamed tuple** `(lx, ly, rx, ry)` -- the transpose of
   the `(left, right, top, bottom)` two callers destructured it as, which swapped
   every vertical bound once the names were added.

And one where the subagent was right and the brief was wrong: the brief named
2121007 as `SkillId::GENESIS`. `SkillId.h:172` says `GENESIS = 2321008`.

## Measurement note

Two numbers in this work item's history were wrong before they were right, both
from the harness rather than the product, and both are in `pm/TRAPS.md`:

- T-11 -- filtering recorded `drawImage` sources with `/^f8\//` when the URL is
  `/api/canvas?path=f8/...`. Everything reported zero.
- the client polls its OWN session, so a judge that opens a second `/session` to
  measure a cast measures nothing at all. Both the zero-cast and the
  `serverTiles=0` rounds above are that mistake.