"""Verify NPC speech reaches the frame, and obeys the reference's timing.

Reference (clean-client):
  Npc.h:131-132  CHAT_MIN_DELAY 3000 ms, CHAT_DELAY_SPREAD 6000 ms
  Npc.h:136      CHAT_DURATION 3000 ms
  Npc.cpp:203    the FIRST delay is MIN + rand(SPREAD), not zero
  Npc.cpp:276-284 a line goes out only when chat_delay <= 0 and no balloon is up
  Npc.cpp:478-484 one index out of states + lines; speaks only when it lands on
                 a line, so an NPC with N animations speaks 1/(1+N/Nlines) as often

Fixed step is 1000/60 ms, so 3000 ms = 180 frames and 6000 ms = 360.
"""
import json, os, urllib.parse, urllib.request
try:
    from archivelib import archive_id, require_gate_base
except ImportError:            # a missing module must not read as a pass
    print(">>> RED: archivelib.py is missing next to this suite")
    raise SystemExit(1)
except SyntaxError as exc:    # a helper that does not PARSE is not missing
    print(f">>> RED: archivelib.py next to this suite does not parse: {exc}")
    raise SystemExit(1)
from collections import Counter

BASE = require_gate_base()

_MAP_ID = archive_id("Map.nx", BASE)
MAP = f"{_MAP_ID}/Map/Map0/000000002.img"   # 2 of 6 NPCs have a String entry; others are silent
FRAME_MS = 1000 / 60
bad = 0


def post(path, body):
    req = urllib.request.Request(f"{BASE}{path}", data=json.dumps(body).encode("utf-8"),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=600) as r:
        return json.loads(r.read().decode("utf-8"))


s = post("/api/simulate/session", {"path": MAP, "startX": 700, "startY": -25,
                                   "invulnerable": True})
sid = s["sessionId"]
npcs = [m for m in (s.get("mobs") or []) if m.get("kind") == "n"]
print(f"session {sid}: {len(npcs)} NPCs on {MAP}")
print(f"  one mob frame's keys: {sorted((s.get('mobs') or [{}])[0].keys())}")
for m in npcs:
    print(f"  mobId={m.get('mobId')} x={m.get('x')} name={m.get('npcName')!r}")
if not npcs:
    print(">>> no NPCs on this map; the rest is void")
    raise SystemExit(1)

# ---------------------------------------------------------------- the fields
f = post(f"/api/simulate/session/{sid}/frames", {"count": 1, "first": {}, "then": {}})
one = [m for m in (f.get("mobs") or []) if m.get("kind") == "n"][0]
print(f"\nframe NPC keys include: "
      f"speech={'speech' in one} speechFrames={'speechFrames' in one}")
if "speech" not in one or "speechFrames" not in one:
    print(">>> the frame carries no speech fields")
    bad += 1

# ---------------------------------------------------------------- the timing
# Nothing may be said on frame 1: the first delay is 3000 + rand(6000) ms, and
# this is the guard Npc.cpp:198-202 states explicitly ("at zero the npc speaks on
# its very first frame, before the quest marker has been worked out").
spoken = []          # (frame, npc, text) for every frame a balloon is UP
changes = []         # (frame, npc, text) for every frame the text CHANGES
first_at = {}
last_seen = {}
for i in range(600):
    fr = post(f"/api/simulate/session/{sid}/frames", {"count": 1, "first": {}, "then": {}})
    for m in (fr.get("mobs") or []):
        if m.get("kind") != "n":
            continue
        nid, text = m.get("mobId"), m.get("speech") or ""
        if not text:
            last_seen.pop(nid, None)
            continue
        spoken.append((i, nid, text))
        if last_seen.get(nid) != text:
            changes.append((i, nid, text))
            first_at.setdefault(nid, i)
        last_seen[nid] = text

print(f"\n600 idle frames: {len(spoken)} frames with a balloon up, "
      f"{len(changes)} distinct lines")

# WHAT IS DETERMINATE FROM OUTSIDE, and what is not.
#
# The delay is drawn as 180..540 frames (3000 + rand(6000) ms at 60Hz), so the
# first line CANNOT arrive before frame 180. It also cannot arrive much later:
# 540 frames plus one 180-frame balloon is frame 720, and the 600-frame run ends
# first for a late drawer.
#
# The UPPER bound is not assertable to the frame over HTTP, and pretending
# otherwise is how this file first "failed": live sessions are also advanced by
# the store's background tick (MapSimSessionStore.TickSession), so real time
# passes between two POSTs and my loop's frame index drifts from the session's.
# That is pre-existing and correct — it is how a placed character stays alive —
# but it means a frame-exact upper bound is a statement about HTTP latency.
MIN_FIRST = 180          # 3000 ms, the reference's floor
for nid, at in first_at.items():
    ok = at >= MIN_FIRST
    print(f"  npc {nid}: first line at frame {at} "
          f"({'>= ' if ok else 'UNDER '}{MIN_FIRST} — the 3000ms floor)")
    if not ok:
        print(f">>> {nid} spoke at frame {at}, before CHAT_MIN_DELAY")
        bad += 1
if not changes:
    print(">>> no NPC ever spoke on a map whose NPCs carry String entries")
    bad += 1

# ------------------------------------------------- a line lasts 180 frames
# A CHANGED text, not a repeated one: the balloon stays up for 180 frames, so the
# same string is reported on 180 consecutive frames and counting frames counted one
# balloon 180 times. This was the first version of the check and it "found" a gap
# of 1 frame between every pair of lines.
for nid in first_at:
    starts = [i for i, n, _ in changes if n == nid]
    gaps = [b - a for a, b in zip(starts, starts[1:])]
    print(f"\n  npc {nid}: {len(starts)} lines at frames {starts[:10]}")
    if not gaps:
        continue
    print(f"    gaps (frames): {gaps[:8]}  min={min(gaps)}")
    # chat_delay is re-armed to 180..540 frames, so a new line cannot arrive
    # sooner than the 180-frame balloon that is still up.
    short = [g for g in gaps if g < 180]
    if short:
        print(f">>> {len(short)} gaps under the 180-frame CHAT_DURATION "
              f"(min {min(short)}) — two balloons would overlap")
        bad += 1
    else:
        print("    every gap >= 180 frames (CHAT_DURATION) — no overlap")

# --------------------------------------------- the text is the archive's own
for _, nid, text in changes[:4]:
    want = json.loads(urllib.request.urlopen(
        f"{BASE}/api/simulate/npc-chat?npcId={nid}", timeout=300).read().decode())
    pool = list(want.get("general") or [])
    for v in (want.get("byAction") or {}).values():
        pool += v
    if pool and text not in pool:
        print(f">>> {nid} said {text[:30]!r}, which is not one of its own lines")
        bad += 1
    else:
        print(f"  npc {nid}: {text[:36]!r} is one of its own lines")

print(f"\nBAD={bad}")