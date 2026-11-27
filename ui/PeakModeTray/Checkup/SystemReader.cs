using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace PeakModeTray.Checkup
{
    // The real readers. Every call only queries: no handle is opened for
    // writing and nothing is set. All of them work without admin (spike in
    // docs/checkup.md). Each method returns empty or unknown values instead
    // of throwing when its API is missing or fails.
    internal sealed class SystemReader : ISystemReader
    {
        // ── displays ─────────────────────────────────────────────────────────
        public IReadOnlyList<DisplayReading> Displays()
        {
            var names = MonitorNames();
            var list = new List<DisplayReading>();
            for (int i = 0; ; i++)
            {
                var dd = new Native.DISPLAY_DEVICE { cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
                if (!Native.EnumDisplayDevicesW(null, i, ref dd, 0)) break;
                if ((dd.StateFlags & Native.DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) == 0) continue;
                var cur = NewDevMode();
                if (!Native.EnumDisplaySettingsExW(dd.DeviceName, Native.ENUM_CURRENT_SETTINGS, ref cur, 0)) continue;
                int max = 0;
                for (int m = 0; ; m++)
                {
                    var dm = NewDevMode();
                    if (!Native.EnumDisplaySettingsExW(dd.DeviceName, m, ref dm, 0)) break;
                    if (dm.dmPelsWidth == cur.dmPelsWidth && dm.dmPelsHeight == cur.dmPelsHeight)
                        max = Math.Max(max, dm.dmDisplayFrequency);
                }
                names.TryGetValue(dd.DeviceName, out var n);
                list.Add(new DisplayReading(n.Name ?? "", n.BuiltIn, dd.DeviceString ?? "",
                    cur.dmPelsWidth, cur.dmPelsHeight, cur.dmDisplayFrequency, Math.Max(max, cur.dmDisplayFrequency)));
            }
            return list;
        }

        private static Native.DEVMODE NewDevMode() => new() { dmSize = (short)Marshal.SizeOf<Native.DEVMODE>() };

        // GDI device name (\\.\DISPLAY1) → monitor name and built-in flag
        private static Dictionary<string, (string? Name, bool BuiltIn)> MonitorNames()
        {
            var map = new Dictionary<string, (string?, bool)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                Native.DISPLAYCONFIG_PATH_INFO[] paths;
                uint np, nm;
                int rc;
                do
                {
                    if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out np, out nm) != 0) return map;
                    paths = new Native.DISPLAYCONFIG_PATH_INFO[np];
                    var modes = new Native.DISPLAYCONFIG_MODE_INFO[nm];
                    rc = Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
                } while (rc == Native.ERROR_INSUFFICIENT_BUFFER);
                if (rc != 0) return map;
                foreach (var path in paths.Take((int)np))
                {
                    var src = new Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME();
                    src.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
                    src.header.size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
                    src.header.adapterId = path.sourceInfo.adapterId;
                    src.header.id = path.sourceInfo.id;
                    if (Native.DisplayConfigGetDeviceInfo(ref src) != 0) continue;

                    var tgt = new Native.DISPLAYCONFIG_TARGET_DEVICE_NAME();
                    tgt.header.type = Native.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                    tgt.header.size = (uint)Marshal.SizeOf<Native.DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                    tgt.header.adapterId = path.targetInfo.adapterId;
                    tgt.header.id = path.targetInfo.id;
                    string? name = Native.DisplayConfigGetDeviceInfo(ref tgt) == 0 ? tgt.monitorFriendlyDeviceName : null;
                    uint tech = path.targetInfo.outputTechnology;
                    bool builtIn = tech is Native.OUTPUT_INTERNAL or Native.OUTPUT_DISPLAYPORT_EMBEDDED or Native.OUTPUT_UDI_EMBEDDED;
                    map[src.viewGdiDeviceName] = (string.IsNullOrWhiteSpace(name) ? null : name, builtIn);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
            return map;
        }

        // ── SMBIOS: memory modules and chassis ───────────────────────────────
        public SmbiosInfo Smbios()
        {
            const uint RSMB = 0x52534D42;
            uint size = Native.GetSystemFirmwareTable(RSMB, 0, null, 0);
            if (size == 0) return SmbiosInfo.Empty;
            var buf = new byte[size];
            if (Native.GetSystemFirmwareTable(RSMB, 0, buf, size) != size) return SmbiosInfo.Empty;
            return Checkup.Smbios.Parse(buf);
        }

        // ── power ────────────────────────────────────────────────────────────
        public PowerReading Power()
        {
            bool? ac = null; bool battery = false, saver = false;
            if (Native.GetSystemPowerStatus(out var s))
            {
                ac = s.ACLineStatus switch { 0 => false, 1 => true, _ => null };
                battery = s.BatteryFlag != 128 && s.BatteryFlag != 255;   // 128: no system battery, 255: unknown
                saver = s.SystemStatusFlag == 1;
            }
            return new PowerReading(ac, battery, saver, EffectivePowerMode(), ViewModels.SystemInfo.ActivePowerPlanName());
        }

        // The callback fires right after registration with the current mode
        private static readonly Native.EffectivePowerModeCallback ModeCallback = OnMode;
        private static int _mode = -1;
        private static readonly ManualResetEventSlim ModeSeen = new(false);
        private static readonly object ModeLock = new();
        private static void OnMode(int mode, IntPtr context) { _mode = mode; ModeSeen.Set(); }

        private static int? EffectivePowerMode()
        {
            lock (ModeLock)
            {
                try
                {
                    ModeSeen.Reset();
                    _mode = -1;
                    if (Native.PowerRegisterForEffectivePowerModeNotifications(Native.EFFECTIVE_POWER_MODE_V2,
                            ModeCallback, IntPtr.Zero, out var handle) != 0) return null;
                    try { ModeSeen.Wait(TimeSpan.FromSeconds(1)); }
                    finally { Native.PowerUnregisterFromEffectivePowerModeNotifications(handle); }
                    return _mode >= 0 ? _mode : null;
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
            }
        }

        // ── GPUs: D3DKMT for name, role, temperature; SetupAPI for the driver
        public IReadOnlyList<GpuReading> Gpus()
        {
            var drivers = DisplayDrivers();
            var list = new List<GpuReading>();
            try
            {
                var e = new Native.D3DKMT_ENUMADAPTERS2();
                if (Native.D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return list;
                int sz = Marshal.SizeOf<Native.D3DKMT_ADAPTERINFO>();
                e.pAdapters = Marshal.AllocHGlobal(sz * (int)e.NumAdapters);
                try
                {
                    if (Native.D3DKMTEnumAdapters2(ref e) != 0) return list;
                    for (int i = 0; i < e.NumAdapters; i++)
                    {
                        var a = Marshal.PtrToStructure<Native.D3DKMT_ADAPTERINFO>(e.pAdapters + i * sz);
                        try
                        {
                            uint type = QueryUInt(a.hAdapter, Native.KMTQAITYPE_ADAPTERTYPE);
                            if ((type & Native.ADAPTERTYPE_SOFTWARE) != 0) continue;
                            string name = QueryName(a.hAdapter);
                            var role = (type & Native.ADAPTERTYPE_HYBRID_DISCRETE) != 0 ? GpuRole.Discrete
                                     : (type & Native.ADAPTERTYPE_HYBRID_INTEGRATED) != 0 ? GpuRole.Integrated
                                     : GpuRole.Single;
                            double? temp = QueryTemperature(a.hAdapter);
                            var drv = drivers.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
                            long luid = ((long)a.AdapterLuid.High << 32) | a.AdapterLuid.Low;
                            list.Add(new GpuReading(name, role, drv.Version ?? "", drv.Date, temp, luid));
                        }
                        finally
                        {
                            var c = new Native.D3DKMT_CLOSEADAPTER { hAdapter = a.hAdapter };
                            Native.D3DKMTCloseAdapter(ref c);
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(e.pAdapters); }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
            return list;
        }

        private static uint QueryUInt(uint adapter, int type)
        {
            IntPtr buf = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(buf, 0);
                var q = new Native.D3DKMT_QUERYADAPTERINFO { hAdapter = adapter, Type = type, pPrivateDriverData = buf, PrivateDriverDataSize = 4 };
                return Native.D3DKMTQueryAdapterInfo(ref q) == 0 ? (uint)Marshal.ReadInt32(buf) : 0;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static string QueryName(uint adapter)
        {
            const int Size = 4 * 260 * 2;   // D3DKMT_ADAPTERREGISTRYINFO: 4 x WCHAR[MAX_PATH]
            IntPtr buf = Marshal.AllocHGlobal(Size);
            try
            {
                Marshal.WriteInt16(buf, 0);
                var q = new Native.D3DKMT_QUERYADAPTERINFO
                    { hAdapter = adapter, Type = Native.KMTQAITYPE_ADAPTERREGISTRYINFO, pPrivateDriverData = buf, PrivateDriverDataSize = Size };
                return Native.D3DKMTQueryAdapterInfo(ref q) == 0 ? (Marshal.PtrToStringUni(buf) ?? "").Trim() : "";
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static double? QueryTemperature(uint adapter)
        {
            int size = Marshal.SizeOf<Native.D3DKMT_ADAPTER_PERFDATA>();
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(new Native.D3DKMT_ADAPTER_PERFDATA(), buf, false);
                var q = new Native.D3DKMT_QUERYADAPTERINFO
                    { hAdapter = adapter, Type = Native.KMTQAITYPE_ADAPTERPERFDATA, pPrivateDriverData = buf, PrivateDriverDataSize = (uint)size };
                if (Native.D3DKMTQueryAdapterInfo(ref q) != 0) return null;
                uint t = Marshal.PtrToStructure<Native.D3DKMT_ADAPTER_PERFDATA>(buf).Temperature;
                return t > 0 ? t / 10.0 : null;   // deci-Celsius; 0 = no sensor
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // Display-class devices: description, driver version and date
        private static List<(string Name, string? Version, DateTime? Date)> DisplayDrivers()
        {
            var list = new List<(string, string?, DateTime?)>();
            var cls = Native.GUID_DEVCLASS_DISPLAY;
            IntPtr set = Native.SetupDiGetClassDevsW(ref cls, IntPtr.Zero, IntPtr.Zero, Native.DIGCF_PRESENT);
            if (set == Native.INVALID_HANDLE_VALUE) return list;
            try
            {
                for (uint i = 0; ; i++)
                {
                    var dev = new Native.SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<Native.SP_DEVINFO_DATA>() };
                    if (!Native.SetupDiEnumDeviceInfo(set, i, ref dev)) break;
                    string? name = StringProp(set, ref dev, Native.DEVPKEY_Device_DeviceDesc);
                    if (name == null) continue;
                    string? version = StringProp(set, ref dev, Native.DEVPKEY_Device_DriverVersion);
                    DateTime? date = FileTimeProp(set, ref dev, Native.DEVPKEY_Device_DriverDate);
                    list.Add((name, version, date));
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(set); }
            return list;
        }

        private static byte[]? Prop(IntPtr set, ref Native.SP_DEVINFO_DATA dev, Native.DEVPROPKEY key, uint expectType)
        {
            Native.SetupDiGetDevicePropertyW(set, ref dev, ref key, out _, null, 0, out uint need, 0);
            if (need == 0) return null;
            var buf = new byte[need];
            return Native.SetupDiGetDevicePropertyW(set, ref dev, ref key, out uint type, buf, need, out _, 0) && type == expectType
                ? buf : null;
        }

        private static string? StringProp(IntPtr set, ref Native.SP_DEVINFO_DATA dev, Native.DEVPROPKEY key) =>
            Prop(set, ref dev, key, Native.DEVPROP_TYPE_STRING) is { } b ? System.Text.Encoding.Unicode.GetString(b).TrimEnd('\0') : null;

        private static DateTime? FileTimeProp(IntPtr set, ref Native.SP_DEVINFO_DATA dev, Native.DEVPROPKEY key) =>
            Prop(set, ref dev, key, Native.DEVPROP_TYPE_FILETIME) is { Length: >= 8 } b
                ? DateTime.FromFileTimeUtc(BitConverter.ToInt64(b, 0)) : null;

        // ── Memory Integrity ─────────────────────────────────────────────────
        public HvciState Hvci()
        {
            try
            {
                var info = new Native.SYSTEM_CODEINTEGRITY_INFORMATION { Length = (uint)Marshal.SizeOf<Native.SYSTEM_CODEINTEGRITY_INFORMATION>() };
                int st = Native.NtQuerySystemInformation(Native.SystemCodeIntegrityInformation, ref info, info.Length, out _);
                if (st != 0) return HvciState.Unknown;
                return (info.CodeIntegrityOptions & Native.CODEINTEGRITY_OPTION_HVCI_KMCI_ENABLED) != 0 ? HvciState.On : HvciState.Off;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return HvciState.Unknown; }
        }

        // ── thermal zones (Kelvin in the counter) ────────────────────────────
        public IReadOnlyList<ThermalZone> ThermalZones() =>
            Pdh.Read(@"\Thermal Zone Information(*)\Temperature", null)
               .Where(v => v.Value > 0)
               .Select(v => new ThermalZone(v.Instance, Math.Round(v.Value - 273.15, 1)))
               .ToList();
    }
}
