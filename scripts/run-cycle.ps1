#Requires -RunAsAdministrator
<#
.SYNOPSIS
  One test cycle: baseline -> activate -> capture -> restore -> compare.
  Restore ALWAYS runs (finally), and the NIC is force-enabled afterwards if
  it is not up, even when a step fails or the script is interrupted.

  Every cycle restores from a fresh process (peakmode_test activate exits
  without restoring), so each one also exercises crash/reboot recovery.

.EXAMPLE
  .\run-cycle.ps1 -PeakModeTest ..\core\build\Release\peakmode_test.exe `
                  -Config nagle-only.json -Name nagle
#>
param(
    [Parameter(Mandatory)] [string] $PeakModeTest,
    [Parameter(Mandatory)] [string] $Config,
    [Parameter(Mandatory)] [string] $Name,
    [int]    $DieAfter = 0,          # simulate a crash after N snapshot entries
    [string] $TargetProcess = "",
    [string] $OutDir = "$PSScriptRoot\..\test-results"
)
$ErrorActionPreference = 'Stop'
$verify = "$PSScriptRoot\verify-state.ps1"
$dir = Join-Path $OutDir $Name
New-Item -ItemType Directory -Force $dir | Out-Null

function Capture([string]$file) {
    & $verify -PeakModeTest $PeakModeTest -Config $Config -TargetProcess $TargetProcess `
              -NicInstanceId $nicId -Out (Join-Path $dir $file) | Out-Null
    Get-Content (Join-Path $dir $file)
}

# After an adapter restart the link needs a few seconds; capture once it is up
function Wait-NicUp([int]$timeoutSec = 30) {
    if (-not $nicId) { return }
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutSec) {
        $a = Get-NetAdapter -Physical -ErrorAction SilentlyContinue |
             Where-Object PnPDeviceID -eq $nicId
        if ($a -and $a.Status -eq 'Up') { return }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "warning: adapter not Up after $timeoutSec s" -ForegroundColor Yellow
}
function Get-StateDiff($a, $b) { Compare-Object $a $b | ForEach-Object { "  $($_.SideIndicator) $($_.InputObject)" } }

# NIC id for the safety net, read before anything changes
$nicId = ((& $PeakModeTest dump $Config) -match 'nic\.instanceId = ' | Select-Object -First 1) -replace '.*nic\.instanceId = ', ''

Write-Host "=== cycle '$Name' ===" -ForegroundColor Cyan
Write-Host "If anything goes wrong, see scripts\RECOVERY.md"
$base = Capture 'before.txt'
$activateRc = $null; $restoreRc = $null; $during = @()

try {
    $pmArgs = @('activate', $Config)
    if ($DieAfter -gt 0) { $pmArgs += @('--die-after', $DieAfter) }
    & $PeakModeTest @pmArgs 2>&1 | Tee-Object (Join-Path $dir 'activate.log') | Out-Host
    $activateRc = $LASTEXITCODE
    Wait-NicUp
    $during = Capture 'during.txt'
    Write-Host "--- changes while active ---" -ForegroundColor Yellow
    $d = Get-StateDiff $base $during
    if ($d) { $d | Out-Host } else { Write-Host "  (none)" }
}
finally {
    try {
        & $PeakModeTest restore 2>&1 | Tee-Object (Join-Path $dir 'restore.log') | Out-Host
        $restoreRc = $LASTEXITCODE
    } catch { Write-Host "restore threw: $_" -ForegroundColor Red }

    # Safety net: the network must come back no matter what
    if ($nicId) {
        try {
            $dev = Get-PnpDevice -InstanceId $nicId -ErrorAction Stop
            if ($dev.Status -ne 'OK') {
                Write-Host "NIC status '$($dev.Status)': force-enabling" -ForegroundColor Red
                Enable-PnpDevice -InstanceId $nicId -Confirm:$false
            }
        } catch { Write-Host "NIC safety net failed: $_" -ForegroundColor Red }
    }
}

Wait-NicUp
$after = Capture 'after.txt'
$residue = Get-StateDiff $base $after

Write-Host "--- result ---" -ForegroundColor Cyan
Write-Host "activate exit: $activateRc   restore exit: $restoreRc"
$pass = (-not $residue) -and ($restoreRc -eq 0)
if ($residue) { Write-Host "Differences after restore:" -ForegroundColor Red; $residue | Out-Host }
if ($pass) { Write-Host "PASS: state after restore == baseline" -ForegroundColor Green }
else       { Write-Host "FAIL" -ForegroundColor Red }
@{ name = $Name; activateExit = $activateRc; restoreExit = $restoreRc; pass = $pass;
   changedWhileActive = ($during.Count -gt 0 -and [bool](Get-StateDiff $base $during)) } |
    ConvertTo-Json | Set-Content (Join-Path $dir 'result.json')
if (-not $pass) { exit 1 }
