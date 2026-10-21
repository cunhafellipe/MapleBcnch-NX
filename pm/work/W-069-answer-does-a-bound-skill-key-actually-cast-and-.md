---
id: W-069
title: Answer: does a bound skill key actually cast and hurt? End-to-end proof
state: done
priority: 86
workstream: simulate
kind: audit
risk: high
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: "livewalk.js:911 onKeyDown; MapSimSessionStore.cs:1467 CastSkill; SkillCastService.cs:229 Plan; SimulatorEndpoints.cs:457 skills binding"
blocked_on:
evidence: "skill-e2e.py: 14 assertions, 3/3 green. It walks the WHOLE chain -- panel binding POST, key resolution through the response-built map, the cast, the pose, the damage, and the victim's HP -- and it reads the SHIPPED MapleBench/wwwroot/js/livewalk.js to assert onKeyDown arms skill.skillId rather than skill.key (the W-065 character), so the client half is the real file, not a copy. bindings={K:1121008} rejected=[], action=skill skillStance=brandish1, damage=[(2220100,103)], victim hp 350 -> 247, and 103 <= the archive's own level/1/damage of 135. Installed as a gate suite. NOT proven: a physical browser keystroke."
verify:
  - cmd: python $S/skill-e2e.py
    expect: 14 assertions, 0 failed
---

## Intent

Answer one question with evidence rather than assurance: does a skill bound in
the settings panel actually cast when the key is pressed, and does it actually
hurt a mob?

## Reference

`livewalk.js:911` `onKeyDown` · `MapSimSessionStore.cs:1467` `CastSkill` ·
`SkillCastService.cs:229` `Plan` (`int.TryParse`) · `SimulatorEndpoints.cs:457`
the skill-binding route. No product change: this is a proof that what exists works.

## Approach

1. Build `skill-e2e.py`, which walks the chain the UI walks, in the UI's order,
   with the UI's request shapes.
2. For the CLIENT half, read the shipped `livewalk.js` and assert on it directly,
   rather than re-implementing `skillFor`/`onKeyDown` in the probe.
3. Install it as a gate suite so the answer cannot rot.

## Acceptance

```
python %LOCALAPPDATA%\hermes\cache\scratch\skill-e2e.py
# EXPECT: VERDICT: skill-e2e 14 assertions, 0 failed
# EXPECT: bindings={'K': '1121008'}  rejected=[]
# EXPECT: frame_action='skill'  frame_skillStance='brandish1'
# EXPECT: frame_damage non-empty, and every value <= the plan's `damage`
# EXPECT: the struck mob's HP is lower on the frame than before the cast

python tools/pm.py verify W-069
# EXPECT: VERDICT: verify W-069 1 steps, 0 failed
```

## Result

**Yes. It casts, and it hurts.** 14 assertions, green 3/3.

```
=== the client half, read out of the SHIPPED livewalk.js ===
  onKeyDown arms skillDown with skill.skillId : True
  onKeyDown arms skillDown with skill.key     : False      <- W-065
  the frame batch sends that value as skillId: True

=== the Skills… panel binds 1121008 to the key K ===
  HTTP 200  bindings={'K': '1121008'}  count=1  rejected=[]

=== the cast ===
  action='skill'  skillId='1121008'  skillStance='brandish1'
  damage=[{'mobId': '2220100', 'value': 103, 'x': 2093, 'y': -76, 'miss': False}]
  the archive's own level/1/damage = 135   ->  103 is inside it
```

and the mob's own HP on the frame: **350 -> 247**.

Three runs dealt 103, 104, 103 — a real roll, not a constant.

**The client half is not a copy.** `sim-skills-check.mjs` imports
`MapleBench/wwwroot/js/livewalk.js` and calls its real `onKeyDown`:

```
PASS  a bound key arms skillDown with the numeric SKILL ID
PASS  the armed value survives int.TryParse, which is what the server does
PASS  keyup clears the armed skill
```

### What is NOT proven

**A physical keystroke in the browser.** Browser scripting was declined twice, so
no real key press has been driven through the page. What is proven is the LOGIC:
the binder accepts a binding, the key resolves to a numeric id, the client arms
that id, the server casts, the damage is bounded by the archive, and the mob's HP
drops. What remains unexercised is the UI wiring between the panel and the
keystroke listener, which is the same gap W-037 and W-050 both named.

### Three bugs the probe caught in itself, all recorded in the file

1. It asked the bind route for `bound` and `hotkeys`, which do not exist — the
   real shape is `{"bindings": {"K": "1121008"}, "count": 1, "rejected": []}` —
   and reported RED against a binding that had succeeded.
2. It checked the HP of the mob NEAREST the player. The skill's collision box
   covers a different one (2110200 at x=2079 is nearer the body at x=2057 than
   2220100 at x=2091), so the struck mob read as untouched. A skill's box picks
   the target, not distance — the probe was asserting something else.
3. It printed its failure text on a passing check.