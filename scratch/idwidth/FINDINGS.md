# MapleBench — archive-key id-width audit

Run 2026-10-16. Server `http://127.0.0.1:5591` (already running, never touched).
File ids re-derived from `GET /api/files` at the start of the run, not copied:

```
f1 Character.nx  f2 Base.nx    f3 String.nx  f4 Map.nx     f5 Mob.nx   f6 Npc.nx
f7 Item.nx      f8 Skill.nx   f9 Quest.nx   f10 Effect.nx f11 Etc.nx  f12 Morph.nx
f13 Reactor.nx  f14 Sound.nx  f15 TamingMob.nx f16 UI.nx
```

Every count below is a **100 % census** of the named container via `/api/children`,
or an explicit `/api/inspect` per spelling. No sampling except where a sample size is
stated. An absent node returns **HTTP 404** (measured), not a 200 with `node: null` —
scripts treat 404 as absent.

## Measured archive facts (this run)

| pool | container | n | key widths |
|---|---|---|---|
| mob image | `f5` root `*.img` | 2053 | 7 = 2053 (100 %) |
| npc image | `f6` root `*.img` | 7198 | 7 = 7198 (100 %) |
| equip image | `f1` root + 16 slot folders `*.img` | 7176 | 8 = 7176 (100 %) |
| map image | `f4/Map/Map<d>/*.img` | 5366 | 9 = 5366 (100 %) |
| item group img | `f7/{Cash,Consume,Etc,Install}` | 100 | 4 = 100 (100 %) |
| item group img | `f7/Pet` | 54 | 7 = 54 (100 %) |
| item group img | `f7/Special/{0900,0910,0911}` | 3 | 4 = 3 (100 %) |
| item entry key | `f7/{Cash,Consume,Etc,Install}` | 5446 | 8 = 5446 (100 %) |
| item entry key | `f7/Special/0900.img` | 4 | 8 = 4 (100 %) |
| **item entry key** | **`f7/Special/{0910,0911}.img`** | **543** | **7 = 543 (100 %)** |
| item entry key | `f7/Special/MaplePoint.img` | 37 | 3/4/5/6 (not item ids) |
| book entry key | `f3/MonsterBook.img` | 344 | 6 = 11, 7 = 333 |
| skill book img | `f8` root `*.img` | 76 | 3 = 45, 4 = 26, 7 = 2, 8 = 1, 9 = 1, 10 = 1 |
| skill entry key | `f8/<book>.img/skill` | 535 | 7 = 353, 8 = 182 (25 of the 353 are zero-padded to 7) |
| string pool | `f3/Mob.img` | 1735 | 6 = 16, 7 = 1719 |
| string pool | `f3/Npc.img` | 7123 | 4 = 14, 5 = 17, 7 = 7092 |
| string pool | `f3/Skill.img` | 613 | 3 = 45, 4 = 26, 7 = 360, 8 = 182 |
| string pool | `f3/Map.img` (12 regions) | 4906 | 1 = 5, 5 = 12, 7 = 20, 9 = 4869 |
| string/sound pools | 24 pools incl. `f14/Mob.img` 1257, `f11/NpcLocation.img` 1695, `f14/Reactor.img` 238 | — | **max key width observed anywhere = 8** |

NPC note that contradicts the brief: the NPC **id** is four-digit (2000, 9101002) but the
NPC **image name** is seven-digit zero-padded — `f6` answers `0002000.img`, `0010000.img`
(measured, 7198/7198 seven-digit). D7 is therefore the correct single width for NPC images.

## Site table

