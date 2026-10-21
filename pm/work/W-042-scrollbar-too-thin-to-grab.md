---
id: W-042
title: Scrollbar too thin to grab
state: done
priority: 20
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: [app.css:42-50]
blocked_on: 
evidence: draggable thumb 4px -> 12px -> 20px (30px track, 5px border); min-height 64px; hover 85% / drag accent 80%; css/app.css three-way md5 equal
---
## Intent

The editor's scrollbars are too thin to grab with a mouse. A near-miss lands on
the content and selects a row instead of scrolling it.

## Reference

None. This is a usability fix on this project's own chrome — no reference
application draws a scrollbar this application draws.

## Approach

1. Measure the DRAGGABLE width, not the track width.
2. Widen the track and shrink the thumb's transparent border so the handle keeps
   a usable size.
3. Give the thumb a floor, so a long list does not shrink it back to nothing.
4. Make the handle findable: a hover state that reads differently from rest, and
   a drag state that reads differently again.

## Acceptance

1. `css/app.css` thumb is at least 20px of visible, grabbable width
2. `python tools/pm.py gate  # unchanged behaviour, 14 suites green`
3. `css/app.css` src ≡ bin ≡ served md5

## Result

**The measurement that explains it.** The thumb is drawn inside a transparent
border with `background-clip: content-box`, so the part you can actually drag is
`width − 2 × border`. At the original `10px / 3px` that was **4px** — and the 3px
either side of it is the content underneath, which is exactly the "clicked it and
selected the data instead" symptom.

| | original | step 1 | **step 2** |
|---|---|---|---|
| track | 10px | 20px | **30px** |
| thumb border | 3px | 4px | **5px** |
| **draggable thumb** | **4px** | 12px | **20px** |
| resting opacity | 35% | 45% | **55%** |
| hover | 60% | 75% | **85%** |
| drag | none | accent 70% | **accent 80%** |
| min thumb height | none | 48px | **64px** |
| radius | 6px | 8px | **10px** |
| Firefox | `scrollbar-width: thin` | `auto` | `auto` |

**Two rounds, both too small, measured the same way.** Step 1 chose 12px — the
usual minimum for a pointer with no modifier held — and it was reported as still
too thin, so the reasoning about "minimum" was wrong for this user and this
mouse. Step 2 goes to 20px.

Why not a solid 30px handle (border 0)? Because a 30px scrollbar on a narrow
panel eats a third of the content column, trading one unusable control for a
different one. 30px of track is about where that stops being true; the
transparent border keeps the handle at 20px while the control reads as a
generous 30px target.

The `min-height` matters on the other axis: a long list shrinks the handle
proportionally, which would reintroduce the same complaint one axis over.
`scrollbar-width: auto` is there because Firefox's `thin` ignores the WebKit
rules entirely and is not page-sizable — a platform limit, recorded rather than
worked around.

Nothing else in the stylesheet sets a scrollbar width; the only other rule is
`.breadcrumb::-webkit-scrollbar { display: none }`, which is a deliberate hide
and is untouched.

**Gate 14/14 green, `css/app.css` src ≡ bin ≡ served.** Not claimed here: that it
feels right on screen — that is W-037.
