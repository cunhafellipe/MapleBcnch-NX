---
id: W-059
title: The cash-shop icon endpoint answers 404 for every item, including ones the old code found
state: todo
priority: 65
owner: 
opened: 2026-10-16
closed: 
depends: []
refs: [Endpoints.cs:266, IconService.cs:51, IconService.cs:178]
blocked_on: 
evidence: 
---

## Intent

`GET /api/cashshop/icon/{itemId}` returns **404 for every id tested**, while the
archive demonstrably carries the icons underneath those ids. An inventory grid
that shows a placeholder for every item is indistinguishable from one with no
icons at all.

## Reference

Own-design investigation — no client behaviour is being ported here yet.

## Approach

Start at `GetIconPng` (`IconService.cs:51`). It wraps the whole lookup in
`try { … } catch (Exception ex) { _log.LogDebug(ex, "Icon lookup failed for
{ItemId}", itemId); }` and returns null, so **any** throw inside becomes a 404
with the cause at Debug level. That is the first thing to change: the catch is
why this is invisible rather than loud.

Then bisect the lookup, which is four steps:

1. `CandidateArchives("Item")` — does it actually return `f7 Item.nx`?
   `GET /api/cashshop/capabilities` answers `{"icons":true}`, but `icons` is
   `HasIconSource`, not "this lookup works".
2. `RoleRoot(file, "Item")` — is the returned directory the Item.nx root?
3. `FindImage(root, "0200.img")` — the recursive walk at `IconService.cs:267-280`
   iterates `directory.WzDirectories`. Does `Special/` appear there, or is it held
   some other way in NX?
4. `ReadIcon` / `Encode` — the node is a `Canvas`, so either `FindByName` misses
   it or `Encode` throws on it.

## Acceptance

```
# a known-good, ordinary 8-digit item -- the one the OLD code targeted, so a
# failure here cannot be blamed on W-041's width change
GET /api/cashshop/icon/2000000          # => 200, PNG bytes > 0

# the archive underneath it, which is present:
GET /api/inspect?path=f7/Consume/0200.img/02000000/info/icon   # => 200, Canvas
```

## Result

**MEASURED while closing `W-041`, not yet diagnosed.**

| id | archive node under it | endpoint |
|---|---|---|
| 2000000 | `f7/Consume/0200.img/02000000/info/icon` → 200, **Canvas** | **404** |
| 2040000 | — | **404** |
| 2270000 | — | **404** |
| 1000000 (equip, D8) | — | **404** |
| 9100000 | `f7/Special/0910.img/9100000/icon` → 200, **Canvas** | **404** |

`GET /api/cashshop/capabilities` → `{"icons":true,"names":true,…}`, so the
service believes it has an icon source.

**Why this is NOT caused by `W-041`.** That change made `FindIconProperty` try
`itemId.ToString("D8")` **first**, then the bare form — the D8 attempt is
byte-for-byte the single call the old code made. So for any id whose correct
spelling is D8 (`2000000`, all of `Consume`/`Etc`/`Cash`/`Install`, all equips)
the new code does exactly what the old one did and still 404s. A superset of the
old key set cannot turn a working lookup off.

Stated as a proof from the code rather than as a re-measurement of the old
binary: rebuilding the pre-change DLL to watch the same 404 would confirm it a
second way, and that is the first thing to do if anyone disputes this.

**Two measurement mistakes are on the record here, both mine, both caught:**

- The first 404 was read as "the width fix does not work". The route is
  `/api/cashshop/icon/…`, not `/api/cash/icon/…`; the 404 was a route miss.
- After the route was corrected, all ids still 404'd, which is what established
  that this is broad rather than Special-specific. Reading only the `910xxxx`
  ids would have produced a confident and completely wrong conclusion that the
  fix failed.

**Not claimed:** the cause. It is somewhere in the four steps above, and
`GetIconPng`'s catch-all is why the run gives no clue which.