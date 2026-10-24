#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""probe-instance-census.py -- does a probe script choose its own instance?

WHY THIS EXISTS, and why it is not gate-base-census.py.

gate-base-census.py's population is pm/SPEC.yaml's `suites:` section cross-checked
against the gate's own Run lines (gate-base-census.py:18-29, :166-187).  It cannot
see tools/probe/ at all.  Its REFUSED test is a SYNTACTIC match on three fixed
shapes (gate-base-census.py:114-123) and it has no .sh entry, so pointing it at
the probes yields REFUSED 3 / NEITHER 7, where NEITHER is defined at :61-62 as
"a file that does not talk to a backend" -- the opposite of the truth.  Widening
that tool would manufacture confident wrong verdicts.

So this is a separate instrument whose rules are about the SHAPE of how a script
obtains an instance, not a list of literal strings.  The three probe .mjs files
read WZ_SERVER and argv; none of them mentions GATE_BASE, so any rule keyed on
GATE_BASE would report all 60 scripts clean and be wrong about every one.

WHAT IT ANSWERS, per file:
  PICKED       it supplies a default instance the caller never named   <- the defect
  SELF-HOSTED  it brings up its own server on its own port
  EXPLICIT     it takes the instance from a required input, with no default
  DEVTOOLS     it talks to the Chrome DevTools Protocol, not a backend
  NO-BACKEND   no instance site at all
  UNDETERMINED an instance site whose shape the rules do not classify;
               reported, never red

Usage:
  python tools/verify/probe-instance-census.py [--root .] [--self-test]

Exit: 0 = no PICKED, 1 = at least one PICKED, 2 = refused (could not scan).

