"""Undo the em-dash repair script's insertion.

That script appended DASH * (count of '??' in the span) immediately after each
comment/string span instead of replacing the '??' in place. Removing every
maximal em-dash run that sits at a span boundary restores the file exactly.
"""
import sys
import importlib.util

spec = importlib.util.spec_from_file_location("fx", r"C:\MUWORK\GAME\MAPLESOTRY\wz工具\追風WZ編輯器\nx-probe\fix-emdash.py")
fx = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fx)

DASH = "\u2014"


def undo(path):
    with open(path, encoding="utf-8", newline="") as fh:
        text = fh.read()
    total = 0
    while True:
        spans = fx.regions(text)
        cuts = []
        for start, end in spans:
            # run ending exactly at the span end (inside, just before newline)
            j = end
            while j > start and text[j - 1] == DASH:
                j -= 1
            if j != end:
                cuts.append((j, end))
                continue
            # run starting exactly at the span end
            k = end
            while k < len(text) and text[k] == DASH:
                k += 1
            if k != end:
                cuts.append((end, k))
        if not cuts:
            break
        out, pos = [], 0
        for j, k in cuts:
            out.append(text[pos:j])
            total += k - j
            pos = k
        out.append(text[pos:])
        text = "".join(out)
    if total:
        with open(path, "w", encoding="utf-8", newline="") as fh:
            fh.write(text)
    return total


if __name__ == "__main__":
    for p in sys.argv[1:]:
        print(f"{undo(p):4d}  {p}")