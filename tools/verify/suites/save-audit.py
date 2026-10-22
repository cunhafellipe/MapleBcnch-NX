"""W-066 — verify the save pipeline's promise, on a COPY, with a known-answer control.

The README says: "MapleBench verifies its output before replacing a destination
and normally creates timestamped backups", and "edits remain in memory until you
save".

THIS NEVER TOUCHES C:\\MUWORK\\GAME\\MPSHS.
It copies the SMALLEST real archive (Etc.nx, 3.5 MB) into a scratch folder, opens
the COPY, and every md5 of the real data root is asserted before and after.

Every check prints ok/FAIL and a failure prints `>>> RED`, because gate.ps1 reads
only explicit markers (pm/GATE.md:64-83).
"""
import hashlib, json, os, shutil, sys, tempfile, time, urllib.request, urllib.parse, uuid

BASE = "http://127.0.0.1:5591"
LIVE = r"C:\MUWORK\GAME\MPSHS"
SUBJECT = "Etc.nx"
# A fresh folder every run, and never rmtree an old one: an NX archive is held
# open as a MEMORY-MAPPED section, so overwriting yesterday's copy fails with
# WinError 1224 "the user-mapped section is open". That error is itself evidence
# -- it is why WzSaveService.SaveNx has to call `remapped.Release()` around the
# swap (WzSaveService.cs:160-165): Windows will not rename a mapped file.
# A fresh, UNIQUE folder every run -- and the file inside it is unique too.
# Seconds are not enough resolution: the server keeps every archive it opened
# memory-mapped, and a mapped file cannot be reopened for writing by this
# process (measured: a plain open(f,'wb') on the file the previous run saved
# fails with Errno 22). Reusing a path an earlier run's archive still holds is
# therefore guaranteed to fail eventually, not merely likely to.
RUN_TAG = time.strftime("%Y%m%d-%H%M%S") + "-" + uuid.uuid4().hex[:6]
SCRATCH = os.path.join(tempfile.gettempdir(), "maplebench-save-audit-" + RUN_TAG)

asserts = 0
fails = 0


def check(name, ok, detail=""):
    global asserts, fails
    asserts += 1
    if ok:
        print(f"  ok    {name}")
    else:
        fails += 1
        print(f"  FAIL  {name} -- {detail}")
        print(f">>> RED: save-audit -- {name} ({detail})")


def md5(p):
    h = hashlib.md5()
    with open(p, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def copy_archived(src, dst, attempts=4):
    """Copy WITHOUT shutil.copy2, and never FROM a file the server has open.

    `shutil.copy2` calls CopyFile, which refuses a file whose USER-MAPPED SECTION
    is open -- and every open NX archive is memory-mapped by NxArchive. Measured
    on Etc.nx (which open-archives.py holds as f2): 3 runs, 1 died at the copy
    step with WinError 1224 before a single assertion ran; after switching to a
    plain read/write handle a different run died with Errno 22 on the same step.
    Both are the same root cause -- the SOURCE is a live mapping -- and neither is
    a statement about the code under test.

    So the live archive is copied ONCE into a staging file that nothing has open,
    with retries, and every run copies from the staging file. What is under test
    is the SAVE, and the save only ever touches the per-run copy.
    """
    last = None
    for attempt in range(attempts):
        try:
            with open(src, "rb") as r, open(dst, "wb") as w:
                shutil.copyfileobj(r, w, length=1 << 20)
            shutil.copystat(src, dst)
            return
        except OSError as e:
            last = e
            print(f"    copy attempt {attempt+1}/{attempts} failed: {type(e).__name__} {e} "
                  f"(src={src!r} dst={dst!r})")
            time.sleep(0.5 * (attempt + 1))
    raise RuntimeError(f"could not stage {src!r} after {attempts} attempts: {last!r}")


STAGING = os.path.join(tempfile.gettempdir(), "maplebench-save-audit-staging")
STAGED = os.path.join(STAGING, SUBJECT)


def call(route, body=None, method=None):
    url = BASE + route
    data = None
    headers = {}
    if body is not None:
        data = json.dumps(body).encode("utf-8")
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, headers=headers,
                                 method=method or ("POST" if data else "GET"))
    try:
        with urllib.request.urlopen(req, timeout=1800) as r:
            return json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        return {"__http__": e.code, "body": e.read().decode("utf-8", "replace")[:400]}


