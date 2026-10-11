$ErrorActionPreference = 'Stop'
$dotnet = 'C:\MUWORK\app\scoop\apps\dotnet-sdk\current\dotnet.exe'
$exe = 'C:\MUWORK\GAME\MAPLESOTRY\wz工具\追風WZ編輯器\agent-wz\src\NxVerify\bin\Release\net10.0-windows\NxVerify.exe'
$src  = 'C:\MUWORK\GAME\MPSH'
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