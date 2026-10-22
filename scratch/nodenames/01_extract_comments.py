#!/usr/bin/env python3
"""Extract every // and /// comment from MapleBench .cs and wwwroot/js .js files.
Writes one JSON record per comment line: {file, line, text}.
Also writes a plain text dump for eyeballing.
"""
import json, os, re, sys

ROOT = r"C:\MUWORK\GAME\MAPLESOTRY\wz工具\MapleBcnch-NX-audit"
CS_ROOT = os.path.join(ROOT, "MapleBench")
JS_ROOT = os.path.join(CS_ROOT, "wwwroot", "js")
OUT = os.path.join(ROOT, "scratch", "nodenames")


def strip_cs_strings_and_region(line):
    """Crude: we only care about comments; a // inside a string literal is rare here."""
    return line


def extract_cs(path):
    out = []
    in_block = False
    with open(path, "r", encoding="utf-8-sig", errors="replace") as fh:
        for i, raw in enumerate(fh, 1):
            line = raw.rstrip("\n").rstrip("\r")
            if in_block:
                out.append((i, line.strip(), "block"))
                if "*/" in line:
                    in_block = False
                continue
            idx = line.find("//")
            if idx >= 0:
                txt = line[idx + 2:].strip()
                kind = "doc" if line[:idx].strip().endswith("/") else "line"
                out.append((i, txt, kind))
            if line.rstrip().endswith("/*") and "*/" not in line:
                in_block = True
    return out


def extract_js(path):
    out = []
    in_block = False
    with open(path, "r", encoding="utf-8-sig", errors="replace") as fh:
        for i, raw in enumerate(fh, 1):
            line = raw.rstrip("\n").rstrip("\r")
            if in_block:
                out.append((i, line.strip(), "block"))
                if "*/" in line:
                    in_block = False
                continue
            idx = line.find("//")
            if idx >= 0:
                out.append((i, line[idx + 2:].strip(), "line"))
            if line.rstrip().endswith("/*") and "*/" not in line:
                in_block = True
    return out


def main():
    records = []
    files = []
    for dirpath, dirnames, filenames in os.walk(CS_ROOT):
        dirnames[:] = [d for d in dirnames if d not in ("obj", "bin", ".git", "node_modules")]
        for fn in filenames:
            if fn.endswith(".cs"):
                files.append((os.path.join(dirpath, fn), extract_cs))
    if os.path.isdir(JS_ROOT):
        for fn in sorted(os.listdir(JS_ROOT)):
            if fn.endswith(".js"):
                files.append((os.path.join(JS_ROOT, fn), extract_js))

    for path, fn in files:
        rel = os.path.relpath(path, ROOT)
        for lineno, txt, kind in fn(path):
            if not txt:
                continue
            records.append({"file": rel.replace("\\", "/"), "line": lineno, "kind": kind, "text": txt})

    with open(os.path.join(OUT, "comments.json"), "w", encoding="utf-8") as fh:
        json.dump(records, fh, ensure_ascii=False, indent=1)
    with open(os.path.join(OUT, "comments.txt"), "w", encoding="utf-8") as fh:
        for r in records:
            fh.write(f"{r['file']}:{r['line']}\t{r['text']}\n")
    print(f"files scanned: {len(files)}")
    print(f"comment lines: {len(records)}")
    kinds = {}
    for r in records:
        kinds[r["kind"]] = kinds.get(r["kind"], 0) + 1
    print("by kind:", kinds)
    exts = {}
    for r in records:
        e = os.path.splitext(r["file"])[1]
        exts[e] = exts.get(e, 0) + 1
    print("by ext:", exts)


if __name__ == "__main__":
    main()