def kids(p):
    r = call("/api/children?" + urllib.parse.urlencode({"path": p}))
    return [] if isinstance(r, dict) else (r or [])


def value(p):
    n = (call("/api/inspect?" + urllib.parse.urlencode({"path": p})) or {}).get("node") or {}
    return n.get("value"), n.get("type")


print("=== setup: a COPY, never the live archive ===")
os.makedirs(SCRATCH, exist_ok=True)
# Stage the live archive ONCE, into a file nothing has open. Reading it on every
# run is what produced the two different setup failures above.
if not os.path.exists(STAGED):
    os.makedirs(STAGING, exist_ok=True)
    copy_archived(os.path.join(LIVE, SUBJECT), STAGED + ".part")
    os.replace(STAGED + ".part", STAGED)
check("the staged source is byte-identical to the live archive",
      md5(STAGED) == md5(os.path.join(LIVE, SUBJECT)), "staging copy differs")
check("the staged source is not open in this session",
      all(x.get("filePath") != STAGED for x in (call("/api/files") or [])), "the server has it open")
copy_path = os.path.join(SCRATCH, SUBJECT)
copy_archived(STAGED, copy_path)
live_before = md5(os.path.join(LIVE, SUBJECT))
copy_before = md5(copy_path)
print(f"  scratch : {SCRATCH}")
print(f"  subject : {SUBJECT}  {os.path.getsize(copy_path)/1024/1024:.1f} MB")
print(f"  live    md5 {live_before}")
print(f"  copy    md5 {copy_before}")
check("the copy is byte-identical to the original", copy_before == live_before)

print()
print("=== open the COPY (read/write) ===")
opened = call("/api/files/open", {"path": copy_path})
if "__http__" in opened:
    print(f">>> RED: save-audit -- could not open the copy: {opened}")
    raise SystemExit(1)
fid = opened.get("id")
print(f"  opened as {fid}  kind={opened.get('kind')}  version={opened.get('mapleVersion')}")
check("the copy opened as NX", opened.get("kind") == "nx", f"kind={opened.get('kind')}")

# Pick a real scalar to edit. Never guess a node name (W-041 clause 2), and do not
# assume one exists at the top: an NX archive's top level is images/directories,
# so walk down until a leaf scalar turns up.
SCALARS = ("Long", "Short", "Int", "Float", "Double", "String", "Ushort", "Byte")


def first_scalar(container, depth=0, maxdepth=3):
    if depth > maxdepth:
        return None
    ch = kids(container)
    # A leaf scalar. NOTE the discriminator is `hasChildren`, NOT `childCount`:
    # an NX image reports childCount = -1 ("not counted") and hasChildren = true,
    # so `childCount > 0` skips every image in the archive and the search walks
    # straight past the whole tree. Measured on the Etc.nx copy.
    for c in ch:
        if not c.get("hasChildren") and c.get("type") in SCALARS:
            return c
    for c in ch:
        if c.get("hasChildren"):
            found = first_scalar(c["path"], depth + 1, maxdepth)
            if found is not None:
                return found
    return None


target = first_scalar(fid)
if target is None:
    print(f">>> RED: save-audit -- no scalar node within depth 3 under {fid} to edit")
    raise SystemExit(1)
path = target["path"]
old_value, old_type = value(path)
print(f"  editing {path}")
print(f"    before: {old_value!r} ({old_type})")

# A value that cannot collide with what is already there.
if old_type == "String":
    new_value = "maplebench-save-audit-7f3c1a"
