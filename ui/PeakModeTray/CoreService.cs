using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace PeakModeTray
{
    internal readonly record struct CoreProgress(int Event, int Step, int Status, int Entries);

    internal readonly record struct CoreResult(bool IsActivate, int Code, string Error);

    // The only caller of PeakModeCore.dll. Runs activate/restore one at a
    // time on a worker thread and raises all events on the UI dispatcher.
    // The core holds its lock for the whole of activate/restore, so the
    // state is cached here instead of calling Orchestrator_GetState while
    // an operation runs.
    internal sealed class CoreService : ICoreService, IDisposable
    {
        public string ReadSessions() => PeakModeCore.ReadSessions();

        public bool RecordGpu(string process, string adapter, string role)
        {
            try { return PeakModeCore.PeakMode_RecordGpu(process, adapter, role) == PeakModeCore.OK; }
            catch (EntryPointNotFoundException) { return false; }   // older core DLL
        }

        private readonly IntPtr                    _core;
        private readonly Dispatcher                _ui;
        private readonly SemaphoreSlim             _gate = new(1, 1);
        private readonly PeakModeCore.ProgressFn   _callback; // kept alive while registered
        private int  _state;
        private bool _busy;

        public event Action<CoreProgress>? Progress;
        public event Action?               StateChanged;   // State or IsBusy changed
        public event Action<CoreResult>?   Completed;

        public CoreService(Dispatcher ui)
        {
            _ui       = ui;
            _core     = PeakModeCore.Orchestrator_GetInstance();
            _callback = OnNativeProgress;
            PeakModeCore.Orchestrator_SetProgressCallback(_core, _callback, IntPtr.Zero);
            _state    = PeakModeCore.Orchestrator_GetState(_core);
        }

        public int  State  => _state;
        public bool IsBusy => _busy;

        public Task<CoreResult> ActivateAsync(string configPath) =>
            RunAsync(true, () => PeakModeCore.Orchestrator_Activate(_core, configPath));

        public Task<CoreResult> RestoreAsync(int reason) =>
            RunAsync(false, () => PeakModeCore.Orchestrator_RestoreWithReason(_core, reason));

        // Blocking restore for logoff/shutdown, where there is no time to
        // go async. Waits for a running operation first.
        public CoreResult Restore(int reason)
        {
            _gate.Wait();
            try
            {
                SetBusy(true);
                int rc = PeakModeCore.Orchestrator_RestoreWithReason(_core, reason);
                return Finish(false, rc);
            }
            finally { _gate.Release(); }
        }

        private async Task<CoreResult> RunAsync(bool activate, Func<int> op)
        {
            await _gate.WaitAsync();
            try
            {
                SetBusy(true);
                int rc = await Task.Run(op);
                return Finish(activate, rc);
            }
            finally { _gate.Release(); }
        }

        private CoreResult Finish(bool activate, int rc)
        {
            var result = new CoreResult(activate, rc, rc == PeakModeCore.OK ? "" : PeakModeCore.LastError());
            int state  = PeakModeCore.Orchestrator_GetState(_core);
            // Always queued, so it lands after progress events still queued
            _ui.BeginInvoke(() =>
            {
                _state = state;
                _busy  = false;
                StateChanged?.Invoke();
                Completed?.Invoke(result);
            });
            return result;
        }

        private void SetBusy(bool busy) => Post(() =>
        {
            _busy = busy;
            StateChanged?.Invoke();
        });

        private void OnNativeProgress(int evt, int step, int status, int entries, IntPtr user)
        {
            var p = new CoreProgress(evt, step, status, entries);
            Post(() => Progress?.Invoke(p));
        }

        private void Post(Action a)
        {
            if (_ui.CheckAccess()) a();
            else _ui.BeginInvoke(a);
        }

        public void Dispose()
        {
            PeakModeCore.Orchestrator_SetProgressCallback(_core, null, IntPtr.Zero);
            GC.KeepAlive(_callback);
        }
    }
}
