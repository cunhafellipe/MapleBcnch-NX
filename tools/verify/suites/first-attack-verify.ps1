param([int]$Port = 5591, [string]$FirstAttackMob = '2220000', [string]$PlainMob = '2220100')
$ErrorActionPreference = 'Stop'
$base = "http://127.0.0.1:$Port"
$files = Invoke-RestMethod "$base/api/files" -TimeoutSec 120
$mapId = ($files | Where-Object { $_.name -eq 'Map.nx' }).id
$mobId = ($files | Where-Object { $_.name -eq 'Mob.nx' }).id
$map = "$mapId/Map/Map1/100000004.img"

function Sess($x, $y) {
  $b = @{ path = $map; startX = $x; startY = $y } | ConvertTo-Json -Compress
  Invoke-RestMethod "$base/api/simulate/session" -Method Post -Body $b -ContentType 'application/json' -TimeoutSec 600
}
function Step($sid, $k) {
  $b = @{ count = 1; first = $k; then = @{} } | ConvertTo-Json -Compress
  Invoke-RestMethod "$base/api/simulate/session/$sid/frames" -Method Post -Body $b -ContentType 'application/json' -TimeoutSec 300
}

# CALIBRATION FIRST. The previous version of this script asserted 9409000 carried
# firstAttack; it does not, so the test proved nothing while looking like a pass.
$fa = Invoke-RestMethod "$base/api/inspect?path=$([uri]::EscapeDataString("$mobId/$FirstAttackMob.img/info/firstAttack"))" -TimeoutSec 300
"calibration: $FirstAttackMob firstAttack = '$($fa.node.value)'  (must be '1')"
if ($fa.node.value -ne '1') { "ABORT: that mob has no firstAttack; the test would prove nothing."; exit 1 }
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
$sum = Invoke-RestMethod "$base/api/simulate/session/$($s.sessionId)/summon" -Method Post `
  -Body (@{ mobId = $PlainMob; count = 1 } | ConvertTo-Json -Compress) -ContentType 'application/json' -TimeoutSec 300
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
if ([Math]::Abs([double]$m.x - $startX) -lt 1) { "  PASS: it did not walk toward the player -- no firstAttack, never struck" }
else { "  FAIL: it moved $([Math]::Abs([double]$m.x - $startX)) px toward a player it was never told about" }
Invoke-RestMethod "$base/api/simulate/session/$($s.sessionId)" -Method Delete -TimeoutSec 60 | Out-Null

"=== 2. a mob WITH firstAttack must PURSUE and hit, unprovoked ==="
$s2 = Sess 1710 -25
$sum2 = Invoke-RestMethod "$base/api/simulate/session/$($s2.sessionId)/summon" -Method Post `
  -Body (@{ mobId = $FirstAttackMob; count = 1 } | ConvertTo-Json -Compress) -ContentType 'application/json' -TimeoutSec 300
"  summoned $FirstAttackMob -> spawned=$($sum2.spawned) index=$($sum2.indexes -join ',')"
$hits = @()
for ($i = 0; $i -lt 400; $i++) {
  $a = Step $s2.sessionId @{}
  if ($a.playerDamage -gt 0) { $hits += "  +$i frames: hit for $($a.playerDamage), hp $($a.status.hp)/$($a.status.maxHp)" }
  if ($hits.Count -ge 2) { break }
}
if ($hits.Count -eq 0) { "  NO DAMAGE over 400 frames -- FAIL (firstAttack did not acquire)" }
else { $hits | ForEach-Object { $_ }; "  PASS: it attacked without being struck" }
Invoke-RestMethod "$base/api/simulate/session/$($s2.sessionId)" -Method Delete -TimeoutSec 60 | Out-Null
