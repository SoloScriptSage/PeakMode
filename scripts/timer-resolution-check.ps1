<#
.SYNOPSIS
  Does PeakMode's timer resolution reach other processes? Measures how long
  Sleep(1) really takes in this process, which asks for no timer resolution
  of its own, like a game that never calls timeBeginPeriod.

.DESCRIPTION
  Since Windows 10 version 2004, timeBeginPeriod "no longer affects global
  timer resolution": only the calling process gets the higher resolution
  (https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod).
  PeakMode requests 0.5 ms with NtSetTimerResolution. If that has the same
  per-process scope, Sleep(1) here stays near 15.6 ms while PeakMode is
  active; if it is still global, Sleep(1) drops to about 1 ms.

  Read-only: it queries the timer and sleeps. With -RequestOneMs it also
  calls timeBeginPeriod(1) for this process only, as a control, and ends it
  before exiting.

  Run it twice and compare:
    1. PeakMode idle:    .\timer-resolution-check.ps1 -Label idle
    2. PeakMode active:  .\timer-resolution-check.ps1 -Label active
  Optionally a third time with -RequestOneMs to see what a process that asks
  for 1 ms itself gets.

.PARAMETER Samples
  Number of Sleep(1) calls to time (default 200).
#>
param(
    [int]    $Samples = 200,
    [string] $Label = '',
    [switch] $RequestOneMs
)
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
public static class PmTimerCheck {
    [DllImport("ntdll.dll")] static extern int NtQueryTimerResolution(out uint min, out uint max, out uint cur);
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
    [DllImport("kernel32.dll")] static extern void Sleep(uint ms);

    // Values in 100 ns units; "min" is the coarsest resolution, "max" the finest
    public static double[] Query() {
        uint min, max, cur;
        int s = NtQueryTimerResolution(out min, out max, out cur);
        if (s != 0) throw new InvalidOperationException("NtQueryTimerResolution failed: 0x" + s.ToString("X8"));
        return new[] { min / 10000.0, max / 10000.0, cur / 10000.0 };
    }

    public static double[] TimeSleeps(int n) {
        var r = new double[n];
        var sw = new Stopwatch();
        Sleep(1);   // align to a tick first
        for (int i = 0; i < n; i++) {
            sw.Restart(); Sleep(1); sw.Stop();
            r[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(r);
        return r;
    }
}
'@

$requested = $false
if ($RequestOneMs) { $requested = ([PmTimerCheck]::timeBeginPeriod(1) -eq 0) }
try {
    $q = [PmTimerCheck]::Query()
    $s = [PmTimerCheck]::TimeSleeps($Samples)
} finally {
    if ($requested) { [void][PmTimerCheck]::timeEndPeriod(1) }
}

function Pct([double[]]$sorted, [double]$p) { $sorted[[Math]::Min($sorted.Length - 1, [int]($p * $sorted.Length))] }
$median = Pct $s 0.5

$os = Get-CimInstance Win32_OperatingSystem
$peak = Get-Process PeakModeTray -ErrorAction SilentlyContinue

[pscustomobject]@{
    Label                 = $Label
    Time                  = (Get-Date).ToString('s')
    Windows               = "$($os.Caption) $($os.Version)"
    PeakModeTrayRunning   = [bool]$peak
    ThisProcessRequests1ms = $requested
    CoarsestMs            = [Math]::Round($q[0], 3)
    FinestMs              = [Math]::Round($q[1], 3)
    CurrentGlobalMs       = [Math]::Round($q[2], 3)
    Sleep1MedianMs        = [Math]::Round($median, 3)
    Sleep1P95Ms           = [Math]::Round((Pct $s 0.95), 3)
    Sleep1MinMs           = [Math]::Round($s[0], 3)
    Sleep1MaxMs           = [Math]::Round($s[-1], 3)
    Verdict               = if ($median -lt 3) { 'fine timer reaches this process' } else { 'this process gets the default timer' }
}
