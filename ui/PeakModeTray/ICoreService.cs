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
    }
}
