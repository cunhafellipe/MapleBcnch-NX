"""Restore each unchanged line's original line ending.

Several files in this repo are `i/mixed` — mostly CRLF, with scattered LF lines that predate
.gitattributes taking effect. An edit writes CRLF onto the lines it inserts, and those scattered
LF lines get normalised along the way, turning a small change into an unreviewable one. The
repo's own .gitattributes says exactly that: a diff that large is not reviewable, so a real
mistake inside one would ship unseen.

For every line whose *content* is identical between HEAD and the working tree, HEAD's ending is
restored. Lines that actually changed keep the working tree's ending, because that is the edit.

Usage: restore-eol.py <repo> <path...>   (paths relative to the repo root, as in git)
"""

import difflib
import os
import subprocess
import sys


def split(text):
    """Split into [content, ending] pairs, keeping each line's own terminator."""
    lines, i = [], 0
    while i < len(text):
        j = i
        while j < len(text) and text[j] not in "\r\n":
            j += 1
        content = text[i:j]
        if j < len(text):
            ending = "\r\n" if text[j:j + 2] == "\r\n" else "\n"
            j += len(ending)
        else:
            ending = ""
        lines.append([content, ending])
        i = j
    return lines


def main(repo, files):
    for rel in files:
        head = subprocess.run(
            ["git", "-C", repo, "show", f"HEAD:{rel}"], capture_output=True
        ).stdout.decode("utf-8", "surrogateescape")
        full = os.path.join(repo, rel)
        work = open(full, "rb").read().decode("utf-8", "surrogateescape")

        h, w = split(head), split(work)
        matcher = difflib.SequenceMatcher(
            a=[c for c, _ in h], b=[c for c, _ in w], autojunk=False
        )

        result, restored = [], 0
        for tag, i1, i2, j1, j2 in matcher.get_opcodes():
            if tag == "equal":
                for k in range(i2 - i1):
                    # Content from the working tree (identical anyway), ending from HEAD.
                    if h[i1 + k][1] != w[j1 + k][1]:
                        restored += 1
                    result.append(w[j1 + k][0] + h[i1 + k][1])
            else:
                for k in range(j1, j2):
                    result.append(w[k][0] + w[k][1])

        data = "".join(result).encode("utf-8", "surrogateescape")
        before = open(full, "rb").read()
        if data != before:
            open(full, "wb").write(data)
        print(f"{rel}: {len(before):,} -> {len(data):,} bytes, {restored} line endings restored")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2:])