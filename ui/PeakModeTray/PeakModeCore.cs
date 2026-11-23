using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PeakModeTray
{
    // P/Invoke bridge into PeakModeCore.dll (C++ core).
    // The DLL must be in the same folder as the .exe.
    // Call it through CoreService, which serializes operations.
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

        // Keep in sync with PeakMode::RestoreReason
        public const int ReasonUnknown     = 0;
        public const int ReasonTray        = 1;
        public const int ReasonWindow      = 2;
        public const int ReasonExit        = 3;
        public const int ReasonSessionEnd  = 4;
        public const int ReasonCrashPrompt = 5;
        public const int ReasonBanner      = 6;

        // Keep in sync with PeakMode::ProgressEvent / Step / StepStatus
        public const int EvStepBegin     = 1;
        public const int EvStepEnd       = 2;
        public const int EvRestoreBegin  = 3;
        public const int EvEntryRestored = 4;
        public const int EvDone          = 5;

        public const int StepProcess   = 0;
        public const int StepTimer     = 1;
        public const int StepPower     = 2;
        public const int StepNagle     = 3;
        public const int StepServices  = 4;
        public const int StepNvidia    = 5;
        public const int StepInterrupt = 6;
        public const int StepCount     = 7;

        public const int StepApplied = 0;
        public const int StepSkipped = 1;
        public const int StepFailed  = 2;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void ProgressFn(int evt, int step, int status, int entries, IntPtr user);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr Orchestrator_GetInstance();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Orchestrator_Activate(IntPtr instance,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string profilePath);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Orchestrator_Restore(IntPtr instance);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Orchestrator_RestoreWithReason(IntPtr instance, int reason);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int Orchestrator_GetState(IntPtr instance);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void Orchestrator_SetProgressCallback(IntPtr instance,
            ProgressFn? fn, IntPtr user);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int PeakMode_HasLeftoverSnapshot();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int PeakMode_GetLastError(byte[]? buf, int bufLen);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int PeakMode_ReadSessions(byte[]? buf, int bufLen);

        public static string LastError() => ReadString(PeakMode_GetLastError);

        // JSON array of session events, oldest first
        public static string ReadSessions() => ReadString(PeakMode_ReadSessions);

        private static string ReadString(Func<byte[]?, int, int> read)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                int len = read(null, 0);
                if (len <= 0) return "";
                var buf = new byte[len + 1];
                int got = read(buf, buf.Length);
                if (got <= len) return Encoding.UTF8.GetString(buf, 0, got);
                // grew between the two calls (session log appended): retry
            }
            return "";
        }
    }
}
