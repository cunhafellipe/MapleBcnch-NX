#!/usr/bin/env python3
"""MapleBench project control — the single writer of work-item state.

WHY THIS EXISTS
---------------
The project's history is 80 journal files, five root plans and four handoffs
that contradict each other. A session that starts cold cannot tell which is
authoritative, and a compacted session cannot tell where it was. So:

  * State lives in ONE place -- pm/work/*.md frontmatter -- and this script is
    the only thing that writes it. A human editing a `state:` line by hand is a
    drift bug, not a shortcut.
  * pm/STATE.md is GENERATED from that state and is never hand-edited.
  * Every item carries an `acceptance:` block: commands and the output they
    must print. An item with no acceptance cannot be closed, because there is
    no way to say what "done" meant.

COMMANDS
--------
  status              the whole project in a bounded screenful
  next [n]            the next n actionable items, in order
  show <id>           one item, rendered
  new --title --state ...   create an item from a template
  set <id> state=X evidence="..." closed=...   mutate fields
  check               structural audit: dangling refs, missing acceptance, orphans
  gate                build + deploy + run every gate suite (needs the test instance)

USAGE
-----
  python tools/pm.py status
  python tools/pm.py show W-006
  python tools/pm.py set W-006 state=done evidence="10 suites green" closed=2026-10-16
"""
from __future__ import annotations

import argparse
import datetime as _dt
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PM = os.path.join(ROOT, "pm")
WORK = os.path.join(PM, "work")
STATE_FILE = os.path.join(PM, "STATE.md")

# The only legal values for `state`. Anything else is a typo and `check` says so.
STATES = ("todo", "measuring", "building", "gating", "done", "blocked", "dropped")
OPEN_STATES = ("todo", "measuring", "building", "gating", "blocked")

MAX_LINES = 200  # a work item longer than this is a journal entry, not a work item


# --------------------------------------------------------------------------- #
# frontmatter
# --------------------------------------------------------------------------- #
def parse_frontmatter(path: str) -> tuple[dict, str]:
    """Minimal YAML-subset frontmatter: scalars, and `key: [a, b]` lists.

    Deliberately not PyYAML. The value of a flat key: value block is that a
    cold agent can read it without a parser, a schema, or a dependency.
    """
    with open(path, "r", encoding="utf-8") as fh:
        text = fh.read()
    # W-063: encoding="utf-8" KEEPS a byte-order mark, and a BOM makes the first
    # three characters `\ufeff-` — so `startswith("---\n")` was false for a file
    # whose frontmatter is perfectly good, and `parse_frontmatter` returned an
    # EMPTY dict. The consequence was not a parse error: the item silently became
    # invisible. It did not count toward `done`, `check` could not see its
    # acceptance, and `status` rendered it as a blank `P99` row that looks like an
    # empty placeholder rather than a closed piece of work. W-055 is the file
    # that had the BOM; this is the second time an encoding assumption has cost
    # this project a wrong answer (TRAPS T-15, T-11), and the tell is the same:
    # something read a file fine in an editor and not in a script.
    #
    # Strip it rather than raise on it. `utf-8-sig` would also do it, but then the
    # body handed back would still carry the mark, and only the first three
    # characters would be clean.
    if text.startswith("\ufeff"):
        text = text[1:]
    if not text.startswith("---\n"):
        return {}, text
    end = text.find("\n---", 3)
    if end == -1:
        return {}, text
    block = text[4:end + 1]
    body = text[end + 4:].lstrip("\n")

    data: dict = {}
    key = None
    lines = block.splitlines()
    i = 0
    while i < len(lines):
        raw = lines[i]
        i += 1
        if not raw.strip() or raw.lstrip().startswith("#"):
            continue
        indented = raw[:1] in (" ", "\t")
        if indented and key is not None:
            body_text = raw.strip()
            if body_text.startswith("- ") and ":" in body_text:
                # D2. A MAPPING inside an indented list. This is what makes
                # `verify:` executable:
                #
                #     verify:
                #       - cmd: "python save-audit.py"
                #         expect: "15 assertions, 0 failed"
                #
                # The old parser kept only the flat text after "- ", so both
                # halves collapsed into one unusable string and the acceptance
                # of an item was prose. That is P1 in the design doc: an
                # acceptance nobody can run is a comment.
                entry = {}
                k, _, v = body_text[2:].partition(":")
                entry[k.strip()] = v.strip().strip("'\"")
                while i < len(lines):
                    sib = lines[i]
                    if not sib.strip():
                        i += 1
                        continue
                    sind = len(sib) - len(sib.lstrip(" \t"))
                    if sib[:1] not in (" ", "\t") or sib.strip().startswith("- "):
                        break
                    if ":" not in sib.strip():
                        break
                    sk, _, sv = sib.strip().partition(":")
                    entry[sk.strip()] = sv.strip().strip("'\"")
                    i += 1
                data.setdefault(key + "_list", []).append(entry)
                continue
            data.setdefault(key + "_list", []).append(
                body_text.lstrip("- ").strip())
            continue
        if ":" not in raw:
            continue
        key, _, value = raw.partition(":")
        key = key.strip()
        value = value.strip()
        if value.startswith('"') and value.endswith('"') and len(value) > 1:
            # A QUOTED scalar, not a list. `refs: "CharStats.cpp:118, EquipData.cpp:52"`
            # is one string whose own colons and commas must survive: splitting it on
            # `[...]` or on `:` is how a citation list turns into a character list.
            data[key] = value[1:-1]
            key = None
        elif value.startswith("[") and value.endswith("]"):
            data[key] = [v.strip().strip("'\"") for v in value[1:-1].split(",") if v.strip()]
            data.pop(key + "_list", None)
        elif value:
            # Unwrap a scalar ONLY when it is quoted at BOTH ends.
            #
            # The unconditional `.strip("'\"")` this replaced ate the closing
            # quote of a value that merely STARTED with one -- and the very
            # first thing written that way was a verify command:
            #   cmd: powershell ... -Map '000000002.img'
            # became, after one parse and one re-render,
            #   cmd: powershell ... -Map '000000002.img
            # which then looked for a map named "'000000002.img" and reported a
            # red against the item under test. Corrupting a stored value on the
            # round trip is worse than not parsing it at all: the item still
            # looks well-formed.
            v = value.strip()
            if len(v) >= 2 and v[0] in "\"'" and v[-1] == v[0]:
                data[key] = v[1:-1]
            else:
                data[key] = v
            key = None
        else:
            data[key] = ""
    return data, body


