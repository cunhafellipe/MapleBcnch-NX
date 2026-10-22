"""W-058 end-to-end: make a body stand on a real portal and prove the transfer.

The path fix is already verified (Map1/100000003 -> 200, Map3/100000003 -> 404).
What is NOT verified is that walking into the door actually changes the map.

Why the earlier attempt was inconclusive: `FootholdPhysics.FindPortal` (:581-590)
only fires inside PortalReachX=15, PortalReachY=12 of the portal, and a body spawned
ON the portal settles ~21px away on the foothold -- just outside. So TakePortal
never ran, and `/debug/portal` reporting no failure could not tell "never fired"
from "fired and worked".

This sweeps the spawn X until the settled body is genuinely inside the portal
box, then holds Up and reads `frame.transfer` (:2878).

CONTROL: the portal's own coordinates are re-read from the archive first, and the
script prints the measured reach so the reader can check the arithmetic rather
than trust it.
"""
import json, urllib.error, urllib.parse, urllib.request

BASE = "http://127.0.0.1:5591"
MAP = "f4/Map/Map1/100000004.img"
REACH_X, REACH_Y = 15, 12


def get(route, **q):
    url = f"{BASE}{route}?" + urllib.parse.urlencode(q)
    try:
        with urllib.request.urlopen(url, timeout=300) as r:
            return r.status, json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        return e.code, None


def post(route, body):
    req = urllib.request.Request(f"{BASE}{route}", data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=600) as r:
            return r.status, json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        return e.code, None


def kids(path):
    st, d = get("/api/children", path=path)
    return d if st == 200 and d else []


def scalar(path):
    _, d = get("/api/inspect", path=path)
    v = ((d or {}).get("node") or {}).get("value")
    return int(v) if v not in (None, "") else None


# CONTROL: archives open and the map resolves.
st, files = get("/api/files")
if st != 200 or get("/api/inspect", path=MAP)[0] != 200:
    raise SystemExit("VOID: archives not open / map unresolvable.")
print(f"CONTROL  {len(files)} archives open; {MAP} -> 200")

exits = []
for p in kids(f"{MAP}/portal"):
    tm, px, py = (scalar(f"{MAP}/portal/{p['name']}/{k}") for k in ("tm", "x", "y"))
    pt = scalar(f"{MAP}/portal/{p['name']}/pt")
    if tm and tm not in (0, 999999999):
        exits.append((p["name"], tm, px, py, pt))
print(f"  exit portals: " + ", ".join(f"{n}->{m}@({x},{y}) pt={t}" for n, m, x, y, t in exits))
name, target, PX, PY, _ = min(exits, key=lambda e: abs(e[3] - (-25)))
print(f"  using portal {name}: x={PX} y={PY}; reach is +/-{REACH_X} x +/-{REACH_Y}")

# Sweep the spawn until the SETTLED body is inside the portal box.
print("\n=== sweep: which spawn lands the body inside the portal? ===")
inside = []
for sx in range(PX - 90, PX + 31, 5):
    _, sess = post("/api/simulate/session",
                   {"path": MAP, "weapon": "1302000.img", "equippedWeapon": "1302000",
                    "invulnerable": True, "startX": sx, "startY": PY})
    if not sess:
        continue
    sid = sess["sessionId"]
    _, fr = post(f"/api/simulate/session/{sid}/frames",
                 {"count": 1, "first": {}, "then": {}})
    if not fr:
        continue
    dx, dy = abs(fr["x"] - PX), abs(fr["y"] - PY)
    hit = dx < REACH_X and dy < REACH_Y
    if hit:
        inside.append((sx, sid, fr["x"], fr["y"]))
    if -60 <= sx - PX <= 60:
        print(f"  spawnX={sx:>5} -> settled ({fr['x']:>5},{fr['y']:>5})  "
              f"dx={dx:>3} dy={dy:>3}  {'INSIDE' if hit else ''}")

if not inside:
    print("  no SPAWN lands inside the portal box on this map -- the foothold "
          "probe snaps to 1800 or 1890 and the portal is at 1911. That is a "
          "property of the geometry, not of the spawn, so walk onto it instead.")

sx, sid, bx, by = inside[0] if inside else (PX, None, None, None)
if not inside:
    # Spawning cannot do it -- the foothold probe snaps the body to 1800 or 1890
    # and the portal is at 1911, so no spawn is ever inside the box. That is a
    # property of this map's geometry, not of the spawn. WALK onto it instead,
    # checking every frame so the body is caught inside the box rather than
    # stepped over it.
    _, sess = post("/api/simulate/session",
                   {"path": MAP, "weapon": "1302000.img", "equippedWeapon": "1302000",
                    "invulnerable": True, "startX": PX - 21, "startY": PY})
    sid = sess["sessionId"]
    _, fr = post(f"/api/simulate/session/{sid}/frames",
                 {"count": 1, "first": {}, "then": {}})
    print(f"\n  spawn settles at ({fr['x']},{fr['y']}), dx={abs(fr['x']-PX)} -- "
          f"outside the box, so WALKING onto the portal")
    sx = PX - 21
else:
    print(f"\n  spawnX={sx} settles at ({bx},{by}) -- inside the portal. Holding Up.")

if sid is None:
    raise SystemExit("VOID: no session")

print("\n=== walk onto the portal, then hold Up ===")
transfer = None
walked = []
for i in range(200):
    # Read the body, then CHOOSE the key for the next frame from where it
    # actually is. Holding `up` alone never moves anyone.
    if not transfer:
        _, cur = post(f"/api/simulate/session/{sid}/frames",
                      {"count": 1, "first": {"up": True}, "then": {}})
    else:
        break
    cx = cur.get("x")
    inside_box = abs(cx - PX) < REACH_X
    if inside_box:
        key = {"up": True}
    elif cx < PX:
        key = {"right": True}
    else:
        key = {"left": True}

    _, fr = post(f"/api/simulate/session/{sid}/frames",
                 {"count": 1, "first": key, "then": {}})
    if fr.get("transfer"):
        transfer = fr["transfer"]
        print(f"  frame {i}: TRANSFER {json.dumps(transfer)}")
        break
    x, y = fr.get("x"), fr.get("y")
    dx = abs(x - PX)
    walked.append((i, x, y, dx))
    if i < 6 or i % 15 == 0:
        print(f"  frame {i}: sent {json.dumps(key):<14} -> x={x} y={y} dx={dx} "
              f"onGround={fr.get('onGround')}")

print(f"  closest approach over the run: {min((d for *_, d in walked), default='n/a')}")
_, dbg = get("/api/simulate/debug/portal")
print(f"  /debug/portal: {json.dumps(dbg)}")

if transfer:
    for i in range(4):
        _, fr = post(f"/api/simulate/session/{sid}/frames",
                     {"count": 1, "first": {}, "then": {}})
    print(f"  after settling: x={fr.get('x')} y={fr.get('y')}")
    print("\nVERDICT: the portal changed the session's map.")
else:
    print("\nVERDICT: >>> no transfer. The fix changes the path but the chain is "
          "still not demonstrated.")