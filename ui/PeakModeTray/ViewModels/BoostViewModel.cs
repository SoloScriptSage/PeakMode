using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using PeakModeTray.Controls;

namespace PeakModeTray.ViewModels
{
    // One card per activation step, in the core's apply order.
    public sealed class ModuleCardViewModel : ObservableObject
    {
        public ModuleCardViewModel(int step, string name, string detail, string icon, bool warn)
        { Step = step; Name = name; Detail = detail; Icon = icon; Warn = warn; }

        public int    Step   { get; }
        public string Name   { get; }
        public string Detail { get; set; }
        public string Icon   { get; }
        public bool   Warn   { get; }

        private bool _included;
        public bool Included { get => _included; set { if (Set(ref _included, value)) OnPropertyChanged(nameof(ShowWarn)); } }
        public bool ShowWarn => Warn && Included;

        private string _status = "READY";
        // READY, QUEUED, APPLYING, APPLIED, NO CHANGE, FAILED, RESTORING, RESTORED, SKIPPED
        public string Status { get => _status; set { if (Set(ref _status, value)) { OnPropertyChanged(nameof(IsLive)); OnPropertyChanged(nameof(IsHot)); } } }
        public bool IsLive => Status is "APPLYING" or "RESTORING";
        public bool IsHot  => Status == "APPLIED";
        public string SwitchName => (Included ? "Included: " : "Skipped: ") + Name;
    }

    public sealed class StatViewModel : ObservableObject
    {
        public StatViewModel(string label) { Label = label; }
        public string Label { get; }
        private string _value = "", _note = "";
        private bool _hot;
        public string Value { get => _value; set => Set(ref _value, value); }
        public string Note  { get => _note;  set => Set(ref _note, value); }
        public bool   Hot   { get => _hot;   set => Set(ref _hot, value); }
    }

    internal sealed class BoostViewModel : ObservableObject
    {
        private readonly ICoreService _core;
        private readonly string?      _configPath;
        private string _profile = "default";
        private int    _enabledSteps = 1, _stepsDone, _applied, _lastTotal, _restoreTotal, _restored;
        private bool   _restoring;

        public ObservableCollection<ModuleCardViewModel> Modules { get; } = new()
        {
            new(PeakModeCore.StepProcess,   "Process priority",    "no target process set",          Icons.Process, false),
            new(PeakModeCore.StepTimer,     "Timer resolution",    "default → 0.5 ms while active", Icons.Timer,   false),
            new(PeakModeCore.StepPower,     "Power plan",          "→ High or Ultimate performance", Icons.Power,  false),
            new(PeakModeCore.StepNagle,     "Nagle off",           "TcpAckFrequency, TCPNoDelay = 1", Icons.Net,    false),
            new(PeakModeCore.StepServices,  "Background services", "SysMain, WSearch stopped",       Icons.Services, false),
            new(PeakModeCore.StepNvidia,    "NVIDIA driver",       "4 driver profile settings",      Icons.Gpu,     false),
            new(PeakModeCore.StepInterrupt, "Interrupts and NIC",  "IRQ affinity, adapter properties", Icons.Irq,   true),
        };

        public StatViewModel ChangesStat  { get; } = new("CHANGES APPLIED");
        public StatViewModel SnapshotStat { get; } = new("SNAPSHOT");
        public StatViewModel PowerStat    { get; } = new("POWER PLAN");
        public StatViewModel TimerStat    { get; } = new("TIMER");

        public RelayCommand ToggleCommand { get; }

        public BoostViewModel(ICoreService core, string? configPath)
        {
            _core = core;
            _configPath = configPath;
            ToggleCommand = new RelayCommand(Toggle, () => !_core.IsBusy);
            _core.Progress     += OnProgress;
            _core.StateChanged += OnStateChanged;
            _core.Completed    += _ => { RefreshFromState(); };
            LoadProfile();
            RefreshFromState();
        }

        // ── bindable state ───────────────────────────────────────────────────
        private GaugeMode _mode;
        public GaugeMode Mode { get => _mode; private set { if (Set(ref _mode, value)) RaiseModeTexts(); } }
        private double _progress;
        public double Progress { get => _progress; private set => Set(ref _progress, value); }