def render_frontmatter(data: dict) -> str:
    out = ["---"]
    for key, value in data.items():
        if key.startswith("_"):
            continue
        # A key whose `_list` sibling holds entries is rendered by the branch
        # below, and only there. Rendering both wrote the header twice, which is
        # how `verify: ` and `verify:` ended up on consecutive lines.
        if isinstance(value, str) and not value.strip() and data.get(key + "_list"):
            continue
        if key.endswith("_list"):
            # An indented list belongs to the scalar that precedes it, and it is
            # NOT optional data: it is where `depends`, `blocks` and -- since D2
            # -- `verify` live. Dropping it here meant that any `pm.py set` on an
            # item silently deleted its executable acceptance, which is the
            # quietest possible way to make a check stop meaning anything.
            base = key[:-5]
            out.append(f"{base}:")
            for entry in value or []:
                if isinstance(entry, dict):
                    first = True
                    for k, v in entry.items():
                        lead = "  - " if first else "    "
                        out.append(f"{lead}{k}: {v}")
                        first = False
                else:
                    out.append(f"  - {entry}")
            continue
        if isinstance(value, list):
            out.append(f"{key}: [{', '.join(str(v) for v in value)}]")
        elif isinstance(value, str) and ('"' in value or "\n" in value or
                                         (value.startswith("[") and value.endswith("]"))):
            # Re-quote a value that would otherwise be read back as a list.
            out.append(f'{key}: "{value.replace(chr(34), chr(39))}"')
        else:
            out.append(f"{key}: {value}")
    out.append("---")
    return "\n".join(out) + "\n"


def parse_frontmatter_checked(path: str) -> tuple[dict, str, str]:
    """parse_frontmatter, plus WHY it failed.

    D1. The old signature had one way to say "no frontmatter": return `{}`.
    That is the same value a frontmatter block full of empty values produces,
    so a file that could not be read was indistinguishable from a file that said
    nothing — and `check` reported `0 problem(s)` about it.

    That is not hypothetical. W-055 carried a UTF-8 BOM (W-063), so
    `startswith("---\n")` was false, the parse returned `{}`, and the item
    vanished: it did not count toward `done`, `check` could not see it, and
    `status` rendered it as a blank row that reads like an empty placeholder
    rather than a closed piece of work.

    An unreadable item is now an ERROR with a name attached. It can never again
    pass as a well-formed item.
    """
    with open(path, "rb") as fh:
        raw = fh.read()
    bom = raw.startswith(b"\xef\xbb\xbf")
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError as e:
        return {}, "", f"not valid UTF-8: {e}"
    if bom:
        text = text[1:]
    if not text.lstrip().startswith("---"):
        return {}, text, "no frontmatter block (file does not start with ---)"
    if "\n---" not in text[3:]:
        return {}, text, "frontmatter block is never closed"
    data, body = parse_frontmatter(path)
    if not data:
        return {}, body, "frontmatter parsed to nothing"
    if not str(data.get("id", "")).strip():
        return {}, body, "frontmatter has no id"
    return data, body, ""


def load_items(strict: bool = False) -> list[dict]:
    """Every work item, with `_parse_error` set on anything unreadable.

    `strict=True` is what `check` and `doctor` use: they must see the broken
    files, not a filtered list that quietly excludes them.
    """
    items = []
    if not os.path.isdir(WORK):
        return items
    for name in sorted(os.listdir(WORK)):
        if not name.endswith(".md"):
            continue
        path = os.path.join(WORK, name)
        data, body, err = parse_frontmatter_checked(path)
        data["_file"] = path
        data["_name"] = name
        data["_lines"] = len(body.splitlines()) + len(data)
        data["_body"] = body
        data["_parse_error"] = err
        if err and strict:
            items.append(data)
            continue
        items.append(data)
    return items


# --------------------------------------------------------------------------- #
# pm/SPEC.yaml — the areas / suites / invariants catalog
# --------------------------------------------------------------------------- #
SPEC_FILE = os.path.join(PM, "SPEC.yaml")


def _scalar(raw: str):
    raw = raw.strip()
    if len(raw) >= 2 and raw[0] == raw[-1] and raw[0] in "\"'":
        return raw[1:-1]
    if raw.startswith("[") and raw.endswith("]"):
        return [v.strip().strip("'\"") for v in raw[1:-1].split(",") if v.strip()]
    low = raw.lower()
    if low in ("true", "yes"):
        return True
    if low in ("false", "no"):
        return False
    return raw


def _spec_block(lines: list[str], start: int, indent: int) -> tuple[object, int]:
    """Parse a block at `indent`. Returns (value, next_index).

    Three shapes, which is everything SPEC.yaml uses:
      - `key:` then a deeper `- ` list of scalars
      - `key:` then a deeper `- key: value` list of mappings
      - `key: value`
    """
    out = {}
    i = start
    while i < len(lines):
        raw = lines[i]
        stripped = raw.strip()
        if not stripped or stripped.startswith("#"):
            i += 1
            continue
        cur = len(raw) - len(raw.lstrip(" "))
        if cur < indent:
            break
        if cur > indent:
            i += 1
            continue
        if ":" not in stripped:
            i += 1
            continue
        key, _, rest = stripped.partition(":")
        key = key.strip()
        rest = rest.strip()
        if rest:
            out[key] = _scalar(rest)
            i += 1
            continue
        # A block under this key: a `- ` list, or a `>` folded scalar.
        j = i + 1
        if j < len(lines) and lines[j].strip().startswith(">"):
            folded = []
            j += 1
            while j < len(lines) and (not lines[j].strip()
                                      or len(lines[j]) - len(lines[j].lstrip(" ")) > indent):
                if lines[j].strip() and not lines[j].strip().startswith("#"):
                    folded.append(lines[j].strip())
                j += 1
            out[key] = " ".join(folded)
            i = j
            continue
        items = []
        while j < len(lines):
            nxt = lines[j]
            if not nxt.strip():
                j += 1
                continue
            nind = len(nxt) - len(nxt.lstrip(" "))
            if nind <= indent:
                break
            nstr = nxt.strip()
            if nstr.startswith("- "):
                item = nstr[2:].strip()
                if ":" in item:
                    # A mapping inside a list: read its sibling keys too.
                    k, _, v = item.partition(":")
                    entry = {k.strip(): _scalar(v.strip())}
                    j += 1
                    while j < len(lines):
                        sib = lines[j]
                        if not sib.strip():
                            j += 1
                            continue
                        sind = len(sib) - len(sib.lstrip(" "))
                        if sind <= nind or sib.strip().startswith("- "):
                            break
                        if ":" not in sib.strip():
                            break
                        sk, _, sv = sib.strip().partition(":")
                        sk = sk.strip()
                        sv = sv.strip()
                        if sv == ">":
                            # A folded scalar. Consume its deeper lines HERE, or
                            # the enclosing list loop sees an indented line that
                            # is not a `- ` item and ends the list early -- which
                            # is how `areas` read as 1 instead of 17.
                            folded = []
                            j += 1
                            while j < len(lines) and (
                                    not lines[j].strip()
                                    or len(lines[j]) - len(lines[j].lstrip(" ")) > sind):
                                if lines[j].strip() and not lines[j].strip().startswith("#"):
                                    folded.append(lines[j].strip())
                                j += 1
                            entry[sk] = " ".join(folded)
                            continue
                        entry[sk] = _scalar(sv)
                        j += 1
                    items.append(entry)
                else:
                    items.append(_scalar(item))
                    j += 1
                continue
            break
        out[key] = items if items else ""
        i = j
    return out, i


