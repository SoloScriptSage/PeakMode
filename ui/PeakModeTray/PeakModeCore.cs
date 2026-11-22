using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PeakModeTray
{
    // P/Invoke bridge into PeakModeCore.dll (C++ core).
    // The DLL must be in the same folder as the .exe.
    internal static class PeakModeCore
    {
        private const string DllName = "PeakModeCore.dll";

        // Keep in sync with PeakMode::Result in core/engine/Orchestrator.h
        public const int OK            = 0;
        public const int ErrConfig     = 1;
        public const int ErrLeftover   = 2;
        public const int ErrSnapshot   = 3;
        public const int ErrPartial    = 4;
        public const int ErrNothing    = 5;
        public const int ErrInternal   = 6;

        // Keep in sync with PeakMode::ProfileState
        public const int StateIdle           = 0;
        public const int StateActive         = 1;
        public const int StateRestorePending = 2;

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr Orchestrator_GetInstance();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Orchestrator_Activate(IntPtr instance,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string profilePath);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Orchestrator_Restore(IntPtr instance);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Orchestrator_GetState(IntPtr instance);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int PeakMode_HasLeftoverSnapshot();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int PeakMode_GetLastError(byte[]? buf, int bufLen);

        public static string LastError()
        {
            int len = PeakMode_GetLastError(null, 0);
            if (len <= 0) return "";
            var buf = new byte[len + 1];
            PeakMode_GetLastError(buf, buf.Length);
            return Encoding.UTF8.GetString(buf, 0, len);
        }
    }
}
