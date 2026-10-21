---
id: W-033
title: Map background music
state: building
priority: 25
owner: 
opened: 2026-10-16
closed: 
depends: [W-001]
refs: MapInfo.cpp; Sound.nx
blocked_on: 
evidence: 
---
## Intent

The map declares `info/bgm = 'Bgm00/FloralLife'` and nothing plays it.

## Reference

`MapInfo.cpp; Sound.nx`

## Acceptance

1. `# GET the bgm node, resolve it under Sound.nx, play it on map entry`

## Result

**MEASURED 2026-10-16, before any code.** Script: `scratch\w033-measure-bgm.py`.
Read through the project's own MCP (`maplebench-assets` v1.4.0, 52 tools),
not ad-hoc HTTP.

### 1. The node is there

`info/bgm` exists on every map sampled, type **String**:

| map | value |
|---|---|
| `f4/Map/Map1/100000004.img` | `Bgm00/FloralLife` |
| `f4/Map/Map0/000000000.img` | `Bgm00/GoPicnic` |
| `f4/Map/Map0/000000002.img` | `BgmJp/FirstStepMaster` |

### 2. The resolution rule, measured not guessed

Split on `/` into group + name, then **`f14/{group}.img/{name}`**, node type
**`Sound`**. All three samples hit on the first spelling tried.

The nesting is flat. Sound.nx carries `Bgm00.img` … `Bgm21.img` plus the
regional ones (`BgmCN` `BgmJp` `BgmMY` `BgmSG` `BgmGL` `BgmUI` `BgmEvent`)
**directly at the archive root** — 45 children. Measured ABSENT: `f14/Bgm`,
`f14/Bgm.img`, `f14/Sound`, `f14/Music`. So a `Bgm/Bgm00.img/FloralLife`
spelling does not exist.

### 3. The audio payload is on the leaf, not in a child

Under `f14/Bgm00.img/FloralLife`: `ext` ABSENT, `0` ABSENT, `mp3` ABSENT.
The stream is carried by the `Sound` node itself.

### The blocker — acceptance clause 3 is not reachable today

`play it on map entry` needs the sound bytes out of the backend. **No tool in
the 52 can do that.** The export family is:

| tool | produces |
|---|---|
| `wz_asset_export_canvas` | PNG |
| `wz_asset_export_img` | IMG |
| `wz_archive_export_img` | IMG |

No audio export, and no sound-serving HTTP endpoint found. Clause 3 therefore
needs a backend capability that does not exist yet.

**Not done, and not faked.** Shipping something that reads `Bgm00/FloralLife`
and then plays nothing is exactly the failure this project keeps recording — a
green gate over a feature that does not work.

**Needs a decision (R-7):** either

1. the acceptance is rewritten to clauses 1–2 only — resolve and expose the
   `Sound` node — and playback becomes its own item; or
2. a sound-export capability is added to the backend first, and this item
   keeps clause 3.

### Two reader bugs caught in this measurement, both mine

`wz_asset_node` returns **the node itself**, not `{"node": …}`. The first
version looked for the wrapper, found nothing, and printed *"no bgm node
found"* while the archive plainly carried one on all three maps — a reader bug
that reads exactly like absent data (`TRAPS.md` T-8 in new costume). The
second run still had that bug live in the resolve step and reported five
misses that were really hits. Both were caught by printing the raw values
instead of trusting the verdict.