def load_spec() -> dict:
    if not os.path.exists(SPEC_FILE):
        return {}
    with open(SPEC_FILE, "r", encoding="utf-8-sig") as fh:
        lines = fh.read().splitlines()
    spec, _ = _spec_block(lines, 0, 0)
    return spec


def spec_rows(spec: dict, key: str) -> list[dict]:
    rows = spec.get(key) or []
    return [r for r in rows if isinstance(r, dict)]


def write_item(data: dict) -> None:
    with open(data["_file"], "w", encoding="utf-8") as fh:
        fh.write(render_frontmatter(data))
        fh.write(data["_body"])


# --------------------------------------------------------------------------- #
# rendering
# --------------------------------------------------------------------------- #
def field(item: dict, key: str, default: str = "") -> str:
    value = item.get(key, default)
    if isinstance(value, list):
        return ", ".join(str(v) for v in value)
    return str(value)


def priority_of(item: dict) -> tuple:
    """Lower is sooner. Unprioritised sorts last, never first."""
    raw = field(item, "priority", "99")
    try:
        top = int(raw)
    except ValueError:
        top = 99
    return (top, field(item, "id"))


def status_line(item: dict) -> str:
    state = field(item, "state", "todo")
    mark = {"done": "x", "blocked": "!", "dropped": "-"}.get(state, " ")
    title = field(item, "title")
    return f"  [{mark}] {field(item, 'id'):8s} {state:9s} P{field(item, 'priority', '99'):>2s}  {title}"


def cmd_status(_args) -> int:
    items = load_items()
    by_state: dict[str, list] = {}
    for item in items:
        by_state.setdefault(field(item, "state", "todo"), []).append(item)

    print(f"MapleBench — {len(items)} work items, {len(by_state.get('done', []))} done, "
          f"{len([i for i in items if field(i, 'state') in OPEN_STATES])} open")
    print()
    for state in ("building", "measuring", "gating", "blocked", "todo"):
        group = sorted(by_state.get(state, []), key=priority_of)
        if not group:
            continue
        print(f"{state.upper()} ({len(group)})")
        for item in group[:14]:
            print(status_line(item))
        if len(group) > 14:
            print(f"      ... and {len(group) - 14} more")
        print()

    ready = [i for i in items
             if field(i, "state") in ("todo", "measuring")
             and all(field(d, "state") in ("done", "dropped")
                     for d in items if field(d, "id") in as_list(i, "depends"))]
    ready.sort(key=priority_of)
    print(f"READY ({len(ready)}) — dependencies satisfied")
    for item in ready[:8]:
        print(status_line(item))
    return 0


def as_list(item: dict, key: str) -> list[str]:
    raw = item.get(key, "")
    if isinstance(raw, list):
        return [str(v).strip() for v in raw]
    text = str(raw).strip()
    # `[a, b]` and `a, b` are the same list. `set X depends=[]` must produce an
    # EMPTY list, not a dependency on an item whose id is the two characters "[]".
    if text.startswith("[") and text.endswith("]"):
        text = text[1:-1]
    return [v.strip().strip("'\"") for v in text.split(",") if v.strip()]


def acceptance_of(item: dict) -> list[str]:
    """The item's acceptance block, as a list of lines.

    Read from the BODY, not a frontmatter field: acceptance is a numbered list
    of commands, and a list of commands does not fit on one line of frontmatter
    without turning into an unreadable string.
    """
    lines = item["_body"].splitlines()
    out, inside = [], False
    for line in lines:
        if line.startswith("## "):
            inside = line.strip().lower().startswith("## acceptance")
            continue
        if inside and line.strip():
            out.append(line.strip())
    return out


def cmd_next(args) -> int:
    items = load_items()
    ready = [i for i in items
             if field(i, "state") in ("todo", "measuring")
             and all(field(d, "state") in ("done", "dropped")
                     for d in items if field(d, "id") in as_list(i, "depends"))]
    ready.sort(key=priority_of)
    if not ready:
        print("Nothing ready. Check `python tools/pm.py status`.")
        return 0
    for item in ready[:args.n]:
        print(f"--- {field(item, 'id')}  {field(item, 'title')}")
        print(f"    file: {os.path.relpath(item['_file'], ROOT)}")
        print(f"    refs: {field(item, 'refs') or '(none recorded)'}")
        rules = acceptance_of(item)
        if rules:
            print("    acceptance:")
            for line in rules[:6]:
                print(f"      {line}")
        else:
            print("    acceptance: NONE — this item cannot be closed as it stands")
        if field(item, "blocked_on"):
            print(f"    blocked on: {field(item, 'blocked_on')}")
        print()
    return 0


def cmd_show(args) -> int:
    for item in load_items():
        if field(item, "id") == args.id:
            print(f"# {field(item, 'id')} — {field(item, 'title')}")
            print(f"state={field(item, 'state')}  priority={field(item, 'priority')}  "
                  f"owner={field(item, 'owner', '-')}")
            print(f"depends={as_list(item, 'depends') or '-'}  "
                  f"blocks={as_list(item, 'blocks') or '-'}")
            print(f"refs={field(item, 'refs') or '-'}")
            if field(item, "blocked_on"):
                print(f"blocked_on={field(item, 'blocked_on')}")
            if field(item, "evidence"):
                print(f"evidence={field(item, 'evidence')}")
            print()
            print(item["_body"])
            return 0
    print(f"no work item with id {args.id}")
    return 1


TEMPLATE = """---
id: {id}
title: {title}
state: todo
priority: 50
owner:
opened: {date}
closed:
depends: []
refs:
blocked_on:
evidence:
---

## Intent

One paragraph. What changes for the user when this is done.

## Reference

`path:line` — the exact construct being ported. If there is none, say so
explicitly and mark the item own-design.

## Approach

Ordered steps. Each names a file and a symbol.

## Acceptance

Commands, and the output each must produce. An item with no acceptance
cannot be closed.

```
# TODO
```

## Result

Filled in when closed: what actually landed, and the evidence.
"""


def next_id(items: list) -> str:
    nums = [int(m.group(1)) for i in items
            if (m := re.fullmatch(r"W-(\d{3})", field(i, "id")))]
    return f"W-{(max(nums) + 1 if nums else 1):03d}"


def cmd_new(args) -> int:
    items = load_items()
    item_id = args.id or next_id(items)
    data = {
        "id": item_id,
        "title": args.title,
        "state": args.state,
        "priority": args.priority,
        "owner": "",
        "opened": _dt.date.today().isoformat(),
        "closed": "",
        "depends": args.depends or [],
        "refs": args.refs or "",
        "blocked_on": "",
        "evidence": "",
    }
    slug = re.sub(r"[^a-z0-9]+", "-", args.title.lower()).strip("-")[:48]
    data["_file"] = os.path.join(WORK, f"{item_id}-{slug}.md")
    data["_body"] = TEMPLATE.format(id=item_id, title=args.title,
                                    date=data["opened"]).split("---\n", 2)[2]
    write_item(data)
    print(f"created {os.path.relpath(data['_file'], ROOT)}")
    return 0


