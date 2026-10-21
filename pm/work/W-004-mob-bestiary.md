---
id: W-004
title: Mob bestiary panel: level / HP / EXP / traits / elemental profile
state: done
priority: 30
owner:
opened: 2026-07-01
closed: 2026-10-16
depends: []
refs: "MobTooltipRenderer.cs:29-330"
blocked_on: 
evidence: mobinfo-verify.py BAD=0 over 12 cases; the panel renders Type, Level, HP, MP, Physical/Magic Damage, DEF rates, Knockback, EXP, Elements, Revives, and the String.nx MonsterBook episode/maps/rewards
---

## Intent

The user asked for a bestiary form on summoned mobs. The picker had a thumbnail grid and nothing else.

## Reference

`MobTooltipRenderer.cs:29-330`

## Acceptance

1. `python ...\mobinfo-verify.py  # BAD=0`
2. `# panel fields present: level maxHp maxMp exp paDamage maDamage pdDamage mdDamage pdRate mdRate acc eva pushed category categoryName elements traits book revive`

## Result

GET /api/simulate/mob-info. Measured over 343 mobs: `category` on 14, `elemAttr` on 60, and charismaEXP/senseEXP/insightEXP/willEXP/craftEXP/charmEXP/wp on NONE — the reference's attribute-EXP block has no data here, so those lines report null and the UI prints a dash. `MonsterBook.img` stores paragraph breaks as a literal backslash-n, unescaped server-side.
