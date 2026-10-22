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

  $hit = @($archives) | Where-Object { $_.name -eq $Name } | Select-Object -First 1
  if ($null -eq $hit) { ">>> RED: $Name is not open on this instance, so there is nothing to judge"; exit 1 }
  if (-not $hit.id) { ">>> RED: $Name is listed by this instance but carries no id"; exit 1 }
  return $hit.id
}