def cmd_set(args) -> int:
    for item in load_items():
        if field(item, "id") != args.id:
            continue
        for pair in args.assignments:
            if "=" not in pair:
                print(f"not a key=value assignment: {pair}")
                return 1
            key, _, value = pair.partition("=")
            item[key.strip()] = value.strip()
        if field(item, "state") == "done" and not field(item, "evidence"):
            print("refusing to close without evidence: an item with no proof of its "
                  "acceptance is exactly the false green this project keeps hitting.")
            return 1
        if field(item, "state") == "done" and not field(item, "closed"):
            item["closed"] = _dt.date.today().isoformat()
        if field(item, "state") == "blocked" and not field(item, "blocked_on"):
            print("refusing to mark blocked without blocked_on: name the condition.")
            return 1
        # A dependency on unfinished work is legal — it is how a backlog is
        # expressed — but `check` flags it, so `set` says so at the moment of the
        # edit rather than leaving it to be discovered later.
        if "depends" in args.assignments:
            others = {field(i, "id"): i for i in load_items()}
            for dep in as_list(item, "depends"):
                if dep not in others:
                    print(f"refusing: {args.id} would depend on unknown id {dep}")
                    return 1
                if field(others[dep], "state") not in ("done", "dropped"):
                    print(f"note: {args.id} now depends on {dep}, which is "
                          f"{field(others[dep], 'state')}. `check` will report it until "
                          f"{dep} closes — that is legal, just not ready.")
        write_item(item)
        print(f"{args.id}: {field(item, 'state')}")
        return 0
    print(f"no work item with id {args.id}")
    return 1


def cmd_check(_args) -> int:
    items = load_items(strict=True)
    by_id = {field(i, "id"): i for i in items if not i["_parse_error"]}
    problems = 0
    migrate: list[str] = []

    # D1/P2. An unreadable work item is a HARD failure, not an empty dict that
    # quietly passes every rule below. W-055 carried a UTF-8 BOM and was
    # invisible to this function while it reported `0 problem(s)` -- the check
    # was passing about a file it had never read.
    for item in items:
        if item["_parse_error"]:
            print(f"  UNREADABLE {item['_name']}: {item['_parse_error']}")
            problems += 1

    # Duplicate ids. `new` computes the next free one, so two items can only
    # collide if one was copied or one was renamed by hand -- and a collision
    # makes `show`, `set` and every `depends` edge silently resolve to whichever
    # file sorted first. That was a live mistake on 2026-10-16 (a new item was
    # written as W-033, which was already Map background music).
    seen: dict[str, list[str]] = {}
    for item in items:
        seen.setdefault(field(item, "id"), []).append(item["_name"])
    for item_id, names in seen.items():
        if len(names) > 1:
            print(f"  DUPLICATE id {item_id}: {', '.join(names)}")
            problems += 1

    for item in items:
        if item["_parse_error"]:
            continue
        item_id = field(item, "id")
        state = field(item, "state", "todo")
        if state not in STATES:
            print(f"  {item_id}: state={state!r} is not one of {STATES}")
            problems += 1
        if item_id not in item["_name"]:
            print(f"  {item_id}: filename {item['_name']} does not start with the id")
            problems += 1
        if item["_lines"] > MAX_LINES:
            print(f"  {item_id}: {item['_lines']} lines — over {MAX_LINES}; "
                  f"a work item that long is a journal entry")
            problems += 1

        # D2. v2 fields. ABSENT is a migration note, not an error: 54 items were
        # written before these existed, and a check that goes red on all of them
        # teaches people to ignore the ones that matter.
        ws = field(item, "workstream")
        if not ws:
            migrate.append(f"{item_id}: no workstream (one of {', '.join(WORKSTREAMS)})")
        elif ws not in WORKSTREAMS:
            print(f"  {item_id}: workstream={ws!r} is not one of {WORKSTREAMS}")
            problems += 1
        kind = field(item, "kind")
        if kind and kind not in KINDS:
            print(f"  {item_id}: kind={kind!r} is not one of {KINDS}")
            problems += 1
        risk = field(item, "risk")
        if risk and risk not in RISKS:
            print(f"  {item_id}: risk={risk!r} is not one of {RISKS}")
            problems += 1

        if state == "done":
            if not field(item, "evidence"):
                print(f"  {item_id}: done with no evidence")
                problems += 1
            if not field(item, "closed"):
                print(f"  {item_id}: done with no closed date")
                problems += 1
            if "TODO" in item["_body"]:
                print(f"  {item_id}: done but its acceptance still says TODO")
                problems += 1
        if state == "blocked" and not field(item, "blocked_on"):
            print(f"  {item_id}: blocked with no blocked_on")
            problems += 1
        for dep in as_list(item, "depends"):
            if dep not in by_id:
                print(f"  {item_id}: depends on unknown id {dep}")
                problems += 1
            elif field(by_id[dep], "state") not in ("done", "dropped"):
                print(f"  {item_id}: depends on {dep} which is "
                      f"{field(by_id[dep], 'state')}")
                problems += 1
        if state in OPEN_STATES and not field(item, "refs") and "own-design" not in item["_body"].lower():
            print(f"  {item_id}: open, with no reference and not marked own-design")
            problems += 1

    closed_dependents = []
    for item in items:
        if field(item, "state") != "done":
            continue
        for other in items:
            if field(other, "id") in as_list(item, "blocks") and \
                    field(other, "state") not in ("done", "dropped"):
                closed_dependents.append(f"{field(item, 'id')} blocks unfinished {field(other, 'id')}")
    for line in closed_dependents:
        print(f"  {line}")
        problems += 1

    # D2. `depends` must be a DAG. A cycle makes every item in it wait on an id
    # that waits back, and `next` returns nothing that can ever become ready --
    # silently, because each individual edge looks fine.
    colour: dict[str, int] = {}
    cycle: list[str] = []

    def visit(node: str, stack: list) -> None:
        colour[node] = 1
        stack.append(node)
        for dep in as_list(by_id[node], "depends"):
            if dep not in by_id:
                continue
            if colour.get(dep) == 1:
                cycle.append(" -> ".join(stack[stack.index(dep):] + [dep]))
            elif colour.get(dep, 0) == 0:
                visit(dep, stack)
        stack.pop()
        colour[node] = 2

    for node in by_id:
        if colour.get(node, 0) == 0:
            visit(node, [])
    for c in sorted(set(cycle)):
        print(f"  DEPENDENCY CYCLE: {c}")
        problems += 1

    # D3. The registry and the runner must agree, or `coverage` is describing a
    # gate that does not exist.
    spec = load_spec()
    if spec:
        declared = {a["id"] for a in spec_rows(spec, "areas")}
        for s in spec_rows(spec, "suites"):
            if not str(s.get("file", "")).strip():
                print(f"  SPEC suite {s.get('id')}: no file")
                problems += 1
            for aid in (s.get("areas") or []):
                if aid not in declared:
                    print(f"  SPEC suite {s.get('id')}: area {aid!r} is not declared")
                    problems += 1

    if migrate:
        print()
        print(f"  migration notes ({len(migrate)} items predate the v2 fields) — "
              f"not errors; add them as you touch the item:")
        for line in migrate[:8]:
            print(f"    {line}")
        if len(migrate) > 8:
            print(f"    ... and {len(migrate) - 8} more")

    print(f"check: {len(items)} items, {problems} problem(s)")
    return 1 if problems else 0


