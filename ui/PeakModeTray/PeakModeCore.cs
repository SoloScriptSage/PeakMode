using System;
using System.Runtime.InteropServices;

namespace PeakModeTray
{
    // P/Invoke bridge into PeakModeCore.dll (C++ core)
    // The DLL must be in the same folder as the .exe
    internal static class PeakModeCore
    {
        private const string DllName = "PeakModeCore.dll";

        // Orchestrator exports
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr Orchestrator_GetInstance();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void Orchestrator_Activate(IntPtr instance,
            [MarshalAs(UnmanagedType.LPStr)] string profilePath);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void Orchestrator_Restore(IntPtr instance);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Orchestrator_GetState(IntPtr instance);
        // 0 = IDLE, 1 = ACTIVE
    }
}
