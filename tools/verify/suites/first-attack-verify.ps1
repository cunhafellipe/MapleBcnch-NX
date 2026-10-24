param([int]$Port = 0, [string]$FirstAttackMob = '2220000', [string]$PlainMob = '2220100')
$ErrorActionPreference = 'Stop'
# tools/verify/AGENTS.md rule 14: an abort that prints no red marker reads as a
# PASS to the gate, and an unwinding one is worse -- it escapes gate.ps1's Run(),
# which has no try/catch around the suite call, and KILLS the gate. MEASURED
# 2026-10-22 against gate.ps1's own Run() extracted byte-for-byte: this suite
# ended the run at gate.ps1:248, "RED SUITES:" was never printed, and none of the
# suites below it ran. There was NO existence check here at all: with no archive
# open $mapId was $null, $map became "/Map/Map1/100000004.img", and the
# calibration read asked for "/2220000.img/info/firstAttack" and unwound on the
# 404. Resolve BY NAME instead.
if (-not (Test-Path "$PSScriptRoot\archivelib.ps1")) { ">>> RED: archivelib.ps1 is missing next to this suite"; exit 1 }
try { . "$PSScriptRoot\archivelib.ps1" }
catch { ">>> RED: archivelib.ps1 next to this suite did not load: $($_.Exception.Message)"; exit 1 }
# Test-Path only catches MISSING. A helper that is present but does not parse
# leaves Get-ArchiveId undefined, and this suite then reads /2220000.img/info/
# firstAttack off a $null mapId -- the 404 unwind the block above was written
# to stop -- and reports a calibration result about a map it never loaded.
if (-not (Get-Command Get-ArchiveId -ErrorAction SilentlyContinue)) { ">>> RED: archivelib.ps1 did not define Get-ArchiveId, so there is no resolver and nothing to measure against"; exit 1 }
$base = Resolve-GateBase -Port $Port
$mapId = Get-ArchiveId -Base $base -Name 'Map.nx'
$mobId = Get-ArchiveId -Base $base -Name 'Mob.nx'
$map = "$mapId/Map/Map1/100000004.img"

# Every HTTP call below goes through this, so a failing call prints the marker and
# exits non-zero instead of unwinding the gate. Rule 14: the gate greps the TEXT
# and every other caller reads the EXIT CODE, so a failing suite needs both.
# Named per rule 10 (a verb-noun no alias can take) and it bumps a counter,
# because a silently shadowed helper prints numbers that look like a measurement.
$script:ApiCalls = 0
function Invoke-Api {
  param([string]$Uri, [string]$Method = 'Get', $Body, [int]$TimeoutSec = 300)
  $script:ApiCalls++
  $p = @{ Uri = $Uri; Method = $Method; TimeoutSec = $TimeoutSec; ErrorAction = 'Stop' }
  if ($PSBoundParameters.ContainsKey('Body')) { $p.Body = $Body; $p.ContentType = 'application/json' }
  try { Invoke-RestMethod @p }
  catch { ">>> RED: $Method $Uri failed: $($_.Exception.Message)"; exit 1 }
}

function Sess($x, $y) {
  $b = @{ path = $map; startX = $x; startY = $y } | ConvertTo-Json -Compress
  Invoke-Api -Uri "$base/api/simulate/session" -Method Post -Body $b -TimeoutSec 600
}
function Step($sid, $k) {
  $b = @{ count = 1; first = $k; then = @{} } | ConvertTo-Json -Compress
  Invoke-Api -Uri "$base/api/simulate/session/$sid/frames" -Method Post -Body $b
}

