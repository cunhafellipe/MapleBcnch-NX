"""Census: does every suite the GATE runs REFUSE to run without GATE_BASE?

A suite that falls back to a port measures whatever is LISTENING there. When
that instance is older than the tree under test it answers every request
happily, so the suite reports a number about a different build and the number
reads as a finding about this one (tools/verify/AGENTS.md rule 17). Measured
consequence, 2026-10-23: per-suite results changed between rounds of the SAME
tree -- ball-art BAD=36 -> 40, contact-damage 7 -> 6, first-attack red ->
green.

There is no default any more: GATE_BASE is the one variable, `BASE` is refused
unless it agrees with it, and an unset GATE_BASE prints the marker and exits
non-zero.  This census is what says whether that is TRUE of every suite.

    python tools/verify/gate-base-census.py --root <REPO ROOT> [--label x]
                                             [--self-test]

THE POPULATION (the part that was wrong before)
-------------------------------------------------
The population is the set of suite files pm/SPEC.yaml declares, read with
pm.py's OWN parser (the same list the doctor and tools/verify/archive-id-census.py
use, so there is no second convention).  It is NOT a directory listing.  A
directory listing is how this tool came to scan 32 files and print
`truncated=False` while the gate ran MORE suites than that scan covered:
some of them live outside tools/verify/suites/ (MapleBench/tools/
sim-skills-check.mjs and MapleBench/tools/browser-verify.mjs), so a census
that only looked at one directory could not see them, and `truncated=False`
said nothing about the population being wrong.  A census with the wrong
population does not truncate; it reports a complete scan of the wrong set.

The counts are COMPUTED, never typed.  There are THREE populations and they
are not interchangeable, so the run prints all three, each naming the set it
counts and the command that measures it:

    spec_declared       the suites pm/SPEC.yaml declares
    gate_runs           the suites gate.ps1's own `Run '...'` entries name
    suites_dir_scripts  the script files under tools/verify/suites/

Each is printed ONCE, in the POPULATIONS block, with its population named and
the command that measures it.

The first two must agree with each other and with the tree -- a mismatch is a
REFUSAL (POPULATION_MATCHES=False, exit 2), not a note.

So the population is cross-checked against two INDEPENDENT sources, and a
mismatch is a REFUSAL, not a note:

  1. every declared suite file exists on disk;
  2. every file the GATE runs (parsed from gate.ps1's own Run lines) is
     declared in pm/SPEC.yaml -- otherwise the gate runs a suite this census
     never looked at;
  3. every code file in tools/verify/suites/ is either declared or a declared
     helper -- otherwise a suite file exists that this census did not scan;
  4. every declared suite the gate does NOT run says so itself
     (`in_gate: false`); an unexplained gap is a refusal.

Any of the four failing exits 2 and prints REFUSING with the named reason.
Exit 1 means a suite still falls back.  Exit 0 means clean.

Classification rule, stated so it can be checked.  Every line is classified
once, and the line number is printed with it:

  FALLBACK  a line that gives the target a DEFAULT: a literal instance URL
            with a port (`http://127.0.0.1:5591`), or a `-Port` default that
            is not the 0 sentinel (`[int]$Port = $(...)`, `[int]$Port = 5591`).
  MENTION   a line that names a port without defaulting to it -- one suite
            keeps a historical note about a measurement taken on 5591.  A
            mention is printed and counted, and it is NOT a fallback: a census
            that called prose a defect would send someone to "fix" a record.
  COMMENT   a wholly commented-out line, whatever it contains.
  REFUSED   no FALLBACK line, AND the language's canonical resolver is
            present -- `BASE = require_gate_base()` with the archivelib
            import, `$base = Resolve-GateBase -Port $Port` with
            `[int]$Port = 0`, or `const BASE = GATE_BASE`.
  NEITHER   no fallback and no resolver: a file that does not talk to a
            backend.  Reported, not red.

`scanned` and `truncated` are both printed (rule 18), the resolved root and a
digest of what was read are printed (rule 20), and the digest prints its
EXTRACTION RULE and the LENGTH it covered (rule 25).

`--self-test` builds three throwaway trees in a temp directory and runs this
tool on them as a CHILD PROCESS, because a reader that cannot detect its own
blindness contributes no evidence (rule 22):

  CONTROL_CLEAN      a tree whose suite refuses and whose population matches
                     -> must exit 0.  This is the control group: a census that
                     refused everything would still pass the other two.
  CONTROL_FALLBACK   a tree whose suite keeps a fallback -> must exit 1.
  CONTROL_POPULATION a tree where the gate runs a file the SPEC does not
                     declare -> must exit 2.
"""
import argparse
import hashlib
import importlib.util
import os
import re
import shutil
import subprocess
import sys
import tempfile

