---
id: W-068
title: The node area (the editor's daily path) has no judge at all
state: done
priority: 74
owner: 
opened: 2026-10-16
closed: 2026-10-16
depends: []
refs: [pm/SPEC.yaml areas[node]; studio rule 2.7; R-2; R-5; T-10]
blocked_on: 
evidence: node-verify.py, 13 assertions, green 3/3. Asserts promises not incidental properties: /api/children descriptors carry no value key (R-2), a node resolves under any case of its name (T-10), an unknown path is an error rather than a quiet null (R-5), and an edit lands in the session with the file reported dirty by /api/changes (README: edits stay in memory until you save). Opens no archive and saves nothing. The judge caught two bugs in itself on first run: it picked an empty SubProperty (not editable as text, HTTP 400) and mis-read /api/changes twice - first assuming a bare list, then reading id off the nested wrapper.
workstream: archive
kind: audit
risk: high
verify:
  - cmd: python $S/node-verify.py
    expect: 13 assertions, 0 failed
---
## Intent

One paragraph. What changes for the user when this is done.

## Reference

`path:line` — the exact construct being ported. If there is none, say so
explicitly and mark the item own-design.

## Approach

Ordered steps. Each names a file and a symbol.

## Acceptance

```
python %LOCALAPPDATA%\hermes\cache\scratch\node-verify.py
# EXPECT: VERDICT: node-verify 13 assertions, 0 failed

# and it must be able to fail
# EXPECT: removing the R-2 descriptor check makes the suite print ">>> RED"
#   (a judge that cannot report red protects nothing)

# three consecutive runs, because this area had no verdict at all before it
# EXPECT: 13 assertions, 0 failed  x3
```

It opens no archive and saves nothing: every mutation is in memory and is
reverted, which save-audit already proved is safe (W-066).

## Result

Filled in when closed: what actually landed, and the evidence.
