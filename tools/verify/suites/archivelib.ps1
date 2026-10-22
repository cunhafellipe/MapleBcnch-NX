# Resolve an archive's session id BY NAME.
#
# The ids are handed out in the order the archives were opened, so "f4" is
# Map.nx on one instance and something else on another. A suite that
# hard-codes an id measures a different archive than the one it means to --
# and on an instance where that id names something else it measures the WRONG
# thing while looking like it measured a difference.
#
# An archive that is not open is a failure that says so: the red marker the
# gate reads, and a non-zero exit. The ways it can fail are told apart -- the
# archives could not be listed, or the archive is listed but carries no id --
# so "this instance is not set up" never looks like "this suite is broken".
#
# ASCII only (tools/verify/AGENTS.md rule 1): a non-ASCII byte in here can
# corrupt the path and make the measurement return nothing.

function Get-ArchiveId {
  param([string]$Base, [string]$Name)

  $archives = $null
  try { $archives = Invoke-RestMethod "$Base/api/files" -TimeoutSec 120 }
  catch { ">>> RED: could not list the open archives on this instance: $($_.Exception.Message)"; exit 1 }

  # /api/files must answer with a LIST of archive entries, and nothing else does.
  # Measured 2026-10-23 under Windows PowerShell 5.1, by asking
  # Invoke-RestMethod what it hands back:
  #     text/html        -> XmlDocument   (it parses the HTML as XML)
  #     JSON scalar 42   -> Int32
  #     text/plain       -> String
  #     JSON array [..]  -> Object[]      <- the only shape /api/files promises
  # None of the wrong shapes THROWS either, so they used to fall through to
  # "is not open on this instance" -- "this instance is not set up" wearing the
  # face of "could not list", which is what this file's own header forbids.
  # NOTE the one trap here: do NOT test with `-is [PSCustomObject]`. Measured,
  # that is True for an XmlDocument, an Int32 and a String as well, because
  # PowerShell wraps every value in a PSObject -- it discriminates nothing.
  # A one-entry JSON array was also measured as Object[], not unrolled, so the
  # array test does not reject a single-archive instance.
  if ($archives -isnot [System.Array]) {
    ">>> RED: could not list the open archives on this instance: the instance answered with $($archives.GetType().Name), not a list of archives (is the URL right?)"; exit 1
  }

  # -ceq, not -eq: PowerShell's -eq is CASE-INSENSITIVE on strings, so this
  # resolver was the only one of the three that matched MAP.NX against Map.nx
  # and reported a hit the other two called "not open".
  $hit = @($archives) | Where-Object { $_.name -ceq $Name } | Select-Object -First 1
  if ($null -eq $hit) { ">>> RED: $Name is not open on this instance, so there is nothing to judge"; exit 1 }
  if (-not $hit.id) { ">>> RED: $Name is listed by this instance but carries no id"; exit 1 }
  return $hit.id
}