# A literal instance URL with a port.  The port must be digits, so the refusal
# messages, which print `http://127.0.0.1:<port>` as an example, do not match.
FALLBACK_URL = re.compile(r"https?://(?:127\.0\.0\.1|localhost):\d+")
# A -Port default other than the 0 sentinel: `[int]$Port = $(...)` or
# `[int]$Port = 5591`.  The group is the default itself, so the census can say
# WHICH default it found.
FALLBACK_PORT = re.compile(r"\[\s*int\s*\]\s*\$Port\s*=\s*(\S+)")
# A bare port number named in text.  A mention, not a default.
PORT_MENTION = re.compile(r"(?<!\d)(5\d{3})(?!\d)")

# The shared helpers.  They are the resolver, not a suite: counting them in the
# suite denominator would make the population wrong.  They ARE scanned, and
# they must not carry a fallback either.
HELPERS = ("archivelib.py", "archivelib.ps1")
# The resolver the helpers DEFINE, rather than call.
HELPER_RESOLVERS = {
    ".py": re.compile(r"^def\s+require_gate_base\s*\(", re.M),
    ".ps1": re.compile(r"^function\s+Resolve-GateBase\b", re.M),
}

# The canonical resolver per language: the line that resolves the target, and
# the line that proves the resolution cannot fall back.  The second pattern is
# searched against the WHOLE file, so it must be MULTILINE: without the flag
# `^` matches only the start of the string and a pattern that is there in
# every file silently matches in none of them.
RESOLVERS = {
    ".py": (re.compile(r"^[ \t]*BASE[ \t]*=[ \t]*require_gate_base\(\)[ \t]*$"),
            re.compile(r"^[ \t]*from\s+archivelib\s+import\s+[^\n]*require_gate_base", re.M)),
    ".ps1": (re.compile(r"^[ \t]*\$base[ \t]*=[ \t]*Resolve-GateBase[ \t]+-Port[ \t]+\$Port[ \t]*$"),
             re.compile(r"\[int\]\$Port[ \t]*=[ \t]*0\b")),
    # `const BASE = GATE_BASE;` and `const BASE = GATE_BASE.replace(...)` are
    # the same shape: the target is GATE_BASE, and nothing else.
    ".mjs": (re.compile(r"^[ \t]*const\s+BASE\s*=\s*GATE_BASE\b"),
             re.compile(r"^[ \t]*const\s+GATE_BASE\s*=\s*\(", re.M)),
}

COMMENT_PREFIXES = ("#", "//", "*", ";", "REM ")

# A Run line in gate.ps1, and the file it runs.  `$S` is the suites directory;
# anything else is repo-relative.
GATE_RUN = re.compile(r"^\s*Run\s+'([^']+)'\s*\{(.*)$")
GATE_FILE = re.compile(r"[\"']([^\"']+)[\"']")
# A Run line can carry other quoted arguments -- `npc-label-check` passes
# `-Map '000000002.img'` -- so the file is the first quoted path that ends in a
# code suffix, not simply the last quoted string.
GATE_CODE_SUFFIXES = (".py", ".ps1", ".mjs")


def is_comment(line):
    return line.strip().startswith(COMMENT_PREFIXES)


def norm(path):
    return path.replace("\\", "/").lstrip("./")


