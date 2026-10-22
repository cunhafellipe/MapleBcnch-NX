# W-069 - NPC labels, with assertions.
#
# W-057 named this suite: 28 lines of Invoke-RestMethod and Write-Host, zero
# comparisons, zero throws. The gate therefore showed `ok` for a suite that
# could not fail -- and "ok" for a suite that checks nothing is worse than not
# having it (pm/GATE.md:78-83).
#
# There was a third, quieter failure here too: the loop `break`s on the first
# region that has NPCs and `continue`s past the ones that do not, so a run where
# NO region has an NPC prints nothing, asserts nothing, and passes. A check that
# verifies nothing must say so loudly.
#
# What it asserts, and the promise behind each:
#   1  A map with NPCs actually has NPCs.            (otherwise this suite is a no-op)
#   2  An NPC that is not hidden carries a name.      (an unlabelled NPC draws nothing;
#      The studio rules 2.8 measured String/Npc.img carrying thousands of entries, so an
#      empty name is a missing read, not an empty archive)
#   3  No mob carries an NPC name.                    (a mob wearing a nameplate is a
#      wrong label, and it is the failure mode a count-only check cannot see)
param([int]$Port = $(if ($env:GATE_BASE) { $q = ([uri]$env:GATE_BASE).Port; if ($q -gt 0) { $q } else { 5591 } } else { 5591 }), [string]$Map = '000000002.img')

$ErrorActionPreference = 'Stop'
$base = "http://127.0.0.1:$Port"
$script:Asserts = 0
$script:Fails = 0

function Check([string]$name, [bool]$ok, [string]$detail) {
  $script:Asserts++
  if ($ok) { "  ok    $name" }
  else {
    $script:Fails++
    "  FAIL  $name -- $detail"
    ">>> RED: npc-label-check -- $name ($detail)"
  }
}

$files = Invoke-RestMethod "$base/api/files" -TimeoutSec 120
$mapId = ($files | Where-Object { $_.name -eq 'Map.nx' }).id

$examined = 0
$npcs = $null
$mobs = $null
$foundIn = $null

foreach ($region in @('Map0', 'Map1', 'Map2', 'Map3')) {
  $p = "$mapId/Map/$region/$Map"
  try { $null = Invoke-RestMethod "$base/api/node?path=$([uri]::EscapeDataString($p))" -TimeoutSec 300 } catch { continue }
  $body = @{ path = $p; startX = 0; startY = 0 } | ConvertTo-Json -Compress
  try { $s = Invoke-RestMethod "$base/api/simulate/session" -Method Post -Body $body -ContentType 'application/json' -TimeoutSec 600 } catch { continue }
  $fb = @{ count = 1; first = @{}; then = @{} } | ConvertTo-Json -Compress
  $f = Invoke-RestMethod "$base/api/simulate/session/$($s.sessionId)/frames" -Method Post -Body $fb -ContentType 'application/json' -TimeoutSec 300
  Invoke-RestMethod "$base/api/simulate/session/$($s.sessionId)" -Method Delete -TimeoutSec 60 | Out-Null

  $examined++
  $mobs = @($f.mobs)
  $npcs = @($mobs | Where-Object { $_.kind -eq 'n' })
  if ($npcs.Count -gt 0) { $foundIn = "$region/$Map"; break }
}

"=== $Map : examined $examined region(s) ==="

if ($npcs -eq $null -or $npcs.Count -eq 0) {
  # Before this change the loop simply ended here and the suite reported success.
  Check "a map with NPCs was actually examined" $false `
    "no NPC in any of Map0..Map3 for $Map -- this suite verified nothing and used to pass silently"
} else {
  Check "a map with NPCs was actually examined" $true "nothing"
  "  found in $foundIn : mobs=$($mobs.Count) npcs=$($npcs.Count)"
  "  with name : " + @($npcs | Where-Object { $_.npcName }).Count + " / " + $npcs.Count
  "  with func : " + @($npcs | Where-Object { $_.npcFunc }).Count
  "  hideName  : " + @($npcs | Where-Object { $_.npcHideName }).Count
  $npcs | Select-Object -First 6 | ForEach-Object { "    id=$($_.mobId) name='$($_.npcName)' func='$($_.npcFunc)' hide=$($_.npcHideName)" }

  # A HIDDEN NPC is allowed to have no name -- that is what the flag means.
  $shown = @($npcs | Where-Object { -not $_.npcHideName })
  $unnamed = @($shown | Where-Object { -not $_.npcName })
  Check "every visible NPC carries a name" ($unnamed.Count -eq 0) `
    "$($unnamed.Count) of $($shown.Count) visible NPCs have no name: $((@($unnamed | ForEach-Object { $_.mobId }) -join ', '))"

  $mislabeled = @($mobs | Where-Object { $_.kind -ne 'n' -and $_.npcName })
  Check "no mob carries an NPC name" ($mislabeled.Count -eq 0) `
    "$($mislabeled.Count) mob(s) wear a nameplate: $((@($mislabeled | ForEach-Object { "$($_.mobId)='$($_.npcName)'" }) -join ', '))"
}

"VERDICT: npc-label-check $script:Asserts assertions, $script:Fails failed"
if ($script:Fails -gt 0) { exit 1 }
exit 0