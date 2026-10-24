"""Create an empty .wz and an empty .nx, and prove the app can open both.

The point of the creator is that an agent can build a fixture WITHOUT touching
the live client folder.  So this suite measures three things:

  1. both containers are created, and the magic in them is the format's;
  2. the app OPENS both through its own open path, and they hold no images;
  3. the refusals hold: no overwrite, no format/name mismatch, and nothing
     written into the folder the open archives live in.

(3) is the part that keeps the data root clean, so it is measured rather than
assumed: the refusal is asked for, and the file is checked to be absent
afterwards.

Written 2026-10-24 for W-096.
"""
import json
import os
import shutil
import tempfile
import urllib.error
import urllib.parse
import urllib.request

try:
    from archivelib import require_gate_base
except ImportError:            # a missing module must not read as a pass
    print(">>> RED: archivelib.py is missing next to this suite")
    raise SystemExit(1)
except SyntaxError as exc:    # a helper that does not PARSE is not missing
    print(f">>> RED: archivelib.py next to this suite does not parse: {exc}")
    raise SystemExit(1)

BASE = require_gate_base()


def _call(req, timeout=180):
    """Run a request and answer (status, body-as-dict)."""
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            raw = r.read().decode("utf-8", "replace")
            status = r.status
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode("utf-8", "replace")
        status = exc.code
    try:
        body = json.loads(raw)
    except Exception:
        body = {"error": raw[:400]}
    return status, body


