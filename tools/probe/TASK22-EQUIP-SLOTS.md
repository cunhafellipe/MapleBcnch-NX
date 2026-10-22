# TASK 22 — Equipment slots: the avatar cannot be changed

The user's words: **「玩家人偶沒有組裝好」**, compared against the reference
simulator which can swap equipment. I found the exact bottleneck. It is not
missing art and it is not missing knowledge — **it is one method signature.**

    A = C:\Users\e7896\AppData\Local\hermes\cache\necro-ref\MapleNecrocer

## The bottleneck

`MapleBench\Services\Look\CharLookComposer.cs:240-242`:

```csharp
public Look Compose(
    string bodyPath, string headPath, string? hairPath, string? facePath,
    string stance, int frame)
```

**Four hardcoded slots. There is no parameter through which a hat, a coat, a
shoe or a weapon could arrive.** Everything downstream already works — the
zmap-derived `z`/`slots`/`priority` arbitration is real (verified: z 76/51/45,
mask 1/1/2, prio 150/150/149) and correctly hides a covered layer. The
composer just never gets handed anything to compose.

So this task is: **generalise the signature, do not rewrite the pipeline.**

## The slots, verbatim from `AvatarForm.Designer.cs`

```
Head, Body, Weapon-1, Weapon-2, Cap-1, Cap-2, Coat, Pants, Longcoat, Cape,
Shield, Glove, Shoes, Hair-1, Hair-2, Face-1, Face-2, FaceAcc, Glass
```

## Folder and layer name per slot, verbatim from `AvatarForm.cs:494-645`

| slot | folder under `Character/` | layer within the image |
|---|---|---|
| Head | *(root)* | `front` |
| Body | *(root)* | `stand1/0/body` |
| Hair | `Hair/` | `hairOverHead` |
| Face | `Face/` | `face` |
| Cap / Coat / Pants / Shoes / Glove / Cape / Shield / Longcoat | same name | `icon` |
| FaceAcc / Glass | `Accessory/` | `icon` |
| Weapon | `Weapon/` | `icon` |

**The layer names are the zmap names, and they are not one-per-slot.** A cap
alone produces several layers — `capBelowHead`, `capOverHair`, and so on. The
reference iterates the image's children and takes each one by its own name:

```csharp
// AvatarForm.cs:625-643
Iter.GetBmp("head");         Iter.GetBmp("0/body");
Iter.GetBmp("face");         Iter.GetBmp("hairOverHead");
```

**Compose every child of the item's image, not a single hardcoded layer.** The
zmap lookup already knows what each name means.

## ID filters, verbatim from `AvatarForm.cs:557-620`

Cap-1 is imgId/1000 in 1000..1003; Cap-2 1004..1006; Hair-1 30..56;
Hair-2 57..85; Face-1 20..23; Face-2 24..65; FaceAcc `0101`; Glass `0102`;
Weapon-1 everything except `0170`; Weapon-2 only `0170`.

## The data is already there — I measured it in our own archive

```
Cap 908   Hair 1518   Face 540   Accessory 503   Weapon 1220
Coat  Pants  Shoes  Glove  Cape  Shield  Longcoat   (+ Afterimage, PetEquip, Ring, Dragon)
```

## Requirements

1. **A general slot map in, a composed look out.** `Compose` must accept an
   arbitrary set of (slot → item id) and resolve each to its folder + image +
   layers. Keep the existing 4-slot behaviour working — `/api/simulate/look`
   is called by the live viewer.
2. **Expose it.** An endpoint that takes a slot map and returns the composed
   look. `GET /api/simulate/look` must still work with no slot map.
3. **Arbitration must decide, not a fixed order.** When a cap covers hair, the
   hair layer must be *hidden by the existing priority rules*, not skipped by
   hand. Report which layers were hidden and why (slot, priority) for at least
   one equipped case.
4. **Anchors must still come from the body.** TASK 17 established
   `headAnchor = body.neck − head.neck`; equipment layers must be placed by the
   same rule, not by a shared origin.

## Verify

1. Screenshot with **at least four slots equipped at once** — e.g. Cap + Coat +
   Shoes + Weapon — correctly layered, character still coherent. Read the
   canvas with `toDataURL()`, not `Page.captureScreenshot`: that returned a
   black frame while the canvas held 577,589 non-background pixels.
2. For one equipped case, print each part with its `z`, `slots`, `mask`,
   `priority`, and `hidden`, and confirm a covered layer was hidden by rule.
3. Compare the returned layer names against the table above — the names must
   be zmap names like `capBelowHead` / `capOverHair`, not invented ones.
4. **Regression:** the existing walk/simulator must be unchanged. Run
   `tools/visual-check/verify-scenes.mjs` and report the totals.

## Constraints

- **Do NOT modify `C:\MUWORK\GAME\MPSH\` and do NOT modify the reference repo.**
- Do not weaken a check to make it pass. Do not special-case a slot to make a
  screenshot look right.

## Scope limit

Finite budget. **First code edit in the first half, saved to disk immediately.**
TASK 21 (the forever-fall physics fix) is running concurrently in the same
tree — **do not touch `CharacterPhysics.cs`, `FootholdPhysics.cs`, or anything
under `Services/Simulate/`**, so the two tasks cannot collide. Work in
`Services/Look/` and the endpoint file.

Ship the slot map plumbing plus at least Cap and Weapon working end to end
before adding more slots. Report how many of the 19 you wired.

## A note on method

I have read the reference and cited line numbers — start from
`AvatarForm.cs:494-645` rather than re-deriving the layout. Where you cite
something, give `file:line`. This project has produced four confident wrong
conclusions in one day, three of them mine.