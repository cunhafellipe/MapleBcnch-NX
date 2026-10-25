---
id: W-106
title: The canvas copy is gone: the save's peak private memory fell 66.8% and the output is bit-identical
state: done
priority: 85
owner: implementer-nx
opened: 2026-10-25
closed: 2026-10-25
depends: []
refs: MapleBench/Services/Nx/NxWriter.cs; C:\mbtest2\task77-no-canvas-copy.md; C:\mbtest2\task69-measure.ps1; C:\mbtest2\task69-verify.py; task-18; task-69; W-092
blocked_on: 
evidence: "MEASURED 2026-10-25, before/after on the same source archives with the same measurement rig (C:\\mbtest2\\task69-measure.ps1, 500 ms sampling of the server process): SAVE_SECONDS 7.55 -> 4.45, HTTP_STATUS 200 both, OUT_BYTES 1548619526 both, PEAK_WS_MB 3715.1 -> 2147.7 (-42.2%), PEAK_PRIV_MB 2335.3 -> 774.7 (-66.8%). task-71's PREDICTION (priv ~815 MB, ws ~2160 MB) was a prediction, and the measurement lands within 5.2% / 0.6% of it. OUTPUT EQUALITY, by digest, with an INDEPENDENT reader (C:\\mbtest2\\task69-verify.py): Map.nx canvas_bytes 543030400, node_count 8400124, canvas_sha256 96360260... ; Mob.nx canvas_bytes 1532315924, node_count 710584, canvas_sha256 c621b7f0... ; and the four output files are byte-identical in pairs: out-before-Map.nx == out-after-Map.nx (sha256 8D6FC33778D3C8F92A0BA4205B893303, 711929486 B), out-before-Mob.nx == out-after-Mob.nx (sha256 312F4B47D04D1ED008770A26291DD656, 1548619526 B)."
---
## Intent

`NxWriter`'s plan held a **copy of every canvas in the archive** before it
wrote anything: `NxWriter.cs:666-671` did `Marshal.Copy` of each canvas's
encoded bytes into a fresh `byte[]`, and the plan was `List<byte[]>`.  That
copy was 1,532,315,924 bytes for Mob.nx alone and was the largest single
term in the save's peak private memory.

## What changed

`NxPlan.Bitmaps` is now `List<BitmapPayload>`, and a payload is either owned
bytes or a **mapped (offset, length)** pair.  The bytes are written straight
out of the mapping when the bitmap block runs
(`w.Write(new ReadOnlySpan<byte>(ptr, length))`, using the `NxSink.Write`
overload that already existed at `NxWriter.cs:189`).

**What was NOT touched, and had to be:**

- the **count, the order and the origin-index dedup** (`NxWriter.cs:651-664`)
  -- that is the writer's correctness core, not a detail;
- the **audio** path (`:755-758`), which still copies and still keeps its guard;
- `RequireFitsOneBuffer(length, "canvas")` -- it **stays**: the canvas is
  written as a single span and a span's length is an `int`, so the limit is
  still a real limit.  The `payload-over-2gb.nx` fixture's over-limit payload
  measures as an **audio** entry (bitmap[0] is 16 bytes long) and is refused
  at **open** time (HTTP 400) -- identical message and status before and
  after;
- the counter `CanvasesCopied` was renamed `CanvasesPlanned`, because after
  the change "copied" would have been false and nothing read it.

## The digests, so the proof outlives the 6 GB

```
source Map.nx   711929486 B   (the archive the measurement ran against)
source Mob.nx  1548619526 B
out-before-Map.nx   sha256 8D6FC33778D3C8F92A0BA4205B893303
out-after-Map.nx    sha256 8D6FC33778D3C8F92A0BA4205B893303   <- identical
out-before-Mob.nx  sha256 312F4B47D04D1ED008770A26291DD656
out-after-Mob.nx   sha256 312F4B47D04D1ED008770A26291DD656   <- identical
```

The four output files were re-derived by the rig above and are reproducible
with it; the digests are the durable record, and the bytes themselves were
deleted on 2026-10-25 to give back 4.4 GB.

## What is still open

- **The mapping's lifetime changed.**  The payload used to be copied during
  `Plan()`, so the write phase depended on nothing; now the bytes are read
  **out of the mapping at write time**.  Every call site completes the whole
  write inside `lock (_session.Gate)` (`WzSaveService.cs:435`,
  `NxMergeService.cs:83`) and archive closing takes the same lock, so the
  mapping cannot be pulled out from under it.  A caller that bypasses the
  gate and calls `NxWriter` directly would read freed memory -- and the writer
  cannot detect that.
- Not measured: in-place saves (both arms did save-as, and the peak is in
  the write phase either way); WZ sources; Character/UI/Item.nx; a >2 GB
  canvas; the merge path.
- The 7.2 GB **warm-up** (W-092) is a different number from the save's
  peak: 16 archives open still costs gigabytes.  This change is about the
  save, not the warm-up.
