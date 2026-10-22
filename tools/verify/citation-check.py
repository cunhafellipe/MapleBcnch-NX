#!/usr/bin/env python3
"""citation-check.py -- check that a `file:line` citation points at what it claims.

WHAT THIS IS FOR
----------------
A citation report is only useful if a reader can re-derive it.  A bare line
number is not re-derivable: the file moves, the tree moves, and a stale
number is indistinguishable from a wrong one.

Measured 2026-10-22 in this repo: a reviewer read a mid-edit working tree,
found the anchors 6 lines above where the report said, and recorded a FALSE
correction against a report whose line numbers were correct.  The excerpt was
real and only its labels were stale -- a reader had no way to tell.

So a citation is blessed only when it is pinned to a REVISION and that
revision is REPRODUCIBLE:

    (file, commit, clean)        <- the triple this tool prints for every row

Every check is made against `git show <commit>:<path>`, never against the
working tree, because the working tree is the one thing allowed to be
half-edited.

THE CHECKS
----------
  1 triple output     every row prints file=, commit=, tree=, head=
  2 dirty tree        a dirty file is reported UNBLESSED, never blessed
  3 ambiguous anchor  text on several lines => NO shift is reported
  4 phantom line      a trailing newline is not a line
  5 text at the line  the cited text must really be on that line
  6 sweep accounting  a sweep always prints scanned= and truncated=
  7 range accounting  a citation `file:A-B` checks BOTH ends
  8 nothing measured  a run that checked nothing is BROKEN, never clean

RULES THIS FILE FOLLOWS (tools/verify/AGENTS.md)
-------------------------------------------------
  rule 1   a tool that cannot measure fails loudly (exit 2); it never
           collapses to 0 and calls that data
  rule 3   known-answer tests only
  rule 9   every field this tool reads is named and checked before use
  rule 12  a text match must end at a BOUNDARY, not inside a word:
           `phobj.h` must NOT match `phobj.hspeed` (incident 15)
  "an empty result from a static scan is not a finding": a sweep that finds
  nothing prints CITATIONS_FOUND=0 and says what it scanned
  "a truncation is not a census": a truncated sweep is not a pass

EXIT CODES
----------
  0  every citation checked, every citation OK and blessed
  1  a citation is wrong, stale, unblessed, or unchecked; OR the run
     measured nothing; OR the census was truncated
  2  the measurement could not be made (bad repo, bad commit, missing file)

WHAT "MEASURED NOTHING" MEANS
-----------------------------
A run is clean only if it actually checked something.  Three shapes used to
read as "clean" and are now BROKEN RUNS:

  * a spec with zero citations;
  * a sweep that found no citation at all;
  * a spec whose only citations name paths that do not exist in this
    repository and were not declared as living outside it.

The last one is the reason this file has an `external` concept.  A citation
into the reference tree (which lives outside this repository) is a real
citation that this tool CANNOT check; a citation whose file name is simply
wrong is a DEFECT.  Those two used to produce byte-identical output, so a
typo was as green as a legitimate cross-repo reference.  They are now told
apart by an EXPLICIT declaration:

  * a spec may carry `"external": ["PlayerStates.cpp", ...]` -- paths that
    are known to live outside this repository;
  * the CLI accepts `--external <path>` (repeatable) for sweeps.

A path that is not in the repository and is NOT declared external is a dead
citation (FILE_NOT_FOUND) and is counted as bad.  A path that IS declared
external is reported NOT_IN_REPO: it was not checked, and the run says so.

USAGE
-----
  python tools/verify/citation-check.py --self-test
  python tools/verify/citation-check.py --spec citations.json
  python tools/verify/citation-check.py --sweep doc.md source.cs
  python tools/verify/citation-check.py --sweep --scan-root MapleBench
  python tools/verify/citation-check.py --sweep --external PlayerStates.cpp doc.md

SPEC FORMAT (JSON)
-------------------
  {
    "repo":   "C:/path/to/repo",          optional, defaults to this repo
    "commit": "fb5d173",                   optional, defaults to HEAD
    "external": ["PlayerStates.cpp"],      optional, paths outside this repo
    "citations": [
      {"file": "MapleBench/Services/MapEditor/CharacterPhysics.cs",
       "line": 298,
       "text": "if (AttackPoseTicks > 0)"},
      {"file": "doc/ENGINEERING/STATE.md", "line": 420},
      {"file": "Big.cpp", "line": 10, "end": 42}
    ]
  }

`text` is optional but a row without it is reported NO_TEXT, which is NOT
a pass: the row was not checked (rule 1).  `end` is optional and names the
LAST line of a range; both ends are checked.

This file is ASCII only and contains no embedded path.  The repository root
is derived from this file's own location, so a non-ASCII path can never be
corrupted into the tool (incident 1).
"""

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

# --------------------------------------------------------------------------
# house style
# --------------------------------------------------------------------------

TOOL = "citation-check"
# Every verdict a row can carry.  A verdict not in this list is a bug here.
VERDICTS = (
    "OK",
    "STALE",
    "NOT_FOUND",
    "NO_TEXT",
    "LINE_EXISTS",
    "LINE_OUT_OF_RANGE",
    "FILE_NOT_FOUND",
    "NOT_IN_REPO",
)

# The verdicts that are NOT defects.  A row carrying one of them is a row
# that was measured and found sound.
#
#   OK           the cited text really is on the cited line
#   LINE_EXISTS  the cited file and the cited line exist at this commit.
#                A SWEPT citation carries no text, so the text cannot be
#                compared -- and saying "NO_TEXT" (which means "nothing was
#                checked") would be a lie.  Before this verdict existed, a
#                sweep could only ever be red: every swept row was NO_TEXT,
#                every NO_TEXT was a defect, and the verdict was inverted
#                (measure nothing -> green, measure something -> red).
GOOD_VERDICTS = ("OK", "LINE_EXISTS")

# The verdicts whose text was actually COMPARED with the file.  A row
# counted here had its text checked; a LINE_EXISTS row did not, and the
# report says so.
TEXT_COMPARED_VERDICTS = ("OK", "STALE", "NOT_FOUND")