        public string ProfileLabel => "PROFILE / " + _profile.ToUpperInvariant();
        public string Title => Mode switch
        {
            GaugeMode.Applying => "BOOSTING...", GaugeMode.Active => "BOOST ACTIVE",
            GaugeMode.Restoring => "RESTORING...", GaugeMode.RestorePending => "RESTORE PENDING",
            _ => "READY TO BOOST",
        };
        public string Chip => Mode switch
        {
            GaugeMode.Applying => "◆ APPLYING", GaugeMode.Active => "● ACTIVE",
            GaugeMode.Restoring => "◆ RESTORING", GaugeMode.RestorePending => "▲ RESTORE PENDING",
            _ => "○ IDLE",
        };
        public bool ChipHot   => Mode is GaugeMode.Applying or GaugeMode.Active or GaugeMode.Restoring;
        public bool ChipAmber => Mode == GaugeMode.RestorePending;
        public bool ChipBusy  => Mode is GaugeMode.Applying or GaugeMode.Restoring;
        // Last two words joined by a no-break space: never a lone word on the last line
        public string Hint => NoOrphan(HintText);
        private static string NoOrphan(string s)
        {
            int i = s.LastIndexOf(' ');
            return i > 0 ? s[..i] + ' ' + s[(i + 1)..] : s;
        }
        private string HintText => Mode switch
        {
            GaugeMode.Applying => "Applying one module at a time. Each change is written to the snapshot first.",
            GaugeMode.Active => "Restore puts every setting back exactly as it was, in reverse order.",
            GaugeMode.Restoring => "Putting your settings back, newest change first.",
            GaugeMode.RestorePending => "PeakMode was still active when it last closed. Click to restore your original settings.",
            _ => "Every original value is saved to disk before it is changed.",
        };
        public string HeroName => Mode is GaugeMode.Active or GaugeMode.RestorePending
            ? "Restore all settings" : "Activate boost profile";

        public string Count => _applied.ToString();
        public bool   CountHot => _applied > 0;
        public string CountSuffix => _lastTotal > 0 ? $"/ {_lastTotal} SETTINGS APPLIED" : "SETTINGS APPLIED";

        private void RaiseModeTexts()
        {
            foreach (var n in new[] { nameof(Title), nameof(Chip), nameof(ChipHot), nameof(ChipAmber),
                                      nameof(ChipBusy), nameof(Hint), nameof(HeroName) })
                OnPropertyChanged(n);
        }

        private void RaiseCounter()
        {
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(CountHot));
            OnPropertyChanged(nameof(CountSuffix));
        }

        // ── actions ──────────────────────────────────────────────────────────
        private async void Toggle()
        {
            if (_core.IsBusy) return;
            if (_core.State == PeakModeCore.StateIdle)
            {
                if (_configPath == null) return;
                LoadProfile();
                await _core.ActivateAsync(_configPath);
            }
            else
            {
                await _core.RestoreAsync(PeakModeCore.ReasonWindow);
            }
        }

        // ── core events ──────────────────────────────────────────────────────
        private void OnProgress(CoreProgress p)
        {
            var card = Modules.FirstOrDefault(m => m.Step == p.Step);
            switch (p.Event)
            {
                case PeakModeCore.EvStepBegin:
                    if (Mode != GaugeMode.Applying) BeginApplying();
                    if (card != null) card.Status = "APPLYING";
                    break;
                case PeakModeCore.EvStepEnd:
                    if (card != null)
                        card.Status = p.Status switch
                        {
                            PeakModeCore.StepApplied => "APPLIED",
                            PeakModeCore.StepFailed  => "FAILED",
                            _                        => "NO CHANGE",
                        };
                    _stepsDone++;
                    _applied = p.Entries;
                    Progress = Math.Min(1, (double)_stepsDone / _enabledSteps);
                    RaiseCounter();
                    RefreshStats();
                    break;
                case PeakModeCore.EvRestoreBegin:
                    _restoring = true;
                    _restoreTotal = Math.Max(p.Entries, 1);
                    _restored = 0;
                    Mode = GaugeMode.Restoring;
                    Progress = 1;
                    break;
                case PeakModeCore.EvEntryRestored:
                    _restored = p.Entries;
                    _applied = Math.Max(0, _restoreTotal - _restored);
                    foreach (var m in Modules.Where(m => m.Status == "RESTORING" && m.Step != p.Step))
                        m.Status = "RESTORED";
                    if (card != null && card.Status != "RESTORED") card.Status = "RESTORING";
                    Progress = 1 - (double)_restored / _restoreTotal;
                    RaiseCounter();
                    break;
                case PeakModeCore.EvDone:
                    if (_restoring)
                        foreach (var m in Modules.Where(m => m.Status is "RESTORING" or "APPLIED"))
                            m.Status = "RESTORED";
                    _restoring = false;
                    break;
            }
        }

