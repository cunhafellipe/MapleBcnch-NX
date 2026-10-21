---
id: W-086
title: The skill judge could not see the skill, and then reported the fix as a regression
state: done
priority: 97
owner:
opened: 2026-10-17
closed: 2026-10-17
depends: [W-084, W-085]
refs: MapleBench/tools/browser-verify.mjs; walkview.js lastEffectRect and lastTransform
blocked_on:
evidence: |
  after: browser-verify 14 assertions, 0 failed, five consecutive runs, and the
         full gate reports RED SUITES: 0
  before: "the first frame sits where the archive says it sits -- drawn (131.7,
          330) vs anchor-origin at scale 1 = (-30, -23)"
workstream: toolchain
kind: harness
risk: med
---

## Intent

`browser-verify` is the only judge that looks at what reached the canvas. It went
red on the effect's position the moment W-084 made the mob's hit effect actually
draw, and every number it reported was wrong about a renderer that was right.

## What was wrong

**1. The filter caught two different animations.** `rects.filter(r =>
r.src.includes('112.img'))` matched the whole skill book, so the mob's hit effect
(`.../skill/1121008/hit/0/0`) landed in the same list as the caster's cast sprite
(`.../skill/1121008/effect/0/0`). They have different origins, so every hit-effect
draw read as "a frame drawn against the wrong offset". MEASURED: 71x69 is the hit
sprite, 66x37 the cast sprite.

This filter was harmless while a hit effect never reached the canvas — which is
exactly what W-084 fixed. **A judge that only ever passed because the thing it
was measuring was broken.**

**2. Two coordinate spaces.** The recorded `drawImage` argument is a screen
coordinate, `px(x) = (x - minX) * s` (walkview.js:2898). The assertion compared it
with the archive's `-origin`, a world offset. Those agree only when the camera's
left edge sits exactly on the character.

**3. `view.minX` does not exist.** `view` is a fresh object built per paint; the
walkview never had a `minX` field. A reader asking for it got `undefined`, which
`?? 0` turned into 0 — a 1700 px error in every offset.

**4. Floats compared as strings.** `-29.99000000000001` against `-30` fails a
`Set.has` even though the first assertion, which allows 1.5 px, passed on the
same numbers.

**5. One of two blit branches.** Unmirrored it is `drawImage(img, px(left),
py(top), …)`; mirrored it is `translate(px(right),0); scale(-1,1); drawImage(img,
0, py(top), …)`, whose recorded x is literally 0. Only one branch is measurable
from the arguments, and the assertion also assumed the unmirrored sign —
`DrawArgument.h:141-156` gives `charX - origin.x` facing right and
`charX + origin.x` facing left.

**6. The judge moved the thing it measured.** The fix for (5) was to walk the body
three frames to the right first. That walked it *away* from the mob it had just
approached, put the cast outside the skill's box, and produced
"the cast took exactly one test hit off the mob it was in range of -- 1000 ->
1000".

## What changed

The renderer now publishes what the judge cannot infer:

- `walkview.js` `lastTransform` — `ox`, `oy`, `s`, `dpr` and the character's own
  x/y, written next to the `px`/`py` that consumed them.
- `walkview.js` `lastEffectRect` — the WORLD rectangle the effect frame is being
  filled, with its frame index, its origin and whether it was mirrored, set
  immediately before the blit.

With `lastEffectRect` the judge reads the rectangle directly, so it no longer has
to reverse-engineer either branch, and the "face right first" walk is gone. Each
recorded draw is then checked against **its own frame's** origin in **its own**
orientation — exact, not "some frame's origin somewhere".

Three timing fixes that are the same class of mistake: the art probe reads the
path captured *during* the pose rather than the frame after it; the client log is
cleared per run, because it is append-only and an error from a build fixed twenty
minutes earlier still failed the assertion; and the HP baseline is read
immediately before the cast, because the retry loop can spend ten seconds walking
while the mob patrols out of range.

## Verification

Five consecutive standalone runs, 14 assertions, 0 failed. One run in six failed
on `net::ERR_NETWORK_CHANGED` with the page mid-load — environmental, not the
product. The full gate reports `RED SUITES: 0`.

See `pm/TRAPS.md` T-18 through T-20.