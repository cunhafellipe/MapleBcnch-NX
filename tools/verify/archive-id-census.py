"""Census: how many tools still HARD-CODE an archive session id?

`f4` is Map.nx on the instance where Map.nx happened to be opened fourth and
something else everywhere else. A tool that hard-codes one measures a
different archive than the one it means to, and on an instance where that id
names something else it measures the WRONG thing while looking like it
measured a difference (rule 16, tools/verify/AGENTS.md).

The census is READ-ONLY. For the tree it was pointed at it prints:

  * the input it measured -- the resolved root, the file count, and a digest
    over the contents -- so two arms of a comparison can be told apart at all
    (rule 20: two arms that report an identical count are a failed
    experiment, not a result);
  * `scanned` AND `truncated` (rule 18: a truncated census is not a census);
  * every hit, split into CODE / PROSE / COMMENT, and the DENOMINATOR it was
    taken from, because "0 hits" over an unstated denominator is the shape of
    incident 12 (a missing field and a zero look identical);
  * TWO denominators, because rule 16's sentence ("18 more suites hardcode
    `f4` and one hardcodes `f3`") is a statement about the SUITES and can
    only be reconciled against the suite denominator:
      A. the suites the SPEC declares (the same list the doctor uses);
      B. every other tool (tools/probe, tools/pm.py, tools/verify/gate.ps1,
         tools/verify/*.py, MapleBench/tools).

The classification rule, stated so it can be checked:

  CODE    the id ADDRESSES an archive in an executable position: it is
          followed by '/' (a hard-coded archive PATH) or it is the value of
          an archive-naming query parameter (fileId= / file= / archive=).
  PROSE   the id is mentioned but does not address an archive: a local
          variable named f2, a stub fixture's id, a docstring, a comment.
  COMMENT the id is inside a comment.

`--other-root` makes the tool assert that two arms measured two different
trees, and refuses to compare them if they are the same tree (rule 20).

Usage:
    python tools/verify/archive-id-census.py --root <repo> [--other-root <other>]
                                              [--label before] [--json]
"""
import argparse
import ast
import hashlib
import importlib.util
import io
import json
import os
import re
import sys
import tokenize

# A session id: `f4`, `f12`, ... -- and the match must END at a boundary, or
# `f4x` and `xf4` would be counted (rule 12: a match must end at a boundary).
ID_RE = re.compile(r"(?<![A-Za-z0-9_])f([0-9]{1,3})(?![A-Za-z0-9_])")
# A query parameter that NAMES an archive: `?fileId=f1` addresses the archive
# `f1` exactly the way a hard-coded path does.
ARCHIVE_PARAM_RE = re.compile(
    r"(?:fileid|file|archive|archivelf|wzid)\s*=\s*$", re.IGNORECASE)

SUFFIXES = (".py", ".ps1", ".mjs", ".js")
# The other tools, as (directory-or-file, label) -- the second denominator.
OTHER_ROOTS = [
    (os.path.join("tools", "probe"), "tools/probe"),
    (os.path.join("tools", "verify"), "tools/verify (not suites/)"),
    (os.path.join("MapleBench", "tools"), "MapleBench/tools"),
    (os.path.join("tools", "pm.py"), "tools/pm.py"),
]
SKIP_DIRS = {".git", "node_modules", "bin", "obj", "dist", "build", "__pycache__"}


class Hit(object):
    def __init__(self, path, line, col, ident, kind, text):
        self.path = path
        self.line = line
        self.col = col
        self.ident = ident
        self.kind = kind          # CODE | PROSE | COMMENT
        self.text = text if len(text) <= 160 else text[:157] + "..."

    def as_dict(self):
        return {"file": self.path, "line": self.line, "id": self.ident,
                "kind": self.kind, "text": self.text}


def read_text(path):
    with open(path, "rb") as fh:
        raw = fh.read()
    return raw.decode("utf-8", errors="replace")


def strip_ps_comment(line):
    """The part of a PowerShell line that is NOT a comment (line classifier).

    A `#` inside a string is not a comment, so this walks the line and tracks
    quotes.
    """
    out = []
    in_s = None
    for i, ch in enumerate(line):
        if in_s:
            out.append(ch)
            if ch == in_s:
                in_s = None
        elif ch in "\"'":
            in_s = ch
            out.append(ch)
        elif ch == "#":
            break
        else:
            out.append(ch)
    return "".join(out)


