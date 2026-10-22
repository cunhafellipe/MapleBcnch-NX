# TASK 18B — Derive slots and priority from zmap.img / smap.img, do not store them

Supersedes the slot-arbitration half of TASK 18. **Read this before writing
code — I have already established what the data is, so you do not have to
rediscover it.**

## What the current code does wrong

`/api/simulate/look` returns, for all three parts of `stand1` frame 0:

```
"slots": "", "slotBits": 0, "priority": 32767
```

Those are the **initialised** values, never computed. `AvatarLayerTables.cs`
exists but is not fed anything.

## What I measured from the actual archives

`Base.nx` is only 12,768 bytes and contains exactly three images:

```
StandardPDD.img   0,1,2,3,4,5
smap.img          Ae, Af, Am, As, At, Ay, Bd, Cp, Fc, Gv, ...   (151 keys)
zmap.img          Ae, Af, Am, As, At, Ay, Bd, Cp, Fc, Gv, ...   (151 keys)
```

**Both tables have 151 two-letter keys.** `Ae` maps to a *layer name string*,
not a number — from the raw bytes:

```
...736f7279456172 02 00 4165 11 00 63617042656c6f774163636573736f7279...
   "...sorYEar"      "Ae"  len17  "capBelowAccessory"
```

More names visible in the same region:

```
mobEquipFront, tamingMobFront, saddleFront, mobEquipUnderSaddle,
tamingMobMid, characterStart, emotionOverBody, weaponWristOverGlove,
capeOverHead, weaponOverGlove, gloveWristOverHair, gloveOverHair,
handOverHair, weaponOverHand, shieldOver...
```

So: **`zmap.img` maps a short code to a layer name. `smap.img` is the
companion slot table.**

## The specification — this is the key file

    C:\MUWORK\GAME\MPSH\clean-client\src\Character\Look\AvatarLayers.h:30-55

```
// - z is the part's layer looked up in Base/zmap.img; a lower value is
// - slots is the owner's vslot intersected with Base/smap.img[z]: the body
...
uint64_t slots;
// The owner's priority_of(islot), copied so parts can be sorted alone.
int16_t priority;

// zmap value of a layer name, or fallback when zmap does not know it.
...
uint64_t slots_of(const std::string& codes);
// The slots a sprite on layer zname claims for an owner with vslot.
...
int16_t priority_of(const std::string& islot);
```

And the comment on `priority_of`:

> How strongly an owner holds its slots, from its islot: **the smallest zmap
> value among the codes.** When two sprites claim the same slot the ...

**So `priority` and `slots` are DERIVED at runtime from zmap/smap. They are
not constants to be looked up, and they are not in the archive as ready-made
numbers.** A lookup table keyed by part name is the wrong shape entirely.

`AvatarLayers.cpp` has the seven `zmap` references — read it for the actual
lookup and the fallback path.

## Also confirmed by the C++

`CharLook.cpp:30` in the header says:

> z is the part's layer looked up in Base/zmap.img; **a lower value is [drawn
> later / in front]**

Re-read `CharLook.cpp:175-179` against this before you write the sort. The
comment there says "Back to front: the highest zmap value first", and the
comparator is `a.part->z > b.part->z`. **These two statements only agree if you
know which direction `z` runs.** Establish it from the code, state it
explicitly, and do not leave it implied — a previous probe sorted `z` as an
integer when NX stores it as a **string**, and got an arbitrary order.

## Requirements

1. Load `Base.nx` and read `zmap.img` / `smap.img`. Report how many of the 151
   codes you can resolve, and what a code resolves to.
2. Compute `z`, `slots` and `priority` **the way `AvatarLayers.cpp` does** —
   derived, with the same fallback when a name is unknown.
3. Apply `CharLook.cpp:155-173`'s arbitration with the computed values, and
   `stable_sort` on the real `z` with the correct direction and type.
4. Keep `slotBits: 0` / `priority: 32767` out of the response unless they are
   genuinely the computed answer. If a part has no slots, say so.

## Verify

1. Build clean, restart, **confirm the DLL timestamp against the server start
   time**.
2. `/api/simulate/look?fileId=f2&stance=stand1&frame=0` — paste the full part
   list with real `z`, `zName`, `slots`, `slotBits`, `priority`. **If they are
   still `32767`/`0`/`""`, nothing was computed and the task is not done.**
3. Report the sort order produced, and compare against the C++ for one pose.
4. **Screenshot with the composed avatar**, feet on the foothold line, zoomed
   in. Give the path and map id.
5. Re-run the input check and confirm no regression.

## Constraints

- **Do NOT modify `C:\MUWORK\GAME\MPSH\`** — read-only reference.
- Do not change physics, Z formulas, session semantics, NX decoding, or the
  `LastAction` fix — all measured and working.
- Do not weaken a check to make it pass.

## Scope limit

Finite budget. First code edit in the first half; three tasks have timed out
mid-write here and lost everything.

If some of the 151 codes genuinely cannot be resolved from the archive, report
which and why — a partial table with an honest gap is a real result. Do not
invent values to make the code path run.

## A note on method

This project has produced three confident wrong conclusions in one day by
generalising from a single sample. I have measured the above from the real
archives, so you do not need to re-derive it — but do re-verify anything you
extend it with, and say which parts are measured and which are inferred.