| file:line | id class | widths the code tries | widths measured present | verdict |
|---|---|---|---|---|
| `Services\Simulate\CombatService.cs:1852` `ImageNames(string)` | mob, npc | D7, plain | 7 (mob 2053/2053, npc 7198/7198) | OK |
| `Services\Simulate\CombatService.cs:1746` `TryMobFrameOrigin` (inline) | mob | D7, plain | 7 | OK |
| `Services\Simulate\CombatService.cs:165` `AfterimageName` | equip | verbatim, D8, D7, plain | 8 | OK |
| `Services\Simulate\CombatService.cs:349` `AttackStances` | equip | verbatim, D8, D7, plain | 8 | OK |
| `Services\Simulate\CombatService.cs:948` `WeaponAttackSpeed` | equip | raw, D7, `"0"+D7`, plain | 8 | OK |
| `Services\Simulate\CombatService.cs:1881` `StringIds(string)` | npc (string pool) | verbatim, plain | 4/5/7 | OK |
| `Services\Simulate\EquipStatsService.cs:249` `ImageNames(int,string)` | equip | asGiven, D8, D7, plain | 8 | OK |
| `Services\Simulate\MobBestiaryService.cs:368` `TryMobImage` | mob | D7, plain | 7 | OK |
| `Services\Simulate\MobBestiaryService.cs:281` `ReadBook` | book | D7, plain | 6, 7 | OK |
| `Services\Simulate\NpcChatService.cs:184` `ReadNpcString` | npc (string pool) | D7, plain | 4/5/7 | OK |
| `Services\Simulate\NpcChatService.cs:201` `TryNpcImage` | npc | D7, plain | 7 | OK |
| `Services\MapEditor\MobSpriteService.cs:346` `TryMobImage` | mob | D7, plain | 7 | OK |
| `Services\MapEditor\MapEditorService.cs:2299` `FindLifeImage` | mob, npc | verbatim+`.img`, D7+`.img` | 7 | OK |
| `Services\MapEditor\MapEditorService.Palette.cs:471` `LifePalette` | mob, npc | **D7 only** | 7 | OK |
| `Services\MapEditor\MapEditorService.cs:218` `MatchRank` | map | D9 (display rank only — no lookup) | 9 | OK (not a key site) |
| `Services\MapEditor\MapEditorService.Create.cs:30` `CreateMap` | map | PadLeft(9), guarded to 9 digits at :23 | 9 | OK |
| `Services\MapEditor\MapEditorService.Minimap.cs:119` `FindMinimapSharers` | map | PadLeft(9), compared against real image paths | 9 | OK |
| `Services\Simulate\MapSimSessionStore.cs:1638` `TakePortal` | map | `:000000000` | 9 | OK |
| `Services\Look\EquipSlotTable.cs:70` `Accepts` | equip (prefix classify) | D8 | 8 | OK |
| `Services\Look\LookService.cs:358` `ResolveImage` | equip | verbatim, `+.img`, `TrimStart('0')+.img`, plus a folder scan at :364 | 8 | OK |
| **`Services\IconService.cs:127`** `ReadIcon(grouped, itemId.ToString("D8"))` | item | **D8 only** | **7 and 8** | **MISSES-A-REAL-WIDTH** |
| `Services\IconService.cs:125` `groupImage` | item | D4 group | 4 (100/100 non-pet) | OK |
| `Services\IconService.cs:119` pet branch | item (pet) | D7, plain | 7 (54/54) | OK |
| `Services\IconService.cs:111` equip branch | equip | D8 | 8 (7176/7176) | OK |
| **`Services\ItemCategories.cs:146`** `(itemId / 10000).ToString("D4")` | item (pet) | **D4 only** | **7** | **MISSES-A-REAL-WIDTH** (no in-repo caller) |
| `Services\ItemCategories.cs:145` | equip | D8 | 8 | OK |
| **`wwwroot\js\database.js:96`** item candidate | item | **D4 group + D8 entry** | **7 and 8** | **MISSES-A-REAL-WIDTH** |
| **`wwwroot\js\database.js:84-87`** skill candidate | skill | **book plain / D4; entry unpadded** | **book 3 and 4; entry 7 and 8** | **MISSES-A-REAL-WIDTH** |
| `wwwroot\js\database.js:76` mob/npc candidate | mob, npc | D7, plain, D8 | 7 | OK |
| `wwwroot\js\database.js:94` equip candidate | equip | D8 | 8 | OK |
| `wwwroot\js\database.js:95` pet candidate | item (pet) | D7, plain | 7 | OK |
| `wwwroot\js\walkview.js:252-254` slot classify | equip | padStart(8) | 8 | OK |
| `wwwroot\js\mapedit.js:765`, `wwwroot\js\simulator.js:491` | map | padStart(9), display text only | 9 | OK (not key sites) |
| `Services\StringEditService.cs:1186-1196` `FindById` | all string pools | plain + every width ≤ 10 | max width measured = 8 | OK |
| `Services\PortSatellites.cs:519-521` `Locate` | satellite images | plain + every width ≤ 10 | max width measured = 8 | OK |

