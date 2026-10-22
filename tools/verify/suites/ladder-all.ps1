# Per-ladder verification, driving the way a player does: stand on the platform,
# walk to under the ladder, climb up, then climb back down.
#
# Two harness bugs this file exists to not repeat:
#  1. The session pins a spawn to the foothold's X1, so asking for the ladder's
#     own x is not the same as standing under the ladder. (x=2272 spawned at 2250,
#     22px off -- outside the client's +-10 reach box.)
#  2. Climbing DOWN has to start from a position the climb actually reached.
#     Spawning at the ladder's top y makes the session fall to the floor first.
param([int]$Port = $(if ($env:GATE_BASE) { $q = ([uri]$env:GATE_BASE).Port; if ($q -gt 0) { $q } else { 5591 } } else { 5591 }), [string]$Map = "")

$base = "http://127.0.0.1:$Port"

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
# loaded -- ladderFrames=0/400, outOfBounds=400, "FAIL" -- and that red is
# about the empty map, not about the resolver. Prove the function exists first.
if (-not (Get-Command Get-ArchiveId -ErrorAction SilentlyContinue)) { ">>> RED: archivelib.ps1 did not define Get-ArchiveId, so there is no resolver and nothing to measure against"; exit 1 }
if (-not $Map) { $Map = "$(Get-ArchiveId -Base $base -Name 'Map.nx')/Map/Map1/100000004.img" }

$script:Last = $null

function New-Session([int]$x, [int]$y) {
  $b = @{ path = $Map; startX = $x; startY = $y; weapon = "01302000.img"; equippedWeapon = "01302000" } | ConvertTo-Json -Compress
  Invoke-RestMethod "$base/api/simulate/session" -Method Post -Body $b -ContentType "application/json" -TimeoutSec 300
}
function Step($sid, $keys) {
  $b = @{ count = 1; first = $keys; then = $keys } | ConvertTo-Json -Compress
  $script:Last = Invoke-RestMethod "$base/api/simulate/session/$sid/frames" -Method Post -Body $b -ContentType "application/json" -TimeoutSec 300
}
function Run($sid, $keys, $n) {
  $rows = New-Object System.Collections.ArrayList
  for ($i = 0; $i -lt $n; $i++) { Step $sid $keys; [void]$rows.Add($script:Last) }
  return $rows.ToArray()
}
# Hold a direction until |x - target| <= tol, or give up. Returns the rows walked.
function WalkTo($sid, [double]$target, [double]$tol, [int]$maxFrames) {
  $rows = New-Object System.Collections.ArrayList
  for ($i = 0; $i -lt $maxFrames; $i++) {
    $dir = if ([double]$script:Last.x -lt $target) { @{ right = $true } } else { @{ left = $true } }
    Step $sid $dir
    [void]$rows.Add($script:Last)
    if ([Math]::Abs([double]$script:Last.x - $target) -le $tol) { break }
  }
  return $rows.ToArray()
}
function OutOfBounds($rows) {
  $n = 0
  for ($i = 0; $i -lt $rows.Count; $i++) {
    $r = $rows[$i]
    if ([double]$r.x -lt $r.bounds.left -or [double]$r.x -gt $r.bounds.right -or
        [double]$r.y -lt $r.bounds.top  -or [double]$r.y -gt $r.bounds.bottom) { $n++ }
  }
  return $n
}

$e = [uri]::EscapeDataString($Map)
$cast = Invoke-RestMethod "$base/api/simulate/cast?path=$e&seed=1&steps=60" -TimeoutSec 300
$fail = 0; $pass = 0

foreach ($l in @($cast.ladderSpans)) {
  $lx = [double]$l.x; $top = [double]$l.top; $bot = [double]$l.bottom
  $pr = Invoke-RestMethod "$base/api/simulate/probe?path=$e&x=$([int]$lx)&y=$([int]$bot)" -TimeoutSec 300
  $platform = [double]$pr.landingSurface

  "--- ladder x=$lx top=$top bottom=$bot | platform surface $platform | reach y ($($top-12))..($($bot+12)) ---"
  $s = New-Session ([int]$lx) ([int]$platform)
  $w = WalkTo $s.sessionId $lx 4 400
  "  walk-to-ladder: x $($w[0].x) -> $($w[$w.Count-1].x) in $($w.Count) frames (action $(($w[$w.Count-1].action)))"
  $at = [double]$script:Last.x
  if ([Math]::Abs($at - $lx) -gt 8) { "  SKIP: could not get under the ladder (x=$at, box is $($lx-10)..$($lx+10))"; continue }

  $bnd = $script:Last.bounds
  $up = Run $s.sessionId @{ up = $true } 400
  $y0 = [double]$up[0].y; $y1 = [double]$up[$up.Count-1].y
  $note = ""
  if (($y0 - $y1) -le 20) {
    # A ladder whose bottom floats above the platform cannot be grabbed from a
    # standstill -- the client's box is ladder.Bottom + 12. Measured on x=2472:
    # bottom -183, platform -145, so the feet are 26px below the box. The client
    # grabs it mid-jump (MapleCharacter.cs:548 clears JumpState once OnLadder), so
    # that is what gets tried here. Not a defect; a reach question.
    $note = "  (bottom floats above the platform; retrying with a jump)"
    $sj = New-Session ([int]$lx) ([int]$platform)
    $wj = WalkTo $sj.sessionId $lx 4 400
    Run $sj.sessionId @{ up = $true } 4 | Out-Null          # settle
    Step $sj.sessionId @{ up = $true; jump = $true }
    $up = Run $sj.sessionId @{ up = $true } 400
    $y0 = [double]$up[0].y; $y1 = [double]$up[$up.Count-1].y
    $s = $sj
  }
  $ladderFrames = @($up | Where-Object { $_.onLadder }).Count
  "  UP   : y $y0 -> $y1  (climbed $($y0 - $y1) px)  ladderFrames=$ladderFrames/400  outOfBounds=$(OutOfBounds $up)  actions=$((@($up | ForEach-Object {$_.action}) | Sort-Object -Unique) -join ',')"
  if ($note) { $note }

  $down = Run $s.sessionId @{ down = $true } 400
  $d0 = [double]$down[0].y; $d1 = [double]$down[$down.Count-1].y
  $ladderFramesD = @($down | Where-Object { $_.onLadder }).Count
  "  DOWN : y $d0 -> $d1  (descended $($d1 - $d0) px)  ladderFrames=$ladderFramesD/400  outOfBounds=$(OutOfBounds $down)  actions=$((@($down | ForEach-Object {$_.action}) | Sort-Object -Unique) -join ',')"

  $okUp = ($y0 - $y1) -gt 20 -and (OutOfBounds $up) -eq 0
  $okDown = ($d1 - $d0) -gt 20 -and (OutOfBounds $down) -eq 0
  if ($okUp -and $okDown) { "  OK"; $pass++ } else { "  FAIL (up=$okUp down=$okDown)"; $fail++ }
}

""
"passed=$pass failed=$fail skipped=$($cast.ladderSpans.Count - $pass - $fail)"
if ($fail -eq 0) { "ALL LADDERS PASS" } else { "SOME LADDERS FAILED"; ">>> RED: $fail ladder(s) failed" }