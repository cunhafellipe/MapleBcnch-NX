"""END TO END: bind a key to a skill -> press it -> does it cast, and does it hurt?

This walks exactly the chain the UI performs, using exactly the request shapes the
UI sends:

  1  POST /api/simulate/session                 create the body
  2  POST /api/simulate/session/{id}/skills     the SKILL BINDER (the "Skills…" panel)
  3  skillFor(key)          -> {key, skillId}   from the map the BINDER RESPONSE built
  4  onKeyDown              -> skillDown        = skill.skillId     (W-065 fixed this)
  5  POST .../frames  {count, first:{skillId}, then:{skillId:null}}
  6  server: CastSkill -> ApplySkillHits -> DamageRoll -> PendingDamage
  7  the frame's `damage[]` is what the browser draws as a number

Step 3/4 are the CLIENT. They are reproduced here from the same code the browser
runs (`livewalk.js skillFor` / `onKeyDown`), read out of the source rather than
re-implemented, so this answers "is the logic implemented" rather than "does my
copy of the logic work".
"""
import json, os, pathlib, re, sys, urllib.request
try:
    from archivelib import archive_id
except ImportError:            # a missing module must not read as a pass
    print(">>> RED: archivelib.py is missing next to this suite")
    raise SystemExit(1)


def _repo_root():
    """The repository this suite belongs to: the nearest ancestor of THIS file
    that holds a `MapleBench` directory.

    It used to be an absolute path, and a suite run from a CLONE therefore read
    the WORKING TREE's client source -- which other agents were editing at the
    time (measured 2026-10-22). A clone must read the clone, so the path is
    derived from the file's own location and never from the current working
    directory or an environment variable. If no ancestor holds a `MapleBench`,
    the suite says so and stops rather than reading somebody else's tree."""
    for parent in pathlib.Path(__file__).resolve().parents:
        if (parent / "MapleBench").is_dir():
            return parent
    raise SystemExit("skill-e2e: no ancestor of this file holds a MapleBench "
                     "directory, so the client source it checks cannot be found")


REPO_ROOT = _repo_root()
# The same override the gate itself uses (gate.ps1:50), so a gate pointed at
# another instance reaches this suite too. The default is the port the suites
# have always used.
BASE = os.environ.get("GATE_BASE") or "http://127.0.0.1:5591"

_MAP_ID = archive_id("Map.nx", BASE)
MAP = f"{_MAP_ID}/Map/Map1/100000004.img"

SKILL = "1121008"          # MultiStrike = false -> the branch that can damage
KEY = "K"

asserts = fails = 0


def Check(name, ok, detail=""):
    global asserts, fails
    asserts += 1
    print(f"  {'ok  ' if ok else 'FAIL'} {name}" + (f"  -- {detail}" if detail else ""))
    if not ok:
        fails += 1
        print(f">>> RED: skill-e2e -- {name} ({detail})")


def call(route, body=None, method=None):
    data = json.dumps(body).encode() if body is not None else None
    h = {"Content-Type": "application/json"} if data else {}
    req = urllib.request.Request(BASE + route, data=data, headers=h,
                                 method=method or ("POST" if data else "GET"))
    try:
        with urllib.request.urlopen(req, timeout=900) as r:
            return r.status, json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        raw = e.read().decode("utf-8", "replace")
        try:
            return e.code, json.loads(raw)
        except Exception:
            return e.code, {"raw": raw[:300]}


def step(sid, keys, count=1):
    return call(f"/api/simulate/session/{sid}/frames",
                {"count": count, "first": keys, "then": {"skillId": None}})[1]


# ---- read the CLIENT half out of the source, do not re-implement it -------------
# The CLIENT source of THIS repository -- the clone's, when the suite runs from a
# clone. See _repo_root() above for why this is not an absolute path.
livewalk = (REPO_ROOT / "MapleBench" / "wwwroot" / "js" / "livewalk.js").read_text(
    encoding="utf-8")

print("=== the client half, read out of livewalk.js ===")
m = re.search(r"onKeyDown\(e\)\s*\{(.*?)\n  \}", livewalk, re.S)
body = m.group(1) if m else ""
armed_by_id = bool(re.search(r"this\.skillDown\s*=\s*skill\.skillId\s*;", body))
armed_by_key = bool(re.search(r"this\.skillDown\s*=\s*skill\.key\s*;", body))
print(f"  onKeyDown arms skillDown with skill.skillId : {armed_by_id}")
print(f"  onKeyDown arms skillDown with skill.key     : {armed_by_key}")
sends = re.search(r"\.\.\.\(skill \? \{ skillId: skill \}", livewalk)
print(f"  the frame batch sends it as the skillId field: {bool(sends)}")
Check("the client arms the numeric skill id, not the key letter", armed_by_id and not armed_by_key,
      f"id={armed_by_id} key={armed_by_key}")
Check("the frame batch sends that value as skillId", bool(sends), "not found")

# ---- 1. a body ---------------------------------------------------------------
print()
print("=== 1. a character on the test map ===")
code, s = call("/api/simulate/session", {"path": MAP, "startX": 1710, "startY": -25,
                                         "weapon": "01302000.img",
                                         "equippedWeapon": "01302000",
                                         "invulnerable": True})
sid = s.get("sessionId")
Check("a session was created", code == 200 and bool(sid), f"HTTP {code}: {str(s)[:120]}")

