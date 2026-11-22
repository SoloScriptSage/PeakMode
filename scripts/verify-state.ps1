#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Read-only capture of every setting PeakMode can change, as sorted
  "key = value" lines. Two captures can be compared with Compare-Object.
.EXAMPLE
  .\verify-state.ps1 -PeakModeTest ..\core\build\Release\peakmode_test.exe -Out before.txt
#>
param(
    [Parameter(Mandatory)] [string] $PeakModeTest,
    [string] $Config = "$PSScriptRoot\..\config\peakmode.json",
    [string] $TargetProcess = "",
    # Read this adapter's keys directly instead of relying on PeakMode's
    # auto-detect (which needs the link up, e.g. not mid-restart)
    [string] $NicInstanceId = "",
    [string] $Out = ""
)
$ErrorActionPreference = 'Stop'
$lines = [System.Collections.Generic.List[string]]::new()
function Add([string]$k, $v) { $lines.Add("$k = $v") }

# Power plans
foreach ($l in (powercfg /list) -match 'GUID') {
    if ($l -match '([0-9a-f-]{36})\s+\((.*?)\)(\s*\*)?') {
        Add "power.scheme.$($Matches[1])" ("$($Matches[2])" + $(if ($Matches[3]) { ' [active]' } else { '' }))
    }
}

# Services
foreach ($s in 'SysMain', 'WSearch') {
    $svc = Get-Service $s
    Add "service.$s" "$($svc.Status) / $($svc.StartType)"
}

# Nagle values per interface (absent is recorded explicitly)
$ifRoot = 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces'
foreach ($k in Get-ChildItem $ifRoot) {
    foreach ($n in 'TcpAckFrequency', 'TCPNoDelay') {
        $v = (Get-ItemProperty $k.PSPath -Name $n -ErrorAction SilentlyContinue).$n
        Add "tcpip.$($k.PSChildName).$n" $(if ($null -eq $v) { '<absent>' } else { $v })
    }
}

# Core-reported values: NIC identity, DRS settings, timer, core mask
$dump = & $PeakModeTest dump $Config 2>&1
$nicInst = $null; $nicDriver = $null
foreach ($l in $dump) {
    $t = ($l -replace '^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d ', '').Trim()
    if ($NicInstanceId -and $t -match '^nic\.') { continue }
    if ($t -match '^(drs\.\S+|power\.active|cpu\.\S+|nic\.\S+) = (.*)$') { Add $Matches[1] $Matches[2] }
    if ($t -match '^nic\.instanceId = (.*)$') { $nicInst = $Matches[1] }
    if ($t -match '^nic\.driverKey = (.*)$')  { $nicDriver = $Matches[1] }
}

if ($NicInstanceId) {
    # Find the driver key by DeviceInstanceID ourselves
    $cls = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}'
    $match = Get-ChildItem $cls -ErrorAction SilentlyContinue | Where-Object {
        (Get-ItemProperty $_.PSPath -Name DeviceInstanceID -ErrorAction SilentlyContinue).DeviceInstanceID -eq $NicInstanceId
    } | Select-Object -First 1
    $nicInst = $NicInstanceId
    Add 'nic.instanceId' $nicInst
    if ($match) {
        $nicDriver = $match.Name -replace '^HKEY_LOCAL_MACHINE\\', ''
        Add 'nic.driverKey' $nicDriver
    } else { Add 'nic.driverKey' '<not found>' }
}

if ($nicInst) {
    # IRQ affinity policy key (absent key/values recorded explicitly)
    $aff = "HKLM:\SYSTEM\CurrentControlSet\Enum\$nicInst\Device Parameters\Interrupt Management\Affinity Policy"
    $mgmt = Split-Path $aff
    Add 'nic.irq.InterruptManagementKey' (Test-Path -LiteralPath $mgmt)
    Add 'nic.irq.AffinityPolicyKey' (Test-Path -LiteralPath $aff)
    if (Test-Path -LiteralPath $aff) {
        $p = Get-ItemProperty -LiteralPath $aff
        foreach ($n in 'DevicePolicy', 'AssignmentSetOverride', 'AffinityPolicy') {
            $v = $p.$n
            if ($v -is [byte[]]) { $v = ($v | ForEach-Object { $_.ToString('x2') }) -join '' }
            Add "nic.irq.$n" $(if ($null -eq $v) { '<absent>' } else { $v })
        }
    }
    # Adapter advanced properties
    $dk = Get-ItemProperty -LiteralPath "HKLM:\$nicDriver"
    foreach ($n in '*InterruptModeration', '*EEE', 'GreenEthernet', 'PowerSavingMode', 'GigaLite',
                   '*WakeOnMagicPacket', '*WakeOnPattern', '*ReceiveBuffers', '*TransmitBuffers',
                   'ReceiveBuffers', 'TransmitBuffers') {
        $v = $dk.$n
        Add "nic.prop.$n" $(if ($null -eq $v) { '<absent>' } else { $v })
    }
    $dev = Get-PnpDevice -InstanceId $nicInst -ErrorAction SilentlyContinue
    Add 'nic.device.status' $dev.Status
}

if ($TargetProcess) {
    $p = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($TargetProcess)) -ErrorAction SilentlyContinue |
         Select-Object -First 1
    if ($p) {
        Add 'target.priority' $p.PriorityClass
        Add 'target.affinity' ('0x{0:X}' -f [int64]$p.ProcessorAffinity)
    } else { Add 'target' '<not running>' }
}

Add 'peakmode.snapshot' (Test-Path 'C:\ProgramData\PeakMode\snapshot.json')

$sorted = $lines | Sort-Object
if ($Out) { $sorted | Set-Content -Encoding utf8 $Out }
$sorted