def generate_state_md() -> str:
    items = load_items()
    by_state: dict[str, list] = {}
    for item in items:
        by_state.setdefault(field(item, "state", "todo"), []).append(item)
    today = _dt.date.today().isoformat()

    lines = [
        "<!-- GENERATED by `python tools/pm.py status`. Do not hand-edit. -->",
        "",
        f"# State — {today}",
        "",
        f"{len(items)} work items · {len(by_state.get('done', []))} done · "
        f"{len([i for i in items if field(i, 'state') in OPEN_STATES])} open",
        "",
        "A cold agent reads this file, then `pm/work/<the item above>.md`, then the",
        "item's `refs`. Nothing else. Everything below is one line per item on",
        "purpose: the journal in `JOURNAL/` is history, not state.",
        "",
    ]
    for state in ("building", "measuring", "gating", "blocked"):
        group = sorted(by_state.get(state, []), key=priority_of)
        if group:
            lines += [f"## {state}", ""]
            lines += [f"- **{field(i, 'id')}** {field(i, 'title')}"
                      + (f" — blocked on: {field(i, 'blocked_on')}"
                         if field(i, "blocked_on") else "")
                      for i in group]
            lines += [""]

    ready = [i for i in items
             if field(i, "state") in ("todo", "measuring")
             and all(field(d, "state") in ("done", "dropped")
                     for d in items if field(d, "id") in as_list(i, "depends"))]
    ready.sort(key=priority_of)
    lines += ["## ready", ""]
    lines += [f"- **P{field(i, 'priority', '99')}** {field(i, 'id')} {field(i, 'title')}"
              for i in ready] or ["- (nothing ready)"]
    lines += [""]

    open_items = sorted((i for i in items if field(i, "state") in OPEN_STATES),
                        key=priority_of)
    lines += ["## open", ""]
    lines += [f"- {field(i, 'state'):9s} P{field(i, 'priority', '99'):>2s} "
              f"{field(i, 'id')} {field(i, 'title')}" for i in open_items] or ["- (none)"]
    lines += [""]

    done = sorted(by_state.get("done", []), key=lambda i: field(i, "closed"))
    lines += ["## done (newest last)", ""]
    lines += [f"- {field(i, 'closed')} {field(i, 'id')} {field(i, 'title')}" for i in done]
    lines += [""]
    return "\n".join(lines)


# --------------------------------------------------------------------------- #
# PM v2 commands: start / doctor / coverage / verify / critical / stream
# --------------------------------------------------------------------------- #
# The gate's suites and its runner live in the repository. They used to live in
# an agent cache under the user profile, and that cache pruned them on
# 2026-10-21; they survived only because a shadow copy taken an hour earlier
# still held them. A directory that prunes itself is not a place to keep the
# thing that decides whether the code is correct.
SUITES = os.path.join(ROOT, "tools", "verify", "suites")
GATE = os.path.join(ROOT, "tools", "verify", "gate.ps1")

WORKSTREAMS = ("save", "simulate", "archive", "editor-ui", "infra", "parity",
               "reference", "unassigned")
KINDS = ("defect", "feature", "audit", "decision", "spike", "chore")
RISKS = ("high", "med", "low")


def item_verify(item: dict) -> list[dict]:
    """The executable acceptance of an item. Empty means "prose only"."""
    out = []
    for entry in item.get("verify_list", []) or []:
        if isinstance(entry, dict) and str(entry.get("cmd", "")).strip():
            out.append(entry)
    return out


def _suite(name: str) -> str:
    """A suite file, resolved inside the repository."""
    return os.path.join(SUITES, name)


def _suite_path(file: str) -> str:
    """Where a suite's file lives.

    A bare name is a suite; a name with a directory is repo-relative. Same
    rule the invariant check uses, so a suite is looked for in one place
    only.
    """
    if not file:
        return ""
    if os.path.dirname(file):
        return os.path.join(ROOT, file)
    return _suite(file)


def _strip_comments(text: str, ext: str) -> list[str]:
    """The source with its comments removed, one line per source line.

    A declaration must not be satisfiable by prose. TRAPS T-2: a check that a
    comment can satisfy is not a check -- the first version of the citation
    audit read a mention in a comment and called the reference present.

    Quote state is tracked, so a '#' inside a string is not a comment. The
    suites print markers like ">>> RED: no damage over 400 frames" and one of
    them puts a '#' in a message, and stripping that would hide the marker
    the gate is supposed to read.
    """
    out: list[str] = []
    in_block = False
    in_triple = None
    for raw in text.splitlines():
        head = raw.lstrip()
        if ext in (".ps1", ".mjs", ".js"):
            if in_block:
                if "#>" in raw or "*/" in raw:
                    in_block = False
                continue
            if head.startswith("<#") or head.startswith("/*"):
                if "#>" not in raw and "*/" not in raw:
                    in_block = True
                continue
        if in_triple:
            # A multi-line string is documentation or a payload, never a
            # statement. A marker quoted inside one is not printed.
            if in_triple in raw:
                in_triple = None
            out.append("")
            continue
        for q in ('"""', "'''"):
            if ext == ".py" and q in raw and raw.count(q) == 1:
                in_triple = q
                break
        if ext == ".ps1" and '@"' in raw and '"@' not in raw:
            in_triple = '"@'
            out.append("")
            continue
        chars: list[str] = []
        quote = None
        i = 0
        while i < len(raw):
            ch = raw[i]
            if quote:
                chars.append(ch)
                if ch == "\\" and i + 1 < len(raw):
                    chars.append(raw[i + 1])
                    i += 2
                    continue
                if ch == quote:
                    quote = None
                i += 1
                continue
            if ch in ("'", '"'):
                quote = ch
                chars.append(ch)
                i += 1
                continue
            if ext in (".py", ".ps1") and ch == "#":
                break
            if ext in (".mjs", ".js") and raw.startswith("//", i):
                break
            chars.append(ch)
            i += 1
        out.append("".join(chars))
    return out


def _gate_red_markers(gate_text: str) -> list[str]:
    """The markers the gate itself treats as red, read out of gate.ps1.

    Parsed rather than copied, so the list cannot drift away from the gate:
    a hardcoded copy here would be a second source of truth, and the whole
    family this file exists to catch is a list that says one thing while the
    gate does another.
    """
    m = re.search(r"\$RED\s*=\s*@\(([^)]*)\)", gate_text, re.S)
    if not m:
        return []
    return re.findall(r"'([^']+)'", m.group(1))


