# TASK 18 — Real layer arbitration, and a harness that survives a schema change

Two parts. Part 2 is five minutes and unblocks every later task — do it first.

---

## PART 1 — The harness reads a schema that changed

`tools/visual-check/verify-scenes.mjs` now reports `placement=BAD` for all 7
scenes. **The rendering is correct** — a screenshot shows a complete avatar
with head, body, arms and face, correctly anchored. The harness is reading
`avatar.part`, which the composed-look work stopped populating.

Every scene prints:

```
stand  map=109050001  drawn=102814  action=stand  placement=BAD  feetΔ=?
```

Update it to read the composed look. Measure the grounding against the
**server's own `bounds`** (`/api/simulate/look` returns
`bounds: {minX,minY,maxX,maxY}` per frame) rather than a client-side field that
can drift. Report `bounds.maxY − tick.y` as the feet contact.

Also fix its scene list: `106020010` is not in the map list
(`have: 0,1,2,3,4,10000,20000,...`), so that scene is dead weight. Replace it
with ids that exist, chosen from the real list.

---

## PART 2 — Slot arbitration is not running

`/api/simulate/look` returns, for all three parts of `stand1` frame 0:

```
"slots": "", "slotBits": 0, "priority": 32767
```

That is the **initialised**, not the **computed** value. `AvatarLayerTables.cs`
exists but is not fed real data, so all three parts sort by collection order
instead of by `CharLook`'s rule. For a bare character the visual result happens
to be right; **the moment a hat, overall or pants is added it will be wrong.**

### The specification

    C:\MUWORK\GAME\MPSH\clean-client\src\Character\Look\CharLook.cpp:155-173

```cpp
constexpr size_t NUM_SLOTS = 64;
int16_t strongest[NUM_SLOTS];
for (int16_t& value : strongest) value = std::numeric_limits<int16_t>::max();

for (const Sprite& sprite : sprites)
    for (size_t bit = 0; bit < NUM_SLOTS; bit++)
        if ((sprite.part->slots >> bit) & 1)
            strongest[bit] = std::min(strongest[bit], sprite.part->priority);

auto visible = [&](const AvatarPart& part)
    {
        for (size_t bit = 0; bit < NUM_SLOTS; bit++)
            if (((part.slots >> bit) & 1) && strongest[bit] < part.priority)
                return false;
        return true;
    };
```

### Where the tables come from

    C:\MUWORK\GAME\MPSH\clean-client\src\Character\Look\AvatarLayers.h
    C:\MUWORK\GAME\MPSH\clean-client\src\Character\Look\AvatarLayers.cpp

and the real data in `Base.nx` (`/api/nxlayer/...` or whatever the reader
already exposes) and `Character.nx`.

**Before writing any arbitration code, report what is actually in the data:**

- What `z` values exist, and in what type? (NX `z` is a **string**, not an
  integer — a previous probe sorted it as an int and got an arbitrary order.)
- What `slots` bit patterns exist?
- What `priority` values exist, and what is their real range?
- Does `Base.nx` carry the layer table, or is it compiled into the client?

**If the data genuinely has no slot information, say so with the node paths you
checked.** "Slot arbitration is unreachable because the archive carries no slot
masks" is a complete and useful answer. Inventing slot values to make the code
path run is not.

### Layer sort

`CharLook.cpp:175-179`:

```cpp
// Back to front: the highest zmap value first.
std::stable_sort(sprites.begin(), sprites.end(), [](const Sprite& a, const Sprite& b) {
    return a.part->z > b.part->z;
});
```

Descending, and **stable** — equal `z` keeps collection order.

---

## Verify

1. Build clean, restart, **confirm the DLL timestamp against the server start
   time**.
2. `/api/simulate/look` — report the real `z`, `slots`, `slotBits`, `priority`
   per part. If they are still `32767`/`0`/`""`, arbitration is still not
   running and the task is not done.
3. Show the resulting sort order for one pose, and compare against the C++.
4. **Screenshot with the composed avatar**, feet on the foothold line. Give the
   path and the map id. Zoom in.
5. Re-run the input check (`LastAction` follows speed) and confirm no
   regression.

## Constraints

- **Do NOT modify `C:\MUWORK\GAME\MPSH\`** — read-only reference.
- Do not change physics, Z formulas, session semantics, NX decoding, or the
  `LastAction` fix — all measured and working.
- Do not weaken a check to make it pass.

## Scope limit

Finite budget. **Part 1 first — it is small and unblocks verification of
everything after.** Make the first code edit in the first half; three tasks
have timed out mid-write here and lost the work.

If the archive carries no slot data, finish by making the sort correct on the
real `z` values, document that arbitration is unavailable and why, and say so
plainly rather than fabricating masks.