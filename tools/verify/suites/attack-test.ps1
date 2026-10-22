# 6.2: does an attack actually produce an attackStance on the wire, and does
# anything land on a mob? Calibration first, per the studio rules 7.1.
#
# The character is INVULNERABLE throughout. MEASURED: the walk below crosses a
# mob corridor (mob 2220100 at x=2091 on the same ledge) and a 50 HP character
# walking into it is dead by x=2097 -- at which point `Swing` correctly refuses
# (`MapSimSessionStore.Swing`, the dead-character guard) and this suite reported
# "attackStance was EMPTY on every swing -- the link is broken", which is a false
# accusation of a broken link. The suite's question is whether the SERVER names a
# stance and lands a hit on a MOB; neither depends on the character surviving, and
# invulnerability does not touch either (the player's damage number comes from
# `mobPatrol.TakeHit`, not from `PlayerStatus.Hurt`).
param([int]$Port = $(if ($env:GATE_BASE) { $q = ([uri]$env:GATE_BASE).Port; if ($q -gt 0) { $q } else { 5591 } } else { 5591 }), [string]$Map = "f4/Map/Map1/100000004.img")

$base = "http://127.0.0.1:$Port"
$script:Last = $null

function New-Session([int]$x, [int]$y, [string]$weapon = "01302000.img", [string]$eq = "01302000") {
  $b = @{ path = $Map; startX = $x; startY = $y; weapon = $weapon; equippedWeapon = $eq; invulnerable = $true } | ConvertTo-Json -Compress
  Invoke-RestMethod "$base/api/simulate/session" -Method Post -Body $b -ContentType "application/json" -TimeoutSec 300
}
function Step($sid, $keys) {
  $b = @{ count = 1; first = $keys; then = $keys } | ConvertTo-Json -Compress
  $script:Last = Invoke-RestMethod "$base/api/simulate/session/$sid/frames" -Method Post -Body $b -ContentType "application/json" -TimeoutSec 300
}

"=== calibration: the server endpoint that names the stances ==="
# NOTE: ?weapon=01302000 returns an EMPTY group on this build; ?attack=1 does
# not. Measure BOTH -- MapSimSessionStore.Swing:577 calls
# AttackStancesFor(session.EquippedWeapon), i.e. the ?weapon= path, so that is
# the one whose emptiness would actually break the swing.
$armed = Invoke-RestMethod "$base/api/simulate/look/attack-stances?attack=1" -TimeoutSec 120
"  ?attack=1        -> stances=$($armed.stances -join ',') armed=$($armed.armed)"
$byWeapon = Invoke-RestMethod "$base/api/simulate/look/attack-stances?weapon=01302000" -TimeoutSec 120
"  ?weapon=01302000 -> stances=[$(@($byWeapon.stances) -join ',')] armed=$($byWeapon.armed)   <-- the path Swing() actually uses"
$bare = Invoke-RestMethod "$base/api/simulate/look/attack-stances?attack=0" -TimeoutSec 120
"  ?attack=0 (bare) -> stances=$(@($bare.stances).Count) armed=$($bare.armed)"
if ($armed.stances.Count -eq 0) { "ABORT: the static table returns nothing for attack=1; downstream numbers would be meaningless."; exit 1 }
""

"=== calibration: walking still moves (known answer: 2.5 px/frame) ==="
$c = New-Session 1710 -25
for ($i = 0; $i -lt 30; $i++) { Step $c.sessionId @{ right = $true } }
$w = $script:Last
"  after 30 right-frames: x=$($w.x) action=$($w.action) speed=$($w.speed)"
if ([double]$w.speed -le 0) { "ABORT: walking is dead; attack measurement would be meaningless."; exit 1 }
""

"=== walk up to the nearest mob, then swing ==="
# Nearest ground-standing mob from the spawn point.
$spawn = Invoke-RestMethod "$base/api/simulate/session" -Method Post -Body (@{path=$Map;startX=1710;startY=-25;weapon="01302000.img";equippedWeapon="01302000";invulnerable=$true}|ConvertTo-Json -Compress) -ContentType "application/json" -TimeoutSec 300
$target = $null
# Same ledge as the spawn. Selecting by x ALONE picks the mob at x=1867 y=-535 --
# 510px ABOVE the spawn -- and walking toward its x leaves the character 78px
# past nothing, which is why this script could report eight swings and zero hits
# while looking like a passing test.
foreach ($m in @($spawn.mobs)) {
  if (-not $m.onGround) { continue }
  if ([Math]::Abs([double]$m.y - (-25)) -gt 100) { continue }
  if ($null -eq $target -or [Math]::Abs([double]$m.x - 1710) -lt [Math]::Abs([double]$target.x - 1710)) { $target = $m }
}
"  nearest ground mob: id=$($target.mobId) x=$($target.x) y=$($target.y) hp=$($target.hp)"
$gap = [double]$target.x - [double]$script:Last.x
# From where the character IS, not from the spawn: the calibration block above
# already walked it 30 frames, so measuring the gap from 1710 over-walks by that
# whole distance and the character ends 79px past the mob with nothing in reach.
$walkFrames = [int][Math]::Ceiling([Math]::Abs($gap) / 2.4)
$dir = if ($gap -gt 0) { @{ right = $true } } else { @{ left = $true } }
for ($i = 0; $i -lt $walkFrames; $i++) { Step $c.sessionId $dir }
"  walked $walkFrames frames -> x=$($script:Last.x)  gap now = $([Math]::Round([double]$target.x - [double]$script:Last.x,1)) px"
""

"=== 8 swings, one frame apart ==="
$stancesSeen = @()
$hits = 0
$lateHits = 0
$lateValues = @()
for ($i = 0; $i -lt 8; $i++) {
  # release, then swing: the attack key is an EDGE (livewalk.js latches it)
  Step $c.sessionId @{}
  Step $c.sessionId @{ attack = $true }
  $r = $script:Last
  # `@($r.damage).Count` is 1 for a NULL `damage` -- PowerShell wraps $null in a
  # one-element array. That is what made this script report "damage entries over
  # 8 swings : 8" for eight swings that hit nothing: the frame has no `damage`
  # key at all until a swing connects, and `@($null)` counted as one hit every
  # time. Filter nulls the way skill-test.ps1:23-24 already does.
  $d = @($r.damage | Where-Object { $_ })
  if ($d.Count -gt 0) { $hits += $d.Count }
  $stancesSeen += $r.attackStance
  "  swing $i : action=$($r.action) attackStance='$($r.attackStance)' damage=$($d.Count) $(if($d.Count){'['+(@($d|ForEach-Object{$_.value}) -join ',')+']'})"
  # The hit is DELAYED (swingDelayTicks measured 15-20 on this weapon) and
  # swinging again on the next frame RESETS the countdown, so eight back-to-back
  # swings resolve nothing at all. Idle frames are part of measuring a one-shot
  # event -- and the damage number arrives on one of THEM, not on the frame the
  # key went down, so the idle frames' own `damage` has to be read too.
  for ($k = 0; $k -lt 24; $k++) {
    Step $c.sessionId @{}
    $late = @($script:Last.damage | Where-Object { $_ })
    if ($late.Count -gt 0) { $lateHits += $late.Count; $lateValues += @($late | ForEach-Object { $_.value }) }
  }
}
""
"distinct attackStance values seen: $((@($stancesSeen | Sort-Object -Unique) -join ', '))"
"server-declared group          : $((@($armed.stances) -join ', '))"
"damage entries over 8 swings   : $hits"
"damage numbers on the DELAYED frames: $lateHits $(if($lateValues.Count){'['+($lateValues -join ',')+']'})"

# ADR-0003: the gate reads EXPLICIT markers only. A negative verdict here that
# does not print `>>> RED` is invisible to it -- and that happened: this suite
# printed a negative VERDICT and the gate still reported the run green, because
# nothing matched. Every failing branch below now says so in the gate's own words.
$unknown = @($stancesSeen | Where-Object { $_ -and ($armed.stances -notcontains $_) })
if ($stancesSeen | Where-Object { $_ }) {
  if ($unknown.Count -eq 0) { "VERDICT: attackStance IS on the wire and always inside the declared group. 6.2's server->avatar link works." }
  else { "VERDICT: attackStance carried a value OUTSIDE the declared group: $($unknown -join ',')"; ">>> RED" }
} else {
  "VERDICT: attackStance was EMPTY on every swing -- the link is broken. AttackStancesFor() returned nothing, so Swing() never called SwingPose()."
  ">>> RED"
}
# And the hits, which the VERDICT above does not speak to: a stance on the wire
# with nothing landing is a green line over a broken swing, which is exactly what
# this file reported for an unknown number of runs.
if ($lateHits -eq 0) { ">>> RED: 8 swings produced NO damage number -- the swing never connected." }
if ($lateHits -gt 0) { "hits: $lateHits damage numbers in [$($lateValues -join ',')]" }

"=== unarmed control: same 8 swings with no equipped weapon ==="
$u = New-Session 1710 -25 "" $null
$us = @()
for ($i = 0; $i -lt 8; $i++) { Step $u.sessionId @{}; Step $u.sessionId @{ attack = $true }; $us += $script:Last.attackStance }
"  unarmed attackStance values: '$((@($us | Sort-Object -Unique) -join ', '))'  (expect all empty -- CharLook.cpp:594)"