def _argv(cmd: str) -> list[str]:
    """Split a command line, unwrapping a fully-quoted token.

    `-Map '000000002.img'` must reach the script as `-Map 000000002.img`.
    A plain split() leaves the quotes in, the script looks for a map whose name
    begins with an apostrophe, finds none, and reports a red that has nothing to
    do with the thing under test. That is exactly what happened the first time
    `verify W-057` ran.
    """
    out = []
    for tok in cmd.split():
        # Strip quotes INDEPENDENTLY at each end. An exactly-wrapped token is the
        # easy case; the one that actually bit is a value whose CLOSING quote was
        # already eaten by the frontmatter parser's own `.strip("'\"")`, leaving
        # `'000000002.img` with a leading quote and nothing to match. So a leading
        # quote has to go even when the trailing one is missing.
        t = tok.strip("'\"")
        out.append(t if t else tok)
    return out


def _decode(raw) -> str:
    """Decode a subprocess's bytes as UTF-8, never as the ANSI code page.

    `subprocess.run(text=True)` decodes with the locale encoding -- cp950 on this
    machine -- and these suites print NPC names. The capture thread then died with
    UnicodeDecodeError inside subprocess itself, so `verify` received an EMPTY
    string and reported the item's acceptance as NOT FOUND while the suite itself
    had exited 0 and printed exactly the line being searched for.

    Same family as the BOM item (W-063) and the CJK path items (T-15): an encoding
    assumption turns a correct answer into a wrong one, silently.
    """
    if raw is None:
        return ""
    if isinstance(raw, str):
        return raw
    for enc in ("utf-8", "utf-8-sig", "cp950", "mbcs"):
        try:
            return raw.decode(enc)
        except UnicodeDecodeError:
            continue
    return raw.decode("utf-8", errors="replace")


def run_cmd(cmd: str, timeout: int = 2400) -> tuple[int, str]:
    import subprocess as sp
    cmd = cmd.replace("$S", _suite("")).replace("%LOCALAPPDATA%",
                                                   os.environ.get("LOCALAPPDATA", ""))
    if cmd.lstrip().lower().startswith("powershell"):
        # Insert -ExecutionPolicy Bypass right after the executable, so the order
        # stays `powershell -NoProfile -ExecutionPolicy Bypass -File <script> <args>`.
        argv = _argv(cmd)
        argv[1:1] = ["-ExecutionPolicy", "Bypass"]
        # CAPTURE, or `expect` can never match: sp.call returns only the exit code
        # and streams the text to this console.
        try:
            p = sp.run(argv, capture_output=True, timeout=timeout)
            return p.returncode, _decode(p.stdout) + _decode(p.stderr)
        except sp.TimeoutExpired:
            return 1, f"timed out after {timeout}s"
    p = sp.run(cmd, shell=True, capture_output=True, timeout=timeout)
    return p.returncode, _decode(p.stdout) + _decode(p.stderr)


def cmd_verify(args) -> int:
    """Run an item's acceptance. P1: an acceptance that cannot run is a comment."""
    items = load_items(strict=True)
    target = [i for i in items if field(i, "id") == args.id]
    if not target:
        print(f"no work item with id {args.id}")
        return 1
    item = target[0]
    steps = item_verify(item)
    if not steps:
        print(f"{args.id}: NO EXECUTABLE ACCEPTANCE.")
        print("  Add to the frontmatter:")
        print("    verify:")
        print("      - cmd: \"<the command>\"")
        print("        expect: \"<the substring it must print>\"")
        return 1
    print(f"{args.id} — {len(steps)} acceptance step(s)")
    bad = 0
    for n, step in enumerate(steps, 1):
        code, out = run_cmd(step["cmd"])
        want = str(step.get("expect", "")).strip()
        ok = (code == 0) and (not want or want in out)
        print(f"  {'ok  ' if ok else 'FAIL'} {n}. {step['cmd']}")
        if want:
            print(f"       expect contains: {want!r}  -> {'found' if want in out else 'NOT FOUND'}")
        if not ok:
            bad += 1
            tail = "\n".join(out.strip().splitlines()[-6:])
            print(f"       exit={code}\n{tail}")
    print(f"VERDICT: verify {args.id} {len(steps)} steps, {bad} failed")
    if bad:
        print(f">>> RED: verify {args.id}")
        return 1
    return 0


def cmd_coverage(_args) -> int:
    """P3: which functional areas have no judge at all."""
    spec = load_spec()
    areas = spec_rows(spec, "areas")
    suites = spec_rows(spec, "suites")
    if not areas:
        print("pm/SPEC.yaml has no areas")
        return 1
    lines = ["<!-- GENERATED by `python tools/pm.py coverage`. Do not hand-edit. -->",
             "", "# Coverage — area x judge", "",
             "A suite in `in_gate: false` runs separately and still counts; a suite",
             "with `asserts: false` CANNOT report red, so it is not a judge.", ""]
    holes, thin = [], []
    for area in areas:
        aid = area["id"]
        mine = [s for s in suites if aid in (s.get("areas") or [])]
        real = [s for s in mine if s.get("asserts") is not False]
        crit = area.get("criticality", "med")
        status = "COVERED" if real else ("NO JUDGE" if crit != "low" else "none")
        if not real and crit == "high":
            holes.append(aid)
        elif len(real) <= 1 and crit != "low":
            thin.append(aid)
        lines.append(f"## `{aid}` — {area.get('title','')} "
                     f"[criticality: {crit}] — **{status}**")
        if area.get("note"):
            lines.append("")
            lines.append(f"> {area['note'].strip()}")
        lines.append("")
        lines.append(f"- routes: {', '.join('`'+r+'`' for r in (area.get('routes') or []))}")
        if mine:
            for s in mine:
                flag = "" if s.get("asserts") is not False else "  **CANNOT REPORT RED**"
                extra = []
                if s.get("flaky"):
                    extra.append("flaky")
                if s.get("one_shot"):
                    extra.append("one-shot per server")
                if s.get("in_gate") is False:
                    extra.append("not in gate")
                tail = f"  ({', '.join(extra)})" if extra else ""
                lines.append(f"- `{s['id']}`{flag}{tail}")
        else:
            lines.append("- **no suite references this area**")
        lines.append("")
    lines += ["## Summary", ""]
    lines.append(f"- areas: {len(areas)}   suites: {len(suites)}")
    lines.append(f"- high-criticality areas with NO judge: {', '.join(holes) or '(none)'}")
    lines.append(f"- high/med areas with one judge or fewer: {', '.join(thin) or '(none)'}")
    cant = [s["id"] for s in suites if s.get("asserts") is False]
    lines.append(f"- suites that cannot report red: {', '.join(cant) or '(none)'}")
    flaky = [s["id"] for s in suites if s.get("flaky")]
    lines.append(f"- suites measured flaky: {', '.join(flaky) or '(none)'}")
    out = "\n".join(lines) + "\n"
    with open(os.path.join(PM, "COVERAGE.md"), "w", encoding="utf-8") as fh:
        fh.write(out)
    print(out)
    print(f"written: pm/COVERAGE.md")
    return 0


