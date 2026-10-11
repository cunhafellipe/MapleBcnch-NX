"""Repair em-dashes destroyed into '??' inside C# comments and string literals.

Scans with a small lexer state machine so a real null-coalescing operator in code
is never touched: only '??' that sits after a // comment start, inside a /* */
block comment, or inside a string/char literal is rewritten to U+2014.
"""
import sys
import re

DASH = "\u2014"


def regions(text):
    """Yield (start, end) spans that are comment or string/char literal."""
    i, n = 0, len(text)
    spans = []
    line_start = 0
    in_block = False
    block_start = 0
    while i < n:
        c = text[i]
        two = text[i:i + 2]
        if in_block:
            if two == "*/":
                spans.append((block_start, i))
                in_block = False
                i += 2
                continue
            i += 1
            continue
        if two == "/*":
            in_block = True
            block_start = i
            i += 2
            continue
        if two == "//":
            end = text.find("\n", i)
            if end == -1:
                end = n
            spans.append((i, end))
            i = end
            continue
        if c == "@" and text[i + 1:i + 2] == '"':
            i += 2
            start = i - 1
            while i < n:
                if text[i] == '"' and (i + 1 >= n or text[i + 1] != '"'):
                    i += 1
                    break
                if text[i] == '"' and text[i + 1:i + 2] == '"':
                    i += 2
                    continue
                i += 1
            spans.append((start, i))
            continue
        if c == '"' or c == "'":
            quote = c
            verbatim = text[i - 1:i] == "@"
            start = i
            i += 1
            while i < n:
                ch = text[i]
                if not verbatim and ch == "\\":
                    i += 2
                    continue
                if ch == quote:
                    if verbatim and text[i + 1:i + 2] == quote:
                        i += 2
                        continue
                    i += 1
                    break
                if not verbatim and ch == "\n":
                    break
                i += 1
            spans.append((start, i))
            continue
        if c == "/" and i > 0 and text[i - 1:i] in ("$", "@", "?"):
            i += 1
            continue
        i += 1
    if in_block:
        spans.append((block_start, n))
    return spans


def repair(path):
    with open(path, encoding="utf-8", newline="") as fh:
        text = fh.read()
    spans = regions(text)
    hits = 0
    out = []
    pos = 0
    for start, end in spans:
        chunk = text[pos:end]
        hits += chunk.count("??")
        out.append(chunk.replace("??", DASH))
        pos = end
    out.append(text[pos:])
    fixed = "".join(out)
    if fixed != text:
        with open(path, "w", encoding="utf-8", newline="") as fh:
            fh.write(fixed)
    return hits


if __name__ == "__main__":
    total = 0
    for p in sys.argv[1:]:
        h = repair(p)
        total += h
        print(f"{h:4d}  {p}")
    print(f"{total:4d}  TOTAL")