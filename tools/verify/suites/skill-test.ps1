# 6.4-D re-measured.
#
# Two harness bugs in the previous version, both of which faked a result:
#   1. `@($null).Count` is 1 in PowerShell, so "damage=1" was printed for frames
#      whose damage array was actually empty. Every count here goes through
#      Count-Damage(), which filters first.
#   2. the character spawns snapped to a foothold's X1 and the chosen mob was on a
#      different LEVEL of the map (y=-535 vs the body's y=-25), so nothing was ever
#      in range. This version walks the body to the mob before casting.
#
# W-062: THIS SUITE HAD NO ASSERTIONS. Its last four lines ended in
# `<-- MUST be refused`, which is a comment, not a check, so gate.ps1 showed
# "ok" for a suite that could not fail. Everything below that used to print a
# string now goes through Check(), which prints the explicit `>>> RED` marker
# gate.ps1 is the only thing allowed to read (pm/GATE.md:64-83, R-13, T-2).
#
# `-ForceFail` deliberately breaks ONE assertion. It exists so the failure path
# is proven to work rather than assumed: acceptance for W-062 requires the gate
# to go red when the suite says red.
param(
  [int]$Port = 0,
  [string]$Map = "",
  [switch]$ForceFail
)

# How many times section 2 will draw a placement and cast again when the cast
# did not reach the mob it was aimed at.  MEASURED 2026-10-25 over 15 runs on
# two instances: the cast reached the mob on the first attempt in 15 of 15
# runs, so this is the safety net for the placement that does not -- and it is
# NOT a retry of a failing assertion: a cast that damages nothing still fails
# "2.1 a cast produces damage".
$CastAttempts = 3


# The archive's session id is resolved BY NAME, never hard-coded: the ids are
# handed out in the order the archives were opened, so "f4" is Map.nx on one
# instance and something else on another. An archive that is not open is a
# failure that says so -- and a missing helper is a failure that says so too,
# because a suite that aborts without the red marker reads as a PASS.
if (-not (Test-Path "$PSScriptRoot\archivelib.ps1")) { ">>> RED: archivelib.ps1 is missing next to this suite"; exit 1 }
try { . "$PSScriptRoot\archivelib.ps1" }
catch { ">>> RED: archivelib.ps1 next to this suite did not load: $($_.Exception.Message)"; exit 1 }
# Test-Path only catches MISSING. A helper that is present but does not parse
# leaves Get-ArchiveId undefined, and the suite then measures a map it never
# loaded and reports a result about it. Prove the function exists first.
if (-not (Get-Command Get-ArchiveId -ErrorAction SilentlyContinue)) { ">>> RED: archivelib.ps1 did not define Get-ArchiveId, so there is no resolver and nothing to measure against"; exit 1 }
$base = Resolve-GateBase -Port $Port
if (-not $Map) { $Map = "$(Get-ArchiveId -Base $base -Name 'Map.nx')/Map/Map1/100000004.img" }

$script:Last = $null
$script:Asserts = 0
$script:Fails = 0

function Check([string]$name, [bool]$ok, [string]$detail) {
  $script:Asserts++
  if ($ok) { "  ok    $name" }
  else {
    $script:Fails++
    "  FAIL  $name -- $detail"
    ">>> RED: skill-test -- $name ($detail)"
  }
}

