"""W-041: independently re-measure the two load-bearing id-width claims.

The sweep reported:
  1. f7/Special/0910.img holds 538 SEVEN-digit entry keys, so IconService's
     D8-only lookup misses all of them.
  2. f8/000.img/skill holds SEVEN-digit keys (0000008), so database.js's unpadded
     entry and its 4-wide book miss 25 of 535.

Both are re-derived here from the live server rather than trusted. 404 means
absent. Python, because PowerShell flattens big arrays (TRAPS T-12).
"""
import json, urllib.error, urllib.parse, urllib.request

BASE = "http://127.0.0.1:5591"


def get(path):
    """Returns (status, parsed). 404 is ABSENT, not an error."""
    url = f"{BASE}/api/inspect?path=" + urllib.parse.quote(path, safe="")
    try:
        with urllib.request.urlopen(url, timeout=300) as r:
            return r.status, json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        return e.code, None


def kids(path):
    """Returns the child list, or [] when the path 404s. A 404 here means the
    SHAPE I guessed is wrong -- not that the archive is empty -- so the caller
    must probe before concluding anything from an empty list."""
    url = f"{BASE}/api/children?path=" + urllib.parse.quote(path, safe="")
    try:
        with urllib.request.urlopen(url, timeout=300) as r:
            return json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError:
        return []


# CONTROL: a spelling that is known to exist, so a run that reads everything as
# absent is void rather than a finding. (R-9 -- calibrate before trusting.)
ctl_status, ctl = get("f5/9400708.img/info/level")
print(f"CONTROL  f5/9400708.img/info/level -> {ctl_status} "
      f"(must be 200; a run that cannot see this measures nothing)")
if ctl_status != 200:
    raise SystemExit("VOID: the control did not resolve.")

print("\n=== claim 1: f7/Special/0910.img entry keys are 7-digit, not 8 ===")
sp = kids("f7/Special/0910.img")
names = [c["name"] for c in sp]
widths = {}
for n in names:
    widths[len(n)] = widths.get(len(n), 0) + 1
print(f"  {len(names)} children, widths {widths}")
for probe in ("9100000", "09100000"):
    st, _ = get(f"f7/Special/0910.img/{probe}")
    print(f"  inspect f7/Special/0910.img/{probe:<9} -> {st}"
          f"   ({'present' if st == 200 else 'ABSENT'})")

print("\n=== claim 1b: the ordinary item groups really are 8-digit ===")
# Probe the shape first: these may be folders holding .img files rather than
# images themselves, and guessing wrong yields an empty list that reads like
# "no data" (TRAPS T-8).
for probe in ("f7/Consume", "f7/Consume.img"):
    print(f"  /api/children f7/{probe:<14} -> {len(kids(probe))} children")
grp = next((g["name"] for g in kids("f7/Consume")), None)
if grp:
    g = kids(f"f7/Consume/{grp}")
    w = {}
    for c in g:
        w[len(c["name"])] = w.get(len(c["name"]), 0) + 1
    print(f"  f7/Consume/{grp}: {len(g)} entry keys, widths {w}")

print("\n=== claim 2: f8/000.img/skill keys are 7-digit zero-padded ===")
sk = kids("f8/000.img/skill")
snames = [c["name"] for c in sk]
sw = {}
for n in snames:
    sw[len(n)] = sw.get(len(n), 0) + 1
print(f"  f8/000.img/skill: {len(snames)} children, widths {sw}")
print(f"  sample: {snames[:8]}")
for probe in ("0000008", "8"):
    st, _ = get(f"f8/000.img/skill/{probe}")
    print(f"  inspect f8/000.img/skill/{probe:<9} -> {st}"
          f"   ({'present' if st == 200 else 'ABSENT'})")

print("\n=== claim 2b: the book stem is 3-wide, and database.js never tries it ===")
st, _ = get("f8/000.img/skill/0000008/level/1")
print(f"  f8/000.img exists as a 3-wide book -> "
      f"{'yes' if get('f8/000.img/skill/0000008')[0] == 200 else 'no'}")
for book in ("000.img", "0000.img", "0.img"):
    st, _ = get(f"f8/{book}/skill/0000008")
    print(f"  inspect f8/{book:<9}/skill/0000008 -> {st}"
          f"   ({'present' if st == 200 else 'ABSENT'})")