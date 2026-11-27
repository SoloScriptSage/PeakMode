#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
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

        // Simulated session log: a few sample sessions, then whatever the
        // preview activates and restores. --preview-pending adds an open
        // session from a "previous run" so the banner shows.
        private readonly List<JsonObject> _log = new();
        private string _session = "";
        private const int PreviewPid = 1111, PrevRunPid = 999;

        public PreviewCoreService(bool pending = false)
        {
            var now = DateTime.UtcNow;
            Sample(now.AddDays(-3).AddHours(-2), TimeSpan.FromMinutes(98), "tray", 7, PreviewPid - 2, PreviewPid - 2);
            Sample(now.AddDays(-1).AddHours(-1), TimeSpan.FromMinutes(72), "crashPrompt", 19, PrevRunPid - 1, PreviewPid - 1);
            Sample(now.AddHours(-3), TimeSpan.FromMinutes(2), "tray", 19, PreviewPid - 1, PreviewPid - 1);
            if (pending)
            {
                _session = Id(now.AddMinutes(-40), PrevRunPid);
                Log("begin", now.AddMinutes(-40), PrevRunPid, new() { ["profile"] = "default" });
                Log("active", now.AddMinutes(-40), PrevRunPid, ActiveFields(19));
                State = PeakModeCore.StateRestorePending;
            }
        }

        public string ReadSessions()
        {
            lock (_log) return new JsonArray(_log.Select(e => (JsonNode)e.DeepClone()).ToArray()).ToJsonString();
        }

        public bool RecordGpu(string process, string adapter, string role)
        {
            if (State != PeakModeCore.StateActive) return false;
            Log("gpu", DateTime.UtcNow, PreviewPid, new() { ["process"] = process, ["adapter"] = adapter, ["role"] = role });
            return true;
        }

        private void Sample(DateTime begin, TimeSpan dur, string reason, int n, int pid, int endPid)
        {
            _session = Id(begin, pid);
            Log("begin", begin, pid, new() { ["profile"] = "default" });
            Log("active", begin, pid, ActiveFields(n));
            Log("end", begin + dur, endPid, EndFields(reason, n));
        }

        private static string Id(DateTime t, int pid) => $"{t:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}-{pid}";

        private void Log(string type, DateTime t, int pid, JsonObject extra)
        {
            var ev = new JsonObject
            {
                ["v"] = 1, ["type"] = type, ["session"] = _session,
                ["time"] = t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), ["pid"] = pid,
            };
            foreach (var (k, v) in extra) ev[k] = v?.DeepClone();
            lock (_log) _log.Add(ev);   // RecordGpu comes from a worker thread
        }

        private static JsonObject EndFields(string reason, int n) => new()
        {
            ["reason"] = reason, ["result"] = "ok", ["restored"] = n, ["failed"] = 0,
        };

        // Entries shaped like a default-profile snapshot (fake values)
        private static JsonObject ActiveFields(int n)
        {
            var all = new List<JsonObject>
            {
                new() { ["kind"] = "powerScheme", ["prevActive"] = "{381b4222-f694-41f0-9685-ff5bb260df2e}", ["module"] = "power" },
            };
            for (int i = 0; i < 6; i++)
                foreach (var name in new[] { "TcpAckFrequency", "TCPNoDelay" })
                    all.Add(new()
                    {
                        ["kind"] = "registryValue", ["module"] = "nagle", ["existed"] = false, ["name"] = name,
                        ["path"] = $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{{{i + 1:X8}-0000-0000-0000-000000000000}}",
                    });
            all.Add(new() { ["kind"] = "service", ["name"] = "SysMain",  ["wasRunning"] = true, ["module"] = "services" });
            all.Add(new() { ["kind"] = "service", ["name"] = "WSearch",  ["wasRunning"] = true, ["module"] = "services" });
            foreach (uint id in new uint[] { 0x1057EB71, 0x007BA09E, 0x00A879CF, 0x20C1221E })
                all.Add(new() { ["kind"] = "drs", ["settingId"] = id, ["existed"] = true, ["value"] = 0, ["module"] = "nvidia" });
            return new()
            {
                ["entries"]     = new JsonArray(all.Take(n).Select(e => (JsonNode)e).ToArray()),
                ["powerScheme"] = "{8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c}",
            };
        }

        public async Task<CoreResult> ActivateAsync(string configPath)
        {
            Busy(true);
            var start = DateTime.UtcNow;
            _session = Id(start, PreviewPid);
            Log("begin", start, PreviewPid, new() { ["profile"] = "default" });
            int n = 0;
            foreach (var (step, status, entries) in Steps)
            {
                Progress?.Invoke(new(PeakModeCore.EvStepBegin, step, 0, n));
                await Task.Delay(650);
                n += entries;
                Progress?.Invoke(new(PeakModeCore.EvStepEnd, step, status, n));
            }
            Progress?.Invoke(new(PeakModeCore.EvDone, -1, PeakModeCore.OK, n));
            Log("active", DateTime.UtcNow, PreviewPid, ActiveFields(n));
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
            string[] names = { "unknown", "tray", "window", "exit", "sessionEnd", "crashPrompt", "banner" };
            Log("end", DateTime.UtcNow, PreviewPid,
                EndFields(reason >= 0 && reason < names.Length ? names[reason] : "unknown", done));
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