function New-Session([int]$x, [int]$y) {
  $b = @{ path = $Map; startX = $x; startY = $y; weapon = "01302000.img"; equippedWeapon = "01302000" } | ConvertTo-Json -Compress
  $s = Invoke-RestMethod "$base/api/simulate/session" -Method Post -Body $b -ContentType "application/json" -TimeoutSec 300
  # W-062. MEASURED: without this the character is DEAD before section 1 casts.
  # The walk from the spawn to the target mob takes ~140 frames sitting inside
  # 2220100's contact box, that mob's info/PADamage is 90, and a fresh character
  # has 50 HP -- so the priming phase killed the subject and every skill
  # assertion after it was measuring a corpse. `CastSkill`'s first gate is
  # `if (session.Status.Dead) return;`, which is CORRECT; the suite was walking
  # into a 90-damage mob and then blaming the skill. This is the same defect as
  # the mob-attack-verify flake, one layer out: the phase that sets the
  # measurement up consumes the thing being measured.
  #
  # Invulnerable suppresses the HP spend only, so the mob still walks and swings
  # -- what changes is that the character survives to be measured.
  Invoke-RestMethod "$base/api/simulate/session/$($s.sessionId)/invulnerable" -Method Post `
    -Body (@{ value = $true } | ConvertTo-Json -Compress) -ContentType "application/json" -TimeoutSec 60 | Out-Null
  return $s
}
function Step($sid, $keys) {
  $b = @{ count = 1; first = $keys; then = $keys } | ConvertTo-Json -Compress
  $script:Last = Invoke-RestMethod "$base/api/simulate/session/$sid/frames" -Method Post -Body $b -ContentType "application/json" -TimeoutSec 300
}
function Count-Damage($frame) { @($frame.damage | Where-Object { $_ }).Count }
function Damage-Values($frame) { (@($frame.damage | Where-Object { $_ } | ForEach-Object { "$($_.mobId)=$($_.value)" }) -join ' ') }

# True once the character is holding the skill pose. Used instead of comparing a
# stance name, so a renamed archive pose cannot make a passing run look broken.
function Is-Casting($frame) { $frame.action -eq 'skill' }

"=== calibration A: idle must not move ==="
$a = New-Session 1980 -25
$xs = @(); for ($i=0;$i -lt 40;$i++) { Step $a.sessionId @{}; $xs += [double]$script:Last.x }
"  x $($xs[0]) -> $($xs[39])  action=$($script:Last.action)  damage on last frame=$(Count-Damage $script:Last)"
# CONTROL. R-9: a known-answer control before the measurements, and the run is
# void if it does not produce the known answer.
Check "control A: 40 idle frames do not move the body" ($xs[0] -eq $xs[39]) "x drifted $($xs[0]) -> $($xs[39])"

"=== calibration B: right walk moves, <=2.5px/frame ==="
$b = New-Session 1710 -25
for ($i=0;$i -lt 60;$i++) { Step $b.sessionId @{ right = $true } }
$mx = 0.0; $prev = $null
for ($i=0;$i -lt 60;$i++) { $x=[double]$script:Last.x; if ($null -ne $prev) { $d=[Math]::Abs($x-$prev); if($d -gt $mx){$mx=$d} }; $prev=$x; Step $b.sessionId @{ right = $true } }
"  max |dx| = $mx  (MaxWalkSpeed is 2.5)"
if ($mx -le 0 -or $mx -gt 2.5) { "ABORT: walking is broken; everything after this is meaningless."; exit 1 }
"  PASS"
Check "control B: walking moves at MaxWalkSpeed" ($true) "calibrated at $mx px/frame"
""

# --- walk the body to a mob on its OWN level -----------------------------------
function Approach([int]$fromX, [int]$y, [double]$targetX) {
  $s = New-Session $fromX $y
  for ($i=0; $i -lt 400; $i++) {
    $k = if ([double]$script:Last.x -lt $targetX) { @{ right = $true } } else { @{ left = $true } }
    Step $s.sessionId $k
    if ([Math]::Abs([double]$script:Last.x - $targetX) -le 6) { break }
  }
  return $s
}
function MobAt($session, [int]$x) {
  @($session.mobs | Where-Object { $_.kind -eq 'm' -and [Math]::Abs([double]$_.x - $x) -lt 3 })[0]
}

# W-065 follow-on. Find a mob by ID and pick the instance NEAREST the body --
# never by "within 3px of where it was three calls ago".
#
# This map carries ELEVEN instances of 2220100 (measured from the live frame:
# x = 2091, 2221, 2767, 2553, 2553, 2681, 2847, 3043, 2006, 2210, 2451, 2435).
# A position-pinned lookup therefore matches an ORDINAL, not an animal: the mob
# walked a few pixels and the filter went empty, `[int]$null` read as 0, and
# "2.2 a MultiStrike=true cast leaves the mob untouched" reported
# `hp 350 -> ` and failed. The same trap was already fixed in knockback-verify.py
# on this map, in the same session, and this suite carried it too.
#
# Nearest-by-id is also the only reading that matches what each assertion is
# actually about: "the cast dealt no damage" is a statement about hp, and a mob
# that walks has still not been damaged.
function Nearest($frame, $mobId) {
  $ms = @($frame.mobs | Where-Object { $_.mobId -eq $mobId })
  if ($ms.Count -eq 0) { return $null }
  return ($ms | Sort-Object { [Math]::Abs([double]$_.x - [double]$frame.x) })[0]
}

# The instance at a PINNED x, so a read after a cast is about the mob the read
# before it was about.  `Nearest` re-resolves "closest to the character" at
# every call, and this map carries ELEVEN instances of 2220100 on FOUR levels
# (the measured list is in the comment above), so that answer can change
# between two reads -- and the instance it can pick on another level is one
# no cast can strike.
function Subject($frame, $mobId, [double]$x) {
  $ms = @($frame.mobs | Where-Object { $_.mobId -eq $mobId })
  if ($ms.Count -eq 0) { return $null }
  return ($ms | Sort-Object { [Math]::Abs([double]$_.x - $x) })[0]
}

"=== 1. a skill that carries a hit count deals that many hits ==="
$seed = New-Session 1710 -25
$mob = @($seed.mobs | Where-Object { $_.kind -eq 'm' -and [Math]::Abs([double]$_.y - (-25)) -lt 3 } | Sort-Object { [Math]::Abs([double]$_.x - 1710) })[0]
"  target mob: $($mob.mobId) x=$($mob.x) y=$($mob.y) hp=$($mob.hp)"
$p1 = Invoke-RestMethod "$base/api/skill-cast/plan?skillId=1121008" -TimeoutSec 300
"  plan: hitCount=$($p1.hitCount) (node '$($p1.hitCountNode)') projectile=$($p1.projectile) actions=$($p1.actions -join ',') collisionBox=$($p1.hasCollisionBox)"
Check "1.0 the premise: the plan carries a hit count for 1121008" ($p1.hitCount -ge 1) "hitCount=$($p1.hitCount) node='$($p1.hitCountNode)'"
Check "1.0b the premise: the plan carries a collision box" ($p1.hasCollisionBox -eq $true) "hasCollisionBox=$($p1.hasCollisionBox)"

$s1 = Approach 1710 -25 ([double]$mob.x - 30)
"  walked to x=$($script:Last.x) y=$($script:Last.y) facing=$($script:Last.facing)"
# The control that would have caught this suite's original defect. R-9: if the
# control does not produce the known answer, the run is void.
Check "1.0c CONTROL: the character survived the approach" (-not $script:Last.status.dead) `
    "status.dead=$($script:Last.status.dead) hp=$($script:Last.status.hp)/$($script:Last.status.maxHp)"
$m1 = Nearest $script:Last $mob.mobId
"  mob hp before = $($m1.hp) (instance at x=$($m1.x))"
Step $s1.sessionId @{ skillId = "1121008" }
$f = $script:Last
$hits1 = Count-Damage $f
"  CAST -> action=$($f.action) skillId='$($f.skillId)' skillStance='$($f.skillStance)' damage=$hits1 [$(Damage-Values $f)]"
Check "1.1 a cast deals damage" ($hits1 -ge 1) "damage entries=$hits1"
Check "1.2 the cast puts the body in the skill pose" (Is-Casting $f) "action='$($f.action)'"

$posFrames = 0; for ($i=0;$i -lt 80;$i++){ Step $s1.sessionId @{}; if (Is-Casting $script:Last) { $posFrames++ } }
# Read the SAME mob the pre-cast read was about: the subject is pinned to
# the x it was measured at, so "the mob lost HP" is a statement about that
# animal and not about whichever instance happened to be closest afterwards.
$m2 = if ($m1) { Subject $script:Last $mob.mobId ([double]$m1.x) } else { Nearest $script:Last $mob.mobId }
"  frames reporting action=skill: $posFrames / 80   (plan totalTime=67)"
# The mob is normally DESTROYED here, not merely damaged: 2220100 has 350 HP and
# a skill removes real damage off it now (W-061). $m2 is then $null, and
# `[int]$null` is 0, which would make "hp went down" true for the wrong reason.
# Absent from the frame's list means DESTROYED -- that is the only way a mob
# leaves `mobs` -- and it counts.
$hpAfter = if ($m2) { $m2.hp } else { 'destroyed' }
"  mob hp after  = $hpAfter"
Check "1.3 the pose lasts more than one frame" ($posFrames -gt 1) "only $posFrames of 80 frames held the pose"
Check "1.4 the mob lost HP, or was destroyed outright" (($m2 -eq $null) -or ([int]$m2.hp -lt [int]$m1.hp)) "hp $($m1.hp) -> $hpAfter"

"=== 2. a cast deals its OWN hit count, not one hit ==="
# CORRECTED 2026-10-18. This section used to read
#
#   "=== 2. MultiStrike = TRUE must deal NO damage ==="
#   Check "2.1 a MultiStrike=true cast deals NO damage" ($hits2 -eq 0)
#   Check "2.2 a MultiStrike=true cast leaves the mob untouched"
#
# which asserted the W-083 DEFECT as if it were the contract. `MultiStrike` was
# being used as a damage gate: 63 of the 66 castable skills dealt nothing and the
# user reported "the skill dealt no damage". W-083 removed the gate, and ADR-0006 fixes every
# landed player hit at 100 -- so the two are not "fewer hits", they are "no hits
# at all", and the second reading is the one the simulator has to pass.
#
# It stayed green for as long as it did for the reason T-21 names: the skill it
# picked, 10001009, was one of the 22 whose level carries `damagepc` instead of
# `damage`, so it dealt zero for a completely unrelated reason and the stale
# assertion could not tell the difference between "this rule says no damage" and
# "this skill's damage node was never being read".
$msId = "10001009"
$pm = Invoke-RestMethod "$base/api/skill-cast/plan?skillId=$msId" -TimeoutSec 300
"  $msId hitCount=$($pm.hitCount) (node '$($pm.hitCountNode)') actions=$($pm.actions -join ',') damage=$($pm.damage) node='$($pm.damageNode)'"
Check "2.0 the premise: the plan carries a hit count for $msId" ($pm.hitCount -ge 1) "hitCount=$($pm.hitCount) node='$($pm.hitCountNode)'"
# The premise the old section never checked, and the one that would have caught
# it: a skill that deals nothing looks identical to a skill whose damage node the
# reader never found.
Check "2.0b the premise: the plan found a damage node for $msId" `
    (-not [string]::IsNullOrEmpty($pm.damageNode)) "damageNode='$($pm.damageNode)' damage=$($pm.damage)"

# THE SUBJECT IS PINNED, AND THE CAST IS RE-DRAWN WHEN IT MISSES IT.
#
# `Nearest` answers "the instance of this mob closest to the character", and
# this map carries ELEVEN instances of 2220100 on FOUR levels (the measured
# list is in the comment above).  Two things can then go wrong in a way that
# reads exactly like a broken rule:
#   * the answer can CHANGE between the read before the cast and the read
#     after it, so the two reads are two different animals;
#   * the instance it picks can be on another level, and no cast can strike a
#     mob on another level -- so the suite prints "hp 1000 -> 1000" for a
#     cast that dealt damage to other mobs.
# MEASURED 2026-10-25 over 15 runs on two instances: the subject was the
# instance at x=2091 on level y=-25 every time, the cast struck it every
# time, and the hit landed on idle frame 1 every time.  The cast is NOT a
# draw; the SUBJECT was the part that could change.
#
# So the subject is pinned to the x it was measured at, and the damage entries
# the cast itself produced are used to say whether the cast reached it.  When
# it did not, the suite RE-DRAWS: walk back, cast again, up to $CastAttempts
# times.  A placement that makes the requirement unmeasurable is measured
# again, not reported as a violation.
$subjectX = $null
$mA = $null
$mB = $null
$hits2 = 0
$struck = @()
$attempts = 0
$reachedCount = 0
while ($attempts -lt $CastAttempts) {
  $attempts++
  $s2 = Approach 1710 -25 ([double]$mob.x - 30)
  $mA = Nearest $script:Last $mob.mobId
  if ($null -eq $mA) { "  attempt $($attempts): no mob of id $($mob.mobId) is in the frame"; continue }
  $subjectX = [double]$mA.x
  "  attempt $($attempts): standing at x=$($script:Last.x); subject x=$subjectX hp=$($mA.hp)"
  Step $s2.sessionId @{ skillId = $msId }
  $f2 = $script:Last
  $hits2 = Count-Damage $f2
  $struck = @($f2.damage | Where-Object { $_ -and $_.mobId -eq $mob.mobId } | ForEach-Object { [double]$_.x })
  "  CAST -> action=$($f2.action) skillStance='$($f2.skillStance)' damage=$hits2   <-- must be >= 1"
  "  the cast struck this mob at x=$(if ($struck.Count -eq 0) { '(none)' } else { ($struck -join ',') })"
  # Wait for the cast on the cast's OWN clock, not on a guessed frame count.
  # MEASURED: this skill's plan says totalTime=57 and the hit landed on idle
  # frame 1, so the old 20-frame window was long enough -- but a window that
  # is right by luck is not a measurement.
  $wait = [Math]::Max(20, [int]$pm.totalTime)
  for ($i=0;$i -lt $wait;$i++){ Step $s2.sessionId @{} }
  $mB = Subject $script:Last $mob.mobId $subjectX
  $reachedCount = @($struck | Where-Object { [Math]::Abs($_ - $subjectX) -le 8 }).Count
  if ($reachedCount -ge 1) { break }
  "  attempt $($attempts): the cast did not reach the subject (struck x=$(if ($struck.Count -eq 0) { '(none)' } else { ($struck -join ',') }), subject x=$subjectX) -- drawing again"
}
$hpB = if ($mB) { $mB.hp } else { 'destroyed' }
"  mob hp after  = $hpB"
# At least one, not exactly one: 10001009 carries `mobCount: 30`, so a cast whose
# box covers several mobs legitimately produces one damage-number entry per struck
# mob (measured 6 on this map). The COUNT is a statement about where the box landed,
# not about the damage rule; 2.2 is the assertion about the rule.
Check "2.1 a cast produces damage" ($hits2 -ge 1) "damage entries=$hits2"
Check "2.2 a cast takes the mob's hitCount x 100 HP" `
    (($mB -eq $null) -or ([int]$mB.hp -eq [int]$mA.hp - (100 * [int]$pm.hitCount))) "hp $($mA.hp) -> $hpB; expected -$((100 * [int]$pm.hitCount)) for hitCount=$($pm.hitCount)"
# The named placement failure.  It fires only when the cast struck mobs and
# none of them is the mob the suite measures: then the report says the cast
# missed this mob, instead of leaving "hp 1000 -> 1000" to be read as "the
# rule is broken".
$struckText = if ($struck.Count -eq 0) { '(no mob of this id)' } else { ($struck -join ',') }
$subjectText = if ($null -eq $subjectX) { '(no subject was found)' } else { "$subjectX" }
Check "2.3 the cast reached the mob the suite measures" ($reachedCount -ge 1) `
    "the cast struck x=$struckText and the pinned subject is at x=$subjectText after $attempts placement(s)"

"=== 3. the cast LOCK: a second cast inside the pose is refused ==="
$s3 = Approach 1710 -25 ([double]$mob.x - 30)
Step $s3.sessionId @{ skillId = "1121008" }
$d1 = Count-Damage $script:Last
"  first  cast: damage=$d1 action=$($script:Last.action)"
Step $s3.sessionId @{ skillId = "1121008" }
$d2 = Count-Damage $script:Last
"  second cast (1 frame later): damage=$d2 action=$($script:Last.action)"
Check "3.1 the first cast of a pair deals damage" ($d1 -ge 1) "damage entries=$d1"
# Skill.cs:321 -- `PlayEnded` refuses the second cast. The pose is still running,
# so the second cast must reach nothing at all.
Check "3.2 a second cast inside the pose is refused" ($d2 -eq 0) "damage entries=$d2"
Check "3.3 the pose is still running after the refused cast" (Is-Casting $script:Last) "action='$($script:Last.action)'"

"=== 4. guards ==="
$s4 = New-Session 1710 -25
Step $s4.sessionId @{ skillId = "99999999" }
"  unknown skill  -> action=$($script:Last.action) skillStance='$($script:Last.skillStance)' damage=$(Count-Damage $script:Last)"
Check "4.1 an unknown skill id is refused" ((-not (Is-Casting $script:Last)) -and (Count-Damage $script:Last) -eq 0) "action='$($script:Last.action)' damage=$(Count-Damage $script:Last)"

Step $s4.sessionId @{ skillId = "" }
"  empty skill id -> action=$($script:Last.action) skillStance='$($script:Last.skillStance)' damage=$(Count-Damage $script:Last)"
Check "4.2 an empty skill id is refused" ((-not (Is-Casting $script:Last)) -and (Count-Damage $script:Last) -eq 0) "action='$($script:Last.action)' damage=$(Count-Damage $script:Last)"

# climb first, THEN cast on the very same frame the body is still on the ladder
$s5 = New-Session 1936 -265
for ($i=0;$i -lt 5;$i++){ Step $s5.sessionId @{ up = $true } }
"  on ladder: onLadder=$($script:Last.onLadder) action=$($script:Last.action)"
$onLadder = $script:Last.onLadder
Step $s5.sessionId @{ up = $true; skillId = "1121008" }
"  cast while ON the ladder -> action=$($script:Last.action) skillStance='$($script:Last.skillStance)' damage=$(Count-Damage $script:Last)"
Check "4.3 the body really was on a ladder for this guard" ($onLadder -ne $null -and $onLadder -ne $false) "onLadder=$onLadder"
Check "4.4 a cast while on a ladder is refused" ((-not (Is-Casting $script:Last)) -and (Count-Damage $script:Last) -eq 0) "action='$($script:Last.action)' damage=$(Count-Damage $script:Last)"

# ---- W-062: prove the failure path actually reports red ---------------------
if ($ForceFail) {
  "  (--ForceFail: deliberately failing one assertion to prove the marker works)"
  Check "zz self-test: this assertion is false on purpose" $false "forced"
}

# `VERDICT:` is one of the five patterns gate.ps1 greps for, so this line is what
# the gate shows instead of "(read the tail)". Without it the block is reported
# but never judged -- the defect W-057 was opened for.
"VERDICT: skill-test $($script:Asserts) assertions, $($script:Fails) failed"
if ($script:Fails -gt 0) { exit 1 }
exit 0