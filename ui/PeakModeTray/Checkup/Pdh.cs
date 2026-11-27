using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PeakModeTray.Checkup
{
    // Reads one performance counter path with wildcards, e.g.
    // \Thermal Zone Information(*)\Temperature. Read-only. Rate counters
    // (GPU utilization) need two samples: pass the interval between them.
    internal static class Pdh
    {
        public static List<(string Instance, double Value)> Read(string englishPath, TimeSpan? interval)
        {
            var result = new List<(string, double)>();
            try
            {
                if (Native.PdhOpenQueryW(null, IntPtr.Zero, out var query) != 0) return result;
                try
                {
                    if (Native.PdhAddEnglishCounterW(query, englishPath, IntPtr.Zero, out var counter) != 0) return result;
                    if (Native.PdhCollectQueryData(query) != 0) return result;
                    if (interval is { } wait)
                    {
                        Thread.Sleep(wait);
                        if (Native.PdhCollectQueryData(query) != 0) return result;
                    }
                    const uint format = Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100;
                    uint size = 0;
                    uint rc = Native.PdhGetFormattedCounterArrayW(counter, format, ref size, out _, IntPtr.Zero);
                    if (rc != Native.PDH_MORE_DATA || size == 0) return result;
                    IntPtr buf = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        if (Native.PdhGetFormattedCounterArrayW(counter, format, ref size, out uint count, buf) != 0) return result;
                        int item = Marshal.SizeOf<Native.PDH_FMT_COUNTERVALUE_ITEM_W>();
                        for (int i = 0; i < count; i++)
                        {
                            var v = Marshal.PtrToStructure<Native.PDH_FMT_COUNTERVALUE_ITEM_W>(buf + i * item);
                            if (v.CStatus > 1) continue;   // PDH_CSTATUS_VALID_DATA 0, NEW_DATA 1
                            result.Add((Marshal.PtrToStringUni(v.szName) ?? "", v.doubleValue));
                        }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                finally { Native.PdhCloseQuery(query); }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
            return result;
        }
    }
}
