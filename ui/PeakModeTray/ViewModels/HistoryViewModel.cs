using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace PeakModeTray.ViewModels
{
    // History screen: sessions from the core's session log (sessions.jsonl,
    // read through the core so the admin-only state folder is never opened
    // here) and the restore-pending banner.
    internal sealed class HistoryViewModel : ObservableObject
    {
        private readonly ICoreService _core;
        private readonly Func<DateTime> _now;
        private bool _bannerDismissed;
        private SessionItem? _selected;

        public HistoryViewModel(ICoreService core, Func<DateTime>? now = null)
        {
            _core = core;
            _now  = now ?? (() => DateTime.Now);
            RestoreNowCommand = new RelayCommand(RestoreNow, () => !_core.IsBusy);
            LaterCommand      = new RelayCommand(() => { _bannerDismissed = true; OnPropertyChanged(nameof(ShowBanner)); });
            _core.StateChanged += () =>
            {
                RestoreNowCommand.Refresh();
                OnPropertyChanged(nameof(ShowBanner));
            };
            _core.Completed += _ => Refresh();
            Refresh();
        }

        public ObservableCollection<SessionItem> Sessions { get; } = new();
        public RelayCommand RestoreNowCommand { get; }
        public RelayCommand LaterCommand      { get; }

        public bool ShowBanner => _core.State == PeakModeCore.StateRestorePending && !_bannerDismissed;
        public bool IsEmpty    => Sessions.Count == 0;
        public bool HasSelection => _selected != null;

        public SessionItem? Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                if (_selected != null) _selected.IsSelected = false;
                _selected = value;
                if (_selected != null) _selected.IsSelected = true;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelection));
            }
        }

        private async void RestoreNow()
        {
            if (_core.IsBusy) return;
            await _core.RestoreAsync(PeakModeCore.ReasonBanner);
        }

        // Re-read the log (on page show and after every activate/restore)
        public void Refresh()
        {
            // Keep the selection unless a new session appeared: then show it
            string? keep   = _selected?.Id;
            string? oldTop = Sessions.FirstOrDefault()?.Id;
            Sessions.Clear();
            foreach (var s in Parse(_core.ReadSessions(), _core.State, _now()))
            {
                s.Pick = new RelayCommand(() => Selected = s);
                Sessions.Add(s);
            }
            _selected = null;
            if (Sessions.FirstOrDefault()?.Id != oldTop) keep = null;
            Selected = Sessions.FirstOrDefault(s => s.Id == keep) ?? Sessions.FirstOrDefault();
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(ShowBanner));
        }

        // ── parsing ──────────────────────────────────────────────────────────
        // Events grouped by session id; newest session first. `state` tells
        // whether the newest session without an end is still active or
        // waiting for a restore.
        internal static List<SessionItem> Parse(string json, int state, DateTime now)
        {
            var byId  = new Dictionary<string, Raw>();
            var order = new List<Raw>();
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return new();
                foreach (var ev in doc.RootElement.EnumerateArray())
                {
                    if (ev.ValueKind != JsonValueKind.Object) continue;
                    string id = Str(ev, "session");
                    if (id.Length == 0) continue;
                    if (!byId.TryGetValue(id, out var r))
                    {
                        r = new Raw(id);
                        byId[id] = r;
                        order.Add(r);
                    }
                    switch (Str(ev, "type"))
                    {
                        case "begin":
                            r.Begin   = Time(ev);
                            r.Pid     = Int(ev, "pid");
                            r.Profile = Str(ev, "profile");
                            break;
                        case "active":
                            r.Begin ??= Time(ev);
                            if (r.Pid == 0) r.Pid = Int(ev, "pid");
                            r.Entries     = ev.TryGetProperty("entries", out var en) && en.ValueKind == JsonValueKind.Array
                                ? en.Clone() : (JsonElement?)null;
                            r.PowerScheme = Str(ev, "powerScheme");
                            break;
                        case "end":   // a retried partial restore adds another end: the last wins
                            r.End      = Time(ev);
                            r.EndPid   = Int(ev, "pid");
                            r.Reason   = Str(ev, "reason");
                            r.Result   = Str(ev, "result");
                            r.Restored = Int(ev, "restored");
                            r.Failed   = Int(ev, "failed");
                            break;
                    }
                }
            }
            catch (JsonException) { return new(); }

            var list = new List<SessionItem>();
            for (int i = order.Count - 1; i >= 0; i--)
            {
                bool newestOpen = i == order.Count - 1 && order[i].End == null;
                list.Add(Build(order[i], newestOpen ? state : PeakModeCore.StateIdle, now));
            }
            return list;
        }

        private sealed class Raw
        {
            public Raw(string id) { Id = id; }
            public string Id;
            public DateTime? Begin, End;
            public int Pid, EndPid, Restored, Failed;
            public string Profile = "", PowerScheme = "", Reason = "", Result = "";
            public JsonElement? Entries;
        }

        private static SessionItem Build(Raw r, int state, DateTime now)
        {
            var entries = new List<EntryItem>();
            if (r.Entries is JsonElement arr)
            {
                int n = 0;
                foreach (var e in arr.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.Object)
                        entries.Add(Describe(e, ++n, r.PowerScheme));
            }
            int count = entries.Count > 0 ? entries.Count : r.Restored + r.Failed;
            bool ended   = r.End != null;
            bool crashed = ended && r.EndPid != 0 && r.Pid != 0 && r.EndPid != r.Pid;
            bool clean   = ended && r.Result == "ok";
            foreach (var e in entries) e.Restored = clean;

            string settings = count == 1 ? "1 setting" : $"{count} settings";
            string line, badge, result;
            BadgeKind kind;
            if (!ended)
            {
                bool active = state == PeakModeCore.StateActive;
                bool pending = state == PeakModeCore.StateRestorePending;
                line   = settings + (active ? " · active now" : pending ? " · restore pending" : " · no restore recorded");
                badge  = active ? "ACTIVE" : pending ? "PENDING" : "NO END";
                kind   = active ? BadgeKind.Hot : BadgeKind.Amber;
                result = active ? "Still active" : pending ? "Waiting" : "Not recorded";
            }
            else
            {
                string when = crashed ? "restored on next start"
                            : $"restored {Local(r.End!.Value).ToString("HH:mm", CultureInfo.InvariantCulture)}";
                line   = $"{settings} · {when}";
                if (!clean) line = $"{settings} · {r.Failed} not restored";
                badge  = !clean ? "PARTIAL" : crashed ? "AFTER CRASH" : "CLEAN";
                kind   = clean && !crashed ? BadgeKind.Green : BadgeKind.Amber;
                result = clean ? (count == 0 ? "Nothing to undo" : $"All {count} back")
                               : $"{r.Failed} failed";
            }

            string whenText = r.Begin is DateTime b ? FormatWhen(Local(b), now) : "Unknown time";
            string profile = string.IsNullOrEmpty(r.Profile) ? "Unknown"
                           : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(r.Profile);
            return new SessionItem
            {
                Id          = r.Id,
                When        = whenText,
                Line        = line,
                Badge       = badge,
                BadgeKind   = kind,
                DetailTitle = whenText.ToUpperInvariant(),
                DetailMeta  = $"{profile} profile · {(entries.Count == 1 ? "1 snapshot entry" : $"{entries.Count} snapshot entries")}",
                ActiveFor   = r.Begin is DateTime b2 && r.End is DateTime e2 ? Duration(e2 - b2)
                            : !ended && state == PeakModeCore.StateActive && r.Begin is DateTime b3 ? Duration(now.ToUniversalTime() - b3)
                            : "n/a",
                RestoredBy  = ended ? ReasonText(r.Reason) : "Not yet",
                Result      = result,
                ResultGood  = ended && clean,
                Entries     = entries,
                EntriesNote = entries.Count == 0
                    ? (r.Entries == null ? "No snapshot entries were recorded for this session (it ended during activation)."
                                         : "Nothing was changed in this session.")
                    : "Restore runs in reverse order: the last entry is put back first.",
            };
        }

        // ── formatting ───────────────────────────────────────────────────────
        private static DateTime Local(DateTime utc) => utc.ToLocalTime();

        internal static string FormatWhen(DateTime local, DateTime now)
        {
            var ci = CultureInfo.InvariantCulture;
            int days = (now.Date - local.Date).Days;
            string time = local.ToString("HH:mm", ci);
            if (days == 0) return $"Today, {time}";
            if (days == 1) return $"Yesterday, {time}";
            if (days > 1 && days < 7) return $"{local.ToString("ddd", ci)}, {time}";
            return local.Year == now.Year ? $"{local.ToString("MMM d", ci)}, {time}"
                                          : $"{local.ToString("MMM d yyyy", ci)}, {time}";
        }

        internal static string Duration(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalMinutes < 1) return $"{Math.Max(1, (int)t.TotalSeconds)} s";
            if (t.TotalHours < 1)   return $"{(int)t.TotalMinutes} min";
            if (t.TotalDays < 1)    return t.Minutes == 0 ? $"{(int)t.TotalHours} h" : $"{(int)t.TotalHours} h {t.Minutes} min";
            return $"{(int)t.TotalDays} d {t.Hours} h";
        }

        internal static string ReasonText(string reason) => reason switch
        {
            "tray"        => "Tray icon",
            "window"      => "Boost screen",
            "exit"        => "Tray exit",
            "sessionEnd"  => "Shutdown",
            "crashPrompt" => "Crash prompt",
            "banner"      => "History banner",
            "cli"         => "Command line",
            "rollback"    => "Rollback",
            _             => "Unknown",
        };

        // One snapshot entry as "what", "before" and "during". The snapshot
        // stores only the original value; "during" is what PeakMode sets.
        internal static EntryItem Describe(JsonElement e, int n, string powerScheme)
        {
            string kind = Str(e, "kind");
            string what, before, during;
            switch (kind)
            {
                case "registryValue":
                {
                    string path = Str(e, "path"), name = Str(e, "name");
                    before = Bool(e, "existed") ? RegValue(e) : "not set";
                    if (path.StartsWith(@"SYSTEM\CurrentControlSet\Services\Tcpip\", StringComparison.OrdinalIgnoreCase))
                    {
                        what   = $"Nagle · {ShortId(LastSegment(path))} · {name}";
                        during = "1";
                    }
                    else if (path.Contains("Affinity Policy", StringComparison.OrdinalIgnoreCase))
                    {
                        what   = $"Adapter IRQ · {name}";
                        during = "changed";
                    }
                    else
                    {
                        what   = $"Adapter · {name.TrimStart('*')}";
                        during = "changed";
                    }
                    break;
                }
                case "registryKey":
                    what = $"Registry key · {LastSegment(Str(e, "path"))}"; before = "absent"; during = "created";
                    break;
                case "powerScheme":
                    what = "Power plan"; before = PlanName(Str(e, "prevActive"));
                    during = string.IsNullOrEmpty(powerScheme) ? "High perf." : PlanName(powerScheme);
                    break;
                case "powerSchemeCreated":
                    what = "Power plan · temporary copy"; before = "absent"; during = "created";
                    break;
                case "service":
                    what = $"Service · {ServiceName(Str(e, "name"))}"; before = "running"; during = "stopped";
                    break;
                case "drs":
                    (what, during) = DrsSetting((uint)Int64(e, "settingId"));
                    before = Bool(e, "existed") ? Int64(e, "value").ToString(CultureInfo.InvariantCulture) : "default";
                    break;
                case "process":
                    what = $"Process · {Str(e, "exe")}"; before = PriorityName(Int64(e, "prevPriority")); during = "High";
                    break;
                case "nicRestart":
                    what = "Adapter restart"; before = "running"; during = "restarted";
                    break;
                default:
                    what = kind.Length > 0 ? kind : "Unknown entry"; before = "saved"; during = "changed";
                    break;
            }
            return new EntryItem { N = n.ToString("00", CultureInfo.InvariantCulture), What = what, Before = before, During = during };
        }

        private static string RegValue(JsonElement e)
        {
            int type = (int)Int64(e, "type");
            byte[] data;
            try { data = Convert.FromHexString(Str(e, "data")); }
            catch (FormatException) { return "set"; }
            if (type == 4 && data.Length >= 4) return BitConverter.ToUInt32(data, 0).ToString(CultureInfo.InvariantCulture);
            if (type is 1 or 2) return Encoding.Unicode.GetString(data).TrimEnd('\0');
            return $"{data.Length} bytes";
        }

        private static string PlanName(string guid) => guid.Trim('{', '}').ToLowerInvariant() switch
        {
            "381b4222-f694-41f0-9685-ff5bb260df2e" => "Balanced",
            "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => "High perf.",
            "a1841308-3541-4fab-bc81-f71556f20b4a" => "Power saver",
            "e9a42b02-d5df-448d-aa00-03f14749eb61" => "Ultimate perf.",
            ""                                     => "unknown",
            _                                      => "custom plan",
        };

        private static string ServiceName(string name) => name switch
        {
            "WSearch" => "Windows Search",
            _         => name,
        };

        private static (string what, string during) DrsSetting(uint id) => id switch
        {
            0x1057EB71 => ("NVIDIA · power management", "max perf."),
            0x007BA09E => ("NVIDIA · max pre-rendered frames", "1"),
            0x00A879CF => ("NVIDIA · vertical sync", "force off"),
            0x20C1221E => ("NVIDIA · threaded optimization", "on"),
            _          => ($"NVIDIA · setting 0x{id:X8}", "changed"),
        };

        private static string PriorityName(long cls) => cls switch
        {
            0x20   => "Normal",
            0x40   => "Idle",
            0x80   => "High",
            0x100  => "Realtime",
            0x4000 => "Below normal",
            0x8000 => "Above normal",
            _      => "unknown",
        };

        private static string LastSegment(string path)
        {
            int i = path.LastIndexOf('\\');
            return i >= 0 ? path[(i + 1)..] : path;
        }

        // Interface GUIDs are long; the first block is enough to tell them apart
        private static string ShortId(string id)
        {
            string t = id.Trim('{', '}');
            int dash = t.IndexOf('-');
            return dash > 0 ? t[..dash] : t;
        }

        // ── JSON helpers (missing or wrong type = default) ───────────────────
        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        private static int Int(JsonElement e, string name) => (int)Int64(e, name);

        private static long Int64(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l) ? l : 0;

        private static bool Bool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        private static DateTime? Time(JsonElement e) =>
            DateTime.TryParseExact(Str(e, "time"), "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                ? t : null;
    }

    internal enum BadgeKind { Green, Amber, Hot }

    internal sealed class SessionItem : ObservableObject
    {
        private bool _isSelected;
        public string Id          { get; init; } = "";
        public string When        { get; init; } = "";
        public string Line        { get; init; } = "";
        public string Badge       { get; init; } = "";
        public BadgeKind BadgeKind { get; init; }
        public bool   BadgeGreen  => BadgeKind == BadgeKind.Green;
        public bool   BadgeHot    => BadgeKind == BadgeKind.Hot;
        public string DetailTitle { get; init; } = "";
        public string DetailMeta  { get; init; } = "";
        public string ActiveFor   { get; init; } = "";
        public string RestoredBy  { get; init; } = "";
        public string Result      { get; init; } = "";
        public bool   ResultGood  { get; init; }
        public IReadOnlyList<EntryItem> Entries { get; init; } = Array.Empty<EntryItem>();
        public string EntriesNote { get; init; } = "";
        public string AutomationName => $"{When}, {Line}, {Badge}";
        public RelayCommand? Pick { get; set; }
        public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    }

    internal sealed class EntryItem
    {
        public string N        { get; init; } = "";
        public string What     { get; init; } = "";
        public string Before   { get; init; } = "";
        public string During   { get; init; } = "";
        public bool   Restored { get; set; }
    }
}