def strip_js_comment(line):
    """The part of a JS/MJS line that is NOT a comment (line classifier)."""
    out = []
    in_s = None
    for i, ch in enumerate(line):
        if in_s:
            out.append(ch)
            if ch == in_s and (i == 0 or line[i - 1] != "\\"):
                in_s = None
        elif ch in "\"'`":
            in_s = ch
            out.append(ch)
        elif ch == "/" and i + 1 < len(line) and line[i + 1] == "/":
            break
        else:
            out.append(ch)
    return "".join(out)


def is_path_use(after):
    """Does the id ADDRESS an archive path? `f4/Map/...` or `^f1\\/`."""
    return after.startswith("/") or after.startswith("\\/")


def is_param_use(before):
    """Does the id ADDRESS an archive through a query parameter?"""
    return bool(ARCHIVE_PARAM_RE.search(before))


def docstring_lines(path):
    """The line ranges of every docstring, so a docstring is PROSE and not
    CODE (a docstring is a description, never an executed address)."""
    try:
        tree = ast.parse(read_text(path))
    except Exception:
        return set()
    lines = set()
    for node in ast.walk(tree):
        if not isinstance(node, (ast.Module, ast.ClassDef, ast.FunctionDef,
                                 ast.AsyncFunctionDef)):
            continue
        body = getattr(node, "body", None)
        if not body:
            continue
        first = body[0]
        if (isinstance(first, ast.Expr) and isinstance(first.value, ast.Constant)
                and isinstance(first.value.value, str)):
            end = getattr(first, "end_lineno", first.lineno) or first.lineno
            for n in range(first.lineno, end + 1):
                lines.add(n)
    return lines


def scan_python(path, rel, hits):
    """Python: tokenize gives the comments exactly; ast gives the docstrings."""
    src = read_text(path)
    docs = docstring_lines(path)
    try:
        toks = list(tokenize.generate_tokens(io.StringIO(src).readline))
    except Exception as exc:
        hits.append(Hit(rel, 0, 0, "f?", "PROSE",
                        "  (could not tokenize: %s)" % exc))
        return
    fstring_middle = getattr(tokenize, "FSTRING_MIDDLE", None)
    for tok in toks:
        if tok.type == tokenize.COMMENT:
            for m in ID_RE.finditer(tok.string):
                hits.append(Hit(rel, tok.start[0], m.start(), "f" + m.group(1),
                                "COMMENT", tok.string.strip()))
        elif tok.type == tokenize.STRING or (
                fstring_middle is not None and tok.type == fstring_middle):
            s = tok.string
            in_doc = tok.start[0] in docs
            for m in ID_RE.finditer(s):
                ident = "f" + m.group(1)
                before = s[:m.start()]
                after = s[m.end():]
                if in_doc:
                    kind = "PROSE"
                else:
                    kind = "CODE" if (is_path_use(after) or is_param_use(before)) \
                        else "PROSE"
                hits.append(Hit(rel, tok.start[0], m.start(), ident,
                                kind, s.strip()))


def scan_lines(path, rel, hits, stripper):
    """A LINE classifier (PowerShell / JavaScript). It says so in its output:
    the comment part of a line is everything from the comment marker on, and
    the code part is a PREFIX of the line, so the two share positions."""
    src = read_text(path)
    for n, line in enumerate(src.splitlines(), 1):
        code_part = stripper(line)
        for m in ID_RE.finditer(line):
            ident = "f" + m.group(1)
            before = line[:m.start()]
            after = line[m.end():]
            if m.start() >= len(code_part):
                kind = "COMMENT"
            elif is_path_use(after) or is_param_use(before):
                kind = "CODE"
            else:
                kind = "PROSE"
            hits.append(Hit(rel, n, m.start(), ident, kind, line.strip()))


def scan_file(path, rel, hits):
    ext = os.path.splitext(path)[1].lower()
    if ext == ".py":
        scan_python(path, rel, hits)
    elif ext == ".ps1":
        scan_lines(path, rel, hits, strip_ps_comment)
    elif ext in (".mjs", ".js"):
        scan_lines(path, rel, hits, strip_js_comment)
    else:
        return False
    return True