def load_pm(root):
    """pm.py FROM THE TREE BEING CENSUSED, under a per-root module name.

    A plain `import pm` would be cached in sys.modules: the second arm would
    then read the FIRST arm's SPEC and resolve the suite paths against the
    FIRST arm's root, and both arms would report the same number while
    measuring different things (rule 20).  pm.py derives ROOT from its own
    location, so loading it from the tree being censused is what makes the
    population belong to that tree.
    """
    pm_path = os.path.join(root, "tools", "pm.py")
    if not os.path.isfile(pm_path):
        return None
    mod_name = "pm_gatebase_%s" % hashlib.sha256(
        os.path.realpath(root).encode("utf-8")).hexdigest()[:12]
    spec = importlib.util.spec_from_file_location(mod_name, pm_path)
    pm = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(pm)
    return pm


def declared_suites(root, pm):
    """The suites pm/SPEC.yaml declares: id, declared file, resolved path.

    Read with pm.py's own parser, so the list is the one the doctor uses.
    """
    rows = pm.spec_rows(pm.load_spec(), "suites")
    out = []
    for row in rows:
        f = row.get("file") or ""
        if not f:
            continue
        resolved = pm._suite_path(f)
        full = resolved if os.path.isabs(resolved) else os.path.join(root, resolved)
        full = os.path.abspath(full)
        if not full.lower().startswith(os.path.abspath(root).lower() + os.sep):
            print(">>> RED: the suite %r resolved to %s, which is OUTSIDE the "
                  "tree being censused (%s)" % (f, full, root))
            sys.exit(2)
        out.append({"id": row.get("id") or f, "file": norm(f), "full": full,
                    "in_gate": row.get("in_gate")})
    return out


def gate_run_files(root, suites_dir):
    """The files the GATE runs, from gate.ps1's own Run lines.

    `$S` means the suites directory; anything else is repo-relative.  A Run
    line whose file cannot be read is a refusal, not a skipped suite: a gate
    run the census cannot see is a gate run the census did not check.
    """
    gate_path = os.path.join(root, "tools", "verify", "gate.ps1")
    if not os.path.isfile(gate_path):
        return None, ["tools/verify/gate.ps1 is missing, so the gate's own "
                      "population cannot be read"]
    out = []
    problems = []
    with open(gate_path, "r", encoding="utf-8", errors="replace") as fh:
        for n, line in enumerate(fh, 1):
            m = GATE_RUN.match(line)
            if not m:
                continue
            name, body = m.group(1), m.group(2)
            files = [f for f in GATE_FILE.findall(body)
                     if f.lower().endswith(GATE_CODE_SUFFIXES)]
            if not files:
                problems.append("gate.ps1:%d runs '%s' but the line names no "
                                "script file, so the population cannot be "
                                "confirmed" % (n, name))
                continue
            raw = files[0].replace("\\", "/")
            if raw.startswith("$S/"):
                rel = "tools/verify/suites/" + raw[3:]
            else:
                rel = raw
            out.append((name, norm(rel)))
    return out, problems


def is_declared_out_of_gate(row):
    """Does the entry itself say the gate does not run it?"""
    v = row.get("in_gate")
    if v is False:
        return True
    if isinstance(v, str) and v.strip().lower() == "false":
        return True
    return False


def suites_dir_scripts(root):
    """The script files under tools/verify/suites/, recursively.

    The extension filter is what keeps the __pycache__ .pyc files out: a
    .pyc is not a suite, and counting it would make the number a function of
    what has been run lately.  This is the SAME walk the cross-check uses, so
    the count this tool prints and the set it checks are one set.
    """
    d = os.path.join(root, "tools", "verify", "suites")
    out = []
    if not os.path.isdir(d):
        return out
    for dirpath, dirnames, filenames in os.walk(d):
        dirnames[:] = sorted(dirnames)
        for name in sorted(filenames):
            if os.path.splitext(name)[1].lower() in GATE_CODE_SUFFIXES:
                out.append(os.path.join(dirpath, name))
    return out


