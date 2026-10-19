# Windows benchmark run (counterpart of bench_s1.sh/run.sh). Runs BOTW-PC in standalone mode on a DISPOSABLE data
# directory whose save is restored from -SaveSource before every run, so each run starts from the same save point.
#
#   bench_win.ps1 -Label <name> -Data <disposable data dir> -SaveSource <dir copied to <Data>\bis\user\save>
#                 [-Bin <dir with Ryujinx.exe>] [-Secs 170] [-InputScript <script>] [-SetEnv KEY=VALUE,...] [-Out <results root>]
#                 [-RefreshHz 0|120|180|240]   (frame-rate mode in <Data>\Config.json: 0 = Switch VSync / 30 FPS,
#                                               120/180/240 Hz custom refresh = 60/90/120 FPS; default: leave as is)
#
# Results in <Out>\<Label>: framelog.csv, counters.csv, gpu.csv (nvidia-smi, measurement window only), cpu.txt,
# stdout.log, shot.png (one screenshot before the measurement window, to verify the scene), summary.json.
# Statistics skip the first 90 s of frames (loading + warm-up). Never point -Data at a real user data directory.
# Use a separate copy of the application for -Bin: Ryujinx keeps only the newest 3 files in <Bin>\Logs and deletes older
# ones at start, so benchmarking the player's installed copy rotates the player's own logs away.
param(
    [Parameter(Mandatory)] [string]$Label,
    [Parameter(Mandatory)] [string]$Data,
    [Parameter(Mandatory)] [string]$SaveSource,
    [string]$Bin = (Join-Path $PSScriptRoot '..\..\..\botw-pc-dev'),
    [int]$Secs = 170,
    [string]$InputScript = (Join-Path $PSScriptRoot '..\bench\continue.input'),
    [string[]]$SetEnv = @(),
    [string]$Out = (Join-Path $PSScriptRoot '..\..\..\bench'),
    [int]$RefreshHz = -1
)
$ErrorActionPreference = 'Stop'
$Bin = [IO.Path]::GetFullPath($Bin); $Data = [IO.Path]::GetFullPath($Data); $dir = Join-Path ([IO.Path]::GetFullPath($Out)) $Label
if (Get-Process Ryujinx -ErrorAction SilentlyContinue) { throw 'another Ryujinx instance is running' }
New-Item -ItemType Directory -Force $dir | Out-Null

if ($RefreshHz -ge 0) {
    $cfgPath = Join-Path $Data 'Config.json'
    $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
    $cfg.vsync_mode = $(if ($RefreshHz -eq 0) { 0 } else { 2 })
    $cfg.enable_custom_vsync_interval = ($RefreshHz -gt 0)
    if ($RefreshHz -gt 0) { $cfg.custom_vsync_interval = $RefreshHz }
    [IO.File]::WriteAllText($cfgPath, ($cfg | ConvertTo-Json -Depth 20), (New-Object Text.UTF8Encoding $false))
}

# Same starting point every run.
$save = Join-Path $Data 'bis\user\save'
if (Test-Path $save) { Remove-Item -Recurse -Force $save }
Copy-Item -Recurse $SaveSource $save
Remove-Item (Join-Path $Data 'screenshots\*') -ErrorAction SilentlyContinue

# Every BOTWPC_* variable, not a fixed list: a -SetEnv switch of an earlier run in the same PowerShell session must not
# leak into this one (it did: a causal delay of one configuration stayed set for the following ones).
Get-ChildItem Env: | Where-Object Name -like 'BOTWPC_*' | ForEach-Object { Remove-Item "Env:$($_.Name)" }
foreach ($kv in $SetEnv) { $k, $v = $kv -split '=', 2; Set-Item "Env:$k" $v }
$env:BOTWPC_FRAMELOG = Join-Path $dir 'framelog.csv'
$env:BOTWPC_INPUT_SCRIPT = [IO.Path]::GetFullPath($InputScript)
$env:BOTWPC_AUTO_CONTROLLER = '0'
if (-not $env:BOTWPC_CAPTURE_INTERVAL) { $env:BOTWPC_CAPTURE_INTERVAL = '85' }
# Record exactly which switches this run had.
Get-ChildItem Env: | Where-Object Name -like 'BOTWPC_*' | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" } |
    Set-Content (Join-Path $dir 'env.txt')

$p = Start-Process (Join-Path $Bin 'Ryujinx.exe') -ArgumentList @('--botw', '-r', "`"$Data`"") -WorkingDirectory $Bin -PassThru
Start-Sleep 100
$smi = Start-Process nvidia-smi -ArgumentList '--query-gpu=timestamp,utilization.gpu,clocks.gr,memory.used,power.draw',
    '--format=csv,noheader,nounits', '-lms', '500', '-f', "`"$(Join-Path $dir 'gpu.csv')`"" -WindowStyle Hidden -PassThru
$cpu0 = (Get-Process -Id $p.Id).TotalProcessorTime.TotalSeconds; $t0 = Get-Date
if (-not $p.WaitForExit(($Secs - 100) * 1000)) {
    $q = Get-Process -Id $p.Id
    $cores = ($q.TotalProcessorTime.TotalSeconds - $cpu0) / ((Get-Date) - $t0).TotalSeconds
    "cpu_cores_mean=$([math]::Round($cores, 2)) of $([Environment]::ProcessorCount) logical; ws_mb=$([int]($q.WorkingSet64 / 1MB)) private_mb=$([int]($q.PrivateMemorySize64 / 1MB))" |
        Set-Content (Join-Path $dir 'cpu.txt')
    Stop-Process -Id $p.Id -Force; $p.WaitForExit(15000) | Out-Null
} else { 'exited early' | Set-Content (Join-Path $dir 'cpu.txt') }
Stop-Process -Id $smi.Id -Force -ErrorAction SilentlyContinue
Start-Sleep 1

$log = Get-ChildItem (Join-Path $Bin 'Logs') -File | Sort-Object LastWriteTime -Descending | Select-Object -First 1
Copy-Item $log.FullName (Join-Path $dir 'stdout.log')
$shots = @(Get-ChildItem (Join-Path $Data 'screenshots') -Filter *.png -ErrorAction SilentlyContinue | Sort-Object Name)
if ($shots.Count -gt 0) { Copy-Item $shots[0].FullName (Join-Path $dir 'shot.png') -Force }
if ($shots.Count -gt 1) {
    # Dense captures (BOTWPC_CAPTURE_INTERVAL with a window, set through -SetEnv): keep them all.
    New-Item -ItemType Directory -Force (Join-Path $dir 'shots') | Out-Null
    $shots | Move-Item -Destination (Join-Path $dir 'shots') -Force
} elseif ($shots.Count -eq 1) { Remove-Item $shots[0].FullName }
$stats = Join-Path $PSScriptRoot 'frametime_stats.py'
py -3 -I $stats (Join-Path $dir 'framelog.csv') --skip 90 --label $Label --counters (Join-Path $dir 'counters.csv') --gpu (Join-Path $dir 'gpu.csv') |
    Out-File -Encoding utf8 (Join-Path $dir 'summary.json')
Get-Content (Join-Path $dir 'cpu.txt')
Select-String -Path (Join-Path $dir 'stdout.log') -Pattern 'DLSS: (active|feature created)|\|E\|' | Select-Object -First 4 | ForEach-Object { $_.Line.Substring(0, [math]::Min(200, $_.Line.Length)) }
