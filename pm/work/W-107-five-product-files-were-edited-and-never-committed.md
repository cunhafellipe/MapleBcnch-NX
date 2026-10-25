---
id: W-107
title: Five product files were edited in a sibling checkout and never committed; they were rescued into the integrated branch
state: done
priority: 90
owner: lead
opened: 2026-10-25
closed: 2026-10-25
depends: []
refs: MapleBench/Services/Nx/NxCanvasDecoder.cs; MapleBench/Services/Nx/NxWriter.cs; MapleBench/Services/WzEditService.cs; MapleBench/Services/WzNodeFactory.cs; MapleBench/Services/ExportService.cs; C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX (branch recovered/uncommitted-2026-10-25)
blocked_on: 
evidence: "MEASURED 2026-10-25: the sibling checkout held 8 uncommitted modified files; comparing each against my branch's HEAD byte-for-byte, 3 were already identical to mine (.github/notify-discord.sh, MapleMcp/ArchiveClient.cs, MapleMcp/MapClient.cs) and 5 were NOT (ExportService.cs, NxCanvasDecoder.cs, NxWriter.cs, WzEditService.cs, WzNodeFactory.cs - 93 insertions / 10 deletions). Those five were committed in the sibling as f1616fd on branch recovered/uncommitted-2026-10-25, and the DIFF was carried across as a 10,357-byte patch (git apply --3way) - a patch travels the diff, a fetch would have dragged the 678 MB blob into this object store. All five applied CLEANLY, including NxWriter.cs, which BOTH sides had changed. Build after the merge: 0 warnings, 0 errors."
---
## What was rescued, and why it matters

The sibling checkout (`C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX`) had
five product files edited in the working tree and **never committed**. Two of
them are a pair that only make sense together:

- **`NxCanvasDecoder.cs`** (+42): Skia's WebP decoder returns
  `SKAlphaType.Premul` -- a **premultiplied** buffer -- while the format and
  the rest of the decoder speak **straight BGRA**. A premultiplied buffer
  copied verbatim is *colour x alpha presented as colour*. The author's own
  note records why it had gone unnoticed: **every opaque canvas hides it**
  (alpha 255 makes the two forms identical), which is why a 543,760-node
  archive read back bit-exact while a hand-fading a mob to death came back
  black. Measured on a 203x142 frame at alpha 113, RGB came back at 0.44 of
  the authored value -- 113/255 exactly.
- **`NxWriter.cs`** (+6): the measurement that pins the decoder's
  behaviour -- an encode from a premultiplied bitmap, from an SKCodec
  Unpremul buffer, and from a hand-unpremultiplied buffer all wrote the same
  WebP, and it reads back as (76, 129, 218, 113) within one unit.

The other three: `ExportService.cs` (a copy that refuses an image with no
children, instead of blaming the copy for something the source already was),
`WzEditService.cs` (+20) and `WzNodeFactory.cs` (+19).

⇒ **A real rendering bug (faded mobs came back black) was one checkout away
from being lost.** It was never committed, never reviewed, and never built by
anyone who read it as a change.

## How it was carried across (and why a patch, not a fetch)

The sibling's branch still sits on the **pre-rewrite** history, so its
history contains the 678 MB `.simdata/Map.nx` that GitHub refuses. A
`git fetch` of that branch would have copied the blob into THIS repository's
object store -- undoing the 523 MB that was just reclaimed. A
`git format-patch` carries only the diff, and `git apply --3way` merges it
against this branch's version, which is what made the two-sided `NxWriter.cs`
change merge cleanly.

## Still open

- The rescued change has **not been judged by the gate yet**. The gate for
  this wave runs once, after wave 0's cards land, and it will judge this
  change together with them.
- The change was **not reviewed by a second person**. It was written by
  someone else, committed by the lead without a maker-checker cycle; the
  author's own notes are the only review it has had.
