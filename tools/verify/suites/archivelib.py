"""Resolve an archive's session id BY NAME.

The ids are handed out in the order the archives were opened, so `f4` is
Map.nx on one instance and something else on another. A suite that hard-codes
an id measures a different archive than the one it means to -- and on an
instance where that id names something else it measures the WRONG thing while
looking like it measured a difference.

An archive that is not open is a failure that says so: the red marker the gate
reads, and a non-zero exit. The ways it can fail are told apart -- the
archives could not be listed, or the archive is listed but carries no id -- so
"this instance is not set up" never looks like "this suite is broken".
"""
import json
import os
import sys
import urllib.request


def require_gate_base():
    """The backend URL every suite talks to, from GATE_BASE -- or a loud refusal.

    There is deliberately no default port.  A suite that guesses one measures
    whatever is listening there, and an instance that is older than the tree
    answers every question happily -- so a fallback turns "this instance is 27
    product commits behind" into a per-suite result that looks like a finding
    about this tree (tools/verify/AGENTS.md rule 17).

    Two variables used to be able to name two instances at once: the suites
    read GATE_BASE, `browser-verify.mjs` reads `BASE ?? GATE_BASE`, and a
    caller who set only one of them got two suites hitting two backends in the
    same run.  GATE_BASE is the one variable; `BASE` is accepted only when it
    agrees with it, and saying so is cheaper than discovering it later.
    """
    gate = (os.environ.get("GATE_BASE") or "").strip()
    other = (os.environ.get("BASE") or "").strip()
    if gate and other and gate != other:
        print(f">>> RED: GATE_BASE and BASE name different instances: "
              f"GATE_BASE={gate!r} BASE={other!r}. Set GATE_BASE only.")
        sys.exit(1)
    if other and not gate:
        print(f">>> RED: BASE is set ({other!r}) but GATE_BASE is not. "
              f"GATE_BASE is the variable the suites read; set it and unset BASE "
              f"rather than relying on BASE.")
        sys.exit(1)
    if not gate:
        print(">>> RED: GATE_BASE is not set, so there is no backend to measure.")
        print("       Set it to the instance under test, for example:")
        print("         $env:GATE_BASE = 'http://127.0.0.1:<port>'")
        print("       There is no default port on purpose: an older instance on")
        print("       the usual port answers every one of these requests.")
        sys.exit(1)
    return gate.rstrip("/")


def archive_id(name, base):
    """The session id of an open archive, BY NAME -- or a loud failure.

    `base` is the instance's root URL, the same one the suite talks to.
    """
    try:
        with urllib.request.urlopen(f"{base}/api/files", timeout=120) as r:
            files = json.loads(r.read().decode("utf-8"))
    except Exception as exc:
        print(f">>> RED: could not list the open archives on this instance: {exc}")
        sys.exit(1)
    if files is None:
        files = []
    if not isinstance(files, list):
        # The isinstance() below guards the ELEMENT. The container was never
        # checked, so a JSON scalar (42) reached `for f in 42` and raised a
        # TypeError, and the traceback then read as "could not reach the
        # backend" -- while the backend was alive and the list was simply
        # not a list. "Could not list" is a different failure from "not open".
        print(f">>> RED: could not list the open archives on this instance: "
              f"the instance returned {type(files).__name__}, not a list")
        sys.exit(1)
    listed = {f.get("name"): f.get("id")
              for f in files if isinstance(f, dict)}
    if name not in listed:
        print(f">>> RED: {name} is not open on this instance, "
              f"so there is nothing to judge")
        sys.exit(1)
    if not listed[name]:
        print(f">>> RED: {name} is listed by this instance but carries no id")
        sys.exit(1)
    return listed[name]
