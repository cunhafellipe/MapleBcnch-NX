#!/usr/bin/env python3
"""Open the archives the gate measures, in the order the project declares.

The three startup scripts that used to do this lived in an agent cache under
the user profile. That cache pruned itself on 2026-10-21 (see pm/TRAPS.md
T-0 and the note in tools/pm.py:876), and the migration into version control
took the gate and its suites but not this one. A directory that prunes itself
is not a place to keep the thing that decides whether the code is correct.

What this obeys:

* pm/PROJECT.yaml is the single place a value that appears in more than one
  command lives. The archive list, the order and the data root are READ from
  it, never typed here.
* rule 16 of tools/verify/AGENTS.md: never hard-code an archive id. The ids
  are handed out in the order the archives were opened, so `f5` is Mob on one
  instance and something else on another. Every archive is confirmed BY NAME
  against what the instance reports, exactly the way
  tools/verify/suites/archivelib.py resolves one.
* rule 1: a tool that cannot measure fails loudly. A missing archive is a
  non-zero exit and a red line, never a warning.

Exit codes: 0 every declared archive is open, 1 otherwise.
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request

import yaml

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PROJECT_YAML = os.path.join(REPO_ROOT, "pm", "PROJECT.yaml")

# The repository path contains non-ASCII characters. Python's stdout encoding
# follows the console code page, so without this the paths printed below come
# out as mojibake (measured: "wz???" for "wz????").
try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:  # noqa: BLE001 - an old interpreter is not a reason to fail
    pass


def load_project():
    with open(PROJECT_YAML, encoding="utf-8") as fh:
        return yaml.safe_load(fh)


def post_json(url, payload, timeout):
    body = json.dumps(payload).encode("utf-8")
    req = urllib.request.Request(
        url, data=body, method="POST",
        headers={"Content-Type": "application/json; charset=utf-8"},
    )
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8"))


def get_json(url, timeout):
    with urllib.request.urlopen(url, timeout=timeout) as resp:
        return json.loads(resp.read().decode("utf-8"))


def main():
    project = load_project()
    data_root = project["data_root"]
    archives = list(project["archives"])
    test_port = str(project["test_port"])

    # GATE_BASE wins. The declared test_port is the fallback because the
    # deploy step starts the instance on exactly that port -- it is a
    # DECLARATION, not a guess, and it is printed so a reader can see which
    # one was used.
    base = (os.environ.get("GATE_BASE") or "").strip().rstrip("/")
    source = "GATE_BASE"
    if not base:
        base = "http://127.0.0.1:{0}".format(test_port)
        source = "pm/PROJECT.yaml test_port"

    print("=== open-archives ===")
    print("REPO_ROOT    : {0}".format(REPO_ROOT))
    print("BASE         : {0}  (from {1})".format(base, source))
    print("DATA_ROOT    : {0}".format(data_root))
    print("ARCHIVES     : {0}".format(len(archives)))

    # The declaration is the expected population (rule 15: a judge must not
    # take its expected values from the thing it judges).
    missing_on_disk = []
    paths = []
    for name in archives:
        path = os.path.join(data_root, name + ".nx")
        if not os.path.isfile(path):
            missing_on_disk.append(name)
        paths.append(path)
    if missing_on_disk:
        print(">>> RED: these declared archives are not on disk: {0}".format(
            ", ".join(missing_on_disk)))
        return 1

    for name, path in zip(archives, paths):
        started = time.time()
        try:
            result = post_json(
                base + "/api/files/open-many",
                {"paths": [path]},
                timeout=1800,
            )
        except urllib.error.URLError as exc:
            print(">>> RED: could not open {0}: {1}".format(name, exc))
            return 1
        except Exception as exc:  # noqa: BLE001 - the reason must be printed
            print(">>> RED: could not open {0}: {1}".format(name, exc))
            return 1
        took = time.time() - started
        opened = result.get("opened") if isinstance(result, dict) else None
        failed = result.get("failed") if isinstance(result, dict) else None
        if failed:
            print(">>> RED: {0} reported failures: {1}".format(name, failed))
            return 1
        print("  opened      {0:<10} {1:>7.1f}s  {2}".format(
            name, took, opened if opened is not None else ""))

    # Confirm every archive BY NAME against what the instance reports.
    try:
        files = get_json(base + "/api/files", timeout=300)
    except Exception as exc:  # noqa: BLE001
        print(">>> RED: could not list the open archives: {0}".format(exc))
        return 1
    if not isinstance(files, list):
        print(">>> RED: the instance returned {0}, not a list".format(
            type(files).__name__))
        return 1
    listed = {f.get("name"): f.get("id") for f in files if isinstance(f, dict)}

    print("OPEN_ARCHIVES")
    missing = []
    for name in archives:
        file_name = name + ".nx"
        if file_name not in listed:
            missing.append(file_name)
            print("  {0:<10} NOT OPEN".format(file_name))
            continue
        if not listed[file_name]:
            missing.append(file_name)
            print("  {0:<10} LISTED WITHOUT AN ID".format(file_name))
            continue
        print("  {0:<10} {1}".format(file_name, listed[file_name]))

    # Extra archives are not a failure by themselves (the one-shot suite opens
    # copies), but they are printed so a reader can see them.
    extras = [n for n in sorted(listed) if n not in {a + ".nx" for a in archives}]
    for name in extras:
        print("  {0:<10} {1}  (extra -- not one of the declared archives)".format(
            name, listed[name]))

    if missing:
        print(">>> RED: {0} declared archive(s) are not open: {1}".format(
            len(missing), ", ".join(missing)))
        return 1

    print("OPEN_ARCHIVES_OK")
    return 0


if __name__ == "__main__":
    sys.exit(main())