elif old_type in ("Long", "Int", "Short", "Ushort", "Byte"):
    new_value = "987654321"
else:
    new_value = "4242.5"

print()
print("=== CONTROL: the edit is in memory and reads back, BEFORE any save ===")
# R-9: without this control a later failure cannot be blamed on the save.
setr = call("/api/node/value", {"path": path, "value": new_value}, method="PUT")
check("the edit was accepted", "__http__" not in setr, str(setr)[:200])
mid_value, _ = value(path)
check("CONTROL: the value reads back from the session", str(mid_value) == new_value,
      f"got {mid_value!r}, wrote {new_value!r}")

check("PROMISE 1: the file on disk is UNCHANGED while the edit is in memory",
      md5(copy_path) == copy_before, f"md5 moved to {md5(copy_path)}")

print()
print("=== save in place (on the copy) ===")
res = call("/api/files/save", {"fileId": fid, "backup": True})
if "__http__" in res:
    print(f">>> RED: save-audit -- save failed: {res}")
    check("the save succeeded", False, str(res)[:300])
    print(f"\n{a} asserted" if False else "")
else:
    print(f"  savedTo      : {res.get('savedTo')}")
    print(f"  backupPath   : {res.get('backupPath')}")
    print(f"  imagesRewritten={res.get('imagesRewritten')}  bytes={res.get('bytes')}")
    print(f"  reopenMs     : {res.get('reopenMs')}   <-- see W-066 finding: dead field")
    print(f"  seconds      : {res.get('seconds')}")
    for w in (res.get("warnings") or []):
        print(f"  warning: {w}")
    check("the save reported success", True)
    check("the save names the file it wrote", bool(res.get("savedTo")), "savedTo is empty")
    check("a backup path was reported", bool(res.get("backupPath")), "backupPath is empty")

print()
print("=== PROMISE 2: the backup really is the pre-save original ===")
backup = res.get("backupPath") if "__http__" not in res else None
if backup and os.path.exists(backup):
    b = md5(backup)
    print(f"  backup md5 {b}")
    check("the backup is byte-identical to the pre-save copy", b == copy_before,
          f"backup {b} vs before {copy_before}")
else:
    check("the backup file exists on disk", False, f"backupPath={backup!r}")

print()
print("=== PROMISE 3: the destination now holds the new bytes ===")
copy_after = md5(copy_path)
print(f"  copy md5 after: {copy_after}")
check("the destination changed", copy_after != copy_before, "bytes did not move")
check("the destination is not zero-length", os.path.getsize(copy_path) > 0)

print()
print("=== PROMISE 4: the saved file reopens and carries the edit ===")
reopened = call("/api/files/open", {"path": copy_path})
if "__http__" in reopened:
    check("the saved file reopens", False, str(reopened)[:200])
else:
    rid = reopened["id"]
    after_value, _ = value(f"{rid}{path[len(fid):]}")
    print(f"  reopened as {rid}; value now {after_value!r}")
    check("the saved archive carries the edit", str(after_value) == new_value,
          f"got {after_value!r}, wrote {new_value!r}")

print()
print("=== THE LIVE DATA ROOT WAS NEVER TOUCHED ===")
live_after = md5(os.path.join(LIVE, SUBJECT))
print(f"  live md5 before {live_before}")
print(f"  live md5 after  {live_after}")
check("C:\\MUWORK\\GAME\\MPSHS is byte-identical", live_after == live_before)

print(f"\nVERDICT: save-audit {asserts} assertions, {fails} failed")

# W-057/W-062 rule: a judge that cannot report red protects nothing. `--force-fail`
# deliberately fails one assertion so the marker path is proven, not assumed.
if "--force-fail" in sys.argv:
    print("  (--force-fail: one assertion is false on purpose)")
    check("zz self-test: this assertion is false on purpose", False, "forced")
    print(f"VERDICT: save-audit {asserts} assertions, {fails} failed")

if fails:
    raise SystemExit(1)