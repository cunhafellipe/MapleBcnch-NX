"""Does our attack reader silently DROP attacks, the way the reference does?

clean-client `Mob.cpp:148`:
    if (!node["0"] || !data) continue;
An attack whose animation OR whose info is missing is skipped with no log and no
fallback. If our reader inherited that, a mob can declare four attacks and
silently swing with one, and nothing anywhere would say so.

MEASURED: for every attack node in a sample, compare
  * what the ARCHIVE has (does it carry an `info` child?)
  * what OUR endpoint reports
and report every node we do not report, with the reason.
"""
import json, os, sys, urllib.parse, urllib.request
from collections import Counter

BASE = os.environ.get("GATE_BASE") or "http://127.0.0.1:5591"


def get(route, **q):
    # The parameter is named `route`, NOT `path`. Naming it `path` makes
    # get("/api/inspect", path=p) a "multiple values for argument 'path'"
    # TypeError -- which is what happened the first two times this exact call was
    # written, and it reads as "the archive has nothing" because the caller
    # swallows the exception.
    with urllib.request.urlopen(f"{BASE}{route}?" + urllib.parse.urlencode(q),
                               timeout=300) as r:
        return json.loads(r.read().decode("utf-8"))


FAILURES = Counter()


def inspect(nodePath):
    """Never swallow a failure silently.

    The first version of this file returned None on any exception and `continue`d,
    so a run in which EVERY inspect failed reported "0 attacks found, 0 dropped"
    and looked like a clean result. A harness that cannot tell "the archive has
    none" from "I could not read it" is worse than no harness -- count the
    failures and report them.
    """
    try:
        node = get("/api/inspect", path=nodePath)
        FAILURES["ok"] += 1
        return node
    except Exception as exc:
        FAILURES[type(exc).__name__] += 1
        return None


cat = get("/api/simulate/mob-catalog")["mobs"]
ids = [m["id"] for m in cat]
# EVERY mob, not a stride of 150 out of the catalog.
#
# The old line was
#     sample = ids[::max(1, len(ids) // 150)][:150]
# which took every 14th mob out of 2100 -- 7.1% of the archive. A stride that
# skips 14 of every 15 mobs cannot see a defect in the mobs it skips, so the
# count this file prints was a statement about the sample, not about the
# archive, and the verdict moved with the stride rather than with the product.
# The population is 2100 mobs; judging all of them costs about a minute.
judged = ids
print(f"judging {len(judged)} of {len(ids)} mobs (the whole catalog)")

dropped = []
no_info = 0
no_anim = 0
counts = Counter()
for mid in judged:
    img = inspect(f"f5/{mid}.img")
    if not img:
        continue
    attack_nodes = [c["name"] for c in (img.get("children") or [])
                    if c["name"].startswith("attack")]
    if not attack_nodes:
        continue
    counts[len(attack_nodes)] += 1
    reported = {a.get("action") for a in get("/api/simulate/mob-attacks", mobId=mid)["attacks"]}
    for node in attack_nodes:
        has_info = inspect(f"f5/{mid}.img/{node}/info") is not None
        kids = inspect(f"f5/{mid}.img/{node}")
        has_anim = bool(kids and any((c.get("childCount") or 0) > 0
                                     or c.get("kind") == "Canvas"
                                     for c in (kids.get("children") or [])))
        if not has_info:
            no_info += 1
        if not has_anim:
            no_anim += 1
        if not has_info or not has_anim:
            dropped.append(f"{mid}/{node}: info={has_info} anim={has_anim} reported={node in reported}")

print(f"\ninspect calls: {dict(FAILURES)}")
too_few_reads = FAILURES.get("ok", 0) < 200
if too_few_reads:
    # The gate decides the verdict from EXPLICIT markers (gate.ps1:47, :97-99)
    # and never reads an exit code, so a run that measured nothing used to be
    # scored "ok" -- the suite that proved nothing read as a pass.
    print(">>> RED: too few successful reads for this run to mean anything")
print(f"\nattack-node count per mob: {dict(sorted(counts.items()))}")
print(f"attack nodes with NO info  : {no_info}")
print(f"attack nodes with NO anim  : {no_anim}")
print(f"nodes the reference would drop: {len(dropped)}")
for row in dropped[:20]:
    print(f"   {row}")

# And the sharper question: of the nodes the REFERENCE would keep, do we report
# every one?
#
# "Readable" is the reference's rule, not "has an info node".
# clean-client Mob.cpp:148:  `if (!node["0"] || !data) continue;`
# An attack whose animation has no frame named "0" is dropped by the reference,
# and MEASURED mob 9400572 is the case: attack1 and attack3 carry frames 1,2,3,4
# and no 0, attack2 carries 0..17, and this port correctly reports only attack2.
#
# So the first version of this file, which treated "has info" as the expectation,
# flagged a MISMATCH that was the port being CORRECT -- a permanent false red in
# a gate suite, which is the failure this project keeps paying for.
mismatch = 0
kept_by_reference = Counter()
for mid in judged:
    img = inspect(f"f5/{mid}.img")
    if not img:
        continue
    nodes = [c["name"] for c in (img.get("children") or []) if c["name"].startswith("attack")]
    good = []
    for n in nodes:
        if inspect(f"f5/{mid}.img/{n}/info") is None:
            continue
        kids = inspect(f"f5/{mid}.img/{n}")
        has_zero = any(c["name"] == "0" for c in (kids.get("children") or []))
        if has_zero:
            good.append(n)
    if not good:
        continue
    kept_by_reference[len(good)] += 1
    reported = get("/api/simulate/mob-attacks", mobId=mid)["attacks"]
    if len(reported) != len(good):
        mismatch += 1
        if mismatch <= 10:
            print(f"   MISMATCH {mid}: the reference would keep {len(good)} "
                  f"({good}), we report {len(reported)} "
                  f"({[a.get('action') for a in reported]})")
print(f"\nattacks the reference KEEPS, per mob: {dict(sorted(kept_by_reference.items()))}")
print(f"mobs where our count differs from that: {mismatch}")
print("Every dropped node is one the reference's own `node[\"0\"]` test drops too; "
      "the counts above are the expectation, not a wish.")

# A count that differs from the reference IS a defect, and until now it was
# only a number in a sentence: this file had no >>> RED, no BAD= and no
# sys.exit anywhere, so the gate scored it "ok" whatever it measured. Fifty
# mobs with the wrong attack count produced one line of text and a green.
if mismatch:
    print(f">>> RED: {mismatch} mob(s) where our attack count differs from what the reference keeps")

# BAD=n is the shape the other python suites use (knockback-verify.py:168,
# projectile-verify.py:404). It is the green shape the gate PRINTS, and for a
# non-zero count it is also the red marker the gate JUDGES on.
bad = (1 if too_few_reads else 0) + mismatch
print(f"\nBAD={bad}")
sys.exit(1 if bad else 0)