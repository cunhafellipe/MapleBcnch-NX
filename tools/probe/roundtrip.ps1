$ErrorActionPreference = 'Stop'
$dotnet = if ($env:DOTNET_PATH) { $env:DOTNET_PATH } else { 'dotnet' }
$exe = if ($env:NXVERIFY_EXE) { $env:NXVERIFY_EXE } else { 'tools/NxVerify/bin/Release/net10.0-windows/NxVerify.exe' }
$src  = $env:NX_FIXTURE_DIR
$out  = 'C:\nxtmp'
New-Item -ItemType Directory -Force -Path $out | Out-Null

foreach ($f in (Get-ChildItem -Path $src -Filter *.nx | Sort-Object Name)) {
    $name = $f.BaseName
    $dst  = Join-Path $out "$name.nx"
    if (Test-Path $dst) { Remove-Item $dst -Force }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & $exe --nxwrite $f.FullName $dst 2>&1 | ForEach-Object { "$name`t$_" }
    $sw.Stop()
    "TIMING`t$name`t$([int]$sw.Elapsed.TotalSeconds)s"
}
"DONE_ALL"