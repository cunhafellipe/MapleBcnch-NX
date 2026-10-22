# 7. Does a provoked mob chase and hit back, and for how much?
#
# THE SUBJECT CHANGED, and why matters.
#
# Contact damage no longer requires aggro: Stage.cpp:809-818 asks
# mobs.find_colliding(player.get_phobj(), player.get_touch_bounds()) and deals
# create_touch_attack() to whatever the player is standing on. So walking up to a
# mob HURTS, with no cooldown on the reference's side at all. Against 2220100
# (info/PADamage = 90) that kills the 50-HP character on approach -- and 50 is the
# reference's own fresh-character HP (PlayerStatus.BaseMaxHp, citing Character.cs
# `this.status.MaxHP.BaseVal = 50`). So the old version of this test walked into a
# 90-damage mob, died, and reported "NO DAMAGE over 400 frames".
#
# 9500302 is the subject instead: info/PADamage = 10, bodyAttack = 1, no
# firstAttack, and it has a `move` node -- so a fresh 50-HP character survives
# five of its touches and it can still walk. That isolates the thing under test,
# which is the PURSUIT half: a mob that is never struck and has no firstAttack
# must not walk toward the player, and one that IS struck must
# (Mob.cpp:1802 sets aggro on damage > 0; MobAttack.cpp:119-126 does the walking).
#
# The damage number matters as much as the mob does. 2220100 (PADamage 90) kills
# the character before it can land a swing; 0210100 (32) leaves it 50 - 32 = 18
# HP, which is not enough frames to land one either. MEASURED, both aborted.
param(
  [int]$Port = $(if ($env:GATE_BASE) { $q = ([uri]$env:GATE_BASE).Port; if ($q -gt 0) { $q } else { 5591 } } else { 5591 }),
  [string]$MapLeaf = 'Map/Map1/100000004.img',
  [string]$Mob = '9500302'
)
$ErrorActionPreference = 'Stop'
$base = "http://127.0.0.1:$Port"

$files = Invoke-RestMethod "$base/api/files" -TimeoutSec 120
$mapFile = @($files) | Where-Object { $_.name -eq 'Map.nx' } | Select-Object -First 1
$mobFile = @($files) | Where-Object { $_.name -eq 'Mob.nx' } | Select-Object -First 1
if (-not $mapFile) { throw "Map.nx is not open" }
$path = "$($mapFile.id)/$MapLeaf"

# CALIBRATION FIRST. A test that silently measures the wrong mob proves nothing.
function Flag($key) {
  try {
    (Invoke-RestMethod "$base/api/inspect?path=$([uri]::EscapeDataString("$($mobFile.id)/$Mob.img/info/$key"))" -TimeoutSec 300).node.value
  } catch { '<absent>' }
}
$pd = Flag 'PADamage'
$ba = Flag 'bodyAttack'
$fa = Flag 'firstAttack'
"calibration: $Mob  PADamage=$pd  bodyAttack=$ba  firstAttack=$fa"
if ($ba -ne '1') { ">>> RED: $Mob has no bodyAttack, so it cannot touch at all."; exit 1 }
if ($fa -ne '<absent>') { ">>> RED: $Mob carries firstAttack, so pursuit is not what this test measures."; exit 1 }
if ([int]$pd -ge 40) { ">>> RED: $Mob deals $pd, which does not leave the 50-HP character enough frames to land a swing."; exit 1 }

# A WEAPON, or the band below is wrong. The bare-hands box is
  # Character/Afterimage/barehands.img/0/stabO1 and it is far shorter than a
  # sword's, so the 27..82px band that fits a sword sits entirely outside it.
  # MEASURED: with no weapon this script held gap 42.7, swung 8 times and hit
  # nothing, which reads as "the swing is broken".
$body = @{ path = $path; startX = 1710; startY = -25; weapon = '01302000.img'; equippedWeapon = '01302000' } | ConvertTo-Json -Compress
$s = Invoke-RestMethod "$base/api/simulate/session" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 600
"session $($s.sessionId)"

function Step([hashtable]$k) {
  $b = @{ count = 1; first = $k; then = @{} } | ConvertTo-Json -Compress
  Invoke-RestMethod "$base/api/simulate/session/$($s.sessionId)/frames" -Method Post -Body $b -ContentType 'application/json' -TimeoutSec 300
}