def cmd_critical(_args) -> int:
    """P5: what unblocks the most, and what is blocked on the user."""
    items = [i for i in load_items(strict=True) if not i["_parse_error"]]
    by_id = {field(i, "id"): i for i in items}

    def blocked_by(i):
        return [d for d in as_list(i, "depends")
                if d in by_id and field(by_id[d], "state") not in ("done", "dropped")]

    unblocks: dict[str, int] = {}
    for i in items:
        if field(i, "state") in ("done", "dropped"):
            continue
        for other in items:
            if i["id"] in as_list(other, "depends"):
                unblocks[i["id"]] = unblocks.get(i["id"], 0) + 1

    ready = [i for i in items
             if field(i, "state") in ("todo", "measuring") and not blocked_by(i)]
    ready.sort(key=lambda i: (-unblocks.get(i["id"], 0), priority_of(i)))
    print("=== ready now, most unblocking first ===")
    for i in ready[:10]:
        print(f"  unblocks {unblocks.get(i['id'], 0):2d}  "
              f"{status_line(i).strip()}")
    if not ready:
        print("  (nothing ready — everything open is blocked or in flight)")

    print()
    print("=== blocked on a person, not on work ===")
    for i in items:
        if field(i, "state") == "blocked" and field(i, "blocked_on"):
            print(f"  {field(i,'id')} {field(i,'title')}\n      waiting for: "
                  f"{field(i,'blocked_on')}")

    print()
    print("=== chains longer than one ===")
    shown = 0
    for i in items:
        b = blocked_by(i)
        if len(b) >= 2 and shown < 6:
            print(f"  {field(i,'id')} waits on {', '.join(b)}")
            shown += 1
    return 0


def cmd_doctor(_args) -> int:
    """D5: is the environment and the state trustworthy right now?"""
    import hashlib
    import json
    import urllib.request
    bad = 0

    print("=== state integrity ===")
    items = load_items(strict=True)
    unparsed = [i for i in items if i["_parse_error"]]
    for i in unparsed:
        print(f"  UNREADABLE {i['_name']}: {i['_parse_error']}")
    print(f"  work items: {len(items)}   unreadable: {len(unparsed)}")
    if unparsed:
        bad += 1
    ids = [field(i, "id") for i in items if not i["_parse_error"]]
    dupes = {x for x in ids if ids.count(x) > 1}
    if dupes:
        print(f"  DUPLICATE ids: {', '.join(sorted(dupes))}")
        bad += 1

    print()
    print("=== invariants (pm/SPEC.yaml) ===")
    spec = load_spec()
    for inv in spec_rows(spec, "invariants"):
        kind = inv.get("kind")
        if kind == "md5":
            p = os.path.join(ROOT, inv["file"])
            if not os.path.exists(p):
                print(f"  {inv['id']}: MISSING {inv['file']}")
                bad += 1
                continue
            h = hashlib.md5(open(p, "rb").read()).hexdigest()
            ok = h == inv["md5"]
            print(f"  {inv['id']}: {'ok' if ok else 'CHANGED'}  {h}")
            if not ok:
                print(f"      expected {inv['md5']} -- {inv.get('why','')}")
                bad += 1
        elif kind == "file-contains":
            # A bare name is a suite; a name with a directory is repo-relative.
            p = inv["file"] if os.path.dirname(inv["file"]) else _suite(inv["file"])
            p = p if os.path.isabs(p) else os.path.join(ROOT, p)
            ok = os.path.exists(p) and inv["contains"] in open(p, encoding="utf-8",
                                                              errors="replace").read()
            print(f"  {inv['id']}: {'ok' if ok else 'MISSING'}")
            if not ok:
                bad += 1
        else:
            print(f"  {inv['id']}: (kind {kind} not checked here)")

    print()
    print("=== gate vs the suite registry (SPEC.yaml) ===")
    gate = GATE
    if os.path.exists(gate):
        text = open(gate, encoding="utf-8", errors="replace").read()
        # Match `Run '<id>'` and NOT a bare substring.
        #
        # The first version of this check asked "does the id appear anywhere in
        # gate.ps1", and reported DRIFT for save-audit because its name appears
        # in the comment explaining why it is NOT in the gate. The detector was
        # measuring the comment. A check that can be satisfied by prose is not a
        # check (TRAPS T-2: a regex is not a verdict).
        registered = set(re.findall(r"Run\s+'([^']+)'", text))
        for s in spec_rows(spec, "suites"):
            in_gate = s["id"] in registered
            should = s.get("in_gate") is not False
            mark = "ok" if in_gate == should else "DRIFT"
            if mark == "DRIFT":
                bad += 1
            print(f"  {mark:5s} {s['id']:22s} registry={should}  gate={in_gate}")
        orphans = sorted(registered - {s["id"] for s in spec_rows(spec, "suites")})
        if orphans:
            print(f"  gate runs suites the registry does not list: {', '.join(orphans)}")
            bad += len(orphans)
    else:
        print("  gate.ps1 not found; skipping")

    print()
    print("=== suite declarations vs the code (SPEC.yaml) ===")
    # The gate decides red from EXPLICIT markers and prints green from a line
    # matching VERDICT|ALL LADDERS PASS|passed,|BAD=|PASS: (gate.ps1:93-94).
    # A suite that declares neither is invisible to both halves of that, and a
    # suite whose FILE IS MISSING prints a python error containing no marker
    # at all -- which the gate scored "ok". A suite file that is not there
    # must not be able to pass the gate. That is the hole this closes.
    #
    # Two classes, counted apart, both loud:
    #   ERROR -- a declaration that is false (file missing, a declared marker
    #            that the source does not contain, no verdict declared at all)
    #   GAP   -- the source prints a red marker the registry does not declare
    decl_err = 0
    decl_gap = 0
    gate_text = ""
    if os.path.exists(GATE):
        gate_text = open(GATE, encoding="utf-8", errors="replace").read()
    red_markers = _gate_red_markers(gate_text)
    suite_rows = spec_rows(spec, "suites")
    for s in suite_rows:
        sid = s.get("id", "?")
        f = s.get("file", "")
        p = _suite_path(f)
        if not p or not os.path.exists(p):
            print(f"  ERROR {sid:22s} FILE MISSING  {f}")
            decl_err += 1
            continue
        ext = os.path.splitext(p)[1].lower()
        blob = "\n".join(_strip_comments(
            open(p, encoding="utf-8", errors="replace").read(), ext))
        verdict = s.get("verdict") or ""
        red = s.get("red_marker") or ""
        problems = []
        gaps = []
        if not verdict:
            problems.append("NO VERDICT DECLARED")
        elif verdict not in blob:
            problems.append(f"VERDICT NOT IN CODE  {verdict!r}")
        if red and red not in blob:
            problems.append(f"RED MARKER NOT IN CODE  {red!r}")
        if red and red_markers and not any(red in rm for rm in red_markers):
            problems.append(f"RED MARKER IS NOT ONE THE GATE READS  {red!r}"
                            f"  gate reads {red_markers}")
        printed = [rm for rm in red_markers if rm in blob]
        if printed and not red:
            gaps.append(f"PRINTS A RED MARKER BUT DECLARES NONE  {printed}")
        for pr in problems:
            print(f"  ERROR {sid:22s} {pr}")
        for gp in gaps:
            print(f"  GAP   {sid:22s} {gp}")
        decl_err += 1 if problems else 0
        decl_gap += 1 if gaps else 0
        if not problems and not gaps:
            print(f"  ok    {sid:22s} verdict={verdict!r} red_marker={red!r}")
    print(f"  suite declarations: {len(suite_rows)} listed, "
          f"{decl_err} with a false declaration, {decl_gap} with a missing one")
    bad += decl_err + decl_gap

    print()
    print("=== test instance ===")
    try:
        with urllib.request.urlopen("http://127.0.0.1:5591/api/files", timeout=20) as r:
            n = len(json.loads(r.read().decode()))
        print(f"  server up on 5591 with {n} archive(s) open")
        if n == 0:
            print("  WARNING: no archives open -- every suite will fail. "
                  "Run open-archives.py after any restart (archive ids f1..fN "
                  "depend on open order).")
    except Exception as e:
        print(f"  server NOT reachable: {type(e).__name__}")
        print("  Start it: build-gate.ps1 (stops the old one) -> deploy.ps1 -> "
              "open-archives.py")

    one_shot = [s for s in spec_rows(spec, "suites") if s.get("one_shot")]
    if one_shot:
        print()
        print("=== one-shot suites ===")
        for s in one_shot:
            print(f"  {s['id']}: run on a FRESH server only "
                  f"(build-gate.ps1 -> deploy.ps1 -> open-archives.py -> {s['file']})")

    print()
    print(f"doctor: {bad} problem(s)")
    return 1 if bad else 0


def cmd_stream(args) -> int:
    items = [i for i in load_items(strict=True) if not i["_parse_error"]]
    by_id = {field(i, "id"): i for i in items}
    streams: dict[str, list] = {}
    for i in items:
        ws = field(i, "workstream", "unassigned")
        streams.setdefault(ws, []).append(i)
    want = args.name if args.name else None
    for ws in sorted(streams, key=lambda k: (k == "unassigned", k)):
        if want and ws != want:
            continue
        group = streams[ws]
        openn = [i for i in group if field(i, "state") in OPEN_STATES]
        done = [i for i in group if field(i, "state") == "done"]
        print(f"== {ws}: {len(group)} items, {len(done)} done, {len(openn)} open")
        for i in sorted(openn, key=priority_of):
            deps = [d for d in as_list(i, "depends")
                    if d in by_id and field(by_id[d], "state") not in ("done", "dropped")]
            why = f"  (waiting on {', '.join(deps)})" if deps else ""
            print("  " + status_line(i) + why)
    return 0


def cmd_start(_args) -> int:
    """D6: the ONE command a cold agent runs. Its output IS the answer."""
    items = [i for i in load_items(strict=True) if not i["_parse_error"]]
    print("MapleBench — cold start")
    print("=" * 60)
    print()
    print("1. State of record")
    print(f"   {len(items)} work items, "
          f"{len([i for i in items if field(i,'state')=='done'])} done, "
          f"{len([i for i in items if field(i,'state') in OPEN_STATES])} open")
    print("   python tools/pm.py status          whole project, one screenful")
    print("   python tools/pm.py coverage        which areas have no judge")
    print("   python tools/pm.py critical        what unblocks the most")
    print()
    print("2. Before you trust anything")
    print("   python tools/pm.py doctor          invariants, registry drift, server")
    print("   python tools/pm.py check           structure (unreadable items included)")
    print()
    print("3. The loop for one item")
    print("   python tools/pm.py next -n 3       pick up to three")
    print("   python tools/pm.py show <id>       the whole item")
    print("   python tools/pm.py set <id> state=building")
    print("   ... measure against the archive through /api/inspect, never a comment")
    print("   ... edit with the edit tool, never Set-Content (R-15)")
    print("   python tools/pm.py verify <id>     RUN the acceptance")
    print("   build-gate.ps1 -> deploy.ps1 -> open-archives.py -> gate.ps1")
    print("   python tools/pm.py set <id> state=done evidence=\"...\"")
    print()
    print("4. Read these four files, in this order")
    for f in ("pm/RULES.md", "pm/GATE.md", "pm/TRAPS.md", "pm/PARITY.md"):
        print(f"   {f}")
    print()
    print("5. Never")
    print("   - edit a state: line by hand          pm.py is the only writer")
    print("   - close an item with no evidence      the tool refuses; mean it")
    print("   - read JOURNAL/ to answer 'the state' it is history, not state")
    print("   - trust a green that proves nothing   see pm/TRAPS.md")
    print()
    ready = [i for i in items if field(i, "state") in ("todo", "measuring")]
    print("6. Ready right now")
    ready.sort(key=priority_of)
    for i in ready[:5]:
        print("   " + status_line(i))
    if not ready:
        print("   (nothing ready)")
    print()
    print("Spec: docs/PM-SYSTEM-DESIGN.md   |   generated: pm/STATE.md pm/COVERAGE.md")

    with open(os.path.join(PM, "START-HERE.md"), "w", encoding="utf-8") as fh:
        fh.write("<!-- GENERATED by `python tools/pm.py start`. Do not hand-edit. -->\n\n")
        fh.write("# START HERE — the cold-start protocol\n\n")
        fh.write("A cold agent runs ONE command and reads ONE screenful:\n\n")
        fh.write("```\npython tools/pm.py start\n```\n")
    return 0


def cmd_gate(args) -> int:
    runner = GATE
    if not os.path.exists(runner):
        print(f"gate runner not found at {runner}")
        return 1
    return subprocess.call(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
                            "-File", runner])


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)

    sub.add_parser("status").set_defaults(fn=cmd_status)

    p = sub.add_parser("next")
    p.add_argument("-n", type=int, default=3)
    p.set_defaults(fn=cmd_next)

    p = sub.add_parser("show")
    p.add_argument("id")
    p.set_defaults(fn=cmd_show)

    p = sub.add_parser("new")
    p.add_argument("--title", required=True)
    p.add_argument("--id")
    p.add_argument("--state", default="todo", choices=STATES)
    p.add_argument("--priority", default="50")
    p.add_argument("--depends", nargs="*", default=[])
    p.add_argument("--refs", nargs="*", default=[])
    p.set_defaults(fn=cmd_new)

    p = sub.add_parser("set")
    p.add_argument("id")
    p.add_argument("assignments", nargs="+")
    p.set_defaults(fn=cmd_set)

    sub.add_parser("check").set_defaults(fn=cmd_check)
    sub.add_parser("gate").set_defaults(fn=cmd_gate)
    sub.add_parser("start").set_defaults(fn=cmd_start)
    sub.add_parser("doctor").set_defaults(fn=cmd_doctor)
    sub.add_parser("coverage").set_defaults(fn=cmd_coverage)
    sub.add_parser("critical").set_defaults(fn=cmd_critical)

    p = sub.add_parser("verify")
    p.add_argument("id")
    p.set_defaults(fn=cmd_verify)

    p = sub.add_parser("stream")
    p.add_argument("name", nargs="?")
    p.set_defaults(fn=cmd_stream)

    args = ap.parse_args()
    if args.cmd == "status":
        with open(STATE_FILE, "w", encoding="utf-8") as fh:
            fh.write(generate_state_md())
    return args.fn(args)


if __name__ == "__main__":
    sys.exit(main())