def population_problems(root, pm, population, gate_files):
    """Every reason the declared population and the real one differ."""
    problems = []
    pop_files = set(r["file"] for r in population)
    # 1. declared but not on disk
    for r in population:
        if not os.path.isfile(r["full"]):
            problems.append("pm/SPEC.yaml declares %s (%s) but the file is "
                            "not on disk" % (r["file"], r["id"]))
    gate_files = gate_files or []
    gate_rel = set(rel for _, rel in gate_files)
    # 2. the gate runs something the SPEC does not declare
    for name, rel in gate_files:
        if rel not in pop_files:
            problems.append("the gate runs %s (Run '%s') but pm/SPEC.yaml "
                            "does not declare it" % (rel, name))
    # 3. a suite file on disk that the SPEC does not declare.  The walk is
    # the SAME one the printed population count uses, recursively, so a suite
    # file in a subdirectory cannot be invisible to the check while the count
    # says otherwise.
    suites_dir = os.path.join(root, "tools", "verify", "suites")
    for full in suites_dir_scripts(root):
        name = os.path.basename(full)
        if os.path.splitext(name)[1] not in RESOLVERS:
            continue
        rel = norm(os.path.relpath(full, root).replace(os.sep, "/"))
        if rel in pop_files or name in HELPERS:
            continue
        problems.append("%s is in the suites directory but pm/SPEC.yaml "
                        "does not declare it" % rel)
    # 4. a declared suite the gate does not run, with no declaration saying so
    for r in population:
        if r["file"] in gate_rel:
            continue
        if is_declared_out_of_gate(r):
            continue
        problems.append("pm/SPEC.yaml declares %s (%s) but the gate does not "
                        "run it and it does not say in_gate: false"
                        % (r["file"], r["id"]))
    return problems


def digest(root, paths):
    """sha256 over the files, with the extraction rule stated.

    The rule: for each file, in repo-relative path order, the repo-relative
    path, a NUL, then the whole file's bytes.  Both the algorithm and the rule
    are printed, because a digest with no stated range is not a measurement
    anyone else can check (rule 25).
    """
    h = hashlib.sha256()
    total = 0
    for full, rel in sorted(paths, key=lambda t: t[1]):
        h.update(rel.encode("utf-8"))
        h.update(b"\0")
        with open(full, "rb") as fh:
            data = fh.read()
        h.update(data)
        total += len(rel) + 1 + len(data)
    return h.hexdigest().upper(), total


def scan_one(path, rel, is_helper):
    ext = os.path.splitext(path)[1]
    with open(path, "r", encoding="utf-8", errors="replace", newline="") as fh:
        text = fh.read()
    # splitlines() drops the line terminators, so a CRLF file and an LF file
    # are classified the same way.  A `$` anchored at the end of a line that
    # still carries its `\r` is a pattern that silently never matches.
    lines = text.splitlines()
    fallback, mention, comment = [], [], []
    for i, line in enumerate(lines, 1):
        s = line.strip()
        if not s:
            continue
        hit = FALLBACK_URL.search(line)
        if not hit:
            m = FALLBACK_PORT.search(line)
            if m and m.group(1) not in ("0", "0,", "0)", "0}"):
                hit = m
        if hit:
            if is_comment(line):
                comment.append((i, s))
            else:
                fallback.append((i, s))
            continue
        pm = PORT_MENTION.search(line)
        if pm:
            mention.append((i, pm.group(1), s))
    resolver_line = ""
    if is_helper:
        # The helper DEFINES the resolver; it does not call it.
        for ext_key, pat in HELPER_RESOLVERS.items():
            if ext_key != ext:
                continue
            for line in lines:
                if pat.match(line):
                    resolver_line = line.strip()
                    break
        has_resolver = bool(resolver_line)
    else:
        resolver = RESOLVERS.get(ext)
        if resolver:
            for line in lines:
                if resolver[0].match(line):
                    resolver_line = line.strip()
                    break
        has_resolver = bool(resolver_line) and bool(resolver[1].search(text))
    if fallback:
        verdict = "FELL_BACK"
    elif has_resolver:
        verdict = "REFUSED"
    else:
        verdict = "NEITHER"
    return {
        "rel": rel, "ext": ext, "verdict": verdict, "lines": len(lines),
        "fallback": fallback, "mention": mention, "comment": comment,
        "resolver_line": resolver_line, "helper": is_helper,
    }