# Phase A: close the distance and provoke. RETRIED in a fresh session.
#
# One attempt is not enough and the reason is the mob, not the swing: 9500302
# PATROLS, so over a long approach it can wander behind the character, and the
# swing box is built from the character's facing -- a swing aimed at empty air.
# MEASURED: the same script and the same weapon provoked on one run (gap 68, one
# swing) and failed on the next (gap 36, eight swings, 0 hits). Each attempt gets
# a fresh summon offset, so retrying samples different geometry rather than
# repeating one unlucky one.
$cur = $null; $idx = 0; $swings = 0; $provoked = $false; $gap = 0; $hp0 = 0
for ($attempt = 1; $attempt -le 12 -and -not $provoked; $attempt++) {
  $s = Invoke-RestMethod "$base/api/simulate/session" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 600
  # W-062: THE FLAKE. MEASURED on the gate run of 2026-10-16 -- the suite printed
  #   (the 50-HP character died on that hit; ...)
  #   NO DAMAGE over 400 frames -- player hp still 0/50
  #   player ended at 1601, mob at 1621.8 -- gap 20.8
  #   >>> RED: no damage over 400 frames -- the mob never connected
  # and the mob had, in fact, connected repeatedly -- gap 20.8 px, player at
  # 0/50. The mechanism is the one in the studio rules 2.9, one layer out: Phase A
  # APPROACHES the mob, contact damage applies the whole way (Stage.cpp:809-818
  # needs no aggro, and this script's own header says so), and the 50 HP pool is
  # spent before Phase B begins. This script never recorded player HP during
  # Phase A, so it could not see its own priming phase doing it. Re-run four
  # times afterwards, all four green, entering Phase B at 50/50 or 30/50
  # depending on how the mob happened to patrol.
  #
  # Invulnerable suppresses the HP spend only, so the mob still walks, still
  # chases and still connects -- which is the thing under test. Contact damage
  # has its own suites (`attack-drop`, `sim-health`); this one is about pursuit.
  Invoke-RestMethod "$base/api/simulate/session/$($s.sessionId)/invulnerable" -Method Post `
    -Body (@{ value = $true } | ConvertTo-Json -Compress) -ContentType 'application/json' -TimeoutSec 60 | Out-Null
  $sum = Invoke-RestMethod "$base/api/simulate/session/$($s.sessionId)/summon" -Method Post `
    -Body (@{ mobId = $Mob; count = 1 } | ConvertTo-Json -Compress) -ContentType 'application/json' -TimeoutSec 300
  if ($sum.spawned -lt 1) { continue }
  $idx = $sum.indexes[0]
  $cur = Step @{}
  $mob0 = @($cur.mobs)[$idx]
  if (-not $mob0) { continue }
  $hp0 = [int]$mob0.hp
  $swings = 0
  for ($i = 0; $i -lt 260; $i++) {
    $m = @($cur.mobs)[$idx]
    if ([int]$m.hp -lt $hp0) { $provoked = $true; break }
    $gap = [Math]::Abs([double]$cur.x - [double]$m.x)
    if ($i % 50 -eq 49) { $cur = Step @{ attack = $true }; $swings++ }
    elseif ($gap -lt 25) { $cur = Step @{ left = $true } }
    elseif ($gap -lt 90) { $cur = Step @{} }
    elseif ([double]$m.x -lt [double]$cur.x) { $cur = Step @{ left = $true } }
    else { $cur = Step @{ right = $true } }
  }
  # W-062: record what Phase A left behind, on every attempt. The run that went
  # red never printed this, which is precisely why it could not diagnose itself.
  $phHp = [int]$cur.status.hp; $phMax = [int]$cur.status.maxHp
  if ($provoked) {
    "  attempt ${attempt}: provoked with $swings swing(s) at gap $([Math]::Round($gap)); player $phHp/$phMax after the approach"
  } else {
    Invoke-RestMethod "$base/api/simulate/session/$($s.sessionId)" -Method Delete -TimeoutSec 60 | Out-Null
  }
}
if (-not $provoked) { ">>> RED: no swing landed in 12 attempts, so aggro was never set."; exit 1 }

