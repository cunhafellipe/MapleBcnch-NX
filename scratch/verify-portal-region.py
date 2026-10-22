"""W-041 adjacent: does `Map{ToMap % 100}` name a folder the archive has?

Two sites disagree about how a map id becomes its region folder:
  MapEditorService.Minimap.cs:118   mapId.ToString()[0]      -- FIRST digit
  MapSimSessionStore.cs:1638        portal.ToMap % 100       -- LAST TWO digits

For the test map 100000004 the first gives Map1 and the second Map4. The archive
path for that map is f4/Map/Map1/100000004.img, so one of them must be wrong.
This reads the map's OWN portals and asks the archive which spelling resolves --
no arithmetic assumed.

CONTROL first: a path known to exist, so a run that reads everything as absent
is void rather than a finding.
"""
import json, urllib.error, urllib.parse, urllib.request

BASE = "http://127.0.0.1:5591"
MAP = "f4/Map/Map1/100000004.img"


def status(path):
    url = f"{BASE}/api/inspect?path=" + urllib.parse.quote(path, safe="")
    try:
        with urllib.request.urlopen(url, timeout=300) as r:
            return r.status
    except urllib.error.HTTPError as e:
        return e.code


def kids(path):
    url = f"{BASE}/api/children?path=" + urllib.parse.quote(path, safe="")
    try:
        with urllib.request.urlopen(url, timeout=300) as r:
            return json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError:
        return []


print(f"CONTROL  inspect {MAP} -> {status(MAP)}  (must be 200)")
if status(MAP) != 200:
    raise SystemExit("VOID: control did not resolve.")

# Where do portal targets live? Walk down until a container of portals appears.
print("\n=== the test map's portal targets ===")
node = MAP
targets = []
for _ in range(6):
    ch = kids(node)
    names = [c["name"] for c in ch]
    if not names:
        break
    if any(n == "portal" for n in names):
        node = f"{node}/portal"
        break
    print(f"  {node}: {len(names)} children, first few {names[:6]}")
    node = f"{node}/{names[0]}"

portals = kids(f"{node}/portal") if kids(f"{node}/portal") else kids(node)
print(f"  portal container: {node}, {len(portals)} entries")
seen = {}
for p in portals[:400]:
    for key in ("tm", "pn", "tn", "pt", "type"):
        url = (f"{BASE}/api/inspect?path="
               + urllib.parse.quote(f"{node}/{p['name']}/{key}", safe=""))
        try:
            with urllib.request.urlopen(url, timeout=60) as r:
                d = json.loads(r.read().decode("utf-8"))
                seen.setdefault(key, []).append(d.get("node", {}).get("value"))
        except urllib.error.HTTPError:
            pass
for k, v in seen.items():
    vals = [x for x in v if x is not None]
    print(f"  portal/{k}: {len(vals)} read, sample {vals[:8]}")

# Now the decisive comparison, using the map ids we actually saw.
print("\n=== which spelling does the archive answer? ===")
for tm in sorted({int(x) for x in seen.get("tm", []) if x and str(x).isdigit()
                  and int(x) not in (0, 999999999)})[:8]:
    s = f"{tm:09d}"
    first = f"f4/Map/Map{s[0]}/{s}.img"
    mod = f"f4/Map/Map{tm % 100}/{s}.img"
    a, b = status(first), status(mod)
    print(f"  tm={s}")
    print(f"    first-digit  Map{s[0]}/{s}.img  -> {a}  "
          f"{'OK' if a == 200 else 'ABSENT'}")
    print(f"    ToMap %% 100  Map{tm % 100}/{s}.img  -> {b}  "
          f"{'OK' if b == 200 else 'ABSENT'}"
          + ("   <-- same folder" if a == b else ""))