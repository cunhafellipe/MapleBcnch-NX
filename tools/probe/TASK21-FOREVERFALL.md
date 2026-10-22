# TASK 21 — The character falls forever off some maps

I chased this one to the source. It is not the spawn point, not the camera,
and not the fall edge. **HTTP 400 is now 0 across the board — that part is
done.** What remains is a specific physics bug I have isolated frame by frame.

    A = C:\Users\e7896\AppData\Local\hermes\cache\necro-ref\MapleNecrocer

## The reproduction

`f1/Map/Map0/000030000.img`, bounds `left=-270 top=-660 right=1170
bottom=409`, spawn at `x=-180 y=215` — correctly inside.

Hold right + down, log every frame:

```
  0   -178.5   215.0  onGround=True   action=walk
 50    -55.0   215.9  onGround=True   action=walk
100    67.5   216.7  onGround=True   action=walk
125   127.5   215.9  onGround=True   action=walk
150   153.7   310.9  onGround=False  action=fall     <- walks off a ledge
163   153.7   414.9  onGround=False  action=fall
170   153.7   470.9  onGround=False  action=fall     <- past bounds.bottom (409)
       ... x frozen at 153.7, y climbing forever, onGround stays False
```

**The character enters `fall` and never lands.** `x` freezes, `y` increases
without bound, `onGround` never returns true. The player falls through the
world permanently. A 60-map sweep finds **5 maps** like this (`000030000`,
`000040000`, `000000004`, `100000003`, `100000004`).

## What the reference does

`MapleCharacter.cs:874-885` — the entire landing test:

```csharp
if ((JumpState == JumpState.jsFalling) && (FallFlag))
{
    Below = FootholdTree.Instance.FindBelow(new Vector2(X, Y - VelocityY - 6), ref BelowFH);
    if (Y >= Below.Y - 3)
    {
        Y = Below.Y;
        MaxFallSpeed = 10;
        JumpState = JumpState.jsNone;
        FH = BelowFH;
        Z = FH.Z * 100000 + 60000;
    }
}
```

Read these details, they are the bug:

- **`new Vector2(X, Y - VelocityY - 6)`** — the probe point leads the fall by
  `VelocityY + 6`. A fixed probe point cannot keep up with an accelerating
  fall and tunnels straight through thin footholds. **Compare against ours.**
- **`if (Y >= Below.Y - 3)`** — landing needs `Y` to *reach* the foothold.
- `Below.Y == 99999` is the **sentinel for "nothing found"**, referenced at
  `:895` (`if (Below.Y != 99999)`). If our `FindBelow` returns 0 instead of a
  sentinel, `Y >= 0 - 3` is always true and the character snaps to Y=0; if it
  returns 99999, the test never passes and it falls forever. **State which one
  ours returns.**
- `MaxFallSpeed = 10` caps the fall; on landing it resets.

Also relevant, `:892` for the down-key drop-through:

```csharp
Below = FootholdTree.Instance.FindBelow(new Vector2(X, Y + 4), ref BelowFH);
if (BelowFH.X2 > BelowFH.X1) { if (Below.Y != 99999) { JumpState = JumpState.jsFalling; } }
```

That `X2 > X1` guard is why a one-way foothold cannot be dropped through.

## Requirements

1. Match the reference's landing probe and sentinel semantics exactly.
2. **A fall must terminate.** Whatever the map's geometry, the character must
   not fall without bound. When there is genuinely nothing below, follow the
   reference's own outcome — find out what it does rather than inventing a
   teleport. If the reference has no answer, clamp to `bounds.bottom` and say
   so plainly in the report.
3. Do not break the maps that currently work — 5366 must still open.

## Verify

Run `tools/visual-check/scan-fallout.mjs 5511 5366`. **Paste the totals.** The
FELL OFF count must be 0.

Then for `f1/Map/Map0/000030000.img`, print the frame-by-frame log in the shape
above and show the character coming to rest on a foothold at frame ~150 rather
than sinking forever.

Then confirm the maps that already worked still work — the sweep number, not a
sample.

## Constraints

- **Do NOT modify `C:\MUWORK\GAME\MPSH\` and do NOT modify the reference repo.**
- Do not weaken a check to make it pass.

## A note on method

I have already located the cause and cited the lines — start from
`MapleCharacter.cs:874-885` rather than re-deriving the fall model.

One thing I got wrong yesterday that is now fixed, do not reintroduce it: I
judged "fell off" by `y > 1200` hardcoded, which wrongly flagged maps whose
own `bounds.bottom` is 3120. The harness now judges against each map's own
bounds. If you find a map where the character legitimately rests below y=1200,
that is correct behaviour, not a bug.

## Scope limit

Finite budget, and this is a **focused physics fix — do not start the
equipment-slot work in this task.** First code edit in the first half, saved to
disk immediately.