**DOCUMENTED-AS-NOT: zero sites.** No site in the repo carries a comment that
deliberately declines a width and states why. The two helpers that come closest
(`StringEditService.FindById`, `PortSatellites.Locate`) do the opposite and probe every
width up to 10, which is exhaustive against the measured maximum of 8.

## MISSES-A-REAL-WIDTH — evidence

### 1. `Services\IconService.cs:127` — item entry key, D8 only

**Measured.** Census of every numeric entry key under every `f7` group image:

```
f7/Consume   entry keys=2293  8-digit=2293  (100 %)
f7/Cash      entry keys= 484  8-digit= 484  (100 %)
f7/Etc       entry keys=2375  8-digit=2375  (100 %)
f7/Install   entry keys= 294  8-digit= 294  (100 %)
f7/Special   entry keys= 584  8-digit=   4  (0.7 %)
   0900.img           4 keys  all 8-digit   -> D8 finds 4
   0910.img         538 keys  all 7-digit   -> D8 finds 0
   0911.img           5 keys  all 7-digit   -> D8 finds 0
   MaplePoint.img    37 keys  3/4/5/6-digit (a points table, not item ids)
```
`D8 finds 4 of 584` in `f7/Special`. **543 real item ids are missed.**

Literal API calls:
```
GET /api/children?path=f7/Special/0910.img          -> 538 children, every name 7 chars
GET /api/inspect?path=f7/Special/0910.img/9100000  -> 200, children ['icon','name']
GET /api/inspect?path=f7/Special/0910.img/09100000  -> 404   <- what the code asks for
GET /api/inspect?path=f7/Special/0910.img/9101599  -> 200, icon present
```

**Reachable.** `Api\Endpoints.cs:266-272` → `GET /api/cash/icon/{itemId}` →
`IconService.GetIconPng` (`IconService.cs:51`) → `FindIconProperty` (`:104`) → `:127`.
Sampled 60 of the 8947 rows in `f11/Commodity.img` (random, seed 7), read their `ItemId`:
all 60 ids are 7-digit, and **8 of the 60 fall in the 910xxxx/911xxxx range**
(9101599, 9101647, 9101660, 9101925, 9102065, 9102148, 9102162, 9110000). For each of
5 tested: group `f7/Special/0910.img` exists = True, D8 entry = **False**, D7 entry = **True**.
The item's `icon` is a direct child (`['icon','name']`), and `ReadIcon` falls back to
`properties.FindByName("icon")` at `IconService.cs:148`, so the D7 spelling resolves the
icon end to end. With D8 the endpoint answers HTTP 404.

**Reference.** The client itself branches on width, and branches to the *unpadded* form
for exactly this range:
- `C:\MUWORK\GAME\MPSH\clean-client\src\IO\UITypes\World\UICashShop.cpp:1111`
  `return nl::nx::item["Special"]["0910.img"][std::to_string(commodity.itemid)]["icon"];`
  — inside `UICashShop::commodity_icon` (`:1108`), the cash-shop icon for a package.
