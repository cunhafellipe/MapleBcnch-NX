# RULES — numbered, citable, and few enough to hold in your head.

The studio rules (`工作室守則.md` at the repo root) are the authority and are not
restated here. This file is the *operational* subset: the rules that, when
broken, have actually cost this project a wrong answer.

Format: **R-n** — the rule. `▸ why` — the instance that made it a rule.

---

## References and evidence

**R-1 — A citation is a construct plus its caller.**
Cite `file:line` for the formula *and* read the function that calls it.
▸ 2026-10-15: a whole architecture was ported with a citation on every line
(`touch damage`) and was wrong, because the caller proved it had no aggro step.
Every line cited, whole structure wrong.

**R-2 — Measure before changing, and measure with `/api/inspect`.**
`/api/children` descriptors carry no `value`. PowerShell flattens large arrays;
use Python for anything that reads an archive.
▸ Six wrong-node measurements in this project's history. The most expensive:
`info/wdef`, which **does not exist** on this archive at all — the node is
`info/PDDamage`, present on 342 of 343 mobs — and that wrong name is why the
defence term had been dropped from the damage roll as "data absent".

**R-3 — A class existing proves nothing. Count `new X(` call sites.**
▸ The C# reference's `AfrmItem` (1116 lines), `AfrmStat` (1022), `AfrmEquip`
(572) and `CharaSim.Character` (919) have **zero** construction sites. A gap
analysis listed their inventory and EXP systems as missing features.

**R-4 — When a reference has code and we do not, check the DATA before the code.**
▸ `Mob.TooltipRenderer` draws an attribute-EXP block. The archive carries
`charismaEXP senseEXP insightEXP willEXP craftEXP charmEXP` on **0 of 343**
sampled mobs. That is a data gap, not a feature.

**R-5 — Report absent as null, never as zero.**
`exp` is missing on ~1 mob in 45; `category` on 96%. A printed 0 is a
statement the archive does not make. `DefaultIgnoreCondition = WhenWritingNull`
is global, so the client must treat a missing key as null.

---

## Scope and honesty

**R-6 — Own-design must be declared in code, at the site, with the reason.**
Not in the PR description, not only in the journal.
▸ `PlayerStatus.Invulnerable`, `AttackCooldown = 60`, `ExpToNextLevel`,
`MoveSpeed` caps. Each says what it is and why, next to the code that does it.

**R-7 — Do not widen scope without a work item.**
If a fix reveals a second defect, open one. Fixing both in one change makes the
gate ambiguous: when the gate goes red you will not know which change did it.

**R-8 — Never delete or degrade a measurement to make a gate pass.**
▸ The gate suite `attack-test.ps1` reported *"damage entries over 8 swings: 8"*
for eight swings that hit nothing, for an unknown number of runs. Fixing the
suite is mandatory; weakening the product to satisfy the broken suite is not.

---

## Measurement

**R-9 — Calibrate the measuring tool before trusting it.**
Every harness prints a known-answer control before its measurements. If the
control does not produce the known answer, the run is void.
▸ Six self-deceptions in one session alone. See `TRAPS.md` for the catalogue.

**R-10 — One-shot events need their priming frames.**
A hit that resolves 15–20 frames after the key goes down is not observable in
the frame the key went down, and re-triggering resets the countdown.
▸ "8 swings, 0 hits" was really "8 swings, 0 *frames past the hit*".

**R-11 — Select targets in two dimensions.**
Choosing the nearest mob by `x` alone picks one on a ledge 510px above you.
▸ Recorded in `attack-test.ps1`, `nomove-acquire.py`, and a mob-sweep: three
separate false "swing is broken" alarms.

**R-12 — A count from PowerShell must filter nulls.**
`@($null).Count == 1`.
▸ This is why the attack gate was green for a product that could not hit
anything.

---

## Build and gate

**R-13 — The gate is `python tools/pm.py gate`, and it judges only explicit markers.**
▸ Three attempts at inferring red from prose each produced a false result.
`$LASTEXITCODE` is not set by a PowerShell script, and `\d+ failed` matches the
`0 failed` in `passed=5 failed=0`.

**R-14 — Stop the server before building.**
It holds `MapleBenchX.dll`; the build retries 10× then fails with MSB3027, which
reads like a code error and is not one.

**R-15 — Never write a source file with `Set-Content` or `Get-Content`.**
They mangle CJK and have broken C# braces twice here.
▸ Twice: invalid UTF-8 on three CJK characters in `MapSimSessionStore.cs`, and a
60-error compile from a doc comment whose opening brace was eaten.
Use the `edit` tool.

**R-16 — Never type a CJK path into a script file.**
A `.ps1` saved UTF-8-without-BOM is read in the ANSI codepage, and `wz工具`
becomes `wz撌亙आ`. Discover the path with a glob instead.
▸ Two scripts died on `Push-Location` against a path that plainly existed.

**R-17 — Three-way md5 before any claim: source, build output, and served.**
`avatar.js` must stay `a1eb2eb47af42e07ee048762c2592456`. It is the regression
tripwire for a file this project does not otherwise touch.

---

## Delivery

**R-18 — Screenshots only. Never synthetic frames, never DOM reads as proof.**
The user sees the pixels; you see JSON. Neither is evidence for the other.

**R-19 — Open the editor once after each complete change.**
Not before, not on partial work. Three conditions: deployed, gate green, md5
identical.

**R-20 — The work item is closed with evidence, or it is not closed.**
`tools/pm.py set <id> state=done` refuses without `evidence=`. An item whose
acceptance still says TODO cannot close.