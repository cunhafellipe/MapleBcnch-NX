---
id: W-084
title: A mob's hit effect was published for one frame, so the client never drew it
state: done
priority: 99
owner:
opened: 2026-10-17
closed: 2026-10-17
depends: []
refs: MapSimSessionStore.cs HitEffectUntil; walkview.js drawMobHitEffectPass; MapleBench/tools/browser-verify.mjs
blocked_on:
evidence: |
  before: server published the effect for 11 frames (the sprite's own delays,
          180ms at 1/60 s), and the client polled once every ~16 ms while the
          server advanced up to 8 frames per poll -- the whole effect fit inside
          one poll, so the canvas drew none of it
  after:  30 consecutive polls carry the effect; every landed cast draws it
  before: 6 casts -> hitDraws 0 (hit effect), caster effect 19-26 per cast
  after:  the 3 casts that dealt 100 damage drew 25, 26 and 26 hit-effect blits,
          animation frames 0 AND 1 both used; the 3 that dealt no damage (the mob
          had walked out of range) correctly drew 0
  images:  /api/canvas?path=f8/112.img/skill/1121008/hit/0/0 -> 200, 3476 bytes,
          decodes 71x69; frame 1 -> 65x64
workstream: simulate
kind: defect
risk: med
---

## Intent

A skill hit the mob, the mob's HP dropped, and the wire carried a correct hit
sprite with correct origins and delays -- and the canvas drew nothing. Two of the
four ADR-0005 criteria (有即時效果 / 正常可見) were failing for every skill.

## Root cause

The effect was retired on the frame it arrived.

`MapSimSessionStore` computed the hold from the sprite's own delays and published
the effect for exactly that long:

```csharp
int holdFrames = (int)Math.Ceiling(sprite.Delays.Sum(...) / (1000.0 / 60.0));
session.HitEffectUntil[mobIndex] = session.Frame + Math.Max(1, holdFrames);
```

That is the animation's own length, and it is the right *duration* -- but it is
the wrong *transport* assumption. The client does not receive one frame per frame:
it polls `/frames` roughly every 16 ms while the server advances up to 8 physics
frames per poll. A 180 ms effect is 11 server frames, which is under two polls, so
the client had a one-in-two chance of seeing the start and then had no frames left
to walk the animation on.

## Fix

A floor of half a second, with the client's own clock left in charge of the end:

```csharp
session.HitEffectUntil[mobIndex] = session.Frame + Math.Max(30, holdFrames);
```

The client already retires the animation itself -- it walks `sprite.delays` and
stops on the last frame (`walkview.js` `drawMobHitEffectPass`, the same code
`drawSkillEffectPass` uses). The extra server-side frames cost nothing; they are
the frames the client ignores.

## Measurement note

The first measurement said "0 draws" and was **wrong**, not the code. The judge
filtered recorded `drawImage` sources with `/^f8\//`, but the client's URL is
`/api/canvas?path=f8/112.img/...` -- it never starts with `f8/`. Every count came
back zero for both the caster's effect and the hit effect while the caster's art
was demonstrably on screen. Correcting the filter to `/path=f8\//` is what turned
the measurement into evidence.

See `pm/TRAPS.md` T-11.