RULES 1-26 of tools/verify/AGENTS.md apply.  The ones this tool exists to
obey are called out at the point they are implemented.
"""

import sys

# Rule 4-adjacent: this tool measures a tree that other people are working in.
# Writing a __pycache__/ into it is a write to the subject of the measurement.
# A sibling census was caught doing exactly that.
sys.dont_write_bytecode = True

import argparse
import os
import re
import shutil
import subprocess
import tempfile

# ----------------------------------------------------------------------------
# population
# ----------------------------------------------------------------------------

SCRIPT_EXTENSIONS = (".mjs", ".js", ".py", ".sh", ".ps1", ".cmd")

# Rule 18: a scan that silently stops early is worse than one that fails.
# Files larger than this are reported as `truncated`, never quietly dropped.
MAX_BYTES = 2 * 1024 * 1024

# The DECLARED EXTRA LIST -- a declared list with a stated rule, NOT a
# hard-named file whose absence is fatal.
#
# Before this, the extra was fatal: every root that did not contain
# MapleBench/tools/soak.py exited 2 with
#
#     >>> REDUSING: MapleBench/tools/soak.py is named in the population but is
#     not a file of the population rule in force.
#
# which meant the census could only ever be exercised on the repository
# itself.  A tool that can only be run against the tree it was written for
# cannot have an independent checker write a fixture against it, and then the
# author is the only judge of their own rule.
#
# Now: a declared extra that the population rule did not SELECT is a smaller
# tree, not a contradiction.  It is REPORTED (rule 18: a member of the
# declared population that is absent is counted, not silently dropped), and the
# census carries on.  A declared extra that WAS selected and then dropped by
# an exclusion is still a contradiction between the rule and the tree, and is
# still refused.
#
# The scope itself is unchanged: every script-shaped file under
# tools/probe/, plus this list.  Widening the scope to all of MapleBench/tools/
# would have added 12 files (browser-verify.mjs, probe-A*.mjs, probe-C*.mjs,
# sim-skills-check.mjs) and changed what this census IS, which is a decision
# for the owner and not for a bug fix.
DEFAULT_EXTRA = ("MapleBench/tools/soak.py",)

# The KIND GATE: which endpoint kinds count as "an instance the caller never
# named".  It is a module constant and not an inline literal so that the
# self-test can NARROW it and prove the narrowing is caught.  A control that
# cannot go RED is not a control.
ADOPTED_KINDS = ("BACKEND", "UNKNOWN")


# ----------------------------------------------------------------------------
# THE POPULATION RULE  (task-146)
# ----------------------------------------------------------------------------
#
# A census population is EITHER
#
#   (a) the tracked tree at a NAMED ref -- the file LIST and the file CONTENT
#       both come from that ref, or
#
#   (b) the working tree with every gitignored path excluded.
#
# There is no third option, and a plain directory walk is not one.  A walk
# sees whatever the machine happens to have on disk.  This repository's
# .gitignore carries 4055 ignored paths, 2869 of them under .chrome*/, and
# tools/probe/out/ gains a PNG every time a probe runs -- so a walk's
# population is a function of what has been run lately, not of the code.
# Measured on this tree: the walk saw 142 files under tools/probe/ and git
# lists 132.  The 10 files in between are exactly the gitignored ones.
#
# The rule in force is PRINTED, with the number of paths it excluded, because
# "which files did you look at" is the question every count depends on.
#
# A third case exists and is DECLARED rather than hidden: a root that is not
# inside a git repository at all -- every temporary tree the harness builds
# under %TEMP%.  There is no gitignore to consult there, so the walk is used
# and the output says so in words.

PROBE_PREFIX = "tools/probe/"


def git_bytes(root, args):
    """Run `git` in `root`; return stdout as bytes, or None if git failed.

    None means "git could not answer".  It is never an empty list: a census
    that cannot ask git has to say it could not ask (rule 1).
    """
    try:
        p = subprocess.run(["git", "-C", root] + list(args),
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    except (OSError, subprocess.SubprocessError):
        return None
    if p.returncode != 0:
        return None
    return p.stdout


def git_lines(root, args):
    raw = git_bytes(root, args)
    if raw is None:
        return None
    return [l for l in raw.decode("utf-8", "replace").splitlines() if l]


def repo_toplevel(root):
    """The top level of the repository holding `root`, or None.

    None means `root` is not inside a git repository at all.
    """
    lines = git_lines(root, ["rev-parse", "--show-toplevel"])
    if not lines:
        return None
    return os.path.abspath(lines[0].strip())


class Population(object):
    """The files a census reads, and the rule that chose them.

    `read()` and `size()` both go through git when the population came from a
    ref, so the LIST and the CONTENT always come from the same tree.  A list
    from one tree and bytes from another is rule 23's own failure.
    """

    def __init__(self, mode, ref, root, toplevel, prefix):
        self.mode = mode        # "worktree" | "ref" | "walk"
        self.ref = ref
        self.root = root
        self.toplevel = toplevel
        self.prefix = prefix    # repo-relative prefix of `root`, "" at the top

    def read(self, rel):
        if self.mode == "ref":
            raw = git_bytes(self.root, ["cat-file", "blob",
                                        "%s:%s" % (self.ref, self.prefix + rel)])
            if raw is None:
                raise RuntimeError("cannot read %s at ref %s" % (rel, self.ref))
            return raw
        with open(os.path.join(self.root, rel.replace("/", os.path.sep)), "rb") as fh:
            return fh.read()

    def size(self, rel):
        if self.mode == "ref":
            raw = git_bytes(self.root, ["cat-file", "-s",
                                        "%s:%s" % (self.ref, self.prefix + rel)])
            if raw is None:
                return None
            try:
                return int(raw.decode("ascii", "replace").strip())
            except ValueError:
                return None
        try:
            return os.path.getsize(
                os.path.join(self.root, rel.replace("/", os.path.sep)))
        except OSError:
            return None


def walk_candidates(root):
    """Every file under <root>/tools/probe/ -- the fallback population rule.

    Used only when `root` is not inside a git repository, and declared when
    it is used.
    """
    probe_dir = os.path.join(root, "tools", "probe")
    out = []
    for dirpath, dirnames, filenames in os.walk(probe_dir):
        dirnames[:] = sorted(d for d in dirnames if not d.startswith("."))
        for name in sorted(filenames):
            full = os.path.join(dirpath, name)
            out.append(os.path.relpath(full, root).replace(os.path.sep, "/"))
    return out


def build_population(root, extra, mode, ref):
    """Choose the population, and say which rule chose it.

    Returns a dict with: pop, files, skipped_ext, skipped_big, ignored,
    ignored_under_probe, absent, rule.
    """
    toplevel = repo_toplevel(root)
    ignored = 0
    ignored_under_probe = 0
    absent = 0

    if toplevel is None:
        pop = Population("walk", ref, root, None, "")
        rule = ("filesystem walk under tools/probe/ -- this root is not inside "
                "a git repository, so NO gitignore exclusion was applied")
        candidates = walk_candidates(root)
    else:
        root_rel = os.path.relpath(root, toplevel).replace(os.path.sep, "/")
        prefix = "" if root_rel == "." else root_rel + "/"
        if mode == "ref":
            lines = git_lines(toplevel, ["ls-tree", "-r", ref, "--name-only"])
            if lines is None:
                print(">>> RED: cannot list the tree at ref %r in %s"
                      % (ref, toplevel))
                sys.exit(2)
            rule = ("the tracked tree at ref %s, read from that ref's blobs"
                    % ref)
            candidates = [l[len(prefix):] for l in lines if l.startswith(prefix)]
        else:
            lines = git_lines(toplevel, ["ls-files", "--full-name", "--cached",
                                         "--others", "--exclude-standard"])
            if lines is None:
                print(">>> RED: git ls-files failed in %s" % toplevel)
                sys.exit(2)
            rule = ("the working tree of %s with every gitignored path "
                    "excluded (git ls-files --cached --others --exclude-standard)"
                    % toplevel)
            candidates = [l[len(prefix):] for l in lines if l.startswith(prefix)]
            ign = git_lines(toplevel, ["ls-files", "--full-name", "--others",
                                       "--ignored", "--exclude-standard"]) or []
            ign = [l[len(prefix):] for l in ign if l.startswith(prefix)]
            ignored = len(ign)
            ignored_under_probe = len([l for l in ign
                                       if l.startswith(PROBE_PREFIX)])
        pop = Population(mode, ref, root, toplevel, prefix)
        if mode != "ref":
            # A path in the index but gone from the working tree is not a file
            # of the working tree.  Counted, not silently dropped.
            kept = []
            for rel in candidates:
                if os.path.isfile(
                        os.path.join(root, rel.replace("/", os.path.sep))):
                    kept.append(rel)
                else:
                    absent += 1
            candidates = kept

    extra_set = set(extra)
    scoped = [rel for rel in candidates
              if rel.startswith(PROBE_PREFIX) or rel in extra_set]
    # The rels the population rule SELECTED, before the extension and size
    # filters.  A declared extra that is not here was never in this tree; one
    # that IS here and then got dropped is a rule/tree contradiction.
    selected = set(scoped)
    if toplevel is None:
        # The walk only covers tools/probe/, so the named extras have to be
        # added from the filesystem.
        # They are added to `selected` as well as to `scoped`: the rule DID
        # choose this file, so if an exclusion then drops it, the refusal
        # has to fire.  Before, `selected` was computed before the extras
        # were added, so in a root that is not inside a git repository the
        # refusal was unreachable and the tool behaved differently from a
        # git root for the same input (finding F2).
        for rel in extra:
            if os.path.isfile(os.path.join(root, rel.replace("/", os.path.sep))):
                scoped.append(rel)
                selected.add(rel)

    files, skipped_ext, skipped_big = [], [], []
    for rel in sorted(scoped):
        if not rel:
            continue
        # Whole-extension comparison.  A substring test would let "foo.mjsx"
        # or "x.mjs.bak" pass as ".mjs" and then be read as JavaScript.
        ext = os.path.splitext(rel.rsplit("/", 1)[-1])[1].lower()
        if ext not in SCRIPT_EXTENSIONS:
            skipped_ext.append(rel)
            continue
        size = pop.size(rel)
        if size is None:
            skipped_ext.append(rel)
            continue
        if size > MAX_BYTES:
            skipped_big.append(rel)
            continue
        files.append(rel)

    # A DECLARED extra that the rule did not SELECT is a smaller tree, not a
    # contradiction: this root simply does not contain it.  It is REPORTED,
    # because a census that silently scans fewer files than its rule names is
    # the shape rule 18 forbids, and because a reader has to be able to see
    # that the population was smaller.
    #
    # It is NOT fatal.  It used to be, and that made the census impossible to
    # exercise on any root but the repository itself -- so no independent
    # checker could write a fixture against it, and the author was the only
    # judge of the rule.  A declared extra that WAS selected and then dropped
    # by an exclusion is still a contradiction, and is still refused.
    extra_absent = []
    for rel in extra:
        if rel in files:
            continue
        if rel in selected:
            print(">>> REDUSING: %s is named in the population and was "
                  "selected by the population rule, but it was excluded."
                  % rel)
            sys.exit(2)
        extra_absent.append(rel)

    return {
        "pop": pop,
        "files": files,
        "skipped_ext": skipped_ext,
        "skipped_big": skipped_big,
        "ignored": ignored,
        "ignored_under_probe": ignored_under_probe,
        "absent": absent,
        "extra_absent": extra_absent,
        "rule": rule,
    }


# ----------------------------------------------------------------------------
# comment stripping
# ----------------------------------------------------------------------------

HASH_COMMENT_EXTENSIONS = (".py", ".sh", ".ps1", ".cmd")

# Which comment syntaxes apply, per extension.  This is the bug that decides
# whether the tool can see anything at all:
#   * In sh and PowerShell `//` is NOT a comment.  Stripping `//` there eats
#     `http://127.0.0.1:5210` and the file reports NO-BACKEND -- an
#     over-stripping exclusion, which produces a clean-looking empty result
#     that is indistinguishable from a clean tree.
#   * In JS/Python `//` (JS) or `#` (Python) IS a comment, and NOT stripping
#     them reports a URL that only exists in a comment as a real finding.
SLASH_COMMENT_EXTENSIONS = (".mjs", ".js")
HASH_COMMENT_EXTENSIONS = (".py", ".sh", ".ps1")


def strip_comments(text, ext):
    """Remove comments; keep string literals and keep every byte offset stable.

    Quote-aware on purpose.  `http://127.0.0.1:5590` contains `//`, so a naive
    stripper deletes the exact thing this tool is looking for.  TRAPS T-2
    (pm.py:918-926) is the same failure: a verdict decided on a search that
    includes comment text.

    Offsets are preserved (comments become spaces) so the line numbers in the
    findings are the real line numbers.
    """
    out = []
    i = 0
    n = len(text)
    quote = None
    slash_is_comment = ext in SLASH_COMMENT_EXTENSIONS
    hash_is_comment = ext in HASH_COMMENT_EXTENSIONS
    while i < n:
        c = text[i]
        if quote is not None:
            out.append(c)
            if c == "\\" and quote != "'":
                out.append(text[i + 1] if i + 1 < n else "")
                i += 2
                continue
            if c == quote:
                quote = None
            i += 1
            continue
        if c in ("'", '"', "`"):
            quote = c
            out.append(c)
            i += 1
            continue
        if text.startswith("/*", i):
            j = text.find("*/", i + 2)
            j = n if j < 0 else j + 2
            out.append(" " * (j - i))
            i = j
            continue
        if slash_is_comment and text.startswith("//", i):
            j = text.find("\n", i)
            j = n if j < 0 else j
            out.append(" " * (j - i))
            i = j
            continue
        if hash_is_comment and c == "#":
            j = text.find("\n", i)
            j = n if j < 0 else j
            out.append(" " * (j - i))
            i = j
            continue
        out.append(c)
        i += 1
    return "".join(out)


# ----------------------------------------------------------------------------
# the shapes
# ----------------------------------------------------------------------------

# A loopback host with whatever follows it.  The port may be a literal, a
# template hole, a shell expansion, or a <placeholder> in a usage message --
# the rules below decide what each of those MEANS, which is why this stays a
# shape and not a literal list.
SITE = re.compile(
    r"(?:https?://)?"
    r"(?P<host>127\.0\.0\.1|localhost|0\.0\.0\.0)"
    r"(?::(?P<port>\d{2,5}|\$\{[^}]*\}|\$[A-Za-z_]\w*|<[A-Za-z_]+>))?"
    r"(?P<path>/[^\s'\"`)]*)?"
)

# Defaulting operators, by language family.  A site is a DEFAULT only when one
# of these governs it; an instance built from required input is not a default.
JS_DEFAULT_BEFORE = re.compile(r"(\?\?|\|\|)")
ARGPARSE_DEFAULT = re.compile(r"\bdefault\s*=")
# The literal must BE the whole right-hand side.  `const url = new URL(x,
# `http://127.0.0.1:${PORT}`)` is a use, not a default -- matching on "there
# is an = on this line" instead flagged it, and a false PICKED is a defect
# invented in a file nobody is changing.
LITERAL_IS_RHS = re.compile(r"=\s*[\"'`]?\s*$")
# Shell default form: ${VAR:-word} / ${VAR:=word} / ${VAR-word}
SH_DEFAULT_BEFORE = re.compile(r"\$\{[A-Za-z_]\w*(:-|:=|[-=])")
PY_ENVGET_BEFORE = re.compile(r"(?:os\.)?(?:environ\.get|getenv)\s*\([^)]*,\s*[\"']?\s*$")
# How a script OBTAINS an instance.  A file can talk to a backend perfectly
# well while containing no loopback literal at all -- it reads argv or an env
# var.  Keying the verdict on literals alone calls those files NO-BACKEND,
# which is the wrong answer to the question this tool exists to answer.
SOURCE_ARGV = re.compile(r"process\.argv\[|process\.argv\b|sys\.argv\b")
SOURCE_ENV = re.compile(
    r"process\.env\.[A-Za-z_]\w*|process\.env\[|os\.environ|os\.getenv"
    r"|\$\{?(GATE_BASE|WZ_SERVER|MAPLEBENCH_[A-Z_]+)\}?"
)
SOURCE_RESOLVER = re.compile(r"require_gate_base\s*\(|Resolve-GateBase")
# Names that a script uses as "the thing I talk to".  This is a shape (the
# value flows into a fetch), not an allow-list of instance literals.
#
# `.bind(` on its own matches Function.prototype.bind.  Measured on this tree:
# zorder-probe.mjs:116,188,190 are `v.draw.bind(v)` / `v.drawAvatar.bind(v)` --
# a CDP client object with no server anywhere in the file -- so the loose
# pattern reported it SELF-HOSTED and HID its DEVTOOLS identity.  A socket bind
# passes a port, so require one.  `.listen(` and `createServer(` are already
# unambiguous and stay as they are.
SOCKET_BIND = re.compile(
    r"\.\s*bind\s*\(\s*(?:PORT|port|[\"']?(?:0\.0\.0\.0|127\.0\.0\.1|localhost))"
)
SELF_HOSTED = re.compile(
    r"\.\s*listen\s*\(|createServer\s*\(|" + SOCKET_BIND.pattern
)

# ----------------------------------------------------------------------------
# self-hosted, SECOND shape: the file SPAWNS the server it then talks to
# ----------------------------------------------------------------------------
# A port constant is only an ADOPTED instance if something ELSE is already
# listening there.  A file that STARTS the server itself is hosting its own,
# and hard-coding the port is then correct.
#
# Measured false positive (reviewer-nx, B3): tools/probe/mcp-assets-real.mjs
# hard-codes 15973 at :16, ASSERTS at :19 that the port is FREE, SPAWNS the
# backend on it at :21, and talks to it at :28.  It creates the thing it talks
# to.  The old SELF_HOSTED shape only recognised a socket bind IN THIS PROCESS
# (`.listen(` / `createServer(` / `.bind(`), so it could not see "spawn an
# external server and then talk to it": the file came out PICKED and the
# census exited 1 on it.
#
# The rule is deliberately NARROW, because a rule that is too wide clears real
# findings: the port a launch command assigns must be THE SAME port the file
# talks to.  "this file spawns something" is NOT the shape -- most of these
# files spawn a client, a browser or a helper.
LAUNCH_CALL = re.compile(
    r"(?:\b(?:spawn|spawnSync|execFile|execFileSync|fork)\s*\()"
    r"|(?:\bsubprocess\.(?:Popen|run|check_call|check_output)\s*\()"
    r"|(?:\bStart-Process\b)"
)
# `--port` and `--remote-debugging-port`.  A boundary is required: `--ports`
# and `--portal` are not port assignments.  `--port` cannot match inside
# `--remote-debugging-port` either: that string has ONE dash before "port".
PORT_FLAG = re.compile(r"--(?:remote-debugging-)?port\b")
# A socket bound IN THIS PROCESS.  The first argument is the port; anything
# else (`v.draw.bind(v)`) is not a port and resolves to nothing.
SOCKET_PORT = re.compile(r"\.\s*(?:listen|bind)\s*\(\s*([A-Za-z_]\w*|\d{2,5})")

# ----------------------------------------------------------------------------
# "the port must be FREE"  -- the abort-if-occupied guard
# ----------------------------------------------------------------------------
# THE DISCRIMINATOR, read off the two real cases:
#
#   tools/probe/mcp-assets-real.mjs:18-20
#       let occupied = false;
#       try { await fetch(`http://127.0.0.1:${port}`); occupied = true; } catch {}
#       assert.equal(occupied, false, 'Test port is occupied');
#     -> it PROBES the port and ABORTS if it is busy.  It will not go on to
#        use somebody else's instance, so the hard-coded port is its own.
#
#   MapleBench/tools/soak.py:76-85 (at HEAD)
#       def ensure_server(open_nx=True):
#           if alive():
#               return False
#           subprocess.Popen([NXRUN, "MapleBenchX.dll", "--port", "5590", ...])
#     -> it PROBES the port and, if something is already listening, USES it;
#        the spawn is only a fallback.  The hard-coded port IS an adopted
#        instance and the caller never named it.
#
# So a file that starts a server on a port it also names as a default is
# SELF-HOSTED only if it ALSO refuses to proceed when that port is busy.
# Without the guard the file is ADOPT-OR-START, and the port constant is a
# default instance.
#
# The guard is looked for inside ONE statement: the window never crosses a
# `;`, so a word in another statement cannot satisfy it.  The association
# between the guard and the port is INFERRED from the file's shape, never
# observed -- which is why every row now prints its basis.
#
# THE VOCABULARY.  These are the words that mean "this port is already
# taken".  It is a SET, it is spelled out here, and the self-test CHECKS that
# the list in its own docstring is the SAME set -- a comment that lies about
# its own pattern is the same defect class as a docstring that lies about
# its rule (finding F1: the pattern said `assert(occupied, False)` and the
# pattern could not match the capital-F).
#
# The set has to cover the spellings a human actually writes, not just the
# ones the first version happened to know:
#     occupied / busy / in use / in-use / inuse / EADDRINUSE /
#     already bound / already_bound / already running / taken / port_taken
#
# The pattern is CASE-INSENSITIVE, so `assert(occupied == False)` -- the
# Python spelling -- matches as well as `assert.equal(occupied, false, ...)`.
# (`\bfalse\b` on its own is case-SENSITIVE, and the first version of this
# pattern missed the capital-F while its own comment promised it.)
GUARD_VOCABULARY = (
    "already_running",
    "already_in_use",
    "already in use",
    "already_bound",
    "already bound",
    "port_taken",
    "is_busy",
    "in_use",
    "in use",
    "in-use",
    "inuse",
    "EADDRINUSE",
    "occupied",
    "busy",
    "taken",
)
GUARD_PHRASE = "(?:" + "|".join(re.escape(p) for p in GUARD_VOCABULARY) + ")"
#
# NOTE THE DOT: `assert[\w.]*`, not `assert\w*`.  `\w` does not match ".",
# so a pattern written as `assert\w*\s*\(` does NOT match `assert.equal(`
# -- which is the form the real file uses, and the form the fixture uses.
# The guard silently never fired, and the fixture it was supposed to protect
# came out PICKED.  A pattern that cannot match is a pattern that cannot fail.
# A REFUSAL: what a guard does when the port turns out to be taken.  Either
# it asserts that the port is NOT taken, or it throws / raises / exits.
#
# A guard has TWO halves -- a vocabulary word and a refusal -- and they can
# be on one line or split across a block (`if occupied:` then `raise ...` on
# the next line).  So they are matched by CONTEXT, not by one regex.
#
# The first version of this was ONE regex with a `[^;]` window, and that
# window crosses NEWLINES: the match started at the first "occupied" in the
# file and ran on to the first "raise", which is not a statement at all.
# (It is also why the reverse arm could not be linked to its port: the
# match began on a line about a different port entirely.)  The window here
# is line-limited, and the block is carried by _guard_context.
#
# NOTE THE DOT: `assert[\w.]*`, not `assert\w*`.  `\w` does not match ".",
# so a pattern written as `assert\w*\s*\(` does NOT match `assert.equal(`
# -- which is the form the real file uses, and the form the fixture uses.
# The guard silently never fired, and the fixture it was supposed to protect
# came out PICKED.  A pattern that cannot match is a pattern that cannot fail.
GUARD_REFUSAL = re.compile(
    # assert.equal(occupied, false, ...) / assert(occupied == False)
    r"assert[\w.]*\s*\([^;\n]{0,200}?\bfalse\b"
    # assert(!occupied, ...)
    r"|assert[\w.]*\s*\([^;\n]{0,200}?![^;\n]{0,40}?"
    # if occupied: raise / if (occupied) { throw ... } / if busy(PORT): exit(1)
    r"|\b(?:throw|raise|sys\.exit|process\.exit|exit)\b",
    re.IGNORECASE)


def _guard_context(code, pos):
    """The guard's CONTEXT: its own line, plus the block under it.

    A guard in Python is `if occupied:` on one line and `raise ...` on the
    next, so the line alone is not the statement -- but the whole `;`
    segment is too wide, because a semicolon-free Python file puts EVERY
    line in one segment, and then the guard's context would contain the
    binding of a DIFFERENT port and link the guard to it.  (That is exactly
    what happened to the reverse-arm fixture: its context swallowed
    `PORT_B = 5616` and the guard about port A was linked to port B.)

    So the context is the guard's line plus the lines indented deeper than
    it -- the block it introduces.
    """
    lines = code.splitlines()
    idx = code.count("\n", 0, pos)
    if idx >= len(lines):
        return ""
    first = lines[idx]
    indent = len(first) - len(first.lstrip())
    out = [first]
    for nxt in lines[idx + 1:]:
        if not nxt.strip():
            out.append(nxt)
            continue
        ind = len(nxt) - len(nxt.lstrip())
        if ind > indent:
            out.append(nxt)
        else:
            break
    return "\n".join(out)


def _mentions_port(statement, port, numeric_vars):
    """Does this statement talk about THAT port?

    Its digits, or a variable bound to it.
    """
    if re.search(r"\b%s\b" % re.escape(port), statement):
        return True
    for name, value in numeric_vars.items():
        if value == port and re.search(r"\b%s\b" % re.escape(name), statement):
            return True
    return False


def _guard_hits(code):
    """Every free-port guard in the code, with the context that says so.

    A guard is a vocabulary word and a REFUSAL in the same context.  The
    context of a word is its own line plus the block indented under it, so
    a Python guard split over two lines is still one guard.
    """
    out = []
    for m in re.finditer(r"\b" + GUARD_PHRASE + r"\b", code, re.IGNORECASE):
        ctx = _guard_context(code, m.start())
        if GUARD_REFUSAL.search(ctx):
            out.append((m.start(), ctx))
    return out


def guarded_ports(code, numeric_vars, candidates):
    """The ports a free-port guard is ABOUT.

    A guard clears a port only if it is about THAT port.  A file that guards
    port A and then starts and adopts port B is ADOPTING port B, and must
    stay PICKED.  (Finding V7: the guard used to be a file-level flag, so
    such a file was cleared wrongly.  The limitation was real and latent.)

    The link is the guard's OWN STATEMENT: if it names the port -- its
    digits, or a variable bound to it -- the guard is about that port.  A
    guard that names NO port at all is treated as FILE-WIDE, which is what
    tools/probe/mcp-assets-real.mjs needs: its guard reads
    `assert.equal(occupied, false, 'Test port is occupied')` and names no
    port in that statement.
    """
    guarded = set()
    any_guard = False
    any_named = False
    # The ports the file MENTIONS, which is wider than the ports it starts
    # or talks to: a guard can be about a port the file only PROBES (port A
    # in the reverse-arm fixture), and that port still has to be linkable,
    # or the guard falls through to file-wide and clears the port the file
    # really adopts.
    mentioned = set(candidates)
    for value in numeric_vars.values():
        mentioned.add(value)
    for _pos, ctx in _guard_hits(code):
        any_guard = True
        hits = set(p for p in mentioned
                   if _mentions_port(ctx, p, numeric_vars))
        if hits:
            guarded |= hits
            any_named = True
    if not any_guard:
        # NO guard at all: nothing is guarded.  (Treating "no guard" as
        # file-wide would clear every file that starts its own server, which
        # is the pre-fix behaviour wearing a new name.)
        return set(), False
    if not any_named:
        # There IS a guard, and it names no port: file-wide.
        return set(candidates), True
    return guarded, False


def _port_from_segment(segment, numeric_vars):
    """The port a launch argument names, or None.

    An identifier bound to a bare number wins over a bare number, because one
    segment can hold both (`str(PORT)` next to a number in a later argument).
    """
    for im in re.finditer(r"\b([A-Za-z_]\w*)\b", segment):
        if im.group(1) in numeric_vars:
            return numeric_vars[im.group(1)]
    for nm in re.finditer(r"\b(\d{2,5})\b", segment):
        return nm.group(1)
    return None


def external_started_ports(code, numeric_vars):
    """The ports this file assigns to a PROCESS IT LAUNCHES.

    `spawn(exe, ['--port', PORT])`, `subprocess.Popen([exe, '--port',
    str(PORT)])`, `['--port=' + port]`, `--remote-debugging-port=${PORT}`.

    The server is a SEPARATE process, so the question "is somebody ELSE
    already listening on that port?" is still OPEN, and the answer decides
    whether the port constant is an adopted instance.

    The window of a launch is cut at the first `;` or `}`, so a later
    launch's arguments cannot be attributed to an earlier one.
    """
    out = set()
    for m in LAUNCH_CALL.finditer(code):
        window = code[m.end(): m.end() + 400]
        cut = len(window)
        for stop in (";", "}"):
            i = window.find(stop)
            if i != -1:
                cut = min(cut, i)
        window = window[:cut]
        pm = PORT_FLAG.search(window)
        if not pm:
            continue
        parts = window[pm.end():].split(",")
        segment = parts[0]
        if not re.search(r"[A-Za-z0-9]", segment):
            # the flag was its own argument (`'--port', PORT`): the value is
            # the NEXT one.
            segment = parts[1] if len(parts) > 1 else ""
        p = _port_from_segment(segment, numeric_vars)
        if p is not None:
            out.add(p)
    return out


def in_process_ports(code, numeric_vars):
    """The ports this file binds in ITS OWN process.

    `srv.listen(PORT, ...)`, `server.listen(0, ...)`.  Here the file IS the
    server: it cannot be talking to somebody else's instance on a port it
    bound itself, so the port constant is not an adopted instance.
    """
    out = set()
    for m in SOCKET_PORT.finditer(code):
        token = m.group(1)
        if token.isdigit():
            out.add(token)
        elif token in numeric_vars:
            out.add(numeric_vars[token])
    return out

# F1: a numeric constant bound to a port is a decidable shape.  The RHS must be
# a BARE number at end of line, which is what separates `const port = 15973;`
# from `const PORT = 9222 + (process.pid % 400);` and from a ternary's `: 59997`
# branch -- both of the latter are computed, not chosen.
NUMERIC_BINDING = re.compile(
    r"\b(?:const|let|var)?\s*([A-Za-z_]\w*)\s*=\s*(\d{2,5})\s*;?\s*$"
)


def endpoint_kind(path):
    """What is this URL an endpoint OF?

    /json/... is the Chrome DevTools Protocol: a browser that this script
    launched, not a backend instance.  /api/... is the MapleBench backend.
    """
    if not path:
        return "BACKEND"
    if path.startswith("/json"):
        return "DEVTOOLS"
    if path.startswith("/api"):
        return "BACKEND"
    return "UNKNOWN"


def numeric_bindings(lines):
    """identifier -> the bare number it is assigned, for THIS file.

    F1.  A script can pick its own instance without ever writing a literal URL:
    `const port = 15973;` then `` fetch(`http://127.0.0.1:${port}`) ``.  The URL
    looks like a template hole and reads as "the caller supplies it", which is
    how mcp-assets-real.mjs came out EXPLICIT / obtained=NONE -- the only row in
    a 102-row cross-tab that found a backend URL, could name no source, and
    could name no default, and was still called clean.

    The RHS must be a bare number at end of line.  That is the discriminator:
    `const PORT = 9222 + (process.pid % 400) + (o || 0)` and
    `const D = x >= 0 ? Number(argv[1]) : 59997` are COMPUTED, and must not be
    picked up here -- both occur live in page-check.mjs.
    """
    out = {}
    for line in lines:
        m = NUMERIC_BINDING.search(line)
        if m:
            out.setdefault(m.group(1), m.group(2))
    return out


def resolve_port(port_text, numeric_vars):
    """The digits a port expression actually evaluates to, or None.

    '5100'          -> '5100'
    '${PORT}'       -> numeric_vars['PORT']   (None if not a bare number)
    '{port}'        -> numeric_vars['port']
    'process.pid'   -> None  (computed)
    """
    if not port_text:
        return None
    t = port_text.strip()
    if t.isdigit():
        return t
    m = re.fullmatch(r"\$\{?([A-Za-z_]\w*)\}?", t)
    if m:
        return numeric_vars.get(m.group(1))
    return None


def is_defaulted(site, stripped_line, ext, numeric_vars):
    """True when the site supplies an instance the caller never named.

    Two ways that happens, and the second is the one a URL-only rule misses:

      1. the port is a numeric LITERAL sitting in a position that supplies a
         value by itself:
             const X = 'http://127.0.0.1:5590'
             X ?? 'http://127.0.0.1:5590'
             ${VAR:-http://127.0.0.1:5210}      (shell)
             os.environ.get("X", "http://...")  (python)
             ... default='http://...'           (argparse)

      2. the port is a VARIABLE whose binding is a numeric constant elsewhere in
         the file (F1).  The binding is itself the supply, so no URL-literal
         position test applies.

    What is deliberately NOT a default, each of which has a live example here:
      ${PORT}   -- a hole whose value comes from argv   (page-check.mjs:62)
      <port>    -- a placeholder printed as usage text (page-check.mjs:239)
    """
    port = site["port"]
    if not port or not port.strip().isdigit():
        # Not a literal in the URL.  It may still be a numeric constant bound
        # elsewhere in this file -- that is shape 2.
        m = re.fullmatch(r"\$\{?([A-Za-z_]\w*)\}?", (port or "").strip())
        return bool(m and m.group(1) in numeric_vars)
    before = stripped_line[:site["start"]]
    if ext in (".mjs", ".js", ".ps1", ".cmd"):
        return bool(JS_DEFAULT_BEFORE.search(before)
                    or ARGPARSE_DEFAULT.search(before)
                    or LITERAL_IS_RHS.search(before))
    if ext == ".py":
        return bool(PY_ENVGET_BEFORE.search(before) or LITERAL_IS_RHS.search(before))
    if ext == ".sh":
        return bool(SH_DEFAULT_BEFORE.search(before) or LITERAL_IS_RHS.search(before))
    return False


def how_obtained(code):
    """How this script obtains its instance.  Reported for every file.

    None of the three probe .mjs files mentions GATE_BASE, so any rule keyed on
    that variable would call all of them instance-free.  The shape that matters
    is "does it read a caller-supplied value at all".
    """
    if SOURCE_RESOLVER.search(code):
        return "RESOLVER"
    if SOURCE_ENV.search(code):
        return "ENV"
    if SOURCE_ARGV.search(code):
        return "ARGV"
    return "NONE"


def ascii_safe(text, limit=70):
    """Printable ASCII only.

    Rule 25: the tool prints fields a verdict is decided on, and those fields
    must survive being pasted into a report that has to stay ASCII.
    """
    s = "".join(ch if 32 <= ord(ch) < 127 else "?" for ch in text)
    s = " ".join(s.split())
    return s[:limit] + ("..." if len(s) > limit else "")


def classify(rel, pop):
    ext = os.path.splitext(rel)[1].lower()
    raw = pop.read(rel).decode("utf-8", "replace")
    code = strip_comments(raw, ext)
    lines = code.splitlines()
    raw_lines = raw.splitlines()

    numeric_vars = numeric_bindings(lines)
    sites = []
    for lineno, line in enumerate(lines, 1):
        for m in SITE.finditer(line):
            kind = endpoint_kind(m.group("path"))
            sites.append({
                "line": lineno,
                "host": m.group("host"),
                "port": m.group("port"),
                "resolved_port": resolve_port(m.group("port"), numeric_vars),
                "path": m.group("path") or "",
                "kind": kind,
                "start": m.start(),
                "defaulted": is_defaulted({"port": m.group("port"),
                                           "start": m.start()}, line, ext,
                                          numeric_vars),
                "text": raw_lines[lineno - 1].strip() if lineno - 1 < len(raw_lines) else "",
            })

    external = external_started_ports(code, numeric_vars)
    own = in_process_ports(code, numeric_vars)
    started = external | own
    self_hosted = bool(SELF_HOSTED.search(code)) or bool(started)
    # The ports a guard is ABOUT.  A guard clears a port only if it is about
    # that port, so a file that guards port A and adopts port B is still
    # PICKED for B.
    candidates = set(external) | set(own)
    for s in sites:
        if s["resolved_port"]:
            candidates.add(s["resolved_port"])
    guard_ports, guard_file_wide = guarded_ports(code, numeric_vars, candidates)
    source = how_obtained(code)
    backends = [s for s in sites if s["kind"] == "BACKEND"]
    # F2.  This was BACKEND-only, so a hard-coded default whose path was
    # something other than /api or empty -- `/status` is the shape the checker
    # named -- landed in UNDETERMINED and exited 0.  The kind gate exists to
    # keep DevTools out, not to keep every other path out.  A browser's
    # /json/list endpoint is not an instance; a /status one is.
    #
    # THE ADOPTION RULE, per site.  A port constant is an ADOPTED instance
    # only when somebody ELSE is already listening there.  A port the file
    # starts itself is not one -- UNLESS the file also refuses to proceed when
    # that port is busy, in which case it provably will not adopt anybody's
    # instance.
    #
    #   disposition        meaning
    #   -----------------  -------------------------------------------------
    #   in-process-bind    the file binds that port itself: it IS the server
    #   spawn-only         the file starts a server on that port AND has a
    #                      free-port guard: it aborts if the port is busy
    #   adopt-or-start     the file starts a server on that port but has NO
    #                      guard: it uses whatever is already listening and
    #                      only starts its own as a fallback
    #   defaulted-port     the file names that port as a default and does not
    #                      start it at all
    dispositions = []
    for s in sites:
        if not s["defaulted"] or s["kind"] not in ADOPTED_KINDS:
            dispositions.append((s, "not-a-default"))
            continue
        p = s["resolved_port"]
        if p in own:
            dispositions.append((s, "in-process-bind"))
        elif p in external and p in guard_ports:
            dispositions.append((s, "spawn-only"))
        elif p in external:
            dispositions.append((s, "adopt-or-start"))
        else:
            dispositions.append((s, "defaulted-port"))

    adopted = [s for s, d in dispositions
               if d in ("adopt-or-start", "defaulted-port")]

    # Order matters and is deliberate.  SELF-HOSTED is judged BEFORE PICKED:
    # a file that creates the thing it talks to is not adopting anybody's
    # instance.  What "creates it" means is decided PER SITE, by the
    # disposition above -- a file can do BOTH (start a server on port A and
    # adopt a default on port B), and a single file-level flag would silently
    # clear the second case.  That was the defect reviewer-nx raised as R1:
    # the implementation said "this file spawns a server on this port =>
    # SELF-HOSTED" while the rule said "a port the file starts itself is
    # SELF-HOSTED".  A file that starts a server on the port it ALSO adopts
    # (MapleBench/tools/soak.py at HEAD: `if alive(): return False` and then
    # a fallback spawn on the same port) is ADOPTED, and must stay PICKED.
    if self_hosted and sites and not adopted:
        verdict = "SELF-HOSTED"
    elif adopted:
        verdict = "PICKED"
    elif backends:
        verdict = "EXPLICIT"
    elif any(s["kind"] == "DEVTOOLS" for s in sites):
        # It talks to a browser it launched, never to a backend instance.  This
        # outranks "it read argv", because the useful fact is the endpoint kind.
        verdict = "DEVTOOLS"
    elif any(s["kind"] == "UNKNOWN" for s in sites):
        verdict = "UNDETERMINED"
    elif source != "NONE":
        # No literal at all: the script reads a caller-supplied value.  That is
        # the normal shape for a probe that takes its instance from argv or an
        # env var, and it is NOT the same as "talks to no backend".
        verdict = "EXPLICIT"
    else:
        verdict = "NO-BACKEND"

    # THE BASIS of this row, printed on the row itself.  Every one of these
    # is INFERRED from the code's shape: the port-busy condition is never
    # observed at run time, because this is a static scan of text.
    #
    #   spawn-only        it starts the server on this port and refuses to
    #                     run if the port is busy
    #   adopt-or-start    it starts the server on this port only as a
    #                     fallback, and uses whatever is already listening
    #   in-process-bind   it binds the port in its own process
    #   defaulted-port    it names the port as a default and starts nothing
    #   not-a-default     the file names no default instance
    if verdict == "PICKED":
        if any(d == "adopt-or-start" for _, d in dispositions):
            basis = "adopt-or-start"
        else:
            basis = "defaulted-port"
    elif verdict == "SELF-HOSTED":
        if any(d == "spawn-only" for _, d in dispositions):
            basis = "spawn-only"
        elif own:
            basis = "in-process-bind"
        else:
            basis = "spawns-its-own"
    else:
        basis = "not-a-default"

    return {
        "file": rel,
        "verdict": verdict,
        "source": source,
        "sites": sites,
        "self_hosted": self_hosted,
        "started_ports": sorted(started),
        "external_ports": sorted(external),
        "own_ports": sorted(own),
        "free_port_guard": bool(guard_ports),
        "guarded_ports": sorted(guard_ports),
        "guard_file_wide": guard_file_wide,
        "basis": basis,
        "line_count": len(raw_lines),
    }


# ----------------------------------------------------------------------------
# the exclusion audit
# ----------------------------------------------------------------------------

def exclusion_audit(files, skipped_ext, skipped_big, notes=()):
    """How many files each exclusion removed, and what is left.

    The trap this answers: a rule written to "not miss anything" whose exclusion
    logic is systematically biased toward "do not report" produces an empty
    result that looks exactly like a clean tree.  That is harder to notice than
    a rule that is simply broken, so the exclusions are printed, with counts,
    instead of being invisible.
    """
    print()
    print("EXCLUSION AUDIT -- an exclusion that eats the population makes an empty")
    print("result worthless, so every one of them is counted out loud.")
    total_files = len(files) + len(skipped_ext) + len(skipped_big)
    print("  %-34s %8s %10s" % ("rule", "excluded", "remaining"))
    print("  %-34s %8d %10d" % ("(population under tools/probe/ + extras)", total_files, total_files))
    print("  %-34s %8d %10d" % ("not a script extension", len(skipped_ext), len(files) + len(skipped_big)))
    print("  %-34s %8d %10d" % ("larger than MAX_BYTES", len(skipped_big), len(files)))
    print()
    for note in notes:
        print("  %s" % note)
    print()
    print("  MAX_BYTES = %d.  Rule 18: files over the limit are reported as" % MAX_BYTES)
    print("  `truncated`, never silently dropped.")
    for rel in skipped_big:
        print("    truncated: %s" % rel)
    non_script_ratio = (len(skipped_ext) / total_files) if total_files else 0.0
    print()
    print("  excluded for being non-script: %d of %d = %.1f%%"
          % (len(skipped_ext), total_files, non_script_ratio * 100))
    # F4.  RETURN the verdict.  It used to be printed and thrown away, and
    # main() carried on to print RESULT=CLEAN -- a tool disagreeing with its own
    # result in the same output.
    if not total_files:
        print("  >>> RED: the population is empty.  There is nothing to conclude.")
        return False, "the population is empty"
    if len(files) < 5:
        return False, ("only %d script file(s) survived the exclusions, which is "
                       "below the 5 this tool will conclude from" % len(files))
    if non_script_ratio > 0.9:
        return False, ("%d of %d files were excluded as non-script (%.1f%%), so "
                       "the scan saw a small remainder of a large tree"
                       % (len(skipped_ext), total_files, non_script_ratio * 100))
    return True, "population intact"


# ----------------------------------------------------------------------------
# the positive control
# ----------------------------------------------------------------------------

FIXTURES = {
    # --- positives: each of these MUST be reported PICKED -------------------
    "picks-js.mjs": (
        "const SERVER = process.env.WZ_SERVER ?? 'http://127.0.0.1:5590/api';\n"
        "fetch(`${SERVER}/files`);\n"
    ),
    "picks-assign.mjs": (
        "const BASE = 'http://127.0.0.1:5460/api';\n"
        "await fetch(BASE + '/files');\n"
    ),
    "picks-py.py": (
        "import os\n"
        "BASE = os.environ.get('GATE_BASE', 'http://127.0.0.1:5100/api')\n"
        "print(BASE)\n"
    ),
    "picks-sh.sh": (
        "BASE=${GATE_BASE:-http://127.0.0.1:5210}/api\n"
        "curl -sS \"$BASE/files\"\n"
    ),
    # F1.  No literal URL anywhere -- the port is a numeric CONSTANT and the URL
    # is a template hole.  The URL-only rule called this shape EXPLICIT/NONE:
    # it found the backend URL, could name no source and no default, and still
    # called the file clean.
    #
    # THIS IS THE POSITIVE CONTROL for the whole tool, and the requirement the
    # lead states for any fix here: a file that hard-codes a port, talks to an
    # already-running backend, and does NOT start one.  It must be PICKED, and
    # the census must still exit non-zero for it.  A fix that makes everything
    # CLEAN is a broken fix, and this fixture is what would catch that.
    #
    # It used to be described as "the live shape of mcp-assets-real.mjs:16,19".
    # That sentence was the ROOT CAUSE of a measured false positive: the
    # fixture was built from the two lines of the real file that MATCHED, and
    # not from the file.  The real file also SPAWNS the backend on that port
    # (:21), so it is self-hosted -- see selfhosted-spawn.mjs below.
    "picks-numeric-const.mjs": (
        "const port = 15973;\n"
        "try { await fetch(`http://127.0.0.1:${port}`); } catch {}\n"
        "await fetch(`http://127.0.0.1:${port}/api/files`);\n"
    ),
    # F2.  A hard-coded default whose path is NOT /api and NOT empty.  Under the
    # old BACKEND-only kind gate this landed in UNDETERMINED and exited 0.
    "picks-status-path.mjs": (
        "const server = process.env.GATE_BASE ?? 'http://127.0.0.1:5733/status';\n"
        "await fetch(server);\n"
    ),
    # ADOPT-OR-START.  This is the shape of MapleBench/tools/soak.py at HEAD:
    # the port is hard-coded, the file PROBES it, and if something is already
    # listening it USES that instance and only starts its own as a fallback.
    # The hard-coded port is therefore an ADOPTED instance and this file must
    # STAY PICKED -- even though the file also starts a server on that port.
    #
    # The POSITIVE CONTROL beside it in the self-test is
    # picks-numeric-const.mjs: the plain adopted shape, which must also be
    # PICKED.  Without that control, a detector that never fires at all would
    # make this case pass for the wrong reason.
    "adopt-or-start.py": (
        "import subprocess\n"
        "import urllib.request\n"
        "BASE = 'http://127.0.0.1:5590'\n"
        "def alive():\n"
        "    try:\n"
        "        urllib.request.urlopen(BASE + '/api/files', timeout=5)\n"
        "        return True\n"
        "    except Exception:\n"
        "        return False\n"
        "def ensure_server():\n"
        "    if alive():\n"
        "        return False\n"
        "    subprocess.Popen(['MapleBenchX.dll', '--port', '5590'])\n"
        "    return True\n"
    ),
    # --- negatives: each of these MUST NOT be reported PICKED ---------------
    "clean-argv.mjs": (
        "const server = process.argv[2];\n"
        "if (!server) { console.log('>>> RED: give me an instance'); process.exit(1); }\n"
        "await fetch(`${server}/api/files`);\n"
    ),
    "clean-env.mjs": (
        "const server = (process.env.WZ_SERVER || '').trim();\n"
        "if (!server) { console.log('>>> RED: WZ_SERVER is not set'); process.exit(1); }\n"
        "await fetch(`${server}/api/files`);\n"
    ),
    # the URL is inside a COMMENT: a naive stripper would report this PICKED
    "commented-out.mjs": (
        "// const SERVER = 'http://127.0.0.1:5590/api';   <- removed in task-139\n"
        "const server = process.argv[2];\n"
        "await fetch(`${server}/api/files`);\n"
    ),
    # the URL is inside a block comment
    "block-commented.mjs": (
        "/* const SERVER = 'http://127.0.0.1:5590/api'; */\n"
        "const server = process.argv[2];\n"
        "await fetch(`${server}/api/files`);\n"
    ),
    # the URL is inside a STRING that is printed as usage text: it is real text
    # but it is not a default, and `//` inside a string must survive stripping
    "usage-string.mjs": (
        "const server = process.argv[2];\n"
        "console.log('for example: http://127.0.0.1:<port>/api');\n"
        "await fetch(`${server}/api/files`);\n"
    ),
    # talks to a browser it launched, not to a backend instance
    # a hard-coded DevTools port: the browser this script launched, not an
    # instance it adopted
    "devtools-defaulted.mjs": (
        "const PORT = 9222;\n"
        "const ver = await fetch(`http://127.0.0.1:${PORT}/json/version`);\n"
        "console.log(ver);\n"
    ),
    "devtools.mjs": (
        "const PORT = process.argv[2];\n"
        "const ver = await fetch(`http://127.0.0.1:${PORT}/json/version`);\n"
        "console.log(ver);\n"
    ),
    # `const U = `http://127.0.0.1:${PORT}`` sits in an assignment with the
    # literal as its whole RHS, and a looser rule reported it PICKED.  The port
    # is a variable, so it is EXPLICIT.  Six real files have this exact shape.
    "template-hole.mjs": (
        "const PORT = process.argv[2];\n"
        "const U = `http://127.0.0.1:${PORT}`;\n"
        "await fetch(U + '/api/files');\n"
    ),
    # `<port>` is a placeholder printed as usage text, in a default-looking spot
    "placeholder.mjs": (
        "const server = process.argv[2];\n"
        "const example = 'http://127.0.0.1:<port>/api';\n"
        "console.log('for example: ' + example);\n"
    ),
    # F5.  `v.draw.bind(v)` is Function.prototype.bind on a CDP view object.
    # There is no server in this file at all, and the loose `\.bind\s*\(` pattern
    # reported it SELF-HOSTED -- which HID the DEVTOOLS verdict underneath it.
    # This is the live shape of zorder-probe.mjs:116,188,190.
    "fn-bind.mjs": (
        "const PORT = process.argv[2];\n"
        "const list = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();\n"
        "const origDraw = list[0].draw.bind(list[0]);\n"
        "origDraw();\n"
    ),
    # brings up its own server
    "selfhosted.js": (
        "const PORT = Number(process.argv[2] ?? 5399);\n"
        "const url = new URL(req.url, `http://127.0.0.1:${PORT}`);\n"
        "srv.listen(PORT, '127.0.0.1');\n"
    ),
    # a REAL socket bind must still be caught by the narrowed pattern.  If
    # narrowing `.bind(` had thrown the whole family away, this fixture would go
    # EXPLICIT and the negative above would pass for the wrong reason.
    "socket-bind.mjs": (
        "const PORT = process.argv[2];\n"
        "srv.bind(PORT, '127.0.0.1');\n"
        "await fetch('http://127.0.0.1:' + PORT + '/api/files');\n"
    ),
    # The REAL shape of tools/probe/mcp-assets-real.mjs: it hard-codes a port,
    # ASSERTS the port is free, SPAWNS the backend on that port and then talks
    # to it.  It creates the thing it talks to, so the hard-coded port is
    # CORRECT and this file must NOT be reported PICKED.
    #
    # Measured false positive: the old SELF_HOSTED shape only saw a socket bind
    # IN THIS PROCESS, so this file came out PICKED and the census exited 1 on
    # it.  The fixture is the whole file, not the two lines that matched.
    "selfhosted-spawn.mjs": (
        "import { spawn } from 'node:child_process';\n"
        "const port = 15973;\n"
        "let occupied = false;\n"
        "try { await fetch(`http://127.0.0.1:${port}`); occupied = true; } catch {}\n"
        "assert.equal(occupied, false, 'Test port is occupied');\n"
        "const backend = spawn('MapleBenchX.exe', ['--port=' + port, '--no-browser'], { stdio: 'ignore' });\n"
        "const res = await fetch(`http://127.0.0.1:${port}/api/files`);\n"
        "console.log(res.ok);\n"
    ),
    # Starts its OWN server on 5399 AND defaults to somebody else's backend on
    # 5590.  It is still adopting an instance it was not given, so it must
    # STAY PICKED.
    #
    # This is the fixture a blind "self-hosted before PICKED" order flip
    # breaks: the file IS self-hosted, and it IS still picking.  The
    # discriminator is the PORT, not the file -- a self-started port is not an
    # adopted instance, and a different port still is.
    # --- one fixture per GUARD SPELLING ------------------------------------
    # A spelling that is not in this suite is a spelling that will silently
    # rot (finding F1).  Each of these STARTS the server on the port it
    # talks to, so each must come out SELF-HOSTED.
    #
    # The capital-F Python assert.  The old pattern's `\bfalse\b` is
    # case-SENSITIVE, so this one was missed while the pattern's own comment
    # promised it.
    "guard-assert-capital.py": (
        "import socket\n"
        "import subprocess\n"
        "PORT = 5617\n"
        "BASE = 'http://127.0.0.1:5617'\n"
        "occupied = False\n"
        "try:\n"
        "    socket.create_connection(('127.0.0.1', PORT), 1)\n"
        "except OSError:\n"
        "    pass\n"
        "else:\n"
        "    occupied = True\n"
        "assert (occupied == False), 'Test port is occupied'\n"
        "subprocess.Popen(['MapleBenchX.dll', '--port', str(PORT)])\n"
    ),
    # 'already in use' + sys.exit(1)
    "guard-in-use.py": (
        "import socket\n"
        "import subprocess\n"
        "import sys\n"
        "PORT = 5618\n"
        "BASE = 'http://127.0.0.1:5618'\n"
        "message = ''\n"
        "try:\n"
        "    socket.create_connection(('127.0.0.1', PORT), 1)\n"
        "except OSError:\n"
        "    pass\n"
        "else:\n"
        "    message = 'already in use'\n"
        "if 'already in use' in message:\n"
        "    sys.exit(1)\n"
        "subprocess.Popen(['MapleBenchX.dll', '--port', str(PORT)])\n"
    ),
    # 'EADDRINUSE' + process.exit(1)
    "guard-eaddrinuse.mjs": (
        "import { spawn } from 'node:child_process';\n"
        "const port = 5619;\n"
        "let error = '';\n"
        "try {\n"
        "  const res = await fetch(`http://127.0.0.1:${port}`);\n"
        "  error = res.status === 200 ? 'EADDRINUSE' : '';\n"
        "} catch {}\n"
        "if (error === 'EADDRINUSE') {\n"
        "  process.exit(1);\n"
        "}\n"
        "const backend = spawn('MapleBenchX.exe', ['--port=' + port], { stdio: 'ignore' });\n"
        "const res = await fetch(`http://127.0.0.1:${port}/api/files`);\n"
    ),
    # 'already bound' + raise
    "guard-already-bound.py": (
        "import socket\n"
        "import subprocess\n"
        "PORT = 5620\n"
        "BASE = 'http://127.0.0.1:5620'\n"
        "message = ''\n"
        "try:\n"
        "    socket.create_connection(('127.0.0.1', PORT), 1)\n"
        "except OSError:\n"
        "    message = 'already bound'\n"
        "if 'already bound' in message:\n"
        "    raise RuntimeError('Test port is already bound')\n"
        "subprocess.Popen(['MapleBenchX.dll', '--port', str(PORT)])\n"
    ),
    # if busy(PORT): sys.exit(1)
    "guard-busy.py": (
        "import subprocess\n"
        "import sys\n"
        "PORT = 5621\n"
        "BASE = 'http://127.0.0.1:5621'\n"
        "if busy(PORT):\n"
        "    sys.exit(1)\n"
        "subprocess.Popen(['MapleBenchX.dll', '--port', str(PORT)])\n"
    ),
    # THE REVERSE ARM.  A guard about port A, and a server started AND
    # adopted on port B.  The guard is about A, so B is still an ADOPTED
    # instance and this file must be PICKED.  While the guard was a
    # file-level flag, B was cleared wrongly (finding V7).
    "guard-other-port.py": (
        "import socket\n"
        "import subprocess\n"
        "PORT_A = 5615\n"
        "PORT_B = 5616\n"
        "BASE = 'http://127.0.0.1:5616'\n"
        "occupied = False\n"
        "try:\n"
        "    socket.create_connection(('127.0.0.1', PORT_A), 1)\n"
        "except OSError:\n"
        "    pass\n"
        "else:\n"
        "    occupied = True\n"
        "if occupied:\n"
        "    raise RuntimeError('port %d is already in use' % PORT_A)\n"
        "subprocess.Popen(['MapleBenchX.dll', '--port', str(PORT_B)])\n"
    ),
    "picks-stub-and-adopted.mjs": (
        "const SERVER = process.env.WZ_SERVER ?? 'http://127.0.0.1:5590/api';\n"
        "const own = 5399;\n"
        "spawn('stub.exe', ['--port=' + own], { stdio: 'ignore' });\n"
        "await fetch(SERVER);\n"
    ),
}


def _vocabulary_from_source():
    """The vocabulary the self-test's docstring lists, read back out of THIS
    file.

    A list written in a docstring is a CLAIM until something compares it
    with the code.  This is that something: the docstring and the code are
    the two sides, and a mismatch is a FAIL, not a style note.
    """
    try:
        src = open(__file__, encoding="utf-8").read()
    except OSError:
        return None
    # The markers are assembled from halves on purpose.  Spelled out in one
    # piece, this regex would match ITS OWN literal -- the first pair of
    # markers in the file would be the ones inside this function, and the
    # list it read back would be one line long.
    begin = "-- GUARD VOCABULARY BEGIN" + " --"
    end = "-- GUARD VOCABULARY END" + " --"
    m = re.search(re.escape(begin) + r"(.*?)" + re.escape(end), src, re.S)
    if not m:
        return None
    out = []
    for line in m.group(1).splitlines():
        t = line.strip()
        if t and not t.startswith("#"):
            out.append(t)
    return out


def self_test():
    """Prove the tool can report a finding, and that it does not fire on noise.

    An instrument that has only ever printed 0 has never been shown able to print
    non-zero.  Both arms run here: SEVEN scripts that DO pick an instance must be
    flagged, and TWELVE that merely mention one must not.

    THE RULE, and the pair that tests it:

      a port constant is an ADOPTED instance only when somebody ELSE is
      already listening there.  A port the file starts itself is SELF-HOSTED
      -- UNLESS the file also refuses to proceed when that port is busy, in
      which case it provably will not adopt anybody's instance.

      adopt-or-start.py     starts a server on the port it also adopts
                            (use-if-alive)          -> PICKED
      selfhosted-spawn.mjs  starts a server on the port it talks to and
                            aborts if the port is busy (abort-if-occupied)
                                                    -> SELF-HOSTED

    Both carry a POSITIVE CONTROL beside them: picks-numeric-const.mjs, the
    plain adopted shape, must still be PICKED.  Without it, a detector that
    never fires could make either case pass for the wrong reason.

    THE GUARD VOCABULARY.  The words that mean "this port is already
    taken".  The list below is the SET the pattern matches, and this
    self-test CHECKS that it is: the lines between the markers are read
    back out of this file and compared with GUARD_VOCABULARY, and a
    mismatch is a FAIL.  A comment that lies about its own pattern is the
    same defect class as a docstring that lies about its rule (finding F1:
    the pattern said `assert(occupied, False)` and the pattern could not
    match the capital-F).

        -- GUARD VOCABULARY BEGIN --
        already_running
        already_in_use
        already in use
        already_bound
        already bound
        port_taken
        is_busy
        in_use
        in use
        in-use
        inuse
        EADDRINUSE
        occupied
        busy
        taken
        -- GUARD VOCABULARY END --

    THE ABLATIONS ARE MEASURED AND PRINTED below, not written here.  This
    docstring used to quote "4 mismatches" for one of them, and the real
    number was different; a hand-typed constant in a docstring is a claim,
    not a measurement, so the numbers now come out of the run.  The ABLATION
    lines print, for each direction, how many fixtures a deliberately broken
    detector would break:

      * narrowing the kind gate (to BACKEND only) must turn
        picks-status-path.mjs red and must NOT turn picks-numeric-const.mjs
        red.  That is the NARROWING ARM, and it is COUNTED as cases, so a
        narrowing that this control cannot see is a FAIL here.
      * widening the kind gate (to include DEVTOOLS) must turn devtools.mjs
        red.
      * emptying the kind gate must turn every PICKED fixture red, and an
        is_defaulted that is always False must do the same.

    So this control catches BOTH directions, and the counts are measured
    rather than remembered.

    What it does NOT do is prove the tool is right about the REAL tree: the
    self-test.ps1 case runs this AND the real tree, so a tool that reports
    nothing at all has to disagree with a known-PICKED tree to pass.
    """
    global ADOPTED_KINDS
    tmp = tempfile.mkdtemp(prefix="probe-census-selftest-")
    try:
        probe_dir = os.path.join(tmp, "tools", "probe")
        os.makedirs(probe_dir)
        for name, body in FIXTURES.items():
            with open(os.path.join(probe_dir, name), "w", encoding="utf-8") as fh:
                fh.write(body)
        popdata = build_population(tmp, (), "worktree", "HEAD")
        files = popdata["files"]
        results = {r["file"].split("/")[-1]: r for r in
                   (classify(f, popdata["pop"]) for f in files)}

        expect_picked = ["picks-js.mjs", "picks-assign.mjs", "picks-py.py",
                          "picks-sh.sh", "picks-numeric-const.mjs",
                          "picks-status-path.mjs",
                          "picks-stub-and-adopted.mjs"]
        expect_not = {
            "clean-argv.mjs": "EXPLICIT",
            "clean-env.mjs": "EXPLICIT",
            "commented-out.mjs": "EXPLICIT",
            "block-commented.mjs": "EXPLICIT",
            "usage-string.mjs": "EXPLICIT",
            "template-hole.mjs": "EXPLICIT",
            "placeholder.mjs": "EXPLICIT",
            "devtools.mjs": "DEVTOOLS",
            # A DEFAULTED DevTools port: the kind gate is what keeps it out of
            # the adopted set, so it is the fixture the WIDENING arm flips.
            # Without it, widening the gate would break nothing and the
            # widening direction would be untested.
            "devtools-defaulted.mjs": "DEVTOOLS",
            "fn-bind.mjs": "DEVTOOLS",
            "selfhosted.js": "SELF-HOSTED",
            # a REAL socket bind, so the narrowed `.bind(` must still fire.  If
            # narrowing had thrown the family away this would report DEVTOOLS
            # or EXPLICIT and fn-bind.mjs would pass for the wrong reason.
            "socket-bind.mjs": "SELF-HOSTED",
            # spawns the server it talks to: the hard-coded port is correct.
            "selfhosted-spawn.mjs": "SELF-HOSTED",
        }

        # THE PAIR.  The rule this census now implements is "a port the file
        # starts itself is SELF-HOSTED only if the file refuses to run when
        # that port is busy".  Each side of that rule needs a fixture, and each
        # needs a POSITIVE CONTROL beside it, so that a detector which never
        # fires cannot make either case pass for the wrong reason.
        control_fixture = "picks-numeric-const.mjs"
        pair = [
            ("adopt-or-start.py", "PICKED",
             "starts a server on the port it also adopts (use-if-alive)"),
            ("selfhosted-spawn.mjs", "SELF-HOSTED",
             "assert.equal(occupied, false, ...): the abort-if-occupied shape"),
            ("guard-assert-capital.py", "SELF-HOSTED",
             "assert (occupied == False): the capital-F Python spelling"),
            ("guard-in-use.py", "SELF-HOSTED",
             "'already in use' + sys.exit(1)"),
            ("guard-eaddrinuse.mjs", "SELF-HOSTED",
             "'EADDRINUSE' + process.exit(1)"),
            ("guard-already-bound.py", "SELF-HOSTED",
             "'already bound' + raise"),
            ("guard-busy.py", "SELF-HOSTED",
             "if busy(PORT): sys.exit(1)"),
            ("guard-other-port.py", "PICKED",
             "THE REVERSE ARM: a guard about port A, and a server started AND "
             "adopted on port B -- B is still an adopted instance"),
        ]

        passed = failed = 0
        print()
        print("SELF-TEST -- positive and negative arms on a synthetic tree.")
        print("  A tool that only ever prints 0 has never been shown to print")
        print("  non-zero.  These are the arms that prove it can, and that it")
        print("  does not fire on comments or on usage strings.")
        print()
        print("  %-24s %-9s %-12s %s" % ("fixture", "expected", "got", "verdict"))
        for name in expect_picked:
            got = results.get(name, {}).get("verdict", "(missing)")
            ok = got == "PICKED"
            passed, failed = (passed + 1, failed) if ok else (passed, failed + 1)
            print("  %-24s %-9s %-12s %s" % (name, "PICKED", got, "ok" if ok else "MISMATCH"))
        for name, want in sorted(expect_not.items()):
            got = results.get(name, {}).get("verdict", "(missing)")
            ok = got == want
            passed, failed = (passed + 1, failed) if ok else (passed, failed + 1)
            print("  %-24s %-9s %-12s %s" % (name, want, got, "ok" if ok else "MISMATCH"))
        print()
        print("  THE PAIR -- the rule this census implements, each case with a")
        print("  POSITIVE CONTROL beside it.")
        for name, want, what in pair:
            got = results.get(name, {}).get("verdict", "(missing)")
            ok = got == want
            passed, failed = (passed + 1, failed) if ok else (passed, failed + 1)
            print("  %-24s %-12s %-12s %s"
                  % (name, want, got, "ok" if ok else "MISMATCH"))
            print("      %s" % what)
            cgot = results.get(control_fixture, {}).get("verdict", "(missing)")
            cok = cgot == "PICKED"
            passed, failed = (passed + 1, failed) if cok else (passed, failed + 1)
            print("  %-24s %-12s %-12s %s"
                  % (control_fixture, "PICKED", cgot,
                     "ok" if cok else "MISMATCH"))
            print("      POSITIVE CONTROL for the case above: the plain adopted"
                  " shape must still be PICKED")
        print()
        print("  THE VOCABULARY CHECK -- the docstring's list against the code's")
        listed = _vocabulary_from_source()
        ok_vocab = (listed is not None
                    and set(listed) == set(GUARD_VOCABULARY)
                    and len(listed) == len(set(listed)))
        passed, failed = (passed + 1, failed) if ok_vocab else (passed, failed + 1)
        print("  %-24s %-12s %-12s %s"
              % ("guard vocabulary", "listed set", "code set",
                 "ok" if ok_vocab else "MISMATCH"))
        print("      %d word(s) in the code, %d in the docstring"
              % (len(GUARD_VOCABULARY), len(listed) if listed else 0))
        if not ok_vocab:
            print("      listed: %s" % (",".join(listed) if listed else "(none)"))
            print("      code  : %s" % ",".join(GUARD_VOCABULARY))
        print()
        print("  THE NARROWING ARM -- narrow the kind gate to BACKEND only, the")
        print("  way a future edit might narrow it.  The fixture that depends")
        print("  on the wide gate must go RED, and its positive control must not.")

        def classify_all():
            return {r["file"].split("/")[-1]: r for r in
                    (classify(f, popdata["pop"]) for f in files)}

        saved_kinds = ADOPTED_KINDS
        try:
            ADOPTED_KINDS = ("BACKEND",)
            narrowed = classify_all()
        finally:
            ADOPTED_KINDS = saved_kinds
        red_case = "picks-status-path.mjs"
        red_got = narrowed.get(red_case, {}).get("verdict", "(missing)")
        green_got = narrowed.get(control_fixture, {}).get("verdict", "(missing)")
        ok_red = red_got != "PICKED"
        ok_green = green_got == "PICKED"
        passed, failed = (passed + 1, failed) if ok_red else (passed, failed + 1)
        passed, failed = (passed + 1, failed) if ok_green else (passed, failed + 1)
        print("  %-24s %-12s %-12s %s"
              % (red_case, "not PICKED", red_got, "ok" if ok_red else "MISMATCH"))
        print("      the narrowed gate must LOSE this one: it is a /status path,")
        print("      and the wide gate is the only thing that finds it")
        print("  %-24s %-12s %-12s %s"
              % (control_fixture, "PICKED", green_got,
                 "ok" if ok_green else "MISMATCH"))
        print("      POSITIVE CONTROL: the narrowed gate must NOT lose the plain")
        print("      adopted shape")
        print()
        print("  THE ABLATIONS -- MEASURED, not remembered.  Each line says how")
        print("  many fixtures a deliberately broken detector would break.")

        all_expect = ([(x, "PICKED") for x in expect_picked]
                      + [(x, w) for x, w in expect_not.items()])

        def flipped(verdicts):
            out = []
            for name, want in all_expect:
                got = verdicts.get(name, {}).get("verdict", "(missing)")
                if got != want:
                    out.append("%s(%s->%s)" % (name, want, got))
            return out

        try:
            ADOPTED_KINDS = ()
            f_empty = flipped(classify_all())
        finally:
            ADOPTED_KINDS = saved_kinds
        print("  ABLATION kind gate empty (PICKED can never fire): %d fixture(s)"
              % len(f_empty))
        for f in f_empty:
            print("      %s" % f)

        saved_df = globals()["is_defaulted"]
        try:
            globals()["is_defaulted"] = lambda *a, **k: False
            f_df = flipped(classify_all())
        finally:
            globals()["is_defaulted"] = saved_df
        print("  ABLATION is_defaulted always False: %d fixture(s)" % len(f_df))
        for f in f_df:
            print("      %s" % f)

        try:
            ADOPTED_KINDS = ("BACKEND", "UNKNOWN", "DEVTOOLS")
            f_wide = flipped(classify_all())
        finally:
            ADOPTED_KINDS = saved_kinds
        print("  ABLATION kind gate widened to include DEVTOOLS: %d fixture(s)"
              % len(f_wide))
        for f in f_wide:
            print("      %s" % f)
        f_narrow = flipped(narrowed)
        print("  ABLATION kind gate narrowed to BACKEND only: %d fixture(s)"
              % len(f_narrow))
        for f in f_narrow:
            print("      %s" % f)
        print()
        # One field per line.  Two fields on one line cannot both be read by
        # a reader that anchors on the start of a line, and the second one
        # then reads as ABSENT while the tool answered correctly -- the shape
        # of AGENTS.md rule 9 and rule 3's "a case that fails for the wrong
        # reason is not a test".  Measured: with both fields on one line the
        # harness case CENSUS-self-test-is-green went red on a tool that had
        # reported 17/0.
        print("  SELFTEST_PASS=%d" % passed)
        print("  SELFTEST_FAIL=%d" % failed)
        print("  RESULT=%s" % ("PASS" if failed == 0 else "FAIL"))
        return 0 if failed == 0 else 1
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


# ----------------------------------------------------------------------------

def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--root", default=".")
    ap.add_argument("--self-test", action="store_true",
                    help="run the positive/negative control arms and exit")
    ap.add_argument("--quiet", action="store_true",
                    help="only print files that are PICKED or UNDETERMINED")
    ap.add_argument("--population", choices=("worktree", "ref"),
                    default="worktree",
                    help="which population rule to use: the working tree with "
                         "every gitignored path excluded, or the tracked tree "
                         "at a named ref")
    ap.add_argument("--ref", default="HEAD",
                    help="the ref to read when --population=ref")
    args = ap.parse_args()

    if args.self_test:
        sys.exit(self_test())

    root = os.path.abspath(args.root)
    popdata = build_population(root, DEFAULT_EXTRA, args.population, args.ref)
    files = popdata["files"]
    skipped_ext = popdata["skipped_ext"]
    skipped_big = popdata["skipped_big"]
    if not files:
        walked = walk_candidates(root)
        print(">>> REFUSING: no scripts found under tools/probe/. A census of")
        print("    nothing is not a clean census (rule 22).")
        print("    population rule in force: %s" % popdata["rule"])
        print("    the directory holds %d file(s) on disk, so the rule and the"
              % len(walked))
        print("    tree disagree -- refusing rather than reporting an empty")
        print("    population as a clean one.")
        sys.exit(2)

    results = [classify(f, popdata["pop"]) for f in files]

    counts = {}
    for r in results:
        counts[r["verdict"]] = counts.get(r["verdict"], 0) + 1

    print()
    print("probe-instance-census -- root %s" % root)
    print()
    if popdata["pop"].mode == "ref":
        rule_field = "tracked-tree-at-ref"
    elif popdata["pop"].mode == "walk":
        rule_field = "filesystem-walk-no-git"
    else:
        rule_field = "worktree-minus-gitignored"
    print("POPULATION_RULE=%s" % rule_field)
    print("  rule: %s" % popdata["rule"])
    print("Population rule: every script-shaped file under tools/probe/, plus"
          " the declared extra list (%s)."
          % (", ".join(DEFAULT_EXTRA) if DEFAULT_EXTRA else "(empty)"))
    print("Population: %d file(s); %d declared extra(s) present, %d absent."
          % (len(files),
             len([r for r in DEFAULT_EXTRA if r in files]),
             len([r for r in DEFAULT_EXTRA if r not in files])))
    for rel in [r for r in DEFAULT_EXTRA if r not in files]:
        print("  note: the declared extra %s is not in this population -- this"
              " root does not contain it, so the population is smaller than"
              " the rule's full list by that file." % rel)
    print("Rule 18: scanned=%d  truncated=%d  skipped_non_script=%d"
          % (len(results), len(skipped_big), len(skipped_ext)))
    print()
    print("%-34s %-13s %-9s %s" % ("file", "verdict", "obtained", "evidence"))
    print("-" * 100)
    for r in results:
        if args.quiet and r["verdict"] not in ("PICKED", "UNDETERMINED"):
            continue
        if r["sites"]:
            s = r["sites"][0]
            ev = "L%d %s" % (s["line"], ascii_safe(s["text"]))
        else:
            ev = "(no instance site)"
        print("%-34s %-13s %-9s %s  basis=%s inferred=code-shape"
              % (r["file"], r["verdict"], r["source"], ev, r["basis"]))

    # F4.  The return value used to be discarded, so the audit could print
    # "treat any clean result here as unproven" and then fall straight through
    # to RESULT=CLEAN (exit 0).  That is AGENTS.md's "Before you trust a
    # measurement" failing verbatim: a detail line that contradicts the verdict,
    # with the verdict untouched.  A verdict the audit disowns must not be
    # printed as a verdict.
    notes = []
    if popdata["ignored"]:
        notes.append("gitignored paths excluded by the population rule: %d "
                     "(of which %d under tools/probe/) -- they were never in "
                     "the population, so they are not in the table above."
                     % (popdata["ignored"], popdata["ignored_under_probe"]))
    if popdata["absent"]:
        notes.append("paths in git's index but absent from the working tree: "
                     "%d (excluded; a missing file is not a file)"
                     % popdata["absent"])
    audit_ok, audit_reason = exclusion_audit(files, skipped_ext, skipped_big,
                                            notes)

    print()
    print("SUMMARY")
    print("  algorithm: every script-shaped file under tools/probe/ (plus %s)"
          % ", ".join(DEFAULT_EXTRA))
    print("  is read as utf-8 with errors='replace', comments are stripped by a")
    print("  quote-aware state machine that preserves byte offsets, and every")
    print("  loopback site in the RESULT is classified by the path that follows")
    print("  it (/json* = DevTools, /api* or empty = backend).")
    print("  range: %d files, %d lines total"
          % (len(results), sum(r["line_count"] for r in results)))
    for verdict in sorted(counts):
        print("  %-13s %d" % (verdict, counts[verdict]))
    hosted = [r for r in results if r["started_ports"]]
    if hosted:
        print("  files that START the instance they talk to: %d" % len(hosted))
        for r in hosted:
            print("    %-42s self_started_ports=%s basis=%s"
                  % (r["file"], ",".join(r["started_ports"]), r["basis"]))
    print("  VERDICT_BASIS=inferred-from-code-shape")
    print("    Every basis= value is read off the CODE's shape.  The port-busy")
    print("    condition is never observed at run time: this is a static scan.")
    print("  basis vocabulary:")
    print("    spawn-only        starts the server on that port AND ABORTS if")
    print("                      the port is occupied (abort-if-occupied)")
    print("                      -> SELF-HOSTED")
    print("    adopt-or-start    starts the server on that port only as a")
    print("                      fallback and USES what is already listening")
    print("                      (use-if-alive) -> PICKED")
    print("    in-process-bind   binds the port in its own process")
    print("                      -> SELF-HOSTED")
    print("    defaulted-port    names the port as a default and starts nothing")
    print("                      -> PICKED")
    print("    spawns-its-own    starts a server, but names no default instance")
    print("    not-a-default     names no default instance")
    for b in sorted(set(r["basis"] for r in results)):
        print("  basis %-18s %d" % (b, len([r for r in results
                                             if r["basis"] == b])))

    picked = [r for r in results if r["verdict"] == "PICKED"]
    undet = [r for r in results if r["verdict"] == "UNDETERMINED"]
    print()
    if picked:
        print("%d script(s) supply a default instance the caller never named."
              % len(picked))
        for r in picked:
            print()
            print("  %s" % r["file"])
            for s in r["sites"]:
                if s["defaulted"] and s["kind"] != "DEVTOOLS":
                    print("    L%-5d port=%-10s %s"
                          % (s["line"], s["port"] or "(none)", ascii_safe(s["text"], 78)))
        print()
        print("RESULT=PICKED")
        print("EXIT_CODE=1")
        sys.exit(1)
    if undet:
        print("%d file(s) could not be classified.  Reported, not red -- but an"
              % len(undet))
        print("unclassified file is not a clean file.")
    if not audit_ok:
        print()
        print(">>> RED: %s" % audit_reason)
        print("    The exclusion audit disowns this run, so 'clean' is not a")
        print("    verdict this tool is willing to print.  Widen the population")
        print("    (--extra) or point --root at a larger tree.")
        print("RESULT=UNPROVEN")
        print("EXIT_CODE=2")
        sys.exit(2)
    print()
    print("No script supplies a default instance.")
    print("RESULT=CLEAN")
    print("EXIT_CODE=0")
    sys.exit(0)


if __name__ == "__main__":
    main()