def post(route, body):
    req = urllib.request.Request(
        BASE + "/" + route,
        data=json.dumps(body).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST")
    return _call(req)


def get(route):
    return _call(urllib.request.Request(BASE + "/" + route, method="GET"))


def delete(route):
    return _call(urllib.request.Request(BASE + "/" + route, method="DELETE"))


def field(body, name):
    """Read a field whatever the JSON casing of the answer is."""
    if not isinstance(body, dict):
        return None
    for key in body:
        if key.lower() == name.lower():
            return body[key]
    return None


def reason(body):
    """The sentence the server gave for a refusal.

    Every failure in the app funnels through ApiError, which answers with
    {"message": ..., "detail": ...}.  The refusal sentences live in the
    message, so that is what is read.
    """
    for name in ("message", "detail", "error"):
        value = field(body, name)
        if isinstance(value, str) and value.strip():
            return value.strip()
    return json.dumps(body)[:200] if isinstance(body, (dict, list)) else str(body)


bad = 0


def fail(why):
    global bad
    bad += 1
    print(f"    >>> RED: {why}")


def magic_of(path):
    try:
        with open(path, "rb") as fh:
            return fh.read(4)
    except OSError:
        return b""


work = tempfile.mkdtemp(prefix="maplebench-empty-")
print(f"=== empty-archive ===")
print(f"WORKDIR      : {work}")

try:
    # ------------------------------------------------------------------ #
    # 1. create both containers
    # ------------------------------------------------------------------ #
    made = {}
    for fmt, magic in (("wz", b"PKG1"), ("nx", b"PKG4")):
        target = os.path.join(work, f"fixture.{fmt}")
        print(f"\n--- create {fmt} ---")
        status, body = post("api/files/create-empty", {"path": target})
        print(f"    status={status} body={json.dumps(body)[:300]}")
        if status != 200:
            fail(f"creating an empty .{fmt} answered {status}, not 200")
            continue
        if not os.path.isfile(target):
            fail(f"the create answered 200 but '{target}' is not on disk")
            continue
        size = os.path.getsize(target)
        got = magic_of(target)
        print(f"    file={target} bytes={size} magic={got!r}")
        if got != magic:
            fail(f"an empty .{fmt} starts with {got!r}, not {magic!r}")
        if size <= 0:
            fail(f"an empty .{fmt} was written with {size} bytes")
        made[fmt] = target

    # ------------------------------------------------------------------ #
    # 2. the app opens both, and they hold no images
    # ------------------------------------------------------------------ #
    for fmt, target in made.items():
        print(f"\n--- open the empty .{fmt} ---")
        payload = {"path": target, "readOnly": True}
        if fmt == "wz":
            # The classic container was written with BMS; open it the same way
            # so the version hash matches what was written.
            payload["mapleVersion"] = "BMS"
        status, body = post("api/files/open", payload)
        print(f"    open status={status} id={field(body, 'id')!r} "
              f"name={field(body, 'name')!r} kind={field(body, 'kind')!r}")
        if status != 200:
            fail(f"the app could not open the empty .{fmt} it had just created "
                 f"(status {status}): {json.dumps(body)[:200]}")
            continue
        fid = field(body, "id")
        if not fid:
            fail(f"the empty .{fmt} opened without an id")
            continue
        cstatus, children = get(f"api/children?path={urllib.parse.quote(str(fid))}")
        print(f"    children status={cstatus} count="
              f"{len(children) if isinstance(children, list) else 'n/a'}")
        if cstatus != 200:
            fail(f"listing the children of the empty .{fmt} answered {cstatus}")
        elif not isinstance(children, list):
            fail(f"the children of the empty .{fmt} came back as "
                 f"{type(children).__name__}, not a list")
        elif children:
            fail(f"an empty .{fmt} came back with {len(children)} children: "
                 f"{[c.get('name') for c in children[:5]]}")
        dstatus, _ = delete(f"api/files/{urllib.parse.quote(str(fid))}")
        print(f"    close status={dstatus}")
        if dstatus not in (200, 204):
            fail(f"closing the empty .{fmt} answered {dstatus}")

    # ------------------------------------------------------------------ #
    # 3. the refusals
    # ------------------------------------------------------------------ #
    print("\n--- refusals ---")

    wz_target = made.get("wz")
    if wz_target:
        status, body = post("api/files/create-empty", {"path": wz_target})
        print(f"    overwrite status={status} reason={reason(body)!r}")
        if status == 200:
            fail("creating over an existing file was allowed")
        elif status != 400:
            fail(f"the overwrite refusal answered {status}, not 400")

    mismatch = os.path.join(work, "mismatch.nx")
    status, body = post("api/files/create-empty", {"path": mismatch, "format": "wz"})
    print(f"    format-mismatch status={status} reason={reason(body)!r}")
    if status == 200:
        fail("a .nx file name was accepted with format 'wz'")
    elif status != 400:
        fail(f"the format mismatch refusal answered {status}, not 400")
    if os.path.exists(mismatch):
        fail("the refused format mismatch still left a file on disk")

    # The folder the open archives live in IS the live client.  A container
    # written there is a file in the data root, so the refusal has to hold.
    status, files = get("api/files")
    live_folder = None
    if isinstance(files, list):
        for f in files:
            if isinstance(f, dict):
                path = field(f, "filePath") or ""
                if path:
                    live_folder = os.path.dirname(path)
                    break
    if not live_folder:
        print("    (no open archive to derive the live folder from -- "
              "the live-folder refusal was NOT measured)")
    else:
        probe = os.path.join(live_folder, "maplebench-empty-probe.wz")
        status, body = post("api/files/create-empty", {"path": probe})
        print(f"    live-folder status={status} folder={live_folder!r} "
              f"reason={reason(body)!r}")
        if status == 200:
            fail(f"a container was created inside the live client folder {live_folder!r}")
        elif status != 400:
            fail(f"the live-folder refusal answered {status}, not 400")
        if os.path.exists(probe):
            fail("the refused live-folder create still left a file on disk")
            try:
                os.remove(probe)
            except OSError:
                pass

    # ------------------------------------------------------------------ #
    # 4. nothing was left behind in the work directory except the two
    #    containers the suite made
    # ------------------------------------------------------------------ #
    leftovers = sorted(os.listdir(work))
    print(f"\n--- workdir ---")
    print(f"    files left: {leftovers}")
    expected = sorted([os.path.basename(p) for p in made.values()])
    if sorted(leftovers) != expected:
        fail(f"the work directory holds {leftovers}, expected {expected}")
finally:
    shutil.rmtree(work, ignore_errors=True)
    print(f"\nremoved {work}")

print(f"\nBAD={bad}")
raise SystemExit(1 if bad else 0)