def build_tree(root, spec_suites, gate_lines, suite_files, helpers):
    """A throwaway tree the self-test can point the census at."""
    d = tempfile.mkdtemp(prefix="gate-base-census-")
    os.makedirs(os.path.join(d, "pm"))
    os.makedirs(os.path.join(d, "tools", "verify", "suites"))
    shutil.copy(os.path.join(root, "tools", "pm.py"),
                os.path.join(d, "tools", "pm.py"))
    with open(os.path.join(d, "pm", "SPEC.yaml"), "w",
              encoding="utf-8", newline="\n") as fh:
        fh.write("suites:\n")
        for sid, rel in spec_suites:
            fh.write("  - id: %s\n" % sid)
            fh.write("    file: %s\n" % rel)
            fh.write("    runner: python\n")
            fh.write('    verdict: "BAD="\n')
            fh.write('    red_marker: [">>> RED"]\n')
            fh.write("    asserts: true\n")
            fh.write("    areas: [mob]\n")
    with open(os.path.join(d, "tools", "verify", "gate.ps1"), "w",
              encoding="utf-8", newline="\n") as fh:
        for line in gate_lines:
            fh.write(line + "\n")
    for rel, body in suite_files.items():
        with open(os.path.join(d, rel), "w", encoding="utf-8", newline="\n") as fh:
            fh.write(body)
    for name in helpers:
        src = os.path.join(root, "tools", "verify", "suites", name)
        if os.path.isfile(src):
            shutil.copy(src, os.path.join(d, "tools", "verify", "suites", name))
    return d


def run_census(root):
    """Run this tool on `root` as a child process, and return its exit code."""
    out = subprocess.run([sys.executable, os.path.abspath(__file__),
                          "--root", root, "--label", "self-test"],
                         capture_output=True, text=True)
    return out.returncode, out.stdout + out.stderr


