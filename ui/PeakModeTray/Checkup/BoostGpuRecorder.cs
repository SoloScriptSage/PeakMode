using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PeakModeTray.Checkup
{
    // Which GPU a process is drawing on right now. Read-only.
    internal interface IGpuUsageSampler
    {
        // null: process not running, or no 3D work seen on any GPU
        GpuReading? Sample(string exeName);
    }

    // "GPU Engine" performance counters: one instance per process, adapter
    // and engine, e.g. pid_10552_luid_0x00000000_0x00010585_phys_0_eng_0_engtype_3D.
    internal sealed class GpuUsageSampler : IGpuUsageSampler
    {
        private readonly ISystemReader _reader;
        public GpuUsageSampler(ISystemReader reader) { _reader = reader; }

        public GpuReading? Sample(string exeName)
        {
            var pids = ProcessIds(exeName);
            if (pids.Count == 0) return null;
            var usage = Pdh.Read(@"\GPU Engine(*engtype_3D)\Utilization Percentage", TimeSpan.FromSeconds(1));
            return Pick(usage, pids, _reader.Gpus());
        }

        private static HashSet<int> ProcessIds(string exeName)
        {
            var ids = new HashSet<int>();
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exeName)))
            {
                ids.Add(p.Id);
                p.Dispose();
            }
            return ids;
        }

        // The adapter with the most 3D utilization summed over the process's
        // instances; null if all are zero or the adapter is unknown.
        public static GpuReading? Pick(IEnumerable<(string Instance, double Value)> usage, ISet<int> pids,
                                       IReadOnlyList<GpuReading> gpus)
        {
            var byLuid = new Dictionary<long, double>();
            foreach (var (instance, value) in usage)
            {
                if (!TryParse(instance, out int pid, out long luid) || !pids.Contains(pid)) continue;
                byLuid[luid] = byLuid.GetValueOrDefault(luid) + value;
            }
            var best = byLuid.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).FirstOrDefault();
            return best.Value > 0 ? gpus.FirstOrDefault(g => g.Luid == best.Key) : null;
        }

        public static bool TryParse(string instance, out int pid, out long luid)
        {
            pid = 0; luid = 0;
            var parts = instance.Split('_');
            int p = Array.IndexOf(parts, "pid"), l = Array.IndexOf(parts, "luid");
            if (p < 0 || p + 1 >= parts.Length || l < 0 || l + 2 >= parts.Length) return false;
            static bool Hex(string s, out uint v) =>
                uint.TryParse(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s,
                              NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v);
            if (!int.TryParse(parts[p + 1], NumberStyles.None, CultureInfo.InvariantCulture, out pid) ||
                !Hex(parts[l + 1], out uint high) || !Hex(parts[l + 2], out uint low)) return false;
            luid = ((long)high << 32) | low;
            return true;
        }
    }

    // While a boost with a target process is active, finds the GPU that
    // process draws on and records it once in the session log (core "gpu"
    // event). Waits for the game to render, retries while nothing is seen,
    // and stops when the boost ends. Only reads the system.
    internal sealed class BoostGpuRecorder : IDisposable
    {
        private readonly ICoreService _core;
        private readonly Func<string> _target;
        private readonly IGpuUsageSampler _sampler;
        private readonly TimeSpan _firstDelay, _retryDelay;
        private readonly int _attempts;
        private CancellationTokenSource? _run;

        public BoostGpuRecorder(ICoreService core, Func<string> targetProcess, IGpuUsageSampler sampler,
                                TimeSpan? firstDelay = null, TimeSpan? retryDelay = null, int attempts = 10)
        {
            _core = core; _target = targetProcess; _sampler = sampler;
            _firstDelay = firstDelay ?? TimeSpan.FromSeconds(10);
            _retryDelay = retryDelay ?? TimeSpan.FromSeconds(30);
            _attempts = attempts;
            _core.StateChanged += OnStateChanged;
            OnStateChanged();
        }

        // The running sampling loop, for tests
        internal Task Running { get; private set; } = Task.CompletedTask;

        private void OnStateChanged()
        {
            bool active = _core.State == PeakModeCore.StateActive && !_core.IsBusy;
            if (active && _run == null)
            {
                string target = (_target() ?? "").Trim();
                if (target.Length == 0) return;    // nothing to sample; Checkup says so
                _run = new CancellationTokenSource();
                var token = _run.Token;
                Running = Task.Run(() => SampleLoop(target, token), token);
            }
            else if (!active && !_core.IsBusy && _run != null)
            {
                _run.Cancel();
                _run.Dispose();
                _run = null;
            }
        }

        private async Task SampleLoop(string target, CancellationToken token)
        {
            try
            {
                await Task.Delay(_firstDelay, token);
                for (int i = 0; i < _attempts; i++)
                {
                    token.ThrowIfCancellationRequested();
                    GpuReading? gpu = null;
                    try { gpu = _sampler.Sample(target); } catch (Exception) { }
                    if (gpu != null)
                    {
                        string role = gpu.Role switch { GpuRole.Discrete => "discrete", GpuRole.Integrated => "integrated", _ => "" };
                        if (!token.IsCancellationRequested) _core.RecordGpu(target, gpu.Name, role);
                        return;
                    }
                    if (i + 1 < _attempts) await Task.Delay(_retryDelay, token);
                }
            }
            catch (OperationCanceledException) { }
        }

        public void Dispose()
        {
            _core.StateChanged -= OnStateChanged;
            _run?.Cancel();
            _run?.Dispose();
            _run = null;
        }
    }
}