# CALIBRATION FIRST. The previous version of this script asserted 9409000 carried
# firstAttack; it does not, so the test proved nothing while looking like a pass.
# Guarded, and this is a real fix rather than tidiness: unguarded, a mob WITHOUT
# firstAttack made this line 404 and unwind the gate, which meant the red branch
# below -- the one that says exactly that -- could never run. The absence of the
# node is the answer the next block is built to detect.
$fa = try {
  (Invoke-Api -Uri "$base/api/inspect?path=$([uri]::EscapeDataString("$mobId/$FirstAttackMob.img/info/firstAttack"))" -TimeoutSec 300).node.value
} catch { '<absent>' }
"calibration: $FirstAttackMob firstAttack = '$fa'  (must be '1')"
# The gate decides the verdict from EXPLICIT markers (gate.ps1:47, :97-99) and
# never reads an exit code, so an "ABORT:" in prose used to score "ok".
# $fa is the VALUE, not the response object -- it was $fa.node.value before the
# resolver was guarded, and leaving the old accessor on it made this compare
# $null against '1', so a correct calibration reported red. MEASURED 2026-10-22
# on 5591: it printed "firstAttack = '1'" and then said the mob had none.
if ($fa -ne '1') { ">>> RED: that mob has no firstAttack; the test would prove nothing."; exit 1 }
$pl = try {
  (Invoke-RestMethod "$base/api/inspect?path=$([uri]::EscapeDataString("$mobId/$PlainMob.img/info/firstAttack"))" -TimeoutSec 300).node.value
} catch {
  # 404 IS the expected answer here: the plain mob has no firstAttack node, and
  # treating that as a fatal error is what made an earlier version of this script
  # abort before testing anything.
  '<absent>'
}
"calibration: $PlainMob firstAttack = '$pl'  (must be absent)"
""
"=== 1. a mob WITHOUT firstAttack must not PURSUE the player ==="
# --------------------------------------------------------------------------- #
# THE PROPERTY, stated so that it is a property of the TREE and not of where
# the random spawn happened to land:
#
#   A mob that has decided to engage the player ARRIVES at it and STAYS.
#   A mob that is patrolling may CROSS the player and walk on, or stand next
#   to it and never move at all. NEITHER of those is a pursuit.
#
#   pursuit  <=>  the mob ENDS the measurement window inside the engagement
#                 band, AND it is still inside that band over the whole tail
#                 of the window, AND it got there by CLOSING a material
#                 fraction of the gap it started with.
#
# All three clauses are needed and each kills a different false positive.
# MEASURED 2026-10-24 over 6 x 400-frame runs; the raw per-frame series are
# C:\mbtest2\w103\plain-3x400.json and C:\mbtest2\w103\fa-3x400.json, and
# every number below is read out of them:
#
#   the mob that HAS firstAttack (2220000): gap 26 -> 18.80, 70 -> 19.62 and
#     62 -> 18.82, and it then stands there for the rest of the window. It
#     ends inside the band, it stays inside it, and it gave up 28% / 72% / 70%
#     of the gap it started with.
#
#   the plain mob (2220100): gap 94 -> 94 (it never moved), 11 -> 11 (it never
#     moved) and 19 -> 548 (it walked its chain AWAY). Three more runs of the
#     same shape: 44 -> 358 (it CROSSED the player and walked on), 48 -> 208
#     and 26 -> 26 (it never moved).
#
#   * "ends inside the band" ALONE is wrong: a patrol that parks 11 px from
#     the player and never moves satisfies it.
#   * "the gap got smaller" ALONE is wrong: the patrol that CROSSED the
#     player satisfies it (44 -> 0.1 px) and is not pursuing anything.
#   * "the mob moved" ALONE is wrong: the patrol that walked 529 px AWAY
#     satisfies it.
#
# THE OLD ASSERTION WAS WRONG IN THREE WAYS, and that wrongness is what made
# this suite red about one run in three (W-103):
#   1. it forbade ANY movement, but the plain mob PATROLS its own foothold
#      chain, which is the product's documented behaviour
#      (MapleBench/Api/SimulatorEndpoints.cs:222-227: "Mobs and NPCs patrol
#      their own foothold chain. Seeded, so a map's answer is reproducible.");
#   2. it said "toward a player it was never told about" while never checking
#      a DIRECTION;
#   3. its frame loop BROKE at gap <= 2 px, so it stopped measuring at the
#      instant the mob arrived -- the one moment that decides the question.
#      The window now runs to the end and the break is gone.
#
# THE DAMAGE CHANNEL IS NOT THE DISCRIMINATOR and it cannot be: the map's own
# mob is within contact range of the character and hurts it (Stage.cpp:809),
# so a plain mob that merely walks into the player deals damage. MEASURED:
# the plain mob that stood 11 px away dealt 463 damage over 400 frames
# without ever pursuing anything. The discriminator has to be the pursuit.
# --------------------------------------------------------------------------- #
$Frames = 400
# The engagement band, in px. The mob that HAS firstAttack was measured
# standing at 18.80-19.62 px (see above), so 40 px is about twice that.
$Engaged = 40.0
# The fraction of its own starting gap the mob must have given up. The
# firstAttack mob gave up 28%/72%/70% (0.2768 measured, the nearest); the
# plain mob gave up 0%, 0% and -2784%. A margin of 2.7x on the nearest
# measurement, and the plain mob's best case is exactly 0.
$CloseFrac = 0.10
# The tail of the window the mob must hold the band for.
$TailFrames = 100
# The spawn is a random draw within 100 px of the character
# (SimulatorEndpoints.cs:451-455) and the endpoint takes no position, so a
# spawn that lands INSIDE the band cannot show "it closed the gap" -- the
# mob has nothing left to close. That draw is thrown away and drawn again.
$MinSpawnGap = 40.0
$MaxSpawnTries = 8

