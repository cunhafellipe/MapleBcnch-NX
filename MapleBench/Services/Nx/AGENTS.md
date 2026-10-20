# AGENTS.md — `MapleBench/Services/Nx`

The NX (PKG4) reader and writer. This directory takes the highest-risk work in
the repository: it **parses untrusted binary files**. A wrong offset is not a
wrong value, it is a dead process (`0xC0000005`). Treat every offset read here
as hostile input until it has been bounds-checked.

---

## Format, in the numbers you need

52-byte header, then four tables: **node**, **string**, **bitmap**, **audio**.

- Node records are **20 bytes**, laid out **breadth-first**.
- The node graph is a **DAG, not a tree**. Multi-parent nodes are shared and are
  referenced more than once. Do not assume unique ownership, and do not free or
  mutate a node just because you reached it.
- Node counts are stored in a **`ushort`** field.

Measured across the real corpus in `C:\MUWORK\GAME\MPSHS`:

| Fact | Value |
|---|---|
| Largest archive | `Mob.nx` 1,477.9 MB → **21 MB** resident after open |
| Largest node count | `Map.nx` 678.9 MB → 185 MB resident, **8,400,019** nodes |
| `Character.nx` | 186.4 MB → 89 MB resident, 3,616,825 nodes |
| Max child count seen | **8,947** (`Etc.nx`) — the ushort ceiling is 65,535 |
| Max tree depth seen | **12** (`UI.nx`) |

---

## The single most important trap

**Do not add a "child ranges must point forward" guard.**

It looks obviously correct and it will break every real archive. The measured
count of nodes whose child range starts at or before their own index — a
backward reference — is **654,180 in `Character.nx`**, 6,284 in `Item.nx`,
1,412 in `Reactor.nx`, 144 in `Npc.nx` and `Skill.nx`, 14 in `Morph.nx`, 1 in
`Sound.nx`. Backward references are normal in this format, not corruption.

If you want a structural check, validate that indices are **within the table**,
not that they are ordered.

---

## Lazy shells: why open cost is small

An NX open creates one `WzImage` *shell* per image.
`NxImageBuilders.BuildIfPending` materialises the contents on first read. The
resident cost of an open is therefore **~22–96 bytes per node**, not the
560×3 you get by materialising everything.

Consequences:

- Any per-node estimate in this codebase must be calibrated against a **lazy**
  open. A number derived from an eager open is wrong by more than an order of
  magnitude — this has already happened once (`WzSessionService.EstimateNxBytesNeeded`
  predicted 13,458 MB for `Map.nx`; the real resident figure is 185 MB).
- `WxSaveService`/`NxToWzFile` **do** materialise. Before building one, call
  `RequireRoomForBuild()` — `NxImageBuilders.Register` records the per-image node
  count from the `MeasureImage` delta so the guard can price it. Measured build
  cost is 363–739 bytes per node; the guard assumes 560.

---

## Rules

- **Bounds-check before every dereference.** Every table offset and every table
  entry index. This is not a style preference: an unchecked offset-table entry
  is a reproducible process kill, and it was a real bug fixed in
  `NxArchive.GetBitmapRange` and `NxArchive.GetString`.
- Check the offset **in the file**, then check the **length the offset implies**
  also fits in the file. A valid start offset with a length running past EOF is
  its own bug and needs its own check.
- Widen to `long` before comparing a file length, and reject anything that will
  not fit an `int` before narrowing — `NxCanvas.DecodedLength` does exactly
  this, throwing `InvalidDataException`.
- Never claim NX is synchronised with its source IMG. They are separate
  artifacts and nothing here maintains that relationship.

---

## Verify before you claim

You cannot verify an NX parser by reading it. Build a **crafted malformed NX**
and feed it to the real reader:

- `C:\mbtest2\` holds copies of real archives for testing.
- `C:\mbtest2-gen\` holds crafted malformed inputs.

For a bounds-check fix, the acceptance test is: the crafted input is **rejected
with a thrown exception and a live process**, not merely "it returned
something". A parser that returns `None` on a corrupt index is *silent data
loss*, which is a worse outcome than a crash.

> Check your recipe before trusting it. A hand-built malformed NX that lacks the
> node type you meant to corrupt will pass for a valid file and prove nothing.

---

## Never

- **Never write to `C:\MUWORK\GAME\MPSHS`.** That is the playable client
  (16 `.nx`, 3.41 GB). Test only against the copies.
- Never add the forward-reference guard above.
- Never widen a bounds check's exception into a silently substituted default.