# walk to a mob, as a player would
mob0 = next((m for m in s["mobs"] if m.get("kind") == "m"
             and abs(m["y"] - (-25)) < 3), None)
last = {"x": 1710}
for _ in range(400):
    k = {"right": True} if last["x"] < mob0["x"] - 30 else {"left": True}
    last = step(sid, k)
    if abs(last["x"] - mob0["x"]) <= 36:
        break
alive_mob = min((m for m in last["mobs"] if m.get("kind") == "m"),
                key=lambda m: abs(m["x"] - last["x"]))
print(f"  standing at x={last['x']}, nearest mob {alive_mob['mobId']} at x={alive_mob['x']}, "
      f"hp {alive_mob['hp']}/{alive_mob['maxHp']}")
# Every mob's HP before the cast, keyed by id -- so the victim can be looked up
# afterwards instead of assumed to be the nearest one.
hp_before_by_id = {m["mobId"]: m["hp"] for m in last["mobs"]}

# ---- 2. THE SKILL BINDER -----------------------------------------------------
print()
print(f"=== 2. the Skills… panel binds {SKILL} to the key {KEY} ===")
code, bind = call(f"/api/simulate/session/{sid}/skills",
                  {"bindings": [{"skillId": SKILL, "key": KEY}]})
# The response shape is `{"bindings": {"K": "1121008"}, "count": 1, "rejected": []}`.
# The first version of this probe asked for `bound` and `hotkeys`, neither of
# which exists, and reported RED against a binding that had in fact succeeded.
print(f"  HTTP {code}  bindings={bind.get('bindings')}  count={bind.get('count')} "
      f" rejected={bind.get('rejected')}")
Check("the panel accepted the binding", code == 200 and bind.get("count", 0) >= 1,
      str(bind)[:200])
Check("it was not rejected", not bind.get("rejected"), str(bind.get("rejected")))

# ---- 3/4. what the client builds from that response --------------------------
print()
print(f"=== 3/4. pressing {KEY}: what the client does with the binding ===")
skillKeys = dict(bind.get("bindings") or {})
print(f"  the client's skillKeys map is built from the response: {skillKeys}")
skill_for = skillKeys.get(KEY.upper())
Check("the pressed key resolves to the skill id", skill_for == SKILL,
      f"{KEY} -> {skill_for!r}")
# isdigit(), and the message only when it is NOT -- printing the failure text on a
# pass is its own small lie.
numeric = skill_for is not None and skill_for.isdigit()
Check("what the client would send as skillId is a NUMBER the server can parse",
      numeric, f"{skill_for!r} does not survive int.TryParse")

# ---- 5/6. the cast -----------------------------------------------------------
print()
print("=== 5/6. the cast, and what it does to the mob ===")
f = step(sid, {"skillId": skill_for})
print(f"  action      = {f.get('action')!r}")
print(f"  skillId     = {f.get('skillId')!r}")
print(f"  skillStance = {f.get('skillStance')!r}   <- the pose the character plays")
damage = f.get("damage") or []
print(f"  damage      = {[(d['mobId'], d['value']) for d in damage]}")
Check("the character entered the skill pose", f.get("action") == "skill",
      f"action={f.get('action')!r}")
Check("the frame names the skill that was cast", f.get("skillId") == SKILL,
      f"skillId={f.get('skillId')!r}")
Check("a pose was chosen from the archive", bool(f.get("skillStance")),
      f"skillStance={f.get('skillStance')!r}")
Check("the cast dealt damage", len(damage) >= 1, f"damage={damage}")

# ---- the mob actually lost HP ------------------------------------------------
print()
print("=== 7. the HP of the mob that ACTUALLY took the hit ===")
# The victim is named by the damage entry, NOT by proximity to the player.
# The first version picked the mob nearest the body and found it untouched: on
# this map 2110200 stands at x=2079, nearer the character at x=2057 than the
# 2220100 at x=2091, and the skill's collision box covers the latter. That is
# correct behaviour -- a skill's box picks the target, not distance -- and the
# probe was asserting something else.
victim_id = damage[0]["mobId"] if damage else None
victim = next((m for m in f["mobs"] if m.get("mobId") == victim_id), None)
hp_after = victim["hp"] if victim else None
print(f"  the cast reported damage on mob {victim_id}")
print(f"  mob {victim_id}: {hp_before_by_id.get(victim_id)} -> {hp_after}")
Check("the struck mob's HP dropped",
      hp_after is not None and hp_after < hp_before_by_id.get(victim_id, 10 ** 9),
      f"{victim_id}: {hp_before_by_id.get(victim_id)} -> {hp_after}")

if damage:
    v = damage[0]["value"]
    Check("the damage number is a real number, not the old 50000..750000 random",
          1 <= v < 50000 or v < hp_before,
          f"value={v}")

# ---- and it is bounded by the skill's own node --------------------------------
plan = call("/api/skill-cast/plan?skillId=" + SKILL)[1]
print(f"  the archive's own level/{plan.get('skillLevel')}/damage = {plan.get('damage')}")
if damage and plan.get("damage"):
    v = damage[0]["value"]
    Check("the damage is bounded by the skill's own damage node",
          1 <= v <= plan["damage"],
          f"dealt {v}, skill node says {plan['damage']}")

print()
print(f"VERDICT: skill-e2e {asserts} assertions, {fails} failed")
if fails:
    sys.exit(1)