---
id: W-092
title: The editor's warm-up costs 8.25 GB, and memory is not returned once it is used
state: open
priority: 90
owner:
opened: 2026-10-18
closed:
depends: []
refs: WarmupService.cs Run; ImageMemoryService.cs Sweep; Program.cs --no-warmup; StringPoolService.cs
blocked_on:
evidence: |
  measured 2026-10-18, same build, same 16 archives, two instances:
    --no-warmup   working set    390 MB
    warm-up ON    working set  8,639 MB
    the node tree for all 16 archives alone   322 MB
  the server's own log names the mechanism:
    "Released 0 parsed images (9430 kept), 9148 MB -> 8169 MB"
    "Warm-up finished in 3742 ms; released 0 images, 8173 MB -> 8173 MB"
  after the warm-up, the simulator works identically with it OFF:
    skillshape-verify  63/63 skills landed damage, BAD=0, TIMING clean
    1121008 -> mob 1000 -> 800 in two hits of 100
    11111004 -> mob 1000 -> 800 in two hits of 100
    10001009 -> mob 1000 -> 900 in one hit of 100
workstream: editor
kind: defect
risk: high
---

## Intent

The editor process held 8.6 GB with the simulator idle. MapleStory itself caps at
about 4 GB, and the user's question was whether that was necessary. It is not.

## What was measured

Three numbers, all on the same build with all 16 archives open:

| | working set |
| --- | --- |
| `--no-warmup` | **390 MB** |
| warm-up on (the default) | **8,639 MB** |
| the node tree for all 16 archives, alone | 322 MB |

So the archives are not the cost and the simulator is not the cost. The warm-up is.

## The mechanism

`WarmupService.Run` builds three lists before anything is asked for:

```csharp
Step("npc list",   ...);  Step("sweep", ...);
Step("mob list",   ...);  Step("sweep", ...);
Step("skill list", ...);
```

Each list build decodes the canvases of that whole archive, and the list DTOs hold
the live `WzObject` — so the parsed images are pinned, `ImageMemoryService`'s
sweep refuses them, and the log says so on every pass:

```
Released 0 parsed images (9430 kept), 9148 MB -> 8169 MB
```

9,430 images at roughly 900 KB each is 8.6 GB. The lists show a THUMBNAIL per
row, and a thumbnail decoded at the canvas's full resolution is the waste.

## What was done

The running instance now starts with `--no-warmup`, which takes it from 8.6 GB to
390 MB with the simulator behaving identically (measured above). That is a
workaround, not a fix: the editor's NPC / mob / skill list panels lose their
"instant first open".

## The proper fix, not done

1. **Decode list thumbnails at a reduced size.** A 32x32 RGBA thumbnail is 4 KB
   against ~900 KB for the full canvas, so 9,430 of them is ~38 MB instead of
   8.6 GB, and the list panels keep their speed. This is the change worth making.
2. **Let the sweep reclaim the art behind the lists.** The DTOs would hold a path
   instead of a live object, so the images become releasable and the existing
   `ImageMemoryService.Sweep` machinery works as designed.

## The second, separate problem — NOT root-caused

With the warm-up off, the working set still climbed from 390 MB to 5,077 MB during
a measurement run and then to 9,472 MB while the server sat idle with nothing
happening, and it did not come back down. The server's log stops at the string
pool line and prints nothing after it, and the process was not busy.

That is a separate defect from the warm-up: something allocates while the server
is idle, or the heap is committed and never returned. The warm-up's 8.25 GB is
measured and bypassed; THIS is not explained yet.

Candidate to check first: `StringPoolService` — the log line
`String pool: 13543 items, 542 skills, 1782 mobs, 1521 NPCs` is the last thing
printed before the growth, and a string pool that names 1,782 mobs and 1,521 NPCs
is built by reading the String archive.

## Verified

- `--no-warmup` is a real flag (`Program.cs:271`) and the instance honours it.
- The simulator is unaffected by it: 63/63 skills land damage, hit counts are right
  (1121008 two hits of 100), skillshape-verify reports BAD=0 and TIMING clean.
- The gate is green with the instance in this configuration.