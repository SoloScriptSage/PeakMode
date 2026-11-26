using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PeakModeTray.ViewModels
{
    public sealed record DetailLine(string K, string V);

    // One config flag inside a row (e.g. the four NVIDIA settings)
    internal sealed class FlagViewModel : ObservableObject
    {
        private readonly TweaksViewModel _owner;
        public FlagViewModel(TweaksViewModel owner, string section, string key, string label, bool fallback = true)
        { _owner = owner; Section = section; Key = key; Label = label; Fallback = fallback; }

        public string Section { get; }
        public string Key     { get; }
        public string Label   { get; }
        // Value when the key is missing from the config (matches core/engine/ProfileManager.h)
        public bool   Fallback { get; }
        public bool   Value
        {
            get => _owner.Store.GetBool(Section, Key, Fallback);
            set => _owner.SetFlag(this, value);
        }
        public bool   CanEdit => _owner.CanEdit;
        public string SwitchName => Label;
        internal void Raise() { OnPropertyChanged(nameof(Value)); OnPropertyChanged(nameof(CanEdit)); }
    }

    internal sealed class TweakRowViewModel : ObservableObject
    {
        private readonly TweaksViewModel _owner;

        internal TweakRowViewModel(TweaksViewModel owner, int step, string name, string desc, string icon,
                                   string? sectionFlag, bool warn, IEnumerable<DetailLine> lines)
        {
            _owner = owner; Step = step; Name = name; Desc = desc; Icon = icon;
            SectionFlag = sectionFlag; Warn = warn; Lines = lines.ToList();
        }

        public int    Step { get; }
        public string Name { get; }
        public string Desc { get; }
        public string Icon { get; }
        public bool   Warn { get; }
        public string Risk => Warn ? "NETWORK BLIP" : "SAFE";
        internal string? SectionFlag { get; }   // top-level flag: windowsTweaks, nvidiaTweaks, interruptTweaks
        public IReadOnlyList<DetailLine> Lines { get; }
        public ObservableCollection<FlagViewModel> Flags { get; } = new();
        public bool HasFlags => Flags.Count > 1;
        public bool HasTarget => Step == PeakModeCore.StepProcess;

        // Opt-in rows: off by default, with a one-line reason and a link to the issue
        public string  Note    { get; init; } = "";
        public string? NoteUrl { get; init; }
        public string  NoteLinkText { get; init; } = "";
        public bool    HasNote => Note.Length > 0;
        public RelayCommand? OpenNote { get; init; }
        public string  OpenNoteName => $"Open {NoteLinkText} on GitHub: why {Name} is opt-in";

        private string _count = "";
        public string Count { get => _count; set => Set(ref _count, value); }

        private bool _open;
        public bool IsOpen { get => _open; set { if (Set(ref _open, value)) OnPropertyChanged(nameof(ExpandName)); } }
        public string ExpandName => (IsOpen ? "Hide details for " : "Show details for ") + Name;
        public RelayCommand ToggleOpen => new(() => IsOpen = !IsOpen);

        public bool Included
        {
            get => _owner.IsIncluded(this);
            set => _owner.SetIncluded(this, value);
        }
        public bool   CanEdit    => _owner.CanEdit;
        public string SwitchName => (Included ? "Skip " : "Include ") + Name;

        public string Target
        {
            get => _owner.Store.GetString("windows", "targetProcess");
            set => _owner.SetTarget(value);
        }
        private string _targetError = "";
        public string TargetError { get => _targetError; set => Set(ref _targetError, value); }

        internal void Raise()
        {
            foreach (var n in new[] { nameof(Included), nameof(CanEdit), nameof(SwitchName), nameof(Target) })
                OnPropertyChanged(n);
            foreach (var f in Flags) f.Raise();
        }
    }

    // Tweaks screen: what each step changes, read from and written to the
    // profile. Editing is only possible while PeakMode is idle.
    internal sealed class TweaksViewModel : ObservableObject
    {
        private static readonly Regex ExeName = new(@"^[\w.\- ]+\.exe$", RegexOptions.IgnoreCase);
        private readonly ICoreService _core;
        private readonly Action<string> _open;

        // #9: the timer request does not reach other processes on Windows 10 2004 and later
        internal const string TimerIssueUrl = SettingsViewModel.RepoUrl + "/issues/9";

        public ProfileStore Store { get; }
        public ObservableCollection<TweakRowViewModel> Rows { get; } = new();

        // Links go through the injected opener so tests never start a process
        public TweaksViewModel(ICoreService core, ProfileStore store, Action<string>? open = null)
        {
            _core = core;
            _open = open ?? (_ => { });
            Store = store;
            Build();
            _core.StateChanged += Refresh;
            Store.Changed += Refresh;
            Refresh();
        }

        public bool   CanEdit => Store.CanEdit && !_core.IsBusy && _core.State == PeakModeCore.StateIdle;
        public string ProfileName => (Store.GetString(null, "name") is { Length: > 0 } n ? n : "default").ToUpperInvariant();
        public string StatusNote => !Store.CanEdit ? "Config cannot be edited: " + Store.Error
            : _core.State != PeakModeCore.StateIdle || _core.IsBusy ? "Restore first to change tweaks."
            : Store.Error.Length > 0 ? Store.Error
            : "Changes are saved to config\\peakmode.json right away.";

        public string ModulesOn   => $"{Rows.Count(r => r.Included)} / {Rows.Count}";
        public string SettingsMax { get; private set; } = "0";
        public string NetRestart  => Rows.First(r => r.Step == PeakModeCore.StepInterrupt).Included ? "1" : "NONE";
        public bool   NetRestartHot => NetRestart != "NONE";

        // ── rows ─────────────────────────────────────────────────────────────
        private void Build()
        {
            TweakRowViewModel R(int step, string name, string desc, string icon, string? section, bool warn,
                                params (string k, string v)[] lines) =>
                new(this, step, name, desc, icon, section, warn, lines.Select(l => new DetailLine(l.k, l.v)));

            var process = R(PeakModeCore.StepProcess, "Process priority", "Raise priority and pin CPU cores for one game",
                Icons.Process, "windowsTweaks", false,
                ("changes", "HIGH_PRIORITY_CLASS; affinity: first thread of each physical core (P-cores on hybrid CPUs)"),
                ("restore", "original priority and affinity, only if it is still the same process"),
                ("needs", "the game already running when you boost"));
            process.Flags.Add(new(this, "windows", "setProcPriorityHigh", "High priority"));
            process.Flags.Add(new(this, "windows", "pinToPCores", "Pin to physical cores"));

            var timer = new TweakRowViewModel(this, PeakModeCore.StepTimer, "Timer resolution",
                "Finer timer for PeakMode's own process", Icons.Timer, "windowsTweaks", false,
                new[]
                {
                    new DetailLine("changes", "requests 0.5 ms timer resolution (NtSetTimerResolution 5000)"),
                    new DetailLine("effect", "the global value drops, but a game that asks for nothing keeps the default timer"),
                    new DetailLine("restore", "request released on restore or exit; not in the snapshot"),
                })
            {
                Note = "Opt-in: on Windows 10 2004 and later, games set their own timer.",
                NoteUrl = TimerIssueUrl,
                NoteLinkText = "#9",
                OpenNote = new RelayCommand(() => _open(TimerIssueUrl)),
            };
            timer.Flags.Add(new(this, "windows", "setTimerResolution", "Timer resolution", fallback: false));

            var power = R(PeakModeCore.StepPower, "Power plan", "Switch to High or Ultimate performance while boosted",
                Icons.Power, "windowsTweaks", false,
                ("changes", "Ultimate Performance (e9a42b02-d5df-448d-aa00-03f14dde8b4c), else High performance (8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c), else a temporary copy"),
                ("restore", "previous plan active again; a temporary copy is deleted"),
                ("note", "skipped if a high performance plan is already active"));
            power.Flags.Add(new(this, "windows", "setUltimatePerf", "Power plan"));

            var nagle = R(PeakModeCore.StepNagle, "Nagle off", "Send small network packets immediately",
                Icons.Net, "windowsTweaks", false,
                ("changes", "TcpAckFrequency = 1, TCPNoDelay = 1 (DWORD)"),
                ("where", @"HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{each interface}"),
                ("restore", "deleted if they did not exist, else the old value is written back"));
            nagle.Flags.Add(new(this, "windows", "disableNagle", "Nagle off"));

            var services = R(PeakModeCore.StepServices, "Background services", "Stop disk-heavy services during play",
                Icons.Services, "windowsTweaks", false,
                ("changes", "stops SysMain and Windows Search (WSearch) if running; start type unchanged"),
                ("restore", "started again if they were running"));
            services.Flags.Add(new(this, "windows", "disableSysMain", "SysMain"));
            services.Flags.Add(new(this, "windows", "disableSearchIndex", "Windows Search (WSearch)"));

            var nvidia = R(PeakModeCore.StepNvidia, "NVIDIA driver", "Tune the global driver profile through NVAPI",
                Icons.Gpu, "nvidiaTweaks", false,
                ("changes", "settings in the global (base) driver profile, the same place as NVIDIA Control Panel"),
                ("restore", "each setting set back, or reset to default if it was not set before"),
                ("needs", "an NVIDIA GPU and driver; skipped otherwise"));
            nvidia.Flags.Add(new(this, "nvidia", "maxPerfMode", "Power management mode → Prefer maximum performance (0x1057EB71)"));
            nvidia.Flags.Add(new(this, "nvidia", "disablePrerenderFrames", "Maximum pre-rendered frames → 1 (0x007BA09E)"));
            nvidia.Flags.Add(new(this, "nvidia", "disableVSync", "Vertical Sync → Force off (0x00A879CF)"));
            nvidia.Flags.Add(new(this, "nvidia", "threadedOptimization", "Threaded optimization → On (0x20C1221E)"));

            var irq = R(PeakModeCore.StepInterrupt, "Interrupts and NIC", "Interrupt affinity and Ethernet adapter properties",
                Icons.Irq, "interruptTweaks", true,
                ("changes", @"Enum\<adapter>\Device Parameters\Interrupt Management\Affinity Policy: DevicePolicy = 4, AssignmentSetOverride = all physical cores except core 0"),
                ("adapter", "*InterruptModeration, *EEE, GreenEthernet, PowerSavingMode, GigaLite, *WakeOnMagicPacket, *WakeOnPattern = 0; *ReceiveBuffers = 2048, *TransmitBuffers = 512; only values the driver declares valid"),
                ("effect", "the adapter restarts on boost and on restore: the network drops for a few seconds"));
            irq.Flags.Add(new(this, "interrupt", "redirectNicIrq", "IRQ affinity away from core 0"));
            irq.Flags.Add(new(this, "interrupt", "disableNicInterruptMod", "Adapter properties"));

            foreach (var r in new[] { process, timer, power, nagle, services, nvidia, irq }) Rows.Add(r);
        }

        // ── include logic (matches Orchestrator::stepsFor) ───────────────────
        internal bool IsIncluded(TweakRowViewModel r)
        {
            bool section = r.SectionFlag == null || Store.GetBool(null, r.SectionFlag, true);
            return section && r.Flags.Any(f => f.Value);
        }

        internal void SetIncluded(TweakRowViewModel r, bool on)
        {
            if (!CanEdit) { Refresh(); return; }
            var changes = new List<(string?, string, bool)>();
            bool windowsRow = r.SectionFlag == "windowsTweaks";
            if (on)
            {
                if (r.SectionFlag != null) changes.Add((null, r.SectionFlag, true));
                // Turning a row on turns its flags on (all of them if none was on)
                if (!r.Flags.Any(f => f.Value) || windowsRow)
                    changes.AddRange(r.Flags.Select(f => ((string?)f.Section, f.Key, true)));
            }
            else if (windowsRow)
                changes.AddRange(r.Flags.Select(f => ((string?)f.Section, f.Key, false)));
            else
                changes.Add((null, r.SectionFlag!, false));   // keep the sub-flags for next time
            Store.SetMany(changes.ToArray());
        }

        internal void SetFlag(FlagViewModel f, bool on)
        {
            if (!CanEdit) { Refresh(); return; }
            var row = Rows.First(r => r.Flags.Contains(f));
            if (on && row.SectionFlag != null && !Store.GetBool(null, row.SectionFlag, true))
                Store.SetMany((null, row.SectionFlag, true), (f.Section, f.Key, true));
            else
                Store.SetBool(f.Section, f.Key, on);
        }

        internal void SetTarget(string value)
        {
            var row = Rows.First(r => r.HasTarget);
            string v = (value ?? "").Trim();
            if (v.Length > 0 && !ExeName.IsMatch(v)) { row.TargetError = "Use the exe file name, e.g. game.exe"; return; }
            row.TargetError = "";
            if (!CanEdit || v == Store.GetString("windows", "targetProcess")) return;
            Store.SetString("windows", "targetProcess", v);
        }

        // ── refresh ──────────────────────────────────────────────────────────
        private void Refresh()
        {
            int interfaces = TcpipInterfaceCount();
            string target = Store.GetString("windows", "targetProcess");
            int total = 0;
            foreach (var r in Rows)
            {
                int on = r.Flags.Count(f => f.Value);
                (string count, int n) = r.Step switch
                {
                    PeakModeCore.StepProcess   => target.Length > 0 ? ("1 value", 1) : ("needs a target", 0),
                    PeakModeCore.StepTimer     => ("runtime", 0),
                    PeakModeCore.StepPower     => ("1 value", 1),
                    PeakModeCore.StepNagle     => ($"{2 * interfaces} values", 2 * interfaces),
                    PeakModeCore.StepServices  => ($"up to {on}", on),
                    PeakModeCore.StepNvidia    => ($"up to {on}", on),
                    _                          => ("runtime", 0),
                };
                r.Count = count;
                if (r.Included) total += n;
                r.Raise();
            }
            SettingsMax = total.ToString();
            foreach (var n in new[] { nameof(CanEdit), nameof(ProfileName), nameof(StatusNote), nameof(ModulesOn),
                                      nameof(SettingsMax), nameof(NetRestart), nameof(NetRestartHot) })
                OnPropertyChanged(n);
        }

        private static int TcpipInterfaceCount()
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces");
                return k?.SubKeyCount ?? 0;
            }
            catch (Exception) { return 0; }
        }
    }

    // Main window: the pages
    internal sealed class MainViewModel
    {
        public MainViewModel(BoostViewModel boost, TweaksViewModel tweaks, HistoryViewModel history,
            SettingsViewModel settings)
        {
            Boost = boost; Tweaks = tweaks; History = history; Settings = settings;
        }
        public BoostViewModel    Boost    { get; }
        public TweaksViewModel   Tweaks   { get; }
        public HistoryViewModel  History  { get; }
        public SettingsViewModel Settings { get; }
    }
}
