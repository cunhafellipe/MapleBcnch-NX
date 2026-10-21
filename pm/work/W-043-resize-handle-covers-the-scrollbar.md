---
id: W-043
title: Resize handle covers the scrollbar
state: done
priority: 6
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: [app.css:1443-1446; qol.js:731-743]
blocked_on: 
evidence: --resize-gutter 10px declared once, used as #tree-pane padding-right and #inspector padding-left and as .resize-handle width; the handle no longer lands on .tree-scroll's scrollbar; gate 14/14 green; css/app.css three-way md5 equal and served contains the gutter
---
## Intent

The file tree's scrollbar and the panel's left-right resize handle occupy the
same 10px of the pane's right edge. Pressing the scrollbar resizes the panel; only
part of it scrolls.

## Reference

None. This is this project's own chrome and its own layout bug — no reference
application contributes either the handle or this pane arrangement.

## Approach

1. Work out which element is the scroll container and which is the overlay.
2. Stop the overlay from landing on the scrollbar by reserving the edge it needs,
   in the pane, rather than by moving the scrollbar somewhere unnatural.
3. Declare the gutter ONCE and use it for both the padding and the handle width,
   because they are the same number and were previously in two places.

## Acceptance

1. `css/app.css` — the handle's width and the reserved padding come from one
   declaration
2. `python tools/pm.py gate  # unchanged behaviour, 14 suites green`
3. `css/app.css` src ≡ bin ≡ served md5, and the served file contains the gutter

## Result

**What is actually in the layout.** `#tree-pane` is `aside.surface.side-panel`,
and `.surface` is `overflow: hidden` (app.css:191-200) — so the pane is NOT a
scroll container and the handle's `right: 0` is stable, not content-relative.
`.tree-scroll` (app.css:495) is the scroll container and it fills the pane, so
**its scrollbar occupies the pane's outermost 30px**. `addHandle`
(qol.js:731-743) appends `.resize-handle` to the pane with `right: 0`,
`width: 10px`, `z-index: 5` — which lands it on the right 10px of that scrollbar.

That is the reported symptom exactly: the scrollbar looks 30px wide, its right
third resizes the panel, and only the left 20px scrolls.

**Worse before the scrollbar was widened.** At the original 10px track the
10px handle covered the WHOLE scrollbar, so the tree's scrollbar was not merely
misaligned — it did not work at all. It was too thin to notice that the scrollbar
was dead. Widening the track (W-042) turned a total failure into a partial one,
which is what made the overlap reportable.

**The fix.** `:root { --resize-gutter: 10px }`, used twice:
`#tree-pane { padding-right }` / `#inspector { padding-left }` reserve the edge,
and `.resize-handle { width }` fills it. `box-sizing: border-box` is global
(app.css:9), so the padding comes out of the pane's width and the grid column is
unchanged — the scroll container simply ends 10px earlier and the handle sits in
the strip that was kept for it.

Two controls cannot share one edge of a panel. Giving each its own is the fix;
the alternative — pushing the scrollbar further inward — was rejected because it
puts a dead gap between the scrollbar and the panel border for no reason.

The same fix applies to `#inspector` (`left: 0` handle, `.inspector-body` scroll
container) because it had the identical overlap.

**Gate 14/14 green, `css/app.css` src ≡ bin ≡ served, and the served file
contains the gutter.** Not claimed here: that it feels right on screen — that is
W-037.
