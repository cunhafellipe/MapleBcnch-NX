#!/usr/bin/env python3
"""Prong 1: node names the CODE actually uses as string literals.

Extracts every string literal from MapleBench .cs and wwwroot/js .js, keeps the
archive-node-looking ones, and records file:line. This is the set of names the
tool genuinely reads/writes -- so a COMMENT naming something else for the same
site is a detectable disagreement.
"""
import json, os, re, collections

ROOT = r"C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX-audit"
CS_ROOT = os.path.join(ROOT, "MapleBench")
JS_ROOT = os.path.join(CS_ROOT, "wwwroot", "js")
OUT = os.path.join(ROOT, "scratch", "nodenames")

# C# verbatim/normal strings
CS_STR = re.compile(r'"((?:[^"\\\n]|\\.)*)"')
CS_VERB = re.compile(r'@"((?:[^"])*)"')
JS_STR = re.compile(r"""(?<![\\\w])['"`]((?:[^'"`\\\n]|\\.)*)['"`]""")

# things that are clearly NOT archive node names
BANNED = {
    "http", "https", "www", "com", "net", "org", "api", "json", "html", "css", "png", "jpg",
    "gif", "apng", "webp", "xml", "svg", "txt", "csv", "md", "none", "null", "true", "false",
    "text", "number", "string", "object", "array", "boolean", "undefined", "application/json",
    "GET", "POST", "PUT", "DELETE", "PATCH", "utf8", "utf-8", "ASCII", "base64",
}


def looks_like_node(s):
    """A bare or slash-separated identifier that could be an archive node name."""
    if not s or len(s) > 80:
        return False
    if s.lower() in BANNED:
        return False
    if " " in s or "\t" in s:
        return False
    if re.search(r"[\\:]", s):      # windows path / url / css selector
        return False
    if s.startswith(".") or s.startswith("/"):
        return False
    # must be identifier-ish segments joined by /
    if not re.fullmatch(r"[A-Za-z_]\w*(?:/[A-Za-z_0-9]+)*", s):
        return False
    # reject obvious api routes / file names
    if re.fullmatch(r"(?:api|wwwroot|scratch|MapleBench|bin|obj)/.*", s):
        return False
    if s.endswith((".cs", ".js", ".nx", ".img", ".json", ".html", ".md", ".sh")):
        return False
    if re.fullmatch(r"\d+", s):
        return False
    return True


code_names = collections.defaultdict(list)
files = []
for dirpath, dirnames, filenames in os.walk(CS_ROOT):
    dirnames[:] = [d for d in dirnames if d not in ("obj", "bin", ".git", "node_modules")]
    for fn in filenames:
        if fn.endswith(".cs"):
            files.append((os.path.join(dirpath, fn), CS_STR, CS_VERB))

for path, rx, verb in files:
    rel = os.path.relpath(path, ROOT).replace("\\", "/")
    with open(path, "r", encoding="utf-8-sig", errors="replace") as fh:
        for i, raw in enumerate(fh, 1):
            line = raw
            for m in rx.finditer(line):
                v = m.group(1)
                # unwrap simple C# escapes and interpolated holes
                v = re.sub(r"\{[^}]*\}", "", v)
                v = v.replace("\\\\", "\\").replace("\\\"", '"')
                if looks_like_node(v):
                    code_names[v].append((rel, i))

with open(os.path.join(OUT, "code_names.json"), "w", encoding="utf-8") as fh:
    json.dump(code_names, fh, ensure_ascii=False, indent=1)

# bare single-segment names, most useful for the audit
bare = {k: v for k, v in code_names.items() if "/" not in k}
with open(os.path.join(OUT, "code_names_bare.txt"), "w", encoding="utf-8") as fh:
    for name, cites in sorted(bare.items(), key=lambda kv: -len(kv[1])):
        fh.write(f"{name}\t{len(cites)}\t{sorted({c[0] for c in cites})[0]}\n")

print("files:", len(files))
print("distinct code-literal node names:", len(code_names), " bare:", len(bare))
print("\n-- top 120 bare names used in code --")
for name, cites in sorted(bare.items(), key=lambda kv: -len(kv[1]))[:120]:
    print(f"{len(cites):5d}  {name}")