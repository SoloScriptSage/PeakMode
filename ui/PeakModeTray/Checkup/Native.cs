using System;
using System.Runtime.InteropServices;

namespace PeakModeTray.Checkup
{
    // Read-only Windows APIs used by the PC Checkup. Constants are taken from
    // the Windows SDK 10.0.22621 headers named next to each group, not from
    // the documentation pages (several list no numeric values).
    internal static class Native
    {
        // ── user32: display modes (winuser.h, wingdi.h) ──────────────────────
        public const int ENUM_CURRENT_SETTINGS = -1;
        public const int DISPLAY_DEVICE_ATTACHED_TO_DESKTOP = 0x1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplayDevicesW(string? device, int index, ref DISPLAY_DEVICE dd, int flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplaySettingsExW(string device, int mode, ref DEVMODE dm, int flags);

        // ── user32: display configuration (wingdi.h) ─────────────────────────
        public const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
        public const int  ERROR_INSUFFICIENT_BUFFER = 122;
        public const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1;
        public const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
        public const uint OUTPUT_DISPLAYPORT_EMBEDDED = 11, OUTPUT_UDI_EMBEDDED = 13, OUTPUT_INTERNAL = 0x80000000;

        [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint Low; public int High; }
        [StructLayout(LayoutKind.Sequential)] public struct DISPLAYCONFIG_RATIONAL { public uint Numerator, Denominator; }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology;
            public uint rotation; public uint scaling; public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }

        // Only its size matters here: the union is not read
        [StructLayout(LayoutKind.Sequential, Size = 64)] public struct DISPLAYCONFIG_MODE_INFO { public uint infoType; }

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
            public uint flags; public uint outputTechnology;
            public ushort edidManufactureId, edidProductCodeId; public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
        }

        [DllImport("user32.dll")] public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
        [DllImport("user32.dll")]
        public static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
                                                    ref uint numModes, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topologyId);
        [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);
        [DllImport("user32.dll")] public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME request);

        // ── kernel32: firmware table, power status ───────────────────────────
        [DllImport("kernel32.dll")]
        public static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[]? buffer, uint size);

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }
        [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

        // ── powrprof: effective power mode (powersetting.h) ──────────────────
        public const uint EFFECTIVE_POWER_MODE_V2 = 2;
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate void EffectivePowerModeCallback(int mode, IntPtr context);
        [DllImport("powrprof.dll")]
        public static extern int PowerRegisterForEffectivePowerModeNotifications(uint version, EffectivePowerModeCallback callback,
                                                                                 IntPtr context, out IntPtr handle);
        [DllImport("powrprof.dll")] public static extern int PowerUnregisterFromEffectivePowerModeNotifications(IntPtr handle);

        // ── gdi32: D3DKMT (shared/d3dkmthk.h) ────────────────────────────────
        public const int KMTQAITYPE_ADAPTERREGISTRYINFO = 8;
        public const int KMTQAITYPE_ADAPTERTYPE         = 15;
        public const int KMTQAITYPE_ADAPTERPERFDATA     = 62;
        // D3DKMT_ADAPTERTYPE bits
        public const uint ADAPTERTYPE_SOFTWARE = 0x4, ADAPTERTYPE_HYBRID_DISCRETE = 0x10, ADAPTERTYPE_HYBRID_INTEGRATED = 0x20;

        [StructLayout(LayoutKind.Sequential)]
        public struct D3DKMT_ADAPTERINFO { public uint hAdapter; public LUID AdapterLuid; public uint NumOfSources; public int bPrecisePresentRegionsPreferred; }
        [StructLayout(LayoutKind.Sequential)] public struct D3DKMT_ENUMADAPTERS2 { public uint NumAdapters; public IntPtr pAdapters; }
        [StructLayout(LayoutKind.Sequential)]
        public struct D3DKMT_QUERYADAPTERINFO { public uint hAdapter; public int Type; public IntPtr pPrivateDriverData; public uint PrivateDriverDataSize; }
        [StructLayout(LayoutKind.Sequential)] public struct D3DKMT_CLOSEADAPTER { public uint hAdapter; }

        // UINT32, then ULONGLONGs aligned to 8 (D3DKMT_ALIGN64)
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        public struct D3DKMT_ADAPTER_PERFDATA
        {
            [FieldOffset(0)]  public uint  PhysicalAdapterIndex;
            [FieldOffset(8)]  public ulong MemoryFrequency;
            [FieldOffset(16)] public ulong MaxMemoryFrequency;
            [FieldOffset(24)] public ulong MaxMemoryFrequencyOC;
            [FieldOffset(32)] public ulong MemoryBandwidth;
            [FieldOffset(40)] public ulong PCIEBandwidth;
            [FieldOffset(48)] public uint  FanRPM;
            [FieldOffset(52)] public uint  Power;
            [FieldOffset(56)] public uint  Temperature;   // deci-Celsius
            [FieldOffset(60)] public byte  PowerStateOverride;
        }

        [DllImport("gdi32.dll")] public static extern int D3DKMTEnumAdapters2(ref D3DKMT_ENUMADAPTERS2 e);
        [DllImport("gdi32.dll")] public static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO q);
        [DllImport("gdi32.dll")] public static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER c);

        // ── setupapi: driver properties (devguid.h, devpkey.h, devpropdef.h) ─
        public static readonly Guid GUID_DEVCLASS_DISPLAY = new("4d36e968-e325-11ce-bfc1-08002be10318");
        public const uint DIGCF_PRESENT = 0x2;
        public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
        public const uint DEVPROP_TYPE_FILETIME = 0x10, DEVPROP_TYPE_STRING = 0x12;

        [StructLayout(LayoutKind.Sequential)] public struct DEVPROPKEY { public Guid fmtid; public uint pid; }
        public static readonly DEVPROPKEY DEVPKEY_Device_DeviceDesc     = new() { fmtid = new("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 2 };
        public static readonly DEVPROPKEY DEVPKEY_Device_DriverDate     = new() { fmtid = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), pid = 2 };
        public static readonly DEVPROPKEY DEVPKEY_Device_DriverVersion  = new() { fmtid = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), pid = 3 };

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key,
                                                            out uint type, byte[]? buffer, uint size, out uint required, uint flags);
        [DllImport("setupapi.dll")] public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        // ── ntdll: code integrity (um/winternl.h) ────────────────────────────
        public const int  SystemCodeIntegrityInformation = 103;
        public const uint CODEINTEGRITY_OPTION_HVCI_KMCI_ENABLED = 0x400;
        [StructLayout(LayoutKind.Sequential)] public struct SYSTEM_CODEINTEGRITY_INFORMATION { public uint Length; public uint CodeIntegrityOptions; }
        [DllImport("ntdll.dll")]
        public static extern int NtQuerySystemInformation(int infoClass, ref SYSTEM_CODEINTEGRITY_INFORMATION info, uint length, out uint returned);

        // ── pdh: performance counters (um/Pdh.h, um/PdhMsg.h) ────────────────
        public const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000;
        public const uint PDH_MORE_DATA = 0x800007D2;

        [StructLayout(LayoutKind.Sequential)]
        public struct PDH_FMT_COUNTERVALUE_ITEM_W
        {
            public IntPtr szName;
            public uint   CStatus;
            public double doubleValue;   // union in PDH_FMT_COUNTERVALUE; aligned to 8
        }

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhOpenQueryW(string? source, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] public static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] public static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr items);
        [DllImport("pdh.dll")] public static extern uint PdhCloseQuery(IntPtr query);
    }
}