def collect_under(root, base):
    """Every scannable file under `base` (a directory or a single file)."""
    out = []
    full_base = os.path.join(root, base)
    if os.path.isfile(full_base):
        if full_base.lower().endswith(SUFFIXES):
            out.append((full_base, base.replace("\\", "/")))
        return out
    if not os.path.isdir(full_base):
        return out
    for dirpath, dirnames, filenames in os.walk(full_base):
        dirnames[:] = sorted(d for d in dirnames if d not in SKIP_DIRS)
        for name in sorted(filenames):
            if name.lower().endswith(SUFFIXES):
                full = os.path.join(dirpath, name)
                rel = os.path.relpath(full, root).replace("\\", "/")
                out.append((full, rel))
    return sorted(out, key=lambda t: t[1])


def declared_suites(root):
    """The suites the SPEC declares -- denominator A, the same list the doctor
    uses. Read with pm.py's own parser, so there is no second convention.

    pm.py is loaded FROM THE TREE BEING CENSUSED, under a per-root module name.
    A plain `import pm` would be cached in sys.modules: the second arm would
    then read the FIRST arm's SPEC and resolve the suite paths against the
    FIRST arm's root, and both arms would report the same number while
    measuring different things (rule 20 -- and the digests are what showed
    it). Every resolved suite path is asserted to be inside `root` for the
    same reason.
    """
    pm_path = os.path.join(root, "tools", "pm.py")
    if not os.path.isfile(pm_path):
        print(">>> RED: %s has no tools/pm.py, so the suite denominator cannot "
              "be read" % root)
        sys.exit(1)
    mod_name = "pm_census_%s" % hashlib.sha256(
        os.path.realpath(root).encode("utf-8")).hexdigest()[:12]
    spec = importlib.util.spec_from_file_location(mod_name, pm_path)
    pm = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(pm)
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
            sys.exit(1)
        out.append((row.get("id") or f, full, f))
    return out


def digest_of(files):
    h = hashlib.sha256()
    for full, rel in files:
        h.update(rel.encode("utf-8"))
        h.update(b"\0")
        with open(full, "rb") as fh:
            h.update(hashlib.sha256(fh.read()).digest())
    return h.hexdigest()


def run_census(root):
    root = os.path.abspath(root)
    if not os.path.isdir(root):
        print(">>> RED: %s is not a directory, so there is nothing to census" % root)
        sys.exit(1)
    hits = []
    suite_files = []
    suite_rows = declared_suites(root)
    for sid, full, f in suite_rows:
        if not os.path.isfile(full):
            hits.append(Hit(f, 0, 0, "f?", "PROSE",
                            "  (declared suite file is missing: %s)" % full))
            continue
        suite_files.append((full, f))
        scan_file(full, f, hits)
    other_files = []
    seen = set(rel for _, rel in suite_files)
    for base, _label in OTHER_ROOTS:
        for full, rel in collect_under(root, base):
            if rel in seen:
                continue
            seen.add(rel)
            other_files.append((full, rel))
            scan_file(full, rel, hits)
    return {
        "root": root,
        "suite_rows": suite_rows,
        "suite_files": suite_files,
        "other_files": other_files,
        "hits": hits,
        "digest": digest_of(suite_files + other_files),
    }


def per_file(hs):
    out = {}
    for h in hs:
        out.setdefault(h.path, []).append(h)
    return out


def summarize(census):
    hits = census["hits"]
    code = [h for h in hits if h.kind == "CODE"]
    prose = [h for h in hits if h.kind == "PROSE"]
    comment = [h for h in hits if h.kind == "COMMENT"]
    code_by = per_file(code)
    suite_paths = set(rel for _, rel in census["suite_files"])
    suite_code = sorted(p for p in code_by if p in suite_paths)
    other_code = sorted(p for p in code_by if p not in suite_paths)
    ids = {}
    for h in code:
        ids[h.ident] = ids.get(h.ident, 0) + 1
    return {"code": code, "prose": prose, "comment": comment,
            "code_by": code_by, "suite_code": suite_code,
            "other_code": other_code, "ids": ids}