def self_test(root):
    """Three controls.  Each one must fail for the reason it names."""
    print()
    print("=== SELF_TEST: three throwaway trees, this tool run as a child ===")
    clean_suite = ("import os\n"
                   "try:\n"
                   "    from archivelib import require_gate_base\n"
                   "except ImportError:\n"
                   '    print(">>> RED: archivelib.py is missing")\n'
                   "    raise SystemExit(1)\n"
                   "BASE = require_gate_base()\n"
                   "print(BASE)\n")
    fallback_suite = ("import os\n"
                      'BASE = os.environ.get("GATE_BASE") or "http://127.0.0.1:5591"\n'
                      "print(BASE)\n")
    run_line = ("Run '%s' { python \"$S\\%s\" }" % ("control-verify",
                                                    "control-verify.py"))
    rel = "tools/verify/suites/control-verify.py"

    # 1. clean: the suite refuses, the population matches -> exit 0
    d = build_tree(root, [("control-verify", rel)], [run_line],
                   {rel: clean_suite}, HELPERS)
    rc, out = run_census(d)
    print("CONTROL_CLEAN exit=%d (expected 0)" % rc)
    if rc != 0:
        print(out)
        print(">>> RED: the census refused a clean tree, so its refusals "
              "prove nothing")
        return False

    # 2. fallback: the suite keeps a fallback -> exit 1
    d = build_tree(root, [("control-verify", rel)], [run_line],
                   {rel: fallback_suite}, HELPERS)
    rc, out = run_census(d)
    print("CONTROL_FALLBACK exit=%d (expected 1)" % rc)
    if rc != 1:
        print(out)
        print(">>> RED: the census did not call its own control a fallback, "
              "so a clean report from it means nothing")
        return False

    # 3. population: the gate runs a file the SPEC does not declare -> exit 2
    other = "tools/verify/suites/undeclared-verify.py"
    d = build_tree(root, [("control-verify", rel)],
                   [run_line, "Run 'undeclared-verify' { python \"$S\\undeclared-verify.py\" }"],
                   {rel: clean_suite, other: clean_suite}, HELPERS)
    rc, out = run_census(d)
    print("CONTROL_POPULATION exit=%d (expected 2)" % rc)
    if rc != 2:
        print(out)
        print(">>> RED: the census did not refuse a population that does not "
              "match the tree, so its population is not checked")
        return False

    print("CONTROL_CAUGHT=yes")
    return True


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True,
                    help="the REPOSITORY ROOT (it holds pm/SPEC.yaml, "
                         "tools/pm.py and tools/verify/gate.ps1)")
    ap.add_argument("--label", default="current")
    ap.add_argument("--self-test", action="store_true",
                    help="also prove the census catches a deliberate fallback "
                         "and a population that does not match")
    args = ap.parse_args()

    root = os.path.abspath(args.root)
    if not os.path.isdir(root):
        print(">>> RED: no such directory: %s" % root)
        return 2
    print("LABEL=%s" % args.label)
    print("ROOT=%s" % root)

    pm = load_pm(root)
    if pm is None:
        print(">>> RED: %s has no tools/pm.py, so the population cannot be read"
              % root)
        return 2
    if not os.path.isfile(os.path.join(root, "pm", "SPEC.yaml")):
        print(">>> RED: %s has no pm/SPEC.yaml, so the population cannot be "
              "read.  --root is the REPOSITORY ROOT, not a suites directory."
              % root)
        return 2

    population = declared_suites(root, pm)
    gate_files, gate_problems = gate_run_files(root, pm.SUITES)
    if gate_files is None:
        print(">>> RED: REFUSING: the gate's own population cannot be read")
        for p in gate_problems:
            print("  - %s" % p)
        return 2

    problems = population_problems(root, pm, population, gate_files)
    problems.extend(gate_problems)

    print("POPULATION_SOURCE=pm/SPEC.yaml 'suites' section, read with "
          "tools/pm.py's own parser")
    # THE THREE POPULATIONS.  They are DIFFERENT sets and their counts are
    # not interchangeable: the SPEC declares 35 suites, the gate RUNS 34 of
    # them (save-audit declares in_gate: false), and the suites directory
    # holds 35 script files.  A tool that printed one of those and called it
    # "the number of suites" would be making a claim, not a measurement.
    #
    # So every count below names the set it counts AND the command that
    # measures it, and a reader can reproduce each one without reading this
    # file.  The counts are COMPUTED here, never typed.
    dir_scripts = suites_dir_scripts(root)
    print()
    print("POPULATIONS -- three DIFFERENT sets; each count names the set it")
    print("counts and the command that measures it:")
    print("  spec_declared=%d" % len(population))
    print("      what    : the suites pm/SPEC.yaml declares")
    print("      command : python -X utf8 tools/pm.py doctor")
    print("                   then count the rows that carry registry=True")
    print("  gate_runs=%d" % len(gate_files))
    print("      what    : the suites tools/verify/gate.ps1 actually RUNS")
    print("      command : count the Run '<id>' entries in tools/verify/gate.ps1")
    print("  suites_dir_scripts=%d" % len(dir_scripts))
    print("      what    : the script files under tools/verify/suites/")
    print("      command : Get-ChildItem tools/verify/suites -File "
          "-Include *.py,*.ps1,*.mjs -Recurse | Measure-Object")
    print("      note    : the suites directory also holds non-script files")
    print("                 (fixtures, and __pycache__ .pyc files); they are")
    print("                 not suites and are not counted here.")
    pop_files = set(r["file"] for r in population)
    gate_rel = set(rel for _, rel in gate_files)
    print("population_minus_gate=%s" % sorted(pop_files - gate_rel))
    print("gate_minus_population=%s" % sorted(gate_rel - pop_files))
    if problems:
        print(">>> RED: REFUSING: the declared population and the tree do not "
              "match, so a scan of it would not be a scan of the gate")
        for p in problems:
            print("  - %s" % p)
        print("POPULATION_MATCHES=False")
        return 2
    print("POPULATION_MATCHES=True")

    # The helpers are the resolver, not a suite: they are scanned, and they
    # are not in the suite denominator.
    helper_paths = []
    for name in HELPERS:
        full = os.path.join(root, "tools", "verify", "suites", name)
        if os.path.isfile(full):
            helper_paths.append((full, "tools/verify/suites/" + name))
    print("helpers_not_counted_as_suites=%s  (they are the resolver, not a "
          "suite; they are scanned too)" % list(HELPERS))
    expected = len(population) + len(helper_paths)

    results = [scan_one(r["full"], r["file"], False) for r in population]
    helper_results = [scan_one(full, rel, True) for full, rel in helper_paths]
    # `scanned` is the number of files actually READ; `truncated` says
    # whether that covers the population this census set out to read.  Both
    # are COMPUTED here, AFTER the reads: a literal `truncated=False`
    # printed BEFORE the reads claims a completeness nobody measured, and
    # the sentence that came with it was true by construction.
    scanned = len(results) + len(helper_results)
    truncated = (scanned != expected) or (expected == 0 and len(population) > 0)
    print("scanned=%d  expected=%d  truncated=%s"
          % (scanned, expected, truncated))
    print("  (scanned = the files actually READ; expected = the declared"
          " suite files plus the helpers; the population was checked"
          " against the tree above)")
    dg, dglen = digest(root, [(r["full"], r["file"]) for r in population]
                       + helper_paths)
    print("DIGEST(sha256, whole file bytes, repo-relative path + NUL + "
          "content, in repo-relative path order, %d byte(s) covered)=%s"
          % (dglen, dg))

    print()
    print("%-46s %-10s %-6s %s" % ("FILE", "VERDICT", "LINES", "EVIDENCE"))
    for r in results + helper_results:
        if r["fallback"]:
            ev = "FALLBACK line %d: %s" % (r["fallback"][0][0],
                                           r["fallback"][0][1][:64])
        elif r["comment"]:
            ev = "comment only, line %d: %s" % (r["comment"][0][0],
                                                r["comment"][0][1][:56])
        elif r["mention"]:
            ev = "mentions port %s, not a default, line %d: %s" % (
                r["mention"][0][1], r["mention"][0][0], r["mention"][0][2][:44])
        else:
            ev = ("resolver: " + r["resolver_line"]) if r["resolver_line"] \
                else "no resolver, no fallback"
        print("%-46s %-10s %-6d %s" % (r["rel"], r["verdict"], r["lines"], ev))

    refused = sum(1 for r in results if r["verdict"] == "REFUSED")
    fell = sum(1 for r in results if r["verdict"] == "FELL_BACK")
    neither = sum(1 for r in results if r["verdict"] == "NEITHER")
    h_refused = sum(1 for r in helper_results if r["verdict"] == "REFUSED")
    h_fell = sum(1 for r in helper_results if r["verdict"] == "FELL_BACK")
    h_neither = sum(1 for r in helper_results if r["verdict"] == "NEITHER")
    mentions = sum(len(r["mention"]) for r in results + helper_results)
    comments = sum(len(r["comment"]) for r in results + helper_results)
    print()
    print("suites_scanned=%d  suites_refused=%d  suites_fell_back=%d  "
          "suites_neither=%d" % (len(results), refused, fell, neither))
    print("helpers_scanned=%d  helpers_refused=%d  helpers_fell_back=%d  "
          "helpers_neither=%d" % (len(helper_results), h_refused, h_fell,
                                   h_neither))
    print("port_mentions_not_defaults=%d  comment_only_hits=%d"
          % (mentions, comments))
    if fell:
        print(">>> RED: %d suite(s) still fall back to a port; the backend a "
              "suite measures must be named, never guessed" % fell)
    if neither:
        print(">>> RED: %d suite(s) have neither a fallback nor the canonical "
              "resolver: a suite that cannot say where its backend comes from "
              "is not measuring anything" % neither)
    if h_fell or h_neither:
        print(">>> RED: a helper does not resolve the target, so the suites "
              "that call it are calling nothing")
    if mentions or comments:
        print("(the port_mentions and comment_only_hits are historical notes "
              "about old measurements; they are prose, not defaults)")

    rc = 1 if (fell or neither or h_fell or h_neither) else 0

    if args.self_test:
        if not self_test(root):
            return 1
    return rc


if __name__ == "__main__":
    sys.exit(main())
