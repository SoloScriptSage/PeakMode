#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Tray app test cycle (needs a person clicking). The script watches
  snapshot.json to follow what the user does, captures state at each
  step and compares with the baseline. Cleanup always runs (finally):
  restore any leftover snapshot, force-enable the NIC, close the tray.

  User steps, in order (each waits up to -StepTimeout seconds):
    1. Left-click the tray icon          -> activate
    2. Left-click again                  -> restore
    3. Left-click again                  -> activate   (script then kills the tray)
    4. Click "Yes" in the restore prompt -> restore leftover after "crash"
    5. Left-click                        -> activate
    6. Right-click > Exit                -> restore on exit
#>
param(
    [Parameter(Mandatory)] [string] $PeakModeTest,
    [Parameter(Mandatory)] [string] $TrayExe,
    [Parameter(Mandatory)] [string] $NicInstanceId,
    [int]    $StepTimeout = 300,
    [string] $OutDir = "$PSScriptRoot\..\test-results\11-tray"
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$snap   = 'C:\ProgramData\PeakMode\snapshot.json'
$verify = "$PSScriptRoot\verify-state.ps1"
$log    = Join-Path $OutDir 'tray-cycle.log'
$results = [ordered]@{}

function Say([string]$m) { $line = "$(Get-Date -Format HH:mm:ss) $m"; Add-Content $log $line; Write-Host $line }
function Capture([string]$f) {
    & $verify -PeakModeTest $PeakModeTest -NicInstanceId $NicInstanceId -Out (Join-Path $OutDir $f) | Out-Null
    Get-Content (Join-Path $OutDir $f)
}
function TimerRes { ((& $PeakModeTest dump) -match 'timer\.current = (\d+)' | Select-Object -First 1) -replace '.*= (\d+).*', '$1' }
function SnapState {
    if (-not (Test-Path $snap)) { return 'none' }
    try { (Get-Content $snap -Raw | ConvertFrom-Json).state } catch { 'unreadable' }
}
function WaitFor([string]$what, [scriptblock]$cond) {
    Say "WAITING: $what"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $StepTimeout) {
        if (& $cond) { Say "  ok after $([int]$sw.Elapsed.TotalSeconds)s"; return }
        Start-Sleep -Milliseconds 500
    }
    throw "timeout: $what"
}
function Wait-NicUp {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt 30) {
        $a = Get-NetAdapter -Physical | Where-Object PnPDeviceID -eq $NicInstanceId
        if ($a.Status -eq 'Up') { return }
        Start-Sleep -Milliseconds 500
    }
    Say "warning: NIC not Up after 30s"
}
function Check([string]$name, $base, $after) {
    $d = Compare-Object $base $after
    $results[$name] = -not $d
    if ($d) { Say "FAIL $name"; $d | ForEach-Object { Say "   $($_.SideIndicator) $($_.InputObject)" } }
    else    { Say "PASS ${name}: state == baseline" }
}
function Start-Tray { $p = Start-Process $TrayExe -PassThru; Start-Sleep 2; Say "tray started (pid $($p.Id))"; $p }

if (Test-Path $snap) { throw "a snapshot already exists; restore it before running this cycle" }
if (Get-Process PeakModeTray -ErrorAction SilentlyContinue) { throw "PeakModeTray is already running; exit it first" }

$tray = $null
try {
    $base = Capture 'before.txt'
    $t0 = TimerRes; Say "timer before: $t0 (100ns)"
    $tray = Start-Tray

    # 1-2: activate / restore through the tray
    WaitFor 'step 1: left-click to ACTIVATE' { (SnapState) -eq 'active' }
    Wait-NicUp
    $during = Capture 'during.txt'
    $t1 = TimerRes; Say "timer while active: $t1 (100ns)"
    $results['timerHeldWhileActive'] = ([int]$t1 -le 5000)
    $results['changedWhileActive']  = [bool](Compare-Object $base $during)

    WaitFor 'step 2: left-click to RESTORE' { (SnapState) -eq 'none' }
    Wait-NicUp
    Check 'toggle-restore' $base (Capture 'after-toggle.txt')
    $t2 = TimerRes; Say "timer after restore: $t2 (100ns)"
    $results['timerReleasedAfterRestore'] = ([int]$t2 -gt 5000)

    # 3-4: crash while active, relaunch, accept the restore prompt
    WaitFor 'step 3: left-click to ACTIVATE (tray will then be killed)' { (SnapState) -eq 'active' }
    Wait-NicUp
    Stop-Process -Id $tray.Id -Force
    Say "tray killed (simulated crash); snapshot present: $(Test-Path $snap)"
    $tray = Start-Tray
    WaitFor 'step 4: click YES in the restore prompt' { (SnapState) -eq 'none' }
    Wait-NicUp
    Check 'crash-prompt-restore' $base (Capture 'after-crash.txt')

    # 5-6: exit while active restores
    WaitFor 'step 5: left-click to ACTIVATE' { (SnapState) -eq 'active' }
    Wait-NicUp
    WaitFor 'step 6: right-click > Exit' { $tray.HasExited }
    Wait-NicUp
    Check 'exit-restores' $base (Capture 'after-exit.txt')
    $results['noSnapshotLeft'] = -not (Test-Path $snap)
}
catch { Say "ERROR: $_"; $results['error'] = "$_" }
finally {
    if ($tray -and -not $tray.HasExited) { Stop-Process -Id $tray.Id -Force; Say "tray closed by cleanup" }
    if (Test-Path $snap) {
        Say "cleanup: restoring leftover snapshot"
        & $PeakModeTest restore 2>&1 | ForEach-Object { Say "  $_" }
    }
    $dev = Get-PnpDevice -InstanceId $NicInstanceId -ErrorAction SilentlyContinue
    if ($dev -and $dev.Status -ne 'OK') {
        Say "cleanup: NIC status '$($dev.Status)', force-enabling"
        Enable-PnpDevice -InstanceId $NicInstanceId -Confirm:$false
    }
    Wait-NicUp
    if ($base) { Check 'final' $base (Capture 'after-final.txt') }
    $results | ConvertTo-Json | Set-Content (Join-Path $OutDir 'result.json')
    Say ("RESULT: " + ($results | ConvertTo-Json -Compress))
}