def print_census(census, s, label):
    print("=== archive-id census ===")
    print("  label      : %s" % label)
    print("  root       : %s" % census["root"])
    print("  digest     : %s" % census["digest"])
    print("  scanned    : %d suite file(s) + %d other file(s) = %d file(s)"
          % (len(census["suite_files"]), len(census["other_files"]),
             len(census["suite_files"]) + len(census["other_files"])))
    print("  truncated  : False")
    print("  CODE   hits: %d   in %d suite file(s) + %d other file(s)"
          % (len(s["code"]), len(s["suite_code"]), len(s["other_code"])))
    print("  PROSE  hits: %d" % len(s["prose"]))
    print("  COMMENT hits: %d" % len(s["comment"]))
    if s["ids"]:
        print("  ids in CODE: %s"
              % ", ".join("%s=%d" % (k, v) for k, v in sorted(s["ids"].items())))
    print("  --- the SUITE files with a CODE hit ---")
    for p in s["suite_code"]:
        print("    %-46s %d" % (p, len(s["code_by"][p])))
        for h in s["code_by"][p]:
            print("        %s:%d  %s  | %s" % (h.path, h.line, h.ident, h.text))
    print("  --- the OTHER files with a CODE hit ---")
    for p in s["other_code"]:
        print("    %-46s %d" % (p, len(s["code_by"][p])))
        for h in s["code_by"][p]:
            print("        %s:%d  %s  | %s" % (h.path, h.line, h.ident, h.text))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True,
                    help="the tree to census (a PARAMETER: rule 20)")
    ap.add_argument("--other-root", default=None,
                    help="a second tree; the tool refuses to compare two arms "
                         "that resolved to the same path")
    ap.add_argument("--label", default="arm")
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args()

    census = run_census(args.root)
    s = summarize(census)

    payload = {
        "label": args.label,
        "root": census["root"],
        "digest": census["digest"],
        "scanned": len(census["suite_files"]) + len(census["other_files"]),
        "truncated": False,
        "suites_scanned": len(census["suite_files"]),
        "suites_declared": len(census["suite_rows"]),
        "others_scanned": len(census["other_files"]),
        "code_hits": len(s["code"]),
        "prose_hits": len(s["prose"]),
        "comment_hits": len(s["comment"]),
        "suites_with_code": len(s["suite_code"]),
        "others_with_code": len(s["other_code"]),
        "code_by_file": {p: len(s["code_by"][p]) for p in sorted(s["code_by"])},
        "hits": [h.as_dict() for h in census["hits"]],
    }

    if args.other_root:
        other = run_census(args.other_root)
        so = summarize(other)
        a = os.path.realpath(census["root"])
        b = os.path.realpath(other["root"])
        if a == b:
            print(">>> RED: both arms resolved to the SAME path (%s), so the "
                  "second arm did not measure a different tree" % a)
            sys.exit(1)
        if census["digest"] == other["digest"]:
            print(">>> RED: the two arms have the SAME content digest, so the "
                  "second arm did not measure a different tree")
            sys.exit(1)
        print("ARMS_DIFFER=True")
        print("  arm A  root=%s" % a)
        print("  arm A  digest=%s  files=%d  CODE=%d  suites_with_CODE=%d"
              % (census["digest"], payload["scanned"], payload["code_hits"],
                 payload["suites_with_code"]))
        print("  arm B  root=%s" % b)
        print("  arm B  digest=%s  files=%d  CODE=%d  suites_with_CODE=%d"
              % (other["digest"],
                 len(other["suite_files"]) + len(other["other_files"]),
                 len(so["code"]), len(so["suite_code"])))
        payload["other"] = {
            "root": b, "digest": other["digest"],
            "scanned": len(other["suite_files"]) + len(other["other_files"]),
            "truncated": False,
            "suites_scanned": len(other["suite_files"]),
            "code_hits": len(so["code"]),
            "suites_with_code": len(so["suite_code"]),
        }

    if args.json:
        print(json.dumps(payload, indent=2, ensure_ascii=True))
        return 0

    print_census(census, s, args.label)
    return 0


if __name__ == "__main__":
    sys.exit(main())
