---
id: W-032
title: NPC speech balloons
state: done
priority: 20
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: [W-001]
refs: UINpcTalk.cpp:2453,2471; Char.cpp:720 ChatBalloon
blocked_on: 
evidence: npcchat-verify BAD=0 (6 NPCs, general+perAction lines match info/speak exactly; balloon 9 edges + arrow, clr=0xFF800000); npcchat-frame-verify BAD=0 (first lines at frame 201/283 past the 180 CHAT_MIN_DELAY floor; every gap >= 180 frames); gate 14/14 green; three-way md5 equal
---
## Intent

NPCs are drawn and named but say nothing.

## Reference

`UINpcTalk.cpp:2453,2471; Char.cpp:720 ChatBalloon`

## Acceptance

1. `python ...\npcchat-verify.py        # the two-hop lookup against the archive`
2. `python ...\npcchat-frame-verify.py  # timing, pool and provenance on the frame`
3. `# an NPC shows a balloon above itself, in the archive's 9-slice and colour`

## Result

**The lookup is TWO hops, and that is the reference's shape rather than a
convenience.** `Npc.nx` names string KEYS (`Npc.cpp:127-128`) and `String.nx`
carries the lines (`:60`). Measured on this archive: `String.nx/Npc.img` holds
**7,123** entries, and over the first 60 `n0` is on 42, `n1` 38, `d0` 28, `d1` 28,
`func` 26. Idle chatter is the common case, not an edge case.

For `9101002` (透德): `info/speak = [n0 n1 n2 n3]`, `finger/speak = [f0]`,
`wink/speak = [w0]`, `heart/speak = [h0]`, and `stand` carries no `speak` at all.

- `NpcChatService.Describe` + `GET /api/simulate/npc-chat`
- `MapSimSession.NpcChats` holds each NPC's lines and its countdown, seeded at
  spawn from `3000 + rand(6000)` ms (`Npc.h:131-132`, `Npc.cpp:203`)
- `AdvanceNpcChat` runs every frame and reproduces `Npc.cpp:276-284` and
  `:478-484` exactly: one index out of `states + lines` TOGETHER, speaking only
  when it lands on a line, and only when no balloon is already up
- `MapSimMobFrame.Speech` / `.SpeechFrames` carry it
- `walkview.js drawBalloon` draws the 9-slice + arrow + `clr` above the NPC

**The balloon measured exactly as `ChatBalloon.cpp` describes it:**
`UI.nx/ChatBalloon.img/npc` with 11 children — the nine edges `nw n ne w c e sw
s se`, an `arrow`, and `clr = -8388608` = `0xFF800000`, the maroon the
reference's own comment names for NPCs.

**Verified (npcchat-frame-verify.py BAD=0)** on `Map0/000000002.img`, 6 NPCs:
first lines at frame 201 and 283 — both past the 180-frame `CHAT_MIN_DELAY`
floor — and every gap between consecutive lines ≥ 180 frames, so two balloons
never overlap. Every line spoken was checked to be one of that NPC's own.

**Three things found while building it, all fixed or recorded:**

1. **`open-archives.py` was only opening NINE archives.** It printed
   `opened: 9 failed: []` and every caller read that as "the archives are open".
   `UI.nx` was among the seven that never opened, so `ChatBalloon.img` was
   unreachable — and a probe reading `f16/ChatBalloon.img` SUCCEEDED against a
   stale pre-restart session rather than failing, which is the worst version of
   this bug. The list is now all sixteen, matching `PROJECT.yaml`, and the
   script exits non-zero if any is missing.
2. **`String.nx/Npc.img` keys are NOT padded.** 7,123 entries, all seven
   characters, and NPC 2000 is stored as `2000`, not `0002000`. Three shapes have
   to be tried (as-given / D7 / bare). The service already did; my VERIFIER did
   not, and it "failed" five times against a correct endpoint. Same lesson as
   W-041, in a fourth archive.
3. **A 26×8-em cap on the balloon's text width is own-design.** The reference
   scales the frame to the measured text with no wrap, so one long Chinese line
   runs off the canvas. Declared at the call site.

**Not proven here: that the balloon looks right on screen.** That is W-037.

**Left open:** the quest-marker condition in `Npc.cpp:280`
(`quest_icon_type < 0`) has no counterpart — this simulator has no quest log, so
it is always true. Stated in the code rather than quietly dropped.