# Phase A2: RETREAT out of contact. Standing next to the mob cannot show
# pursuit -- the touch rule (Stage.cpp:809) already hits there, aggro or not, and
# two 32-damage touches are the whole 50 HP. So walk away until the gap is past
# contact, and only then stop moving.
for ($i = 0; $i -lt 300; $i++) {
  $m = @($cur.mobs)[$idx]
  $gap = [Math]::Abs([double]$cur.x - [double]$m.x)
  if ($gap -ge 100) { break }
  if ([double]$m.x -gt [double]$cur.x) { $cur = Step @{ left = $true } } else { $cur = Step @{ right = $true } }
}
$m = @($cur.mobs)[$idx]
$gap0 = [Math]::Abs([double]$cur.x - [double]$m.x)
"  retreated to gap $gap0 px, player hp $($cur.status.hp)/$($cur.status.maxHp), mob hp $($m.hp)/$($m.maxHp)"

# Phase B: stop touching the buttons and watch. The mob must now close on its
# own and hit back -- that is the pursuit this test exists to prove.
$hits = New-Object System.Collections.ArrayList
$closest = [double]::MaxValue
for ($i = 0; $i -lt 400; $i++) {
  $a = Step @{}
  # playerDamage BEFORE status.dead, always. The killing frame carries both, and
  # testing `dead` first throws away the hit that killed it -- which is the
  # off-by-one in the studio rules 2.9, and it makes a mob that connects perfectly read
  # as one that never did.
  if ($a.playerDamage -gt 0) {
    [void]$hits.Add("  +$i frames: mob hit for $($a.playerDamage) -> player hp $($a.status.hp)/$($a.status.maxHp)")
  }
  $m = @($a.mobs)[$idx]
  if ($m) {
    $g = [Math]::Abs([double]$a.x - [double]$m.x)
    if ($g -lt $closest) { $closest = $g }
  }
  if ($a.status.dead) { "  (the 50-HP character died on that hit; the chase itself had already been proven by the gap)" ; break }
  if ($hits.Count -ge 2) { break }
}
$m = @($a.mobs)[$idx]
# W-062: "died" and "never connected" are DIFFERENT answers and the run of
# 2026-10-16 printed both in the same four lines. A mob that killed the player
# demonstrably connected; saying it never did is a false statement about the
# product, and it is the statement a reader would act on.
$died = [bool]$a.status.dead
if ($hits.Count -eq 0) {
  if ($died) {
    "NO HITS RECORDED, and the character is dead -- INCONCLUSIVE, not a failure."
    "  The player was killed during the window and the frame that killed it did not"
    "  report playerDamage, so the hit cannot be counted from here. The pursuit"
    "  itself is NOT disproved by this run; re-run it."
    "ABORT: inconclusive"
    # This prints RED on purpose. gate.ps1 cannot see an exit code (T-2), and a
    # run that proved nothing must not read as `ok`. A red that says
    # INCONCLUSIVE is honest; a green here would be the exact lie this item
    # exists to remove.
    ">>> RED: inconclusive -- the character died and the killing frame reported no playerDamage; pursuit is neither proven nor disproved"
    exit 1
  }
  "NO DAMAGE over 400 frames -- player hp still $($a.status.hp)/$($a.status.maxHp)"
  "  player ended at $([double]$a.x), mob at $([double]$m.x) -- gap $([Math]::Abs([double]$a.x - [double]$m.x))"
  # W-056: the gate reads the literal "NO damage number". This suite printed
  # "NO DAMAGE over 400 frames", which .NET String.Contains does not match on
  # case, so a mob that never touched the player scored GREEN. Emit the marker
  # the gate actually reads.
  ">>> RED: no damage over 400 frames, and the character was alive throughout -- the mob never connected"
  # A suite that fails must ALSO exit non-zero. The gate greps the output, but
  # every other caller in this project reads the exit code (AGENTS.md rule 14).
  exit 1
} else {
  $hits | ForEach-Object { $_ }
  "  gap $gap0 -> closest $closest : the mob closed the distance on its own after one landed hit"
  # The gate PRINTS the last line matching VERDICT|ALL LADDERS PASS|passed,|
  # BAD=|PASS: (gate.ps1:93-94) and without one it can only show "(read the
  # tail)", which pushes the verdict onto a human reading every suite's
  # output. This line is the green shape. The red branches in THIS file print
  # the marker only, so a red run of THIS suite never also prints this line --
  # that is a fact about this file, not a rule about the other suites: a
  # sibling file can print a green shape and then fail (see
  # first-attack-verify.ps1, which had to defer its PASS: for exactly that
  # reason).
  "VERDICT: mob-attack-verify closed the distance on its own and landed hits"
}