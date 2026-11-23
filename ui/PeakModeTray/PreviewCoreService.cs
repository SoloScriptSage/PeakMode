#if DEBUG
using System;
using System.Threading.Tasks;

namespace PeakModeTray
{
    // Debug-only stand-in for CoreService (--window-preview): emits the same
    // event sequence as a default-profile activate/restore, with delays, and
    // changes nothing on the system.
    internal sealed class PreviewCoreService : ICoreService
    {
        private static readonly (int step, int status, int entries)[] Steps =
        {
            (PeakModeCore.StepProcess,  PeakModeCore.StepSkipped, 0),
            (PeakModeCore.StepTimer,    PeakModeCore.StepApplied, 0),
            (PeakModeCore.StepPower,    PeakModeCore.StepApplied, 1),
            (PeakModeCore.StepNagle,    PeakModeCore.StepApplied, 12),
            (PeakModeCore.StepServices, PeakModeCore.StepApplied, 2),
            (PeakModeCore.StepNvidia,   PeakModeCore.StepApplied, 4),
        };

        public int  State  { get; private set; } = PeakModeCore.StateIdle;
        public bool IsBusy { get; private set; }

        public event Action<CoreProgress>? Progress;
        public event Action?               StateChanged;
        public event Action<CoreResult>?   Completed;

        public string ReadSessions() => "[]";

        public async Task<CoreResult> ActivateAsync(string configPath)
        {
            Busy(true);
            int n = 0;
            foreach (var (step, status, entries) in Steps)
            {
                Progress?.Invoke(new(PeakModeCore.EvStepBegin, step, 0, n));
                await Task.Delay(650);
                n += entries;
                Progress?.Invoke(new(PeakModeCore.EvStepEnd, step, status, n));
            }
            Progress?.Invoke(new(PeakModeCore.EvDone, -1, PeakModeCore.OK, n));
            State = PeakModeCore.StateActive;
            return Finish(true);
        }

        public async Task<CoreResult> RestoreAsync(int reason)
        {
            Busy(true);
            Progress?.Invoke(new(PeakModeCore.EvRestoreBegin, -1, 0, 19));
            int done = 0;
            for (int i = Steps.Length - 1; i >= 0; i--)
                for (int k = 0; k < Steps[i].entries; k++)
                {
                    await Task.Delay(Steps[i].entries > 4 ? 40 : 160);
                    Progress?.Invoke(new(PeakModeCore.EvEntryRestored, Steps[i].step, 0, ++done));
                }
            Progress?.Invoke(new(PeakModeCore.EvDone, -1, PeakModeCore.OK, done));
            State = PeakModeCore.StateIdle;
            return Finish(false);
        }

        private void Busy(bool b) { IsBusy = b; StateChanged?.Invoke(); }

        private CoreResult Finish(bool activate)
        {
            var r = new CoreResult(activate, PeakModeCore.OK, "");
            Busy(false);
            Completed?.Invoke(r);
            return r;
        }
    }
}
#endif
