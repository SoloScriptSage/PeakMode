<#
.SYNOPSIS
  PC Checkup spike: which of the Checkup's reads work without admin. Read-only.
.DESCRIPTION
  Queries the display modes, memory, power state and effective power mode,
  GPU drivers and adapters (D3DKMT), GPU Engine counters, Memory Integrity
  and thermal zones, and writes one line per read to a text file. Nothing is
  changed. Run it twice to compare: once from an elevated prompt and once
  from a normal one (or start it through explorer.exe, which uses the
  desktop's non-elevated token), then compare the two files.

    .\checkup-probe.ps1 -Label elevated
    .\checkup-probe.ps1 -Label standard

  Results on the test PC: docs/checkup.md.
#>
param([string]$Label = 'run', [string]$OutDir = (Join-Path $env:TEMP 'peakmode-checkup-probe'))
$out = Join-Path $OutDir "probe-$Label.txt"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Collections.Generic; using System.Threading;
public static class Spike {
  // ── display ──
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct DEVMODE {
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmDeviceName; public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
    public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput; public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmFormName; public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
    public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight; }
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct DISPLAY_DEVICE { public int cb; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string DeviceName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string DeviceString; public int StateFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string DeviceID, DeviceKey; }
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern bool EnumDisplayDevices(string dev, int i, ref DISPLAY_DEVICE dd, int f);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern bool EnumDisplaySettingsEx(string dev, int mode, ref DEVMODE dm, int f);
  public static string Displays() { var r = new List<string>();
    for (int i = 0; ; i++) { var dd = new DISPLAY_DEVICE(); dd.cb = Marshal.SizeOf(dd); if (!EnumDisplayDevices(null, i, ref dd, 0)) break; if ((dd.StateFlags & 1) == 0) continue;
      var cur = new DEVMODE(); cur.dmSize = (short)Marshal.SizeOf(cur); EnumDisplaySettingsEx(dd.DeviceName, -1, ref cur, 0);
      int max = 0; for (int m = 0; ; m++) { var dm = new DEVMODE(); dm.dmSize = (short)Marshal.SizeOf(dm); if (!EnumDisplaySettingsEx(dd.DeviceName, m, ref dm, 0)) break;
        if (dm.dmPelsWidth == cur.dmPelsWidth && dm.dmPelsHeight == cur.dmPelsHeight && dm.dmDisplayFrequency > max) max = dm.dmDisplayFrequency; }
      r.Add(dd.DeviceString + " " + cur.dmPelsWidth + "x" + cur.dmPelsHeight + " " + cur.dmDisplayFrequency + "/" + max + " Hz"); }
    return string.Join("; ", r); }

  // ── power ──
  [StructLayout(LayoutKind.Sequential)] public struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
  [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
  public static string Power() { SYSTEM_POWER_STATUS s; if (!GetSystemPowerStatus(out s)) return "failed " + Marshal.GetLastWin32Error();
    return "AC=" + s.ACLineStatus + " BatteryFlag=" + s.BatteryFlag + " EnergySaver=" + s.SystemStatusFlag; }
  public delegate void ModeCallback(int mode, IntPtr ctx);
  [DllImport("powrprof.dll")] static extern int PowerRegisterForEffectivePowerModeNotifications(uint version, ModeCallback cb, IntPtr ctx, out IntPtr handle);
  [DllImport("powrprof.dll")] static extern int PowerUnregisterFromEffectivePowerModeNotifications(IntPtr handle);
  static int _mode = -1; static ModeCallback _cb = (m, c) => { _mode = m; };
  public static string PowerMode() { IntPtr h; int hr = PowerRegisterForEffectivePowerModeNotifications(2, _cb, IntPtr.Zero, out h);
    if (hr != 0) return "register hr=0x" + hr.ToString("X8");
    for (int i = 0; i < 40 && _mode < 0; i++) Thread.Sleep(25);
    PowerUnregisterFromEffectivePowerModeNotifications(h);
    return "effective mode=" + _mode + " (0 saver,1 better battery,2 balanced,3 high perf,4 max perf,5 game,6 MR)"; }

  // ── D3DKMT: adapters and temperature ──
  [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint Low; public int High; }
  [StructLayout(LayoutKind.Sequential)] public struct ADAPTERINFO { public uint hAdapter; public LUID AdapterLuid; public uint NumOfSources; public int bPrecisePresentRegionsPreferred; }
  [StructLayout(LayoutKind.Sequential)] public struct ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }
  [StructLayout(LayoutKind.Sequential)] public struct QUERYADAPTERINFO { public uint hAdapter; public int Type; public IntPtr pPrivateDriverData; public uint PrivateDriverDataSize; }
  [StructLayout(LayoutKind.Sequential)] public struct CLOSEADAPTER { public uint hAdapter; }
  [StructLayout(LayoutKind.Explicit, Size=64)] public struct ADAPTER_PERFDATA {
    [FieldOffset(0)] public uint PhysicalAdapterIndex; [FieldOffset(8)] public ulong MemoryFrequency; [FieldOffset(16)] public ulong MaxMemoryFrequency;
    [FieldOffset(24)] public ulong MaxMemoryFrequencyOC; [FieldOffset(32)] public ulong MemoryBandwidth; [FieldOffset(40)] public ulong PCIEBandwidth;
    [FieldOffset(48)] public uint FanRPM; [FieldOffset(52)] public uint Power; [FieldOffset(56)] public uint Temperature; [FieldOffset(60)] public byte PowerStateOverride; }
  [DllImport("gdi32.dll")] static extern int D3DKMTEnumAdapters2(ref ENUMADAPTERS2 e);
  [DllImport("gdi32.dll")] static extern int D3DKMTQueryAdapterInfo(ref QUERYADAPTERINFO q);
  [DllImport("gdi32.dll")] static extern int D3DKMTCloseAdapter(ref CLOSEADAPTER c);
  const int KMTQAITYPE_ADAPTERPERFDATA = 62;   // d3dkmthk.h, SDK 10.0.22621
  public static string Adapters() {
    var e = new ENUMADAPTERS2(); int st = D3DKMTEnumAdapters2(ref e); if (st != 0) return "enum count status=0x" + st.ToString("X8");
    int sz = Marshal.SizeOf(typeof(ADAPTERINFO)); e.pAdapters = Marshal.AllocHGlobal(sz * (int)e.NumAdapters);
    var r = new List<string>();
    try {
      st = D3DKMTEnumAdapters2(ref e); if (st != 0) return "enum status=0x" + st.ToString("X8");
      for (int i = 0; i < e.NumAdapters; i++) {
        var a = (ADAPTERINFO)Marshal.PtrToStructure(e.pAdapters + i * sz, typeof(ADAPTERINFO));
        var pd = new ADAPTER_PERFDATA(); IntPtr buf = Marshal.AllocHGlobal(64);
        try { Marshal.StructureToPtr(pd, buf, false);
          var q = new QUERYADAPTERINFO { hAdapter = a.hAdapter, Type = KMTQAITYPE_ADAPTERPERFDATA, pPrivateDriverData = buf, PrivateDriverDataSize = 64 };
          int qs = D3DKMTQueryAdapterInfo(ref q); pd = (ADAPTER_PERFDATA)Marshal.PtrToStructure(buf, typeof(ADAPTER_PERFDATA));
          // KMTQAITYPE_ADAPTERTYPE = 15 (UINT bitfield), KMTQAITYPE_ADAPTERREGISTRYINFO = 8 (4 x WCHAR[260])
          IntPtr tb = Marshal.AllocHGlobal(4); IntPtr nb = Marshal.AllocHGlobal(4 * 260 * 2);
          string name = "?"; uint type = 0;
          try {
            var qt = new QUERYADAPTERINFO { hAdapter = a.hAdapter, Type = 15, pPrivateDriverData = tb, PrivateDriverDataSize = 4 };
            if (D3DKMTQueryAdapterInfo(ref qt) == 0) type = (uint)Marshal.ReadInt32(tb);
            var qn = new QUERYADAPTERINFO { hAdapter = a.hAdapter, Type = 8, pPrivateDriverData = nb, PrivateDriverDataSize = 4 * 260 * 2 };
            if (D3DKMTQueryAdapterInfo(ref qn) == 0) name = Marshal.PtrToStringUni(nb);
          } finally { Marshal.FreeHGlobal(tb); Marshal.FreeHGlobal(nb); }
          string flags = ((type & 1) != 0 ? "render " : "") + ((type & 2) != 0 ? "display " : "") + ((type & 4) != 0 ? "software " : "") +
                         ((type & 16) != 0 ? "hybridDiscrete " : "") + ((type & 32) != 0 ? "hybridIntegrated " : "") + ((type & (1u << 13)) != 0 ? "runtimePM" : "");
          r.Add(name + " [" + flags.Trim() + "] luid_0x" + a.AdapterLuid.High.ToString("X8") + "_0x" + a.AdapterLuid.Low.ToString("X8") + " perf status=0x" + qs.ToString("X8") + " temp=" + (pd.Temperature / 10.0) + "C powered=" + pd.PowerStateOverride);
        } finally { Marshal.FreeHGlobal(buf); var c = new CLOSEADAPTER { hAdapter = a.hAdapter }; D3DKMTCloseAdapter(ref c); }
      }
    } finally { Marshal.FreeHGlobal(e.pAdapters); }
    return string.Join("; ", r); }
}
'@

function Try-Read([string]$name, [scriptblock]$read) {
    try { "{0,-22} {1}" -f $name, (& $read) } catch { "{0,-22} ERROR {1}" -f $name, $_.Exception.Message }
}

& {
    "label: $Label  time: $(Get-Date -Format s)"
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    "user: $($id.Name)  admin role: $(([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))"
    "integrity: " + ((whoami /groups | Select-String 'Mandatory Label') -replace '\s{2,}.*','' -join ',')
    Try-Read 'display' { [Spike]::Displays() }
    Try-Read 'ram' { (Get-CimInstance Win32_PhysicalMemory -ErrorAction Stop | % { "$($_.BankLabel) type=$($_.SMBIOSMemoryType) form=$($_.FormFactor) $($_.ConfiguredClockSpeed)/$($_.Speed)" }) -join '; ' }
    Try-Read 'power status' { [Spike]::Power() }
    Try-Read 'power mode' { [Spike]::PowerMode() }
    Try-Read 'chassis' { (Get-CimInstance Win32_SystemEnclosure -ErrorAction Stop).ChassisTypes -join ',' }
    Try-Read 'gpu driver (WMI)' { (Get-CimInstance Win32_VideoController -ErrorAction Stop | % { "$($_.Name) $($_.DriverVersion) $($_.DriverDate.ToString('yyyy-MM-dd'))" }) -join '; ' }
    Try-Read 'gpu engine counters' { $s = (Get-Counter '\GPU Engine(*engtype_3D)\Utilization Percentage' -ErrorAction Stop).CounterSamples; "$($s.Count) instances, e.g. $($s[0].InstanceName)" }
    Try-Read 'd3dkmt adapters' { [Spike]::Adapters() }
    Try-Read 'hvci (WMI)' { $d = Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard Win32_DeviceGuard -ErrorAction Stop; "running={$($d.SecurityServicesRunning -join ',')} vbs=$($d.VirtualizationBasedSecurityStatus)" }
    Try-Read 'hvci (registry)' { $k = Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity' -ErrorAction Stop; "Enabled=$($k.Enabled)" }
    Try-Read 'thermal zone counter' { ((Get-Counter '\Thermal Zone Information(*)\Temperature' -ErrorAction Stop).CounterSamples | % { "$($_.InstanceName)=$([math]::Round($_.CookedValue - 273.15,1))C" }) -join '; ' }
    Try-Read 'thermal zone (WMI)' { ((Get-CimInstance -Namespace root\wmi MSAcpi_ThermalZoneTemperature -ErrorAction Stop) | % { "$([math]::Round($_.CurrentTemperature / 10 - 273.15,1))C" }) -join '; ' }
} *>&1 | Out-File -Encoding utf8 $out