# The literal a caller greps for when the run measured nothing.  It is
# printed on a line of its own so that a caller can find it without
# depending on the shape of any other line.
BROKEN_RUN = "BROKEN RUN - nothing was measured."


def token(name, value):
    """One `NAME=value` line, the shape tools/verify already greps for."""
    print("%s=%s" % (name, value))


def fail_hard(message):
    """A measurement that could not be made.  Never a zero, never a pass."""
    print("FATAL: %s" % message)
    print("RESULT=FAIL")
    print("TRUST=NONE")
    sys.exit(2)


# --------------------------------------------------------------------------
# git plumbing
# --------------------------------------------------------------------------


def run_git(repo, args):
    """Run git and return (returncode, stdout_text, stderr_text)."""
    proc = subprocess.run(
        ["git", "-C", repo] + list(args),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    return (
        proc.returncode,
        proc.stdout.decode("utf-8", "replace"),
        proc.stderr.decode("utf-8", "replace"),
    )


def resolve_commit(repo, rev):
    """(full_sha, error).  A rev that does not resolve is a hard failure."""
    rc, out, err = run_git(repo, ["rev-parse", "--verify", "%s^{commit}" % rev])
    if rc != 0:
        return None, err.strip() or ("cannot resolve %r" % rev)
    return out.strip(), None


def is_dirty(repo, rel_path):
    """True when the working tree copy of rel_path differs from HEAD.

    None means "could not measure" -- and None is NOT False (rule 9).
    """
    rc, out, _ = run_git(repo, ["status", "--porcelain", "--", rel_path])
    if rc != 0:
        return None  # could not measure
    return out.strip() != ""


def read_blob_lines(repo, commit, rel_path):
    """(lines, real_line_count, error).

    The file is read from the COMMIT, never from the working tree.
    `real_line_count` excludes the phantom element produced by splitting a
    file that ends in a newline (check 4).
    """
    rc, out, err = run_git(repo, ["show", "%s:%s" % (commit, rel_path)])
    if rc != 0:
        return None, 0, err.strip() or ("no such path at %s" % commit)
    text = out
    if text == "":
        return [], 0, None
    lines = text.split("\n")
    phantom = 1 if (text.endswith("\n") and lines and lines[-1] == "") else 0
    lines = lines[: len(lines) - phantom] if phantom else lines
    return lines, len(lines), None


# --------------------------------------------------------------------------
# text matching (rule 12: end at a BOUNDARY, not inside a word)
# --------------------------------------------------------------------------

IDENT = set(
    "abcdefghijklmnopqrstuvwxyz"
    "ABCDEFGHIJKLMNOPQRSTUVWXYZ"
    "0123456789_"
)


def contains_at_boundary(line, needle):
    """True when `needle` occurs in `line` ending at a word boundary.

    `phobj.h` must not match `phobj.hspeed` (incident 15).  The rule is
    applied to each side only when the needle's own edge is an identifier
    character, so a needle that starts or ends with punctuation is not
    constrained on that side.
    """
    if not needle:
        return False
    start = 0
    while True:
        i = line.find(needle, start)
        if i < 0:
            return False
        end = i + len(needle)
        ok = True
        if needle[0] in IDENT and i > 0 and line[i - 1] in IDENT:
            ok = False
        if ok and needle[-1] in IDENT and end < len(line) and line[end] in IDENT:
            ok = False
        if ok:
            return True
        start = i + 1


def occurrences(lines, needle):
    """Every 1-based line number on which `needle` occurs at a boundary."""
    return [i + 1 for i, line in enumerate(lines) if contains_at_boundary(line, needle)]


# --------------------------------------------------------------------------
# the core check
# --------------------------------------------------------------------------


def bless_row(row, eligible):
    """Mark a row blessed when its line numbers are reproducible from the tree.

    A row is blessed when the file is clean in the working tree AND the
    commit being measured is HEAD.  `eligible` says whether this row's
    VERDICT is one that can be blessed at all -- a row that is wrong in
    some other way is never blessed, however reproducible it is.
    """
    if eligible and row["tree"] == "clean" and row["head"] == "yes":
        row["blessed"] = "yes"
    elif row["tree"] == "dirty":
        row["detail"] = (row["detail"] + "; " if row["detail"] else "") + \
            "the working tree copy of this file is modified, so the line numbers are not reproducible from the tree"
    elif row["head"] == "no":
        row["detail"] = (row["detail"] + "; " if row["detail"] else "") + \
            "this commit is not HEAD, so a reader at HEAD sees different lines"


def check_citation(repo, commit, head_commit, citation, external=(),
                   text_required=True):
    """Check one citation.  Returns a dict; never raises, never guesses.

    `external` is the set of paths (relative, '/'-separated) that are known
    to live OUTSIDE this repository.  A cited path that is neither in the
    repository nor declared external is a dead citation.

    `text_required` is False for a SWEPT citation.  A swept citation is
    "file:line" and carries no words, so the text cannot be compared; what
    a sweep CAN check is that the file and the line exist at this commit.
    That is a real measurement and it is reported as one (LINE_EXISTS).
    For a citation that came from a spec the text is required, and a
    citation with no text is a citation that was never measured (NO_TEXT,
    which is a defect).
    """
    rel = citation.get("file") or ""
    line = citation.get("line")
    end = citation.get("end")
    text = citation.get("text")
    row = {
        "file": rel,
        "line": line,
        "end": end,
        "verdict": None,
        "actual_line": "n/a",
        "shift": "n/a",
        "ambiguous": 0,
        "blessed": "no",
        "tree": "unknown",
        "head": "no",
        "checked": 0,
        "detail": "",
    }

    if not rel:
        row["verdict"] = "FILE_NOT_FOUND"
        row["detail"] = "the citation names no file"
        return row
    if not isinstance(line, int) or line < 1:
        row["verdict"] = "LINE_OUT_OF_RANGE"
        row["detail"] = "the citation names no line number"
        return row
    if end is not None:
        if not isinstance(end, int) or end < 1:
            row["verdict"] = "LINE_OUT_OF_RANGE"
            row["detail"] = "the citation names a range whose end is not a line number"
            return row
        if end < line:
            row["verdict"] = "LINE_OUT_OF_RANGE"
            row["detail"] = "the range %d-%d ends before it starts" % (line, end)
            return row

    dirty = is_dirty(repo, rel)
    row["tree"] = "dirty" if dirty else ("clean" if dirty is not None else "unknown")
    row["head"] = "yes" if commit == head_commit else "no"

    lines, count, err = read_blob_lines(repo, commit, rel)
    if lines is None:
        # The path is not in the repository AT THIS COMMIT.  Three cases, and
        # they must not share an output:
        #
        #   * it exists at HEAD but not here  => a dead citation (bad);
        #   * it is declared external         => a reference into the tree
        #     that lives outside this repository.  Not checkable here, so
        #     it is NOT a defect, but it was NOT checked either;
        #   * neither                        => the file name is simply
        #     wrong, and a wrong file name IS a defect.
        at_head, _, _ = read_blob_lines(repo, head_commit, rel)
        if at_head is not None:
            row["verdict"] = "FILE_NOT_FOUND"
            row["detail"] = ("the path exists at HEAD but not at %s: the "
                             "citation is dead" % commit[:12])
        elif rel in external:
            row["verdict"] = "NOT_IN_REPO"
            row["detail"] = ("this path is declared as living outside this "
                             "repository, so the citation was NOT checked here")
        else:
            row["verdict"] = "FILE_NOT_FOUND"
            row["detail"] = ("the path does not exist in this repository and "
                             "was not declared as living outside it: the "
                             "citation is dead")
        return row

    # The cited line must EXIST before anything else is asked of it.  A
    # citation past the end of the file is out of range whatever text it
    # quotes -- text that happens to occur elsewhere must not turn it into
    # "stale".  The known-answer test PHANTOM_LAST_LINE_IS_NOT_A_LINE is
    # what caught that.
    if line > count:
        row["verdict"] = "LINE_OUT_OF_RANGE"
        row["detail"] = "line %d is past the end of the file (%d lines)" % (line, count)
        return row

    # The END of a range must exist too.  Before this check existed, a
    # citation `file:1-999` recorded only the first line: the tail of the
    # range was silently dropped, and the one checkable thing -- does the
    # line exist -- was swallowed by the dropped half.
    if end is not None and end > count:
        row["verdict"] = "LINE_OUT_OF_RANGE"
        row["detail"] = ("the range %d-%d ends past the end of the file "
                         "(%d lines)" % (line, end, count))
        return row

    if text is None or not str(text).strip():
        if not text_required:
            # A SWEPT citation carries no text by construction: the
            # extractor found "file:line", not the words on that line.
            # What a sweep CAN check is that the file and the line exist at
            # this commit.  That IS a measurement, and reporting it as
            # NO_TEXT ("nothing was checked") would be a lie -- and it is
            # exactly what made the sweep verdict inverted.
            row["verdict"] = "LINE_EXISTS"
            row["detail"] = ("the file and the line exist at this commit; a "
                             "swept citation carries no text, so the text "
                             "was NOT compared")
            bless_row(row, True)
            row["checked"] = 1
            return row
        # No text to check against.  This row was NOT checked, and must not
        # be allowed to read as a pass (rule 1).
        row["verdict"] = "NO_TEXT" if 1 <= line <= count else "LINE_OUT_OF_RANGE"
        if row["verdict"] == "LINE_OUT_OF_RANGE":
            row["detail"] = "line %d is outside 1..%d" % (line, count)
        else:
            row["detail"] = "no text given; the text check did not run"
        return row

    text = str(text)
    hits = occurrences(lines, text)
    if len(hits) > 1:
        row["ambiguous"] = 1

    if line in hits:
        row["verdict"] = "OK"
        if len(hits) == 1:
            row["actual_line"] = line
            row["shift"] = 0
        else:
            # The cited line does hold the text, but the text also occurs
            # elsewhere, so no single "actual line" can be claimed and NO
            # shift may be reported (check 3).
            row["actual_line"] = ",".join(str(h) for h in hits)
            row["shift"] = "n/a"
            row["detail"] = "text occurs on %d lines; no shift is claimed" % len(hits)
    elif hits:
        row["verdict"] = "STALE"
        if len(hits) == 1:
            row["actual_line"] = hits[0]
            row["shift"] = hits[0] - line
        else:
            row["actual_line"] = ",".join(str(h) for h in hits)
            row["shift"] = "n/a"
            row["detail"] = "text occurs on %d lines, none of them the cited one" % len(hits)
    else:
        row["verdict"] = "NOT_FOUND"
        row["detail"] = "the text does not occur anywhere in the file at this commit"

    # Blessing: the revision must be reproducible.  A dirty file is not
    # reproducible by a reader looking at the tree, and a commit that is not
    # HEAD describes a tree the reader no longer has (check 2).
    bless_row(row, row["verdict"] in GOOD_VERDICTS)

    row["checked"] = 1
    return row


# --------------------------------------------------------------------------
# reporting
# --------------------------------------------------------------------------


def report_rows(rows, commit, repo, truncated):
    """Print the report.  Returns the number of defects.

    A run is a PASS only if it checked something, checked everything it
    found, and found nothing wrong.  A run that measured nothing, or a
    census that was truncated, is a FAIL -- and it says which one it was.
    """
    counts = dict((v, 0) for v in VERDICTS)
    unblessed = 0
    checked = 0
    for row in rows:
        counts[row["verdict"]] = counts.get(row["verdict"], 0) + 1
        if row["blessed"] != "yes":
            unblessed += 1
        checked += row.get("checked", 0)

    print("")
    print("=" * 96)
    print("REPO=%s" % repo)
    print("COMMIT=%s" % commit)
    print("=" * 96)
    for n, row in enumerate(rows, start=1):
        print("[%d] file=%s line=%s" % (n, row["file"], row["line"]))
        if row.get("end") is not None:
            print("    range_end=%s" % row["end"])
        print("    commit=%s tree=%s head=%s blessed=%s"
              % (commit, row["tree"], row["head"], row["blessed"]))
        print("    verdict=%s actual_line=%s shift=%s ambiguous=%d"
              % (row["verdict"], row["actual_line"], row["shift"], row["ambiguous"]))
        if row["detail"]:
            print("    detail: %s" % row["detail"])

    bad = 0
    for v in VERDICTS:
        if v in GOOD_VERDICTS or v == "NOT_IN_REPO":
            continue
        bad += counts.get(v, 0)
    unblessed_ok = sum(
        1 for r in rows
        if r["verdict"] in GOOD_VERDICTS and r["blessed"] != "yes"
    )
    total_bad = bad + unblessed_ok
    text_compared = sum(
        1 for r in rows if r["verdict"] in TEXT_COMPARED_VERDICTS
    )

    token("CITATIONS_SCANNED", len(rows))
    token("CITATIONS_CHECKED", checked)
    token("CITATIONS_TEXT_CHECKED", text_compared)
    token("CITATIONS_TRUNCATED", truncated)
    for v in VERDICTS:
        token("CITATION_%s" % v.upper(), counts.get(v, 0))
    token("CITATION_UNBLESSED", unblessed_ok)
    token("CITATION_BAD", total_bad)
    if counts.get("NOT_IN_REPO"):
        print("NOTE: %d citation(s) name a path that is not in this repository"
              " and was declared as living outside it. They were NOT checked,"
              " and they are NOT counted as bad." % counts["NOT_IN_REPO"])
    if counts.get("LINE_EXISTS") and text_compared:
        print("NOTE: %d citation(s) were checked for the FILE AND THE LINE "
              "only. A swept citation carries no text, so the text was NOT "
              "compared." % counts["LINE_EXISTS"])
    if text_compared == 0:
        # A zero in a field that was just added reads as "there was nothing
        # to compare", and a missing field and a zero look identical
        # (incident 12).  So this zero is NEVER printed on its own: it is
        # printed with what the rows were instead, and with what a zero
        # here means.
        print("NOTE: CITATIONS_TEXT_CHECKED=0 -- NO citation text was compared "
              "in this run. This does not mean 'there was nothing to check'; "
              "it means the run compared no text at all.")
        if counts.get("LINE_EXISTS"):
            print("      %d citation(s) were checked for the FILE AND THE LINE "
                  "only: a swept citation carries no text, so there was "
                  "nothing to compare." % counts["LINE_EXISTS"])
        if counts.get("NO_TEXT"):
            print("      %d citation(s) carried no text at all, so their text "
                  "check did not run; they are counted as defects."
                  % counts["NO_TEXT"])
        if not counts.get("LINE_EXISTS") and not counts.get("NO_TEXT"):
            print("      No row in this run carries a text-bearing verdict; "
                  "the rows above say what each one was.")

    # The three ways a run can be green without having measured anything.
    broken = None
    if not rows:
        broken = "the run found no citation at all"
    elif checked == 0:
        broken = ("not one of the %d citation(s) could be checked in this "
                  "repository" % len(rows))
    if broken is not None:
        print(BROKEN_RUN)
        print("BROKEN_REASON=%s" % broken)
        token("RESULT", "FAIL")
        token("TRUST", "NONE")
        return 1

    if truncated:
        print("TRUNCATED CENSUS - %d citation(s) were not checked because the "
              "run stopped early. A truncated census is not a census."
              % truncated)
        token("RESULT", "FAIL")
        token("TRUST", "LOW")
        return 1

    if total_bad:
        token("RESULT", "FAIL")
    else:
        token("RESULT", "PASS")
    unmeasurable = counts.get("FILE_NOT_FOUND", 0)
    token("TRUST", "LOW" if unmeasurable else "HIGH")
    return total_bad


# --------------------------------------------------------------------------
# citation extraction (sweep mode)
# --------------------------------------------------------------------------

# A citation is `path.ext:NNN` or `path.ext:NNN-MMM`.  The digits must END
# at a non-identifier character, so `Foo.cpp:718abc` is not a citation of
# `Foo.cpp:718`.  The range form is captured as a whole: before the range
# group existed, `MapMobs.cpp:245-253.` recorded only `:245` and the tail
# of the range was silently dropped.
CITE_RE = re.compile(
    r"(?<![A-Za-z0-9_./\\-])"
    r"([A-Za-z0-9_][A-Za-z0-9_./\\-]*\.[A-Za-z0-9_]+)"
    r":(\d+)(?:-(\d+))?"
    r"(?![A-Za-z0-9_])"
)
# A bare `:NNN` is a RELATIVE citation of the file named earlier on the same
# line, which is the shape this repo uses (`MapleCharacter.cs:718, :735`).
REL_RE = re.compile(r"(?<![A-Za-z0-9_./\\-]):(\d+)(?![A-Za-z0-9_])")


def extract_citations(text):
    """[(file, line, end)] found in one block of text, in order.

    `end` is None for a single line and the LAST line of the range for a
    `file:A-B` citation.
    """
    found = []
    taken = []
    last_file = None
    for line in text.split("\n"):
        full = list(CITE_RE.finditer(line))
        for m in full:
            found.append((m.group(1), int(m.group(2)),
                          int(m.group(3)) if m.group(3) else None))
            taken.append((m.start(), m.end()))
            last_file = m.group(1)
        for m in REL_RE.finditer(line):
            if any(m.start() >= s and m.start() < e for s, e in taken):
                continue
            if last_file is None:
                continue  # a relative citation with no file to be relative to
            found.append((last_file, int(m.group(1)), None))
    return found


# --------------------------------------------------------------------------
# modes
# --------------------------------------------------------------------------


def repo_root():
    """The repository this file lives in, derived from the file itself."""
    return os.path.dirname(os.path.dirname(os.path.dirname(
        os.path.abspath(__file__))))


def mode_verify(args):
    repo = args.repo or repo_root()
    if not os.path.isdir(repo):
        fail_hard("the repository root %r does not exist" % repo)

    external = set()
    if args.spec:
        try:
            with open(args.spec, "r", encoding="utf-8") as fh:
                spec = json.load(fh)
        except Exception as exc:  # noqa: BLE001 - the reason must be named
            fail_hard("cannot read the spec %r: %s" % (args.spec, exc))
        if not isinstance(spec, dict):
            fail_hard("the spec must be a JSON object")
        citations = spec.get("citations")
        if not isinstance(citations, list):
            fail_hard("the spec has no 'citations' list")
        if spec.get("repo"):
            repo = spec["repo"]
        ext = spec.get("external")
        if ext is not None:
            if not isinstance(ext, list):
                fail_hard("'external' must be a list of paths")
            external.update(str(e) for e in ext)
        commit_rev = args.commit or spec.get("commit") or "HEAD"
    else:
        citations = []
        commit_rev = args.commit or "HEAD"

    for path in (args.external or []):
        external.add(path.replace("\\", "/"))

    if not os.path.isdir(repo):
        fail_hard("the repository root %r does not exist" % repo)

    commit, err = resolve_commit(repo, commit_rev)
    if commit is None:
        fail_hard("cannot resolve the commit %r: %s" % (commit_rev, err))
    head, err = resolve_commit(repo, "HEAD")
    if head is None:
        fail_hard("cannot resolve HEAD: %s" % err)

    rows = []
    for cit in citations:
        if not isinstance(cit, dict):
            fail_hard("every citation must be an object, found %r" % (cit,))
        rows.append(check_citation(repo, commit, head, cit, external))

    bad = report_rows(rows, commit, repo, 0)
    sys.exit(1 if bad else 0)


def mode_sweep(args):
    repo = args.repo or repo_root()
    if not os.path.isdir(repo):
        fail_hard("the repository root %r does not exist" % repo)

    external = set(str(e).replace("\\", "/") for e in (args.external or []))

    commit_rev = args.commit or "HEAD"
    commit, err = resolve_commit(repo, commit_rev)
    if commit is None:
        fail_hard("cannot resolve the commit %r: %s" % (commit_rev, err))
    head, err = resolve_commit(repo, "HEAD")
    if head is None:
        fail_hard("cannot resolve HEAD: %s" % err)

    targets = list(args.files)
    if args.scan_root:
        base = os.path.join(repo, args.scan_root)
        if not os.path.isdir(base):
            fail_hard("--scan-root %r is not a directory" % args.scan_root)
        for dirpath, dirnames, filenames in os.walk(base):
            dirnames[:] = [d for d in dirnames
                           if d not in (".git", "bin", "obj", "node_modules")]
            for name in filenames:
                if name.endswith((".cs", ".md", ".ps1", ".mjs", ".js", ".py", ".txt")):
                    targets.append(os.path.join(dirpath, name))
    if not targets:
        fail_hard("nothing to sweep: give file arguments or --scan-root")

    scanned = 0
    truncated = 0
    rows = []
    for path in targets:
        try:
            with open(path, "r", encoding="utf-8", errors="replace") as fh:
                text = fh.read()
        except Exception as exc:  # noqa: BLE001 - the reason must be named
            fail_hard("cannot read %r: %s" % (path, exc))
        rel = os.path.relpath(path, repo).replace("\\", "/")
        for cite_file, cite_line, cite_end in extract_citations(text):
            scanned += 1
            if args.max and len(rows) >= args.max:
                truncated += 1
                continue
            # A citation names a path relative to the repository.  A file
            # that is not in the repository cannot be checked here, and saying
            # so is the honest answer -- not "0 found".
            #
            # A SWEPT citation carries no text, so the text check is NOT
            # APPLICABLE (the extractor found "file:line", not the words on
            # that line).  What is checked is that the file and the line
            # exist at this commit.
            row = check_citation(repo, commit, head,
                                 {"file": cite_file, "line": cite_line,
                                  "end": cite_end}, external,
                                 text_required=False)
            row["swept_from"] = rel
            rows.append(row)

    print("SWEEPED_FILES=%d" % len(targets))
    print("SWEEPED_FROM=%s" % (args.scan_root or "(explicit files)"))
    print("CITATIONS_FOUND=%d" % scanned)
    bad = report_rows(rows, commit, repo, truncated)
    sys.exit(1 if bad else 0)


# --------------------------------------------------------------------------
# known-answer tests (rule 3)
# --------------------------------------------------------------------------

FIXTURE = """using System;
class Sample {
    void M() {
        if (AttackPoseTicks > 0)
        {
            AttackPoseTicks--;
        }
        phobj.hspeed = phobj.vx = 0;
        LastAction = "climb";
        int x = 1;  // LastAction = "climb";
    }
}
"""

# The line numbers of FIXTURE are NOT written down here.  They are DERIVED
# from the blob by line_of() below, and PRINTED in the self-test output, so
# there is exactly ONE authority for them: the file itself.
#
# An earlier version of this file carried a hand-written list of them here.
# Every entry in that list was off by one, and the expectation built on it
# was wrong; the cost was a full re-verification round.  A hand-copied
# number is a second authority, and two authorities that can disagree are
# how a correct tool gets reported as broken.


def build_fixture_repo():
    """A real git repo with two commits, so `git show` has something to show.

    Commit B PREPENDS three lines, so every line of commit A moves by three.
    That is the case a stale citation actually suffers from: the file grew
    ABOVE the citation, not below it.
    """
    d = tempfile.mkdtemp(prefix="citation-check-")
    with open(os.path.join(d, "sample.cs"), "w", encoding="utf-8", newline="\n") as fh:
        fh.write(FIXTURE)
    run_git(d, ["init", "-q"])
    run_git(d, ["add", "-A"])
    run_git(d, ["-c", "user.email=t@t", "-c", "user.name=t", "commit", "-qm", "A"])
    commit_a, _ = resolve_commit(d, "HEAD")
    with open(os.path.join(d, "sample.cs"), "r", encoding="utf-8") as fh:
        body = fh.read()
    with open(os.path.join(d, "sample.cs"), "w", encoding="utf-8", newline="\n") as fh:
        fh.write("// three lines added ABOVE\n// the second\n// the third\n")
        fh.write(body)
    run_git(d, ["add", "-A"])
    run_git(d, ["-c", "user.email=t@t", "-c", "user.name=t", "commit", "-qm", "B"])
    commit_b, _ = resolve_commit(d, "HEAD")
    return d, commit_a, commit_b


def line_of(repo, commit, path, needle):
    """The 1-based line of `needle` in `path` at `commit`, DERIVED from the
    blob.  Hand-counting fixture line numbers is how the first version of
    this test got every expectation wrong by one line; nothing here is
    transcribed by hand."""
    lines, count, err = read_blob_lines(repo, commit, path)
    if lines is None:
        raise AssertionError("cannot read %s at %s: %s" % (path, commit, err))
    hits = occurrences(lines, needle)
    if not hits:
        raise AssertionError("%r is not in %s at %s" % (needle, path, commit))
    return hits[0]


def selftest():
    cases = []
    d, commit_a, commit_b = build_fixture_repo()
    try:
        head, _ = resolve_commit(d, "HEAD")

        def row(commit, file, line, text=None, end=None):
            cit = {"file": file, "line": line}
            if text is not None:
                cit["text"] = text
            if end is not None:
                cit["end"] = end
            return check_citation(d, commit, head, cit)

        T_IF = "if (AttackPoseTicks > 0)"
        T_PHSPEED = "phobj.hspeed = phobj.vx = 0;"
        T_CLIMB = 'LastAction = "climb";'

        L_IF_B = line_of(d, commit_b, "sample.cs", T_IF)
        L_PHSPEED_B = line_of(d, commit_b, "sample.cs", T_PHSPEED)
        L_CLIMB_B = line_of(d, commit_b, "sample.cs", T_CLIMB)
        L_IF_A = line_of(d, commit_a, "sample.cs", T_IF)
        _, COUNT_A, _ = read_blob_lines(d, commit_a, "sample.cs")
        _, COUNT_B, _ = read_blob_lines(d, commit_b, "sample.cs")
        # The fixture's line numbers are DERIVED from the blob, and they are
        # printed here so a reader can check them against the file without
        # counting lines by hand -- and without trusting a comment that
        # could be wrong (it was, and it cost a re-verification round).
        print("FIXTURE_LINES (derived from the blob): "
              "A: if=%d count=%d | B: if=%d phspeed=%d climb=%d count=%d"
              % (L_IF_A, COUNT_A, L_IF_B, L_PHSPEED_B, L_CLIMB_B, COUNT_B))

        # 1. a correct citation passes
        r = row(commit_b, "sample.cs", L_IF_B, T_IF)
        cases.append(("OK_UNIQUE_CITATION_ACCEPTED",
                      r["verdict"] == "OK" and r["blessed"] == "yes",
                      "verdict=%s blessed=%s" % (r["verdict"], r["blessed"])))

        # 2. THE MANDATORY ONE: a genuinely wrong citation is caught
        r = row(commit_b, "sample.cs", L_IF_B, T_PHSPEED)
        caught = (r["verdict"] == "STALE"
                  and r["actual_line"] == L_PHSPEED_B
                  and r["shift"] == L_PHSPEED_B - L_IF_B)
        cases.append(("A_WRONG_CITATION_IS_CAUGHT", caught,
                      "verdict=%s actual_line=%s shift=%s"
                      % (r["verdict"], r["actual_line"], r["shift"])))

        # 3. text that is nowhere in the file
        r = row(commit_b, "sample.cs", L_IF_B, "this text is not in the file at all")
        cases.append(("TEXT_NOT_FOUND", r["verdict"] == "NOT_FOUND",
                      "verdict=%s" % r["verdict"]))

        # 4. an ambiguous anchor reports NO shift (check 3)
        r = row(commit_b, "sample.cs", L_CLIMB_B, T_CLIMB)
        cases.append(("AMBIGUOUS_REPORTS_NO_SHIFT",
                      r["verdict"] == "OK" and r["ambiguous"] == 1
                      and r["shift"] == "n/a",
                      "verdict=%s ambiguous=%s shift=%s"
                      % (r["verdict"], r["ambiguous"], r["shift"])))

        # 5. rule 12: a match must end at a boundary (incident 15)
        r = row(commit_b, "sample.cs", L_PHSPEED_B, "phobj.h")
        r2 = row(commit_b, "sample.cs", L_PHSPEED_B, "phobj.hspeed")
        cases.append(("BOUNDARY_MATCHES_NOT_INSIDE_A_WORD",
                      r["verdict"] == "NOT_FOUND" and r2["verdict"] == "OK",
                      "phobj.h->%s phobj.hspeed->%s" % (r["verdict"], r2["verdict"])))

        # 6. the phantom trailing newline is not a line (check 4)
        r = row(commit_a, "sample.cs", COUNT_A, "}")
        r2 = row(commit_a, "sample.cs", COUNT_A + 1, "}")
        cases.append(("PHANTOM_LAST_LINE_IS_NOT_A_LINE",
                      r["verdict"] == "OK" and r2["verdict"] == "LINE_OUT_OF_RANGE",
                      "count=%d last=%s past_end=%s"
                      % (COUNT_A, r["verdict"], r2["verdict"])))

        # 7. the CLI itself must exit NON-ZERO on a wrong citation
        spec_path = os.path.join(d, "spec.json")
        with open(spec_path, "w", encoding="utf-8") as fh:
            json.dump({"repo": d, "commit": commit_b,
                       "citations": [{"file": "sample.cs", "line": L_IF_B,
                                      "text": T_PHSPEED}]}, fh)
        cli = subprocess.run(
            [sys.executable, os.path.abspath(__file__),
             "--spec", spec_path, "--repo", d],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        out = cli.stdout.decode("utf-8", "replace")
        cases.append(("CLI_EXITS_NONZERO_ON_A_WRONG_CITATION",
                      cli.returncode != 0 and "RESULT=FAIL" in out
                      and "CITATION_BAD=1" in out,
                      "exit=%d" % cli.returncode))

        # 8. a dirty tree is never blessed (check 2)
        with open(os.path.join(d, "sample.cs"), "a", encoding="utf-8", newline="\n") as fh:
            fh.write("// an uncommitted edit\n")
        r = row(commit_b, "sample.cs", L_IF_B, T_IF)
        cases.append(("DIRTY_TREE_IS_UNBLESSED",
                      r["verdict"] == "OK" and r["blessed"] == "no"
                      and r["tree"] == "dirty",
                      "verdict=%s blessed=%s tree=%s"
                      % (r["verdict"], r["blessed"], r["tree"])))
        run_git(d, ["checkout", "--", "sample.cs"])

        # 9. a citation valid at commit A is stale at commit B, by exactly 3
        #
        #    The expected actual_line is L_IF_B -- DERIVED from the blob, not
        #    counted by hand.  It used to be written as `L_CLIMB_B - 3`, which
        #    came from a hand-written list of fixture line numbers in which
        #    every entry was off by one; the tool was right and the
        #    expectation was wrong.
        r = row(commit_a, "sample.cs", L_IF_A, T_IF)
        r2 = row(commit_b, "sample.cs", L_IF_A, T_IF)
        cases.append(("STALE_AFTER_EDIT_REPORTS_THE_SHIFT",
                      r["verdict"] == "OK" and r2["verdict"] == "STALE"
                      and r2["shift"] == 3 and r2["actual_line"] == L_IF_B,
                      "A=%s B=%s shift=%s actual=%s (expected actual_line=%d)"
                      % (r["verdict"], r2["verdict"], r2["shift"],
                         r2["actual_line"], L_IF_B)))

        # 10. a sweep always reports scanned= and truncated=
        sweep_file = os.path.join(d, "doc.md")
        with open(sweep_file, "w", encoding="utf-8", newline="\n") as fh:
            fh.write("see sample.cs:5 and sample.cs:9, also :7\n")
        found = extract_citations(open(sweep_file, encoding="utf-8").read())
        cases.append(("SWEEP_FINDS_RELATIVE_CITATIONS",
                      ("sample.cs", 5, None) in found
                      and ("sample.cs", 9, None) in found
                      and ("sample.cs", 7, None) in found,
                      "found=%s" % (found,)))

        # 11. A CITATION INTO THE REFERENCE TREE, and A MISSPELLED FILE
        #     NAME, MUST NOT SHARE AN OUTPUT.
        #
        #     Before this case existed, a path that was not in the repository
        #     was reported NOT_IN_REPO and was NOT counted as bad -- so a
        #     MISSPELLED file name was exactly as green as a legitimate
        #     cross-repo reference.  The two are now told apart by an
        #     explicit declaration: a path declared external is uncheckable
        #     here (NOT_IN_REPO, not bad); a path that is neither in the
        #     repository nor declared is a DEAD CITATION (FILE_NOT_FOUND,
        #     bad).
        #
        #     The two cases below assert DIFFERENT verdicts for the two
        #     shapes, so neither can pass by being the other.
        outside_path = os.path.join(d, "outside.json")
        with open(outside_path, "w", encoding="utf-8") as fh:
            json.dump({"repo": d, "commit": commit_b,
                       "external": ["PlayerStates.cpp"],
                       "citations": [{"file": "PlayerStates.cpp", "line": 96,
                                      "text": "is_attacking()"}]}, fh)
        cli = subprocess.run(
            [sys.executable, os.path.abspath(__file__),
             "--spec", outside_path, "--repo", d],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        out = cli.stdout.decode("utf-8", "replace")
        cases.append(("A_CITATION_OUTSIDE_THE_REPO_IS_NOT_A_DEFECT",
                      cli.returncode != 0
                      and "CITATION_NOT_IN_REPO=1" in out
                      and "CITATION_FILE_NOT_FOUND=0" in out
                      and "CITATION_BAD=0" in out
                      and BROKEN_RUN in out,
                      "exit=%d verdict=NOT_IN_REPO" % cli.returncode))

        typo_path = os.path.join(d, "typo.json")
        with open(typo_path, "w", encoding="utf-8") as fh:
            json.dump({"repo": d, "commit": commit_b,
                       "citations": [{"file": "sampl.cs", "line": 4,
                                      "text": "the marker line"}]}, fh)
        cli = subprocess.run(
            [sys.executable, os.path.abspath(__file__),
             "--spec", typo_path, "--repo", d],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        out = cli.stdout.decode("utf-8", "replace")
        cases.append(("A_MISSPELLED_FILE_NAME_IS_A_DEFECT",
                      cli.returncode != 0
                      and "CITATION_FILE_NOT_FOUND=1" in out
                      and "CITATION_NOT_IN_REPO=0" in out
                      and "CITATION_BAD=1" in out,
                      "exit=%d verdict=FILE_NOT_FOUND" % cli.returncode))

        # 12. the END of a range is checked, not only its first line
        r = row(commit_b, "sample.cs", 1, None, end=COUNT_A + 100)
        cases.append(("THE_END_OF_A_RANGE_IS_CHECKED",
                      r["verdict"] == "LINE_OUT_OF_RANGE",
                      "verdict=%s" % r["verdict"]))

        # 13. a run that measured nothing is a BROKEN RUN, never a pass
        empty_path = os.path.join(d, "empty.json")
        with open(empty_path, "w", encoding="utf-8") as fh:
            json.dump({"repo": d, "commit": commit_b, "citations": []}, fh)
        cli = subprocess.run(
            [sys.executable, os.path.abspath(__file__),
             "--spec", empty_path, "--repo", d],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        out = cli.stdout.decode("utf-8", "replace")
        cases.append(("A_RUN_THAT_MEASURED_NOTHING_IS_NOT_A_PASS",
                      cli.returncode != 0 and BROKEN_RUN in out
                      and "RESULT=FAIL" in out,
                      "exit=%d" % cli.returncode))

        # 14. a COMPLETE census of two good citations is green and says it
        #     was not truncated.  This is the control for case 16: the two
        #     runs differ by --max and by nothing else.
        many_path = os.path.join(d, "many.json")
        with open(many_path, "w", encoding="utf-8") as fh:
            json.dump({"repo": d, "commit": commit_b,
                       "citations": [{"file": "sample.cs", "line": L_IF_B,
                                      "text": T_IF},
                                     {"file": "sample.cs", "line": L_IF_B,
                                      "text": T_IF}]}, fh)
        cli = subprocess.run(
            [sys.executable, os.path.abspath(__file__),
             "--spec", many_path, "--repo", d],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        out = cli.stdout.decode("utf-8", "replace")
        cases.append(("A_COMPLETE_CENSUS_IS_GREEN_AND_NOT_TRUNCATED",
                      cli.returncode == 0 and "CITATION_OK=2" in out
                      and "CITATIONS_TRUNCATED=0" in out,
                      "exit=%d" % cli.returncode))

        # 15. a sweep that CHECKED something is not red.
        #
        #     A swept citation carries no text, so the text check is not
        #     APPLICABLE to it.  Before the LINE_EXISTS verdict existed,
        #     every swept row came back NO_TEXT, every NO_TEXT was counted
        #     as a defect, and the sweep verdict was inverted: a sweep that
        #     measured nothing was green, and a sweep that measured
        #     something was red.
        ok_sweep = os.path.join(d, "ok-sweep.md")
        with open(ok_sweep, "w", encoding="utf-8", newline="\n") as fh:
            fh.write("the rule lives in sample.cs:%d\n" % L_IF_B)
        cli = subprocess.run(
            [sys.executable, os.path.abspath(__file__),
             "--sweep", ok_sweep, "--repo", d],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        out = cli.stdout.decode("utf-8", "replace")
        cases.append(("A_SWEEP_THAT_CHECKED_SOMETHING_IS_NOT_RED",
                      cli.returncode == 0
                      and "CITATION_LINE_EXISTS=1" in out
                      and "CITATION_BAD=0" in out,
                      "exit=%d verdict=LINE_EXISTS" % cli.returncode))

        # 16. a REAL truncated census is not a pass.
        #
        #     The checked row must be CLEAN, so the only thing that can turn
        #     this case red is the truncation itself.  A case that goes red
        #     for an incidental reason is not a test (rule 3).
        trunc_sweep = os.path.join(d, "trunc.md")
        with open(trunc_sweep, "w", encoding="utf-8", newline="\n") as fh:
            fh.write("first sample.cs:%d then sample.cs:%d\n"
                     % (L_IF_B, L_PHSPEED_B))
        cli = subprocess.run(
            [sys.executable, os.path.abspath(__file__),
             "--sweep", trunc_sweep, "--max", "1", "--repo", d],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        out = cli.stdout.decode("utf-8", "replace")
        cases.append(("A_TRUNCATED_CENSUS_IS_NOT_A_PASS",
                      cli.returncode != 0
                      and "CITATIONS_TRUNCATED=1" in out
                      and "CITATION_BAD=0" in out
                      and "RESULT=FAIL" in out,
                      "exit=%d truncated=1 bad=0" % cli.returncode))

        # 17. THE POSITIVE CONTROL for the change in case 15.
        #
        #     The sweep verdict was changed (defect 3): a SWEPT citation
        #     carries no text, so it now gets LINE_EXISTS.  That change must
        #     not have turned the SPEC path green as well.  In spec mode a
        #     citation that quotes nothing was never measured, so it must
        #     stay NO_TEXT and stay a defect.
        #
        #     The good citation in the same spec is what makes this a
        #     control and not just another red run: it proves the run
        #     MEASURED something, so the red verdict cannot be a BROKEN RUN
        #     (rule 3 -- a case that fails for the wrong reason is not a
        #     test).  `BROKEN_RUN not in out` is the assertion for that.
        notext_path = os.path.join(d, "notext.json")
        with open(notext_path, "w", encoding="utf-8") as fh:
            json.dump({"repo": d, "commit": commit_b,
                       "citations": [{"file": "sample.cs", "line": L_IF_B,
                                      "text": T_IF},
                                     {"file": "sample.cs", "line": L_CLIMB_B}]},
                      fh)
        cli = subprocess.run(
            [sys.executable, os.path.abspath(__file__),
             "--spec", notext_path, "--repo", d],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        out = cli.stdout.decode("utf-8", "replace")
        cases.append(("A_SPEC_CITATION_WITH_NO_TEXT_IS_STILL_RED",
                      cli.returncode != 0
                      and "CITATION_NO_TEXT=1" in out
                      and "CITATION_OK=1" in out
                      and "CITATION_BAD=1" in out
                      and "RESULT=FAIL" in out
                      and BROKEN_RUN not in out,
                      "exit=%d ok=1 no_text=1 bad=1 (measured, not broken)"
                      % cli.returncode))
    finally:
        shutil.rmtree(d, ignore_errors=True)

    passed = 0
    failed = 0
    print("")
    print("=" * 96)
    print("KNOWN-ANSWER TESTS")
    print("=" * 96)
    for name, ok, detail in cases:
        print("%-42s %-4s %s" % (name, "PASS" if ok else "FAIL", detail))
        if ok:
            passed += 1
        else:
            failed += 1
    token("CASES_PASS", passed)
    token("CASES_FAIL", failed)
    token("CASES_SKIP", 0)
    token("RESULT", "PASS" if failed == 0 else "FAIL")
    token("TRUST", "HIGH" if failed == 0 else "NONE")
    sys.exit(0 if failed == 0 else 1)


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------


def main(argv=None):
    ap = argparse.ArgumentParser(
        prog="citation-check.py",
        description="Check that file:line citations point at what they claim.")
    ap.add_argument("--repo", help="repository root (default: this file's repo)")
    ap.add_argument("--commit", help="the revision to measure against (default HEAD)")
    ap.add_argument("--spec", help="a JSON file of citations to check")
    ap.add_argument("--sweep", action="store_true",
                    help="extract citations from files and check them")
    ap.add_argument("--scan-root", help="directory to sweep (with --sweep)")
    ap.add_argument("--external", action="append", default=[],
                    help="a path that lives outside this repository (repeatable)")
    ap.add_argument("--max", type=int, default=0,
                    help="stop after N citations and report the rest as truncated")
    ap.add_argument("files", nargs="*", help="files to sweep")
    ap.add_argument("--self-test", action="store_true",
                    help="run the known-answer tests and exit")
    args = ap.parse_args(argv)

    if args.self_test:
        selftest()
    if args.sweep:
        mode_sweep(args)
    if args.spec:
        mode_verify(args)
    ap.error("give --spec, --sweep or --self-test")


if __name__ == "__main__":
    main()
