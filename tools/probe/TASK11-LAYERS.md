# TASK 11 — Sprite layering from the real client, not from guesswork

## The specification exists. It is C++, on this machine, and it is right there.

    C:\MUWORK\GAME\MPSH\clean-client\src\
      Character\Look\CharLook.cpp     ← the authoritative draw order
      Character\Look\AvatarLayers.*   ← zmap / priority / slots
      Graphics\Animation.cpp          ← frame delay, update, interframe
      Graphics\Texture.cpp            ← origin handling
    C:\MUWORK\GAME\MPSH\AGENTS.md      ← read this first, it is rules not a template

594 files / 125,988 lines of the actual v83 private-server client. Our C#
simulator is reimplementing what this code already does. **When they disagree,
this code wins.**

## Three things we have wrong. Read the C++ for each.

### 1. Layer order — `CharLook.cpp:175-179`

```cpp
// Back to front: the highest zmap value first.
std::stable_sort(sprites.begin(), sprites.end(), [](const Sprite& a, const Sprite& b)
    {
        return a.part->z > b.part->z;
    });
```

Z descends, and the sort is **stable** — equal `z` keeps collection order.
We currently do something equivalent for a single body; make it exact and
prove the stability requirement holds.

### 2. Slot arbitration — `CharLook.cpp:155-173`

We do not implement this at all. A 64-bit mask:

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

Where two parts claim the same slot, only the lower `priority` number survives.
Read `AvatarLayers.h` / `AvatarLayers.cpp` for where `z`, `priority` and `slots`
come from, and confirm against the real data in `Base.nx` / `Character.nx`
what those numbers actually are.

### 3. The origin is NOT the feet — `CharLook.cpp:98-104`

```cpp
// MSW's marker is the part's navel, not the character's ground origin.
// Align it with the same per-pose body anchor used by normal clothing.
```

This is the same class of bug as the `placement()` NaN: assuming the origin is
the ground contact point when it is not. Read `drawinfo.get_body_position()`
and `getfacepos()` — the face is drawn with a **separate offset** from the
body, not the body's. Our current code composes all layers at one anchor.

Also from `Animation.cpp:58-102` — `Frame::Frame(nl::node src)` reads, per
frame: `delay`, `dir`, `head`, `a0`/`a1` (opacity pair), `z0`/`z1` (scale
pair), and `delay == 0` falls back to **100**. We implement delay; we do not
implement the opacity/scale pairs. `Animation::update()` (`:265+`) shows how
interframe interpolation and the zigzag frame walk work.

## Verify

1. Build clean, restart against the new build, confirm the DLL timestamp.
2. Open `C:\MUWORK\GAME\MPSHS\Map.nx` and `Character.nx`.
3. **Screenshot with the character visible**, feet on the foothold line, and
   the face in the right place relative to the body. Give the path. Say which
   map and which action.
4. Report the actual `z`, `priority` and `slots` values you read from the real
   data for `00002000.img` `stand1` layers, and show the sort that results.
5. Compare against the C++ for one concrete case and show the two orders side
   by side.

Use `tools/visual-check/verify-char.mjs <cdpPort> <out.png>` — it drives the
real page, opens the simulator, walks a real map and reads canvas pixels. I
already fixed its globals and its WebSocket usage. Do not write a new harness.

## Constraints

- **Do NOT modify `C:\MUWORK\GAME\MPSH\`** — it is the reference, read-only.
  Including its two git repos.
- Do not change physics, camera, session semantics, or NX canvas decoding — all
  verified working.
- Do not weaken a check to make it pass.

## Scope limit

Finite budget. Prioritise in this order: **origin/feet correctness first** (the
user can see when a character is floating or buried), then layer order, then
slot arbitration. A correctly-anchored character with imperfect slot arbitration
is worth more than a perfectly-arbitrated one drawn at the wrong height — say
which you finished and which you did not.

## A note on method

Seven tasks into this project the recurring failure has been guessing at
behaviour from a single observation and generalising. `MPSH` has the answer in
source form. **Read it before you write code.** If the C++ and our C# disagree,
the C++ is the specification and ours is the bug.