- `C:\MUWORK\GAME\_necrocer_src\MapleNecrocer\CharaSimControl\ItemTooltipRender2.cs:78`
  `string.Format(@"Item\Special\{0:D4}.img\{1}", this.item.ItemID / 10000, this.item.ItemID)`
  — `{1}` with no width: unpadded entry key. Its D4 group and MapleBench's `:125` agree.
- Contrast `C:\MUWORK\GAME\MPSH\clean-client\src\Gameplay\MapleMap\MapDrops.cpp:40-45`,
  which reads the 8-digit group with literal 8-digit keys `["09000000"]`…`["09000003"]` —
  matching the measurement that `0900.img` alone is 8-digit.

**So the fix is to try both, not to swap D8 out:** D8 is right for the 4 entries in
`0900.img` and for all 5446 entries in the ordinary groups; plain/D7 is right for the 543
in `0910.img`/`0911.img`. `IconService.cs:127` → `IconService.cs:125-127`.

### 2. `wwwroot\js\database.js:96` — the same D8 entry key, second implementation

`return [{ image: \`${pad(Math.floor(id / 10000), 4)}.img\`, children: [pad(id, 8)] }];`

Same measurement, same 4-of-584 result (`IconService.cs:127` above). Reached through
`derivePath` (`database.js:592-626`), which probes `api.node(target)` at `:616` and falls
back to the bare `.img` at `:623` when the entry is absent — so this degrades to the group
image instead of failing, but the exact entry is never reached for those 543 ids.
`database.js:96` is the line to change; the D4 group at the same line is correct
(measured: `0900`/`0910`/`0911` are 4-digit, 3/3).

### 3. `wwwroot\js\database.js:84-87` — skill book and skill entry key

```
const book = Math.floor(id / 10000);
return [
  { image: `${book}.img`, children: [`skill/${id}`] },
  { image: `${pad(book, 4)}.img`, children: [`skill/${id}`] },
];
```

**Measured.** Census of `f8/<book>.img/skill`: **535 entries**, widths 7 = 353, 8 = 182,
of which **25 are zero-padded to seven** (all in `f8/000.img`: `0000008`, `0000012`,
`0000100`, `0001000`…`0001031`, `0009000`…`0009002`). Replaying both candidates against all
535:

```
[database.js] full path resolves            : 510 / 535
[database.js] .img itself not found         :  25 / 535   (all tried '0.img' / '0000.img')
[C++ spelling]   full path resolves         : 535 / 535
```

Both halves fail for the same 25, verified separately:
```
f8/000.img/skill/0000008   book tried '0.img' / '0000.img' -> both 404;  '000.img' -> 200
                           entry '8'      -> 404 ;  entry '0000008' -> 200
```
The archive's book stems are **3-digit** (45 of 76: `000`, `100`, `110`, `111`, …) and
4-digit (26 of 76: `1000`, `1100`, `1110`, …); the code never tries a 3-wide book.

**Reference — the client branches on width exactly here.**
`C:\MUWORK\GAME\MPSH\clean-client\src\Character\Buffs\PassiveBuffs.cpp:229-238`
```cpp
std::string strid;
if (skill_id < 10000000)
    strid = string_format::extend_id(skill_id, 7);
else
    strid = std::to_string(skill_id);
// Cygnus/Aran use four-digit books (e.g. 1511.img, 2111.img).
const auto book = string_format::extend_id(skill_id / 10000, 3);
nl::node src = nl::nx::skill[book + ".img"]["skill"][strid]["level"][skill_level];
```
i.e. **D7 below 10^7, unpadded at/above; book 3 wide** — which reproduces the measured
353/182 split exactly and resolves 535/535. Same file family: `Data\SkillData.cpp:33-35`
(`extend_id(id, 7)`, book = `strid.substr(0,3)`); the same `substr(0,3)` book appears at
`Character\Player.cpp:340`, `Character\OtherChar.cpp:223`, `Gameplay\Combat\Combat.cpp:264`.
`database.js:84-87` → add the 3-wide book and the `id < 10^7 ? pad(id,7) : String(id)` entry.

### 4. `Services\ItemCategories.cs:146` — item group image, D4 for pets

`return (itemId / 10000).ToString("D4") + ".img";` — for a pet id 5000000 this returns
`"0500.img"`. Measured: **0 of the 155 images in `f7` is named `0500.img`**; the 54 pet
images are all 7-digit per-pet files (`f7/Pet/5000000.img` … `5000102.img`, measured
exists, 54/54 carry `info/icon`). Stem widths across all of `f7`: 4 = 100, 7 = 54, 10 = 1.
So for all 54 pets this names an image the archive does not have; the archive's spelling is
`itemId.ToString("D7") + ".img"`.

**Reachability: none measured.** A repo-wide grep for `DataImageName` returns exactly one
hit — the definition at `ItemCategories.cs:142`. No caller in `MapleBench`, `MapleMcp`,
`tools`, `scripts` or the JS. `IconService.cs:117-121` already carries the correct pet
branch, so this looks like the stale copy of it rather than a live path. Reported because
it is a real width error in shipped code, with reachability stated as measured.

## Adjacent observation, NOT a width defect

`Services\Simulate\MapSimSessionStore.cs:1638`
`$"{session.Archive}/Map/Map{portal.ToMap % 100}/{portal.ToMap:000000000}.img"` — the 9-digit
width is correct (measured 5366/5366 map images are 9-digit), but the **folder** is built
with `ToMap % 100`, which for a 9-digit id like 910023000 yields `Map0`, while the archive
and the client both use `Map<first digit>` (`Map9`, per the path dialect documented at
`MapEditorService.Minimap.cs:111-114`). Out of scope for this audit; recorded so it is not
mistaken for a width result.

## Excluded, as instructed

- `CombatService.NpcText` / `StringIds` — the already-fixed defect. Not re-reported;
  `StringIds` is listed above only to record that its two spellings cover the measured
  `f3/Npc.img` widths 4/5/7 (verified: `2000`, `2001`, `10000`, `9101002` all resolve by plain).
- Archive typos `PADamge` / `MADamge` / `comMP` — not re-walked.
- `MobService.cs` case-collapsing `present` dictionary — not re-walked.

## Scripts (all in `scratch\idwidth\`)

| script | what it measures |
|---|---|
| `nx.py` | API client. 404 ⇒ absent; `/api/children` = descriptors, `/api/inspect` = values |
| `a00_files.py` | re-derives f1..f16 from `GET /api/files` |
| `a01_probe.py` | API shape probe + top-level folders |
| `m01_images.py` | mob / npc / map / character / item image-name width histograms |
| `m02_special.py` | `f7/Special`, `f7/Pet`, `f1/Afterimage`, `f1` root |
| `m03_itementry.py` | `f7/Special` entry-key widths, D8-vs-D7 per id |
| `m04_strings.py` | `f3` pool key widths incl. regioned `Map.img` |
| `m05_satellites.py` | satellite pool widths; max observed key width |
| `m06_cash.py`, `m07_cashids.py` | `f11/Commodity.img` shape + 60-row item-id sample |
| `m08_confirm.py` | `0500.img` absence, Special icon node, short pool keys |
| `m09_census.py` | per-id resolution rate: mob / npc / book / item groups |
| `m10_skill.py` … `m15_skillsecond.py` | skill book + entry widths, candidate replay, C++ replay |
| `m01…m15 *.json` | raw results |

`m14_skillresolve2.py` supersedes a first attempt that dropped the `.img` suffix when
building the probe path; the deleted attempt reported "0 / 535 resolved", which was the
script's bug and not a measurement. Every number quoted above comes from `m14`.