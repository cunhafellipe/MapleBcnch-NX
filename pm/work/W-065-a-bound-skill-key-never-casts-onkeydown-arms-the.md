---
id: W-065
title: A bound skill key never casts: onKeyDown arms the LETTER, the server needs the ID
state: done
priority: 85
owner: pm-v2
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: 
blocked_on: 
evidence: livewalk.js onKeyDown armed skillDown with skill.key (the LETTER); the frame loop sent that as the skillId field and SkillCastService.Plan (int.TryParse, :231) returned null, so every cast from the panel died at MapSimSessionStore.cs:1479. Measured on the live server: skillId 'A' -> action=stand no cast; '1121008' -> action=skill brandish1. Fixed to skill.skillId. sim-skills-check.mjs gains 6 Node assertions (27 -> 33) covering onKeyDown; with the old line restored they fail (got K want 1121008, >>> RED), so the regression test has teeth.
workstream: simulate
kind: defect
risk: high
verify:
  - cmd: node MapleBench/tools/sim-skills-check.mjs
    expect: 33 passed, 0 failed
---
## Intent

A skill bound to a key in the settings panel could never be cast. The binding
succeeded, the panel reported it bound, and pressing the key did nothing — with
no error anywhere. The client was sending the bound KEY where the server wanted
the skill ID.

## Reference

Own-design at the seam, not a port. The client's contract with the server is
this project's: `MapSimSessionStore.CastSkill` reads `input.SkillId` and hands it
to `SkillCastService.Plan`, which starts with `int.TryParse` (`SkillCastService.cs:231`).

## Approach

1. `livewalk.js` `onKeyDown` — arm `skillDown` with `skill.skillId`, not `skill.key`.
2. `MapleBench/tools/sim-skills-check.mjs` — assert the armed value, in Node.

## Acceptance

```
node MapleBench/tools/sim-skills-check.mjs
# EXPECT: 33 passed, 0 failed   (was 27 — six new assertions)

# and the new assertions must FAIL on the old line, or they are not a regression test
# EXPECT with `this.skillDown = skill.key` restored:
#   FAIL  a bound key arms skillDown with the numeric SKILL ID
#         got  "K"
#         want "1121008"
#   30 passed, 3 failed
#   >>> RED (3 failed)

python tools/pm.py gate
# EXPECT: RED SUITES: 0, and sim-skills-check reports "33 passed, 0 failed"
```

## Result

**One character.** `livewalk.js:932` read `skill.key`.

`skillFor()` has always returned **both** halves —
`return { key, skillId };` (`:1009`) — and `skillId` was computed and then read
by nothing at all. The frame loop took whatever `skillDown` held and sent it as
the `skillId` field (`:1158-1173`), so the wire carried the letter.

Measured against the running server, same endpoint, same frame, same session:

```
{"skillId": "A"}        -> action='stand'  skillId=''              no cast
{"skillId": "1121008"}  -> action='skill'  skillStance='brandish1'  cast
POST /session/{id}/skills  bindings=[{skillId:1121008, key:A}]
  rejected: []          <- the panel reports it bound, with no complaint
```

`Plan` returns null for "A", `CastSkill` returns at `if (plan is null) return;`
(`MapSimSessionStore.cs:1479`), and the cast is dropped without a trace.

**Why every existing test missed it**: each one posts the numeric id directly
(`skill-test.ps1`, `skillkey-verify.py`) and never travels through `onKeyDown`.
`sim-skills-check.mjs` asserted that `skillFor` returns the right pair — which
was true — and never checked what the caller did with it. W-050 closed with
these words: *"Not proven here: that pressing a bound key now casts... a browser
keystroke is not something an HTTP suite can send."* That gap is now closed
without a browser keystroke, because the defect was in the data flow and not in
the input event.

### Verification

```
node MapleBench/tools/sim-skills-check.mjs                -> 33 passed, 0 failed
same, with skill.key re-introduced                         -> 30 passed, 3 failed, >>> RED
livewalk.js BOM check                                     -> absent (Set-Content was not used in the final edit)
deploy.ps1                                                 -> 59 wwwroot files identical; avatar.js md5 unchanged
python tools/pm.py gate                                    -> see the segment journal
```

**Still unproven, and it is the user's half**: no real keypress has been driven
through the browser in this session, so "press K in the walk view and the
character plays brandish1" rests on the code path and the Node assertions above.
The skill binder's dropdown must also not be on `Z` — the server refuses that
one with a reason (W-050).
