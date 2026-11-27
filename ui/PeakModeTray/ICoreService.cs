using System;
using System.Threading.Tasks;

namespace PeakModeTray
{
    // What the UI needs from the core. CoreService is the real one; the
    // Debug-only PreviewCoreService simulates it for --window-preview.
    internal interface ICoreService
    {
        int  State  { get; }
        bool IsBusy { get; }

        event Action<CoreProgress>? Progress;
        event Action?               StateChanged;
        event Action<CoreResult>?   Completed;

        Task<CoreResult> ActivateAsync(string configPath);
        Task<CoreResult> RestoreAsync(int reason);
        string           ReadSessions();   // JSON array, oldest first

        // PC Checkup: adds a "gpu" event to the active session. Only writes
        // the session log; false when nothing is active. May block while an
        // activate or restore runs, so call it off the UI thread.
        bool RecordGpu(string process, string adapter, string role) => false;
    }
}