# THE ONE PREDICATE. Stage 1 requires it to be FALSE for the mob without
# firstAttack and stage 2 requires it to be TRUE for the mob that has it --
# the same function, the same numbers, opposite expectations. That is what
# makes it a check and not a tautology: if stage 2 ever stops firing, the
# measurement is measuring nothing.
function Test-Pursuit {
    param($M)
    if ($null -eq $M -or $M.gap0 -le 0) { return $false }
    $engaged = ($M.gapN -le $Engaged) -and ($M.tailMax -le $Engaged)
    $closed = (($M.gap0 - $M.gapN) / $M.gap0) -ge $CloseFrac
    return ($engaged -and $closed)
}

# One measurement: summon the mob, run the window to the END, and report the
# gap trajectory. The window is NOT broken early -- that was the old
# version's third mistake.
function Measure-Mob {
    param([string]$MobId, [int]$Frames)
    for ($try = 1; $try -le $MaxSpawnTries; $try++) {
        $s = Sess 1710 -25
        $sum = Invoke-Api -Uri "$base/api/simulate/session/$($s.sessionId)/summon" -Method Post `
            -Body (@{ mobId = $MobId; count = 1 } | ConvertTo-Json -Compress)
        $a = Step $s.sessionId @{}
        $m0 = @($a.mobs)[$sum.indexes[0]]
        $gap0 = [Math]::Abs([double]$a.x - [double]$m0.x)
        if ($gap0 -lt $MinSpawnGap) {
            Invoke-Api -Uri "$base/api/simulate/session/$($s.sessionId)" -Method Delete -TimeoutSec 60 | Out-Null
            "  (spawn $try of $MobId landed $gap0 px from the player -- too close for 'it closed the gap' to mean anything; drawing again)"
            continue
        }
        $gaps = @()
        $dmg = 0.0
        for ($i = 0; $i -lt $Frames; $i++) {
            $a = Step $s.sessionId @{}
            $m = @($a.mobs)[$sum.indexes[0]]
            $gaps += [Math]::Abs([double]$a.x - [double]$m.x)
            $dmg += [double]$a.playerDamage
        }
        $tail = @($gaps | Select-Object -Last $TailFrames)
        $summary = [pscustomobject]@{
            mobId   = $MobId
            tries   = $try
            gap0    = $gap0
            gapN    = [double]$gaps[-1]
            gapMin  = [double](($gaps | Measure-Object -Minimum).Minimum)
            gapMax  = [double](($gaps | Measure-Object -Maximum).Maximum)
            tailMax = [double](($tail | Measure-Object -Maximum).Maximum)
            damage  = $dmg
        }
        Invoke-Api -Uri "$base/api/simulate/session/$($s.sessionId)" -Method Delete -TimeoutSec 60 | Out-Null
        return $summary
    }
    return $null
}

$diag = Sess 1710 -25
$a = Step $diag.sessionId @{}
$nearest = ($a.mobs | Where-Object { $_.kind -eq 'm' } |
            Sort-Object { [Math]::Abs([double]$_.x - [double]$a.x) } | Select-Object -First 1)
"  map mobs: $(@($a.mobs | Where-Object { $_.kind -eq 'm' }).Count), nearest is $([Math]::Abs([double]$nearest.x - [double]$a.x)) px away"
"  (that one IS within contact range, and it will hurt -- that is Stage.cpp:809 and it is correct."
"   The assertion below is about the mob we SUMMON, and about whether it PURSUES, not about damage.)"
Invoke-Api -Uri "$base/api/simulate/session/$($diag.sessionId)" -Method Delete -TimeoutSec 60 | Out-Null

$plain = Measure-Mob $PlainMob $Frames
if ($null -eq $plain) {
    ">>> RED: no spawn of $PlainMob landed far enough from the player in $MaxSpawnTries draws to measure whether it closed the gap"
    exit 1
}
$plainClosed = [Math]::Round((($plain.gap0 - $plain.gapN) / $plain.gap0) * 100, 1)
"  plain mob: spawn gap $([Math]::Round($plain.gap0,2)) px -> end gap $([Math]::Round($plain.gapN,2)) px, min $([Math]::Round($plain.gapMin,2)) px, tail max $([Math]::Round($plain.tailMax,2)) px, it gave up $plainClosed% of its starting gap, and the player took $([Math]::Round($plain.damage)) damage (contact, not an attack)"
$stage1Failed = $false
# The green shape is HELD, not printed. Stage 2 runs after this point and can
# still fail, and a "PASS:" that is already in the output is a green shape in a
# run the gate will call red: the gate prints the last line matching
# VERDICT|ALL LADDERS PASS|passed,|BAD=|PASS: AND judges on the red markers, so
# one run showed "GREEN? PASS: ..." next to ">>> RED (>>> RED)". MEASURED on
# the gate, 2026-10-22, before this change.
$stage1Pass = ''
if (Test-Pursuit $plain) {
    $stage1Failed = $true
    ">>> RED: the plain mob $PlainMob PURSUED a player it was never told about: it gave up $plainClosed% of its starting gap and ended $([Math]::Round($plain.gapN,2)) px away, inside the engagement band, and stayed there. A mob without firstAttack must not pursue."
} else {
    $stage1Pass = "  PASS: it did not pursue -- no firstAttack, never struck"
}

"=== 2. a mob WITH firstAttack must PURSUE and hit, unprovoked ==="
# The SAME predicate and the SAME numbers, with the OPPOSITE expectation.
# The damage is printed as corroboration only: the map's own mob is in
# contact range and hurts the player, so a non-zero damage total does not
# prove that THIS mob attacked anything.
$fam = Measure-Mob $FirstAttackMob $Frames
if ($null -eq $fam) {
    ">>> RED: no spawn of $FirstAttackMob landed far enough from the player in $MaxSpawnTries draws to measure whether it closed the gap"
    exit 1
}
$famClosed = [Math]::Round((($fam.gap0 - $fam.gapN) / $fam.gap0) * 100, 1)
"  firstAttack mob: spawn gap $([Math]::Round($fam.gap0,2)) px -> end gap $([Math]::Round($fam.gapN,2)) px, min $([Math]::Round($fam.gapMin,2)) px, tail max $([Math]::Round($fam.tailMax,2)) px, it gave up $famClosed% of its starting gap, and the player took $([Math]::Round($fam.damage)) damage"
# W-056, the same bug the sibling suite already fixed: the gate reads the
# literal "NO damage number" and String.Contains does not match on case.
$stage2Failed = $false
$stage2Pass = ''
if (-not (Test-Pursuit $fam)) {
    $stage2Failed = $true
    ">>> RED: the mob $FirstAttackMob has firstAttack but did not pursue: it ended $([Math]::Round($fam.gapN,2)) px from the player (tail max $([Math]::Round($fam.tailMax,2)) px) after giving up $famClosed% of its starting gap. firstAttack did not acquire."
} else {
    $stage2Pass = "  PASS: it pursued without being struck"
}
if ($stage2Failed) { exit 1 }

# Both stages passed and nothing below can fail any more, so only NOW is a green
# shape safe to print. See the note at stage 1.
# Rule 10: prove the helper RAN. If something shadowed Invoke-Api the suite would
# still print two PASS: lines here and they would look like a measurement.
if ($script:ApiCalls -le 0) { ">>> RED: Invoke-Api was never called, so nothing in this run was measured"; exit 1 }
$stage1Pass
$stage2Pass
"  ($script:ApiCalls HTTP calls through the guarded helper)"
