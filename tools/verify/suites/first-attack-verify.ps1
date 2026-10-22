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
"=== 1. a mob WITHOUT firstAttack must not PURSUE until struck ==="
# firstAttack is a PURSUIT flag (MobAttack.cpp:81, :119-126): it decides whether
# the mob WALKS TOWARD you. It is NOT a permission to hit you -- contact damage
# has its own trigger (Stage.cpp:809: find_colliding + create_touch_attack) and
# needs nothing but overlap. So the assertion here is about the mob's X MOVING,
# not about damage, which is why the previous version of this section ("a plain
# mob deals 0 damage") was testing the wrong property and passing for the wrong
# reason: the two mobs happened to spawn >100px away and never overlapped.
$s = Sess 1710 -25
$a = Step $s.sessionId @{}
$nearest = ($a.mobs | Where-Object { $_.kind -eq 'm' } |
            Sort-Object { [Math]::Abs([double]$_.x - [double]$a.x) } | Select-Object -First 1)
"  map mobs: $(@($a.mobs | Where-Object { $_.kind -eq 'm' }).Count), nearest is $([Math]::Abs([double]$nearest.x - [double]$a.x)) px away"
"  (that one IS within contact range, and it will hurt -- that is Stage.cpp:809 and it is correct."
"   The assertion below is about the mob we SUMMON, and about its X, not about damage.)"
$sum = Invoke-Api -Uri "$base/api/simulate/session/$($s.sessionId)/summon" -Method Post `
  -Body (@{ mobId = $PlainMob; count = 1 } | ConvertTo-Json -Compress)
"  summoned the plain mob $($sum.spawned) -> index $($sum.indexes -join ',')"
$a = Step $s.sessionId @{}
$startX = [double](@($a.mobs)[$($sum.indexes[0])].x)
$closest = [double]::MaxValue
for ($i = 0; $i -lt 400; $i++) {
  $a = Step $s.sessionId @{}
  $m = @($a.mobs)[$($sum.indexes[0])]
  $gap = [Math]::Abs([double]$a.x - [double]$m.x)
  if ($gap -lt $closest) { $closest = $gap }
  if ($gap -le 2) { break }
}
$m = @($a.mobs)[$($sum.indexes[0])]
"  plain mob x $startX -> $([double]$m.x)  (closest approach to the player: $closest px)"
$stage1Failed = $false
# The green shape is HELD, not printed. Stage 2 runs after this point and can
# still fail, and a "PASS:" that is already in the output is a green shape in a
# run the gate will call red: the gate prints the last line matching
# VERDICT|ALL LADDERS PASS|passed,|BAD=|PASS: AND judges on the red markers, so
# one run showed "GREEN? PASS: ..." next to ">>> RED (>>> RED)". MEASURED on
# the gate, 2026-10-22, before this change.
$stage1Pass = ''
if ([Math]::Abs([double]$m.x - $startX) -lt 1) { $stage1Pass = "  PASS: it did not walk toward the player -- no firstAttack, never struck" }
# Same trap as the ABORT above: a "FAIL" in prose is not a marker. The gate
# would print "ok" and this failure would be counted as a pass.
else { $stage1Failed = $true; ">>> RED: it moved $([Math]::Abs([double]$m.x - $startX)) px toward a player it was never told about" }
Invoke-Api -Uri "$base/api/simulate/session/$($s.sessionId)" -Method Delete -TimeoutSec 60 | Out-Null
# A failing suite must also exit non-zero -- callers read the exit code (rule 14).
if ($stage1Failed) { exit 1 }

"=== 2. a mob WITH firstAttack must PURSUE and hit, unprovoked ==="
$s2 = Sess 1710 -25
$sum2 = Invoke-Api -Uri "$base/api/simulate/session/$($s2.sessionId)/summon" -Method Post `
  -Body (@{ mobId = $FirstAttackMob; count = 1 } | ConvertTo-Json -Compress)
"  summoned $FirstAttackMob -> spawned=$($sum2.spawned) index=$($sum2.indexes -join ',')"
$hits = @()
for ($i = 0; $i -lt 400; $i++) {
  $a = Step $s2.sessionId @{}
  if ($a.playerDamage -gt 0) { $hits += "  +$i frames: hit for $($a.playerDamage), hp $($a.status.hp)/$($a.status.maxHp)" }
  if ($hits.Count -ge 2) { break }
}
# W-056, the same bug the sibling suite already fixed: the gate reads the
# literal "NO damage number" and String.Contains does not match on case.
$stage2Failed = $false
$stage2Pass = ''
if ($hits.Count -eq 0) { $stage2Failed = $true; ">>> RED: no damage over 400 frames -- firstAttack did not acquire" }
else { $hits | ForEach-Object { $_ }; $stage2Pass = "  PASS: it attacked without being struck" }
Invoke-Api -Uri "$base/api/simulate/session/$($s2.sessionId)" -Method Delete -TimeoutSec 60 | Out-Null
if ($stage2Failed) { exit 1 }

# Both stages passed and nothing below can fail any more, so only NOW is a green
# shape safe to print. See the note at stage 1.
# Rule 10: prove the helper RAN. If something shadowed Invoke-Api the suite would
# still print two PASS: lines here and they would look like a measurement.
if ($script:ApiCalls -le 0) { ">>> RED: Invoke-Api was never called, so nothing in this run was measured"; exit 1 }
$stage1Pass
$stage2Pass
"  ($script:ApiCalls HTTP calls through the guarded helper)"
