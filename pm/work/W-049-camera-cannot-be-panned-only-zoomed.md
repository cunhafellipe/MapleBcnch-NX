---
id: W-049
title: Camera cannot be panned, only zoomed
state: done
priority: 4
owner:
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: "MapleCharacter.cs:749-755, 757-767"
blocked_on:
evidence: "camera pan added: cam.free suspends follow(); middle-drag and space+drag, four nudge buttons, Recentre; pan goes through rectify() so it stops at the map edge; gate 18/18 green; walkview.js three-way md5 equal"
---

## Intent

The walk view could only zoom. There was no way to move the camera to look at
anything but the character.

## Reference

`MapleCharacter.cs:749-755` (the camera is driven from the character every
frame) and `:757-767` (`rectify`, the four clamps).

## Approach

1. Read what the reference's camera is and is not allowed to do.
2. Add the state that lets a reader override it, and name the departure.
3. Reuse the gesture this application already uses in `pixeleditor.js`.

## Acceptance

1. `python tools/pm.py gate  # 18 suites green`
2. `js/walkview.js` three-way md5 equal
3. `# user drags and nudges, and Recentre puts the follow-cam back`

## Result

**The reference has no camera controls at all.** `MapleCharacter.cs:749-755`
drives the camera from the character's position every frame, unconditionally,
and nothing else moves it. So panning is a declared OWN-DESIGN departure, not a
port — and the guard that makes it survivable is one line:

```javascript
follow(x, y) {
    if (this.cam.free) { this.rectify(); return; }   // the reference has no such state
```

Without it the character walks off the edge of the picture within a second of
every pan, which is what "the camera cannot be moved" feels like from the other
side.

**What was added:**

| | |
|---|---|
| middle-button drag | the gesture `pixeleditor.js:690-717` already uses in this application |
| space + drag | held, not toggled, so it backs out by letting go |
| four nudge buttons | 64px each, in their own row so the arrows never split across a flex wrap |
| Recentre | clears `cam.free` and resets `placed`, so `follow()` SNAPS rather than lerping across the whole map |
| `is-free` / `is-panning` | cursor states driven from `cam.free` and the live drag, so no second flag can disagree with either |

`panBy` divides by `layout()`'s `scale` — one world unit is `scale` pixels — and
goes through `rectify()`, so a pan to an edge stops at the edge instead of
showing blank space. That is the same four clamps the follow-cam uses
(`MapleCharacter.cs:757-767`).

NOT gated on `this.live`: a replayed trace has a camera too, and being unable to
look at a walk because nobody is driving it is the same complaint as not being
able to zoom one.

Not proven here: that it feels right on screen. That is W-037.