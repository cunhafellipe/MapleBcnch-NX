---
id: W-063
title: A UTF-8 BOM makes pm.py drop a work item entirely: W-055 is invisible to the state
state: done
priority: 80
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: 
blocked_on: 
evidence: parse_frontmatter now strips a leading BOM before the startswith('---') test. Unparsed frontmatter 1 -> 0; W-055 reads back as done, P1, with its evidence; check 51 items 0 problems; status 50 items/37 done -> 51 items/38 done and the blank P99 row is gone. W-055's bytes deliberately left alone; its mojibake Intent recorded, not reconstructed (T-11).
workstream: infra
kind: defect
risk: low
verify:
  - cmd: python -c "import sys;sys.path.insert(0,'tools');import pm;print('unparsed:',len([i for i in pm.load_items(strict=True) if i['_parse_error']]))"
    expect: unparsed: 0
---
## Intent

A work item with a UTF-8 byte-order mark is dropped **silently** by `pm.py`.
Not an error, not a warning: `parse_frontmatter` returned an empty dict, so the
item vanished from the counts, from `check`'s reach, and from `STATE.md`, where
it rendered as a blank `P99` row indistinguishable from an empty placeholder. A
closed piece of work with real evidence simply stopped existing as far as the
project's own source of truth was concerned.

## Reference

Own-design for the tool, but the incident is T-15 and T-11 again: an encoding
assumption silently changing an answer. `build-gate.ps1` and `deploy.ps1` died
because a CJK path decoded wrong; `clean-client`'s Chinese is destroyed at byte
level. Here a byte-order mark turned one closed item into a phantom.

## Approach

1. `tools/pm.py` `parse_frontmatter` — strip a leading `\ufeff` before the
   `startswith("---\n")` test, with the reason in a comment at the site.
2. Do **not** rewrite W-055's bytes. The reader is the defect: a file whose
   frontmatter is correct must be readable, and silently deleting an item because
   of three bytes is a worse failure mode than carrying them.
3. Leave W-055's own mojibake alone. Its `## Intent` line holds a mis-decoded
   Chinese user report; the trailing English sentence carries the same content,
   and T-11 says not to budget time reconstructing destroyed text. Recorded, not
   repaired.

## Acceptance

```
python -c "import sys; sys.path.insert(0,'tools'); import pm; \
           print('unparsed:', len([i for i in pm.load_items() if not pm.field(i,'id')]))"
# EXPECT: unparsed: 0      (was 1)

python -c "import sys; sys.path.insert(0,'tools'); import pm; \
           w=[i for i in pm.load_items() if pm.field(i,'id')=='W-055'][0]; \
           print(pm.field(w,'state'), pm.field(w,'evidence'))"
# EXPECT: done revive-verify.py BAD=0; gate 25 suites RED SUITES: 0

python tools/pm.py check   # EXPECT: 0 problem(s), and W-055 is among the items
python tools/pm.py status  # EXPECT: no blank P99 row; done count rises by one
```

## Result

Fixed, and the phantom is gone.

```
unparsed frontmatter now: 0            (was 1)
W-055 -> done | 1 | A dead character is a permanent dead end: no attack, no skills, no way back
  evidence: revive-verify.py BAD=0; gate 25 suites RED SUITES: 0
check: 51 items, 0 problem(s)
MapleBench — 51 work items, 38 done, 13 open
```

The counts moved by exactly one in the right place: **done 37 → 38**, and the
blank `P99` todo row is replaced by real rows. Before the fix the status screen
claimed 37 done while 38 files on disk said `state: done`, and the difference
rendered as an empty line — which is how a missing item stays missing.

W-055's file still carries its BOM (deliberately — the reader now tolerates it).
`tools/probe/nx/NX-TECHNICAL-ROUTE.md` is the only other BOM in the repository
and is not a work item, so nothing else is affected today.