        private void BeginApplying()
        {
            _stepsDone = 0;
            _applied = 0;
            _enabledSteps = Math.Max(1, Modules.Count(m => m.Included));
            foreach (var m in Modules) m.Status = m.Included ? "QUEUED" : "SKIPPED";
            Progress = 0;
            Mode = GaugeMode.Applying;
            RaiseCounter();
        }

        private void OnStateChanged()
        {
            ToggleCommand.Refresh();
            if (!_core.IsBusy) RefreshFromState();
        }

        // Settle on the core's state when no operation is running
        private void RefreshFromState()
        {
            if (_core.IsBusy) return;
            switch (_core.State)
            {
                case PeakModeCore.StateActive:
                    Mode = GaugeMode.Active;
                    Progress = 1;
                    foreach (var m in Modules.Where(m => m.Status is "QUEUED" or "APPLYING" or "READY" && m.Included))
                        m.Status = "APPLIED";
                    break;
                case PeakModeCore.StateRestorePending:
                    Mode = GaugeMode.RestorePending;
                    Progress = 0;
                    break;
                default:
                    Mode = GaugeMode.Idle;
                    Progress = 0;
                    _applied = 0;
                    foreach (var m in Modules) m.Status = m.Included ? "READY" : "SKIPPED";
                    break;
            }
            _lastTotal = LastSessionTotal();
            RaiseCounter();
            RefreshStats();
        }

        private void RefreshStats()
        {
            bool idle = Mode == GaugeMode.Idle;
            ChangesStat.Value = _lastTotal > 0 ? $"{_applied} / {_lastTotal}" : _applied.ToString();
            ChangesStat.Note  = _applied > 0 ? "Originals saved first" : "Nothing changed yet";
            ChangesStat.Hot   = _applied > 0;
            SnapshotStat.Value = idle ? "NONE" : "SAVED";
            SnapshotStat.Note  = idle ? "Saved before any change" : "Survives crash and reboot";
            SnapshotStat.Hot   = !idle;
            string plan = SystemInfo.ActivePowerPlanName();
            PowerStat.Value = plan.ToUpperInvariant();
            PowerStat.Note  = "Windows power scheme";
            PowerStat.Hot   = plan.Contains("performance", StringComparison.OrdinalIgnoreCase);
            double ms = SystemInfo.TimerResolutionMs();
            TimerStat.Value = ms > 0 ? $"{ms:0.##} MS" : "N/A";
            TimerStat.Note  = "System timer resolution";
            TimerStat.Hot   = ms > 0 && ms <= 0.51;
        }

        // ── profile and session log ──────────────────────────────────────────
        private void LoadProfile()
        {
            JsonNode? root = null;
            try
            {
                if (_configPath != null && File.Exists(_configPath))
                    root = JsonNode.Parse(File.ReadAllText(_configPath), null,
                        new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            }
            catch (JsonException) { }

            bool B(string? section, string key, bool def)
            {
                var n = section == null ? root?[key] : root?[section]?[key];
                return n is JsonValue v && v.TryGetValue(out bool b) ? b : def;
            }
            string S(string? section, string key) =>
                (section == null ? root?[key] : root?[section]?[key]) is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : "";

            _profile = S(null, "name") is { Length: > 0 } n ? n : "default";
            OnPropertyChanged(nameof(ProfileLabel));
            bool win = B(null, "windowsTweaks", true);
            string target = S("windows", "targetProcess");
            int nv = new[] { "maxPerfMode", "disableVSync", "disablePrerenderFrames", "threadedOptimization" }
                .Count(k => B("nvidia", k, true));

            foreach (var m in Modules)
            {
                m.Included = m.Step switch
                {
                    PeakModeCore.StepProcess   => win && (B("windows", "setProcPriorityHigh", true) || B("windows", "pinToPCores", true)),
                    PeakModeCore.StepTimer     => win && B("windows", "setTimerResolution", true),
                    PeakModeCore.StepPower     => win && B("windows", "setUltimatePerf", true),
                    PeakModeCore.StepNagle     => win && B("windows", "disableNagle", true),
                    PeakModeCore.StepServices  => win && (B("windows", "disableSysMain", true) || B("windows", "disableSearchIndex", true)),
                    PeakModeCore.StepNvidia    => B(null, "nvidiaTweaks", true),
                    PeakModeCore.StepInterrupt => B(null, "interruptTweaks", true) &&
                                                  (B("interrupt", "redirectNicIrq", true) || B("interrupt", "disableNicInterruptMod", true)),
                    _ => false,
                };
            }
            Modules[0].Detail = target.Length > 0 ? $"{target}: High priority, P-cores" : "no target process set";
            Modules[5].Detail = $"{nv} driver profile setting{(nv == 1 ? "" : "s")}";
            OnPropertyChanged(nameof(Modules));
        }

        // Entries in the last completed activation of this profile (0 = none yet)
        private int LastSessionTotal()
        {
            try
            {
                var events = JsonNode.Parse(_core.ReadSessions()) as JsonArray;
                if (events == null) return 0;
                var profiles = events.Where(e => (string?)e?["type"] == "begin")
                    .ToDictionary(e => (string?)e!["session"] ?? "", e => (string?)e!["profile"] ?? "");
                var last = events.LastOrDefault(e => (string?)e?["type"] == "active" &&
                    profiles.TryGetValue((string?)e!["session"] ?? "", out var p) && p == _profile);
                return (last?["entries"] as JsonArray)?.Count ?? 0;
            }
            catch (Exception) { return 0; }
        }
    }

    internal static class Icons
    {
        // 24 x 24 stroke icons from the mockups
        public const string Boost    = "M13 2L4 14h7l-1 8 9-12h-7z";
        public const string Tweaks   = "M4 6h10M18 6h2M4 12h4M12 12h8M4 18h12";
        public const string History  = "M3 12a9 9 0 103-6.7L3 8M3 3v5h5M12 7v5l3 2";
        public const string Settings = "M12 15a3 3 0 100-6 3 3 0 000 6zM19 12h2M3 12h2M12 3v2M12 19v2";
        public const string Power    = "M13 2L4 14h7l-1 8 9-12h-7z";
        public const string Net      = "M5 12.5a10 10 0 0114 0M8.5 16a5 5 0 017 0M12 19.5h.01";
        public const string Services = "M4 6h16M4 12h16M4 18h10";
        public const string Timer    = "M12 8v4l3 2M12 21a9 9 0 100-18 9 9 0 000 18z";
        public const string Process  = "M4 4h16v16H4zM9 9h6v6H9z";
        public const string Gpu      = "M3 7h18v10H3zM7 17v3M17 17v3M8 12h3";
        public const string Irq      = "M4 12h4l2-6 4 12 2-6h4";
    }

    // Read-only system values for the stat cards
    internal static class SystemInfo
    {
        [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr guid);
        [DllImport("powrprof.dll")] private static extern uint PowerReadFriendlyName(IntPtr root, IntPtr scheme,
            IntPtr sub, IntPtr setting, byte[]? buffer, ref uint size);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);
        [DllImport("ntdll.dll")] private static extern int NtQueryTimerResolution(out uint min, out uint max, out uint cur);

        public static string ActivePowerPlanName()
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out var g) != 0) return "unknown";
            try
            {
                uint size = 0;
                PowerReadFriendlyName(IntPtr.Zero, g, IntPtr.Zero, IntPtr.Zero, null, ref size);
                if (size == 0) return "unknown";
                var buf = new byte[size];
                if (PowerReadFriendlyName(IntPtr.Zero, g, IntPtr.Zero, IntPtr.Zero, buf, ref size) != 0) return "unknown";
                return System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');
            }
            finally { LocalFree(g); }
        }

        public static double TimerResolutionMs() =>
            NtQueryTimerResolution(out _, out _, out uint cur) == 0 ? cur / 10000.0 : 0;
    }
}
