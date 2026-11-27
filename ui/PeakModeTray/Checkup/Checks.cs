using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using PeakModeTray.ViewModels;

namespace PeakModeTray.Checkup
{
    internal enum CheckStatus { Ok, WorthChecking, Info }

    // One row of the Checkup screen. Fix: the amber "how to fix" line (only
    // with WorthChecking). Note: a neutral line with its own label.
    internal sealed record CheckResult(
        string Id, string Name, string Icon, CheckStatus Status, string Value, string Detail,
        string Fix = "", string NoteLabel = "", string Note = "",
        IReadOnlyList<DetailLine>? Lines = null, string LinkText = "", string LinkUrl = "")
    {
        public IReadOnlyList<DetailLine> AllLines => Lines ?? Array.Empty<DetailLine>();
    }

    // The newest "gpu" event of the session log (recorded during a boost)
    internal sealed record BoostGpu(string Process, string Adapter, string Role, DateTime? Time);

    internal sealed record CheckContext(DateTime Now, bool BoostActive, BoostGpu? LastGpu);

    // Turns readings into results. Pure: no system access, so every rule is
    // tested on fake readings. docs/checkup.md describes the rules.
    internal static class Checks
    {
        public const string GamePerfUrl =
            "https://support.microsoft.com/en-us/windows/options-to-optimize-gaming-performance-in-windows-11-a255f612-2949-4373-a566-ff6f3f474613";
        public static readonly TimeSpan DriverAgeLimit = TimeSpan.FromDays(365);

        public static class Icons
        {
            public const string Display = "M3 4h18v12H3zM8 20h8M12 16v4";
            public const string Memory  = "M3 7h18v10H3zM7 7v10M11 7v10M15 7v10M5 17v3M19 17v3";
            public const string Power   = "M13 2L4 14h7l-1 8 9-12h-7z";
            public const string Gpu     = "M3 7h18v10H3zM7 17v3M17 17v3M8 12h3";
            public const string Shield  = "M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z";
            public const string Thermo  = "M14 14.8V4a2 2 0 10-4 0v10.8a4 4 0 104 0z";
        }

        private static DetailLine L(string k, string v) => new(k, v);

        // ── all checks; one failing read never stops the others ─────────────
        public static IReadOnlyList<CheckResult> RunAll(ISystemReader reader, CheckContext ctx)
        {
            T? Safe<T>(Func<T> read) where T : class { try { return read(); } catch (Exception) { return null; } }
            var smbios = Safe(reader.Smbios) ?? SmbiosInfo.Empty;
            var power  = Safe(reader.Power);
            var gpus   = Safe(reader.Gpus) ?? Array.Empty<GpuReading>();
            bool laptop = IsLaptop(smbios, power);

            CheckResult Run(string id, string name, string icon, Func<CheckResult> check)
            {
                try { return check(); }
                catch (Exception) { return Unreadable(id, name, icon); }
            }
            return new[]
            {
                Run("display", "Display refresh rate", Icons.Display, () => Display(reader.Displays())),
                Run("memory",  "Memory speed",         Icons.Memory,  () => Memory(smbios, laptop)),
                Run("power",   "Power",                Icons.Power,   () => Power(power ?? throw new InvalidOperationException(), laptop, ctx.BoostActive)),
                Run("gpu",     "Graphics",             Icons.Gpu,     () => Gpu(gpus, ctx.LastGpu, ctx.Now, laptop)),
                Run("hvci",    "Memory integrity",     Icons.Shield,  () => Hvci(reader.Hvci())),
                Run("thermal", "Temperatures",         Icons.Thermo,  () => Thermals(gpus, reader.ThermalZones())),
            };
        }

        public static CheckResult Unreadable(string id, string name, string icon) =>
            new(id, name, icon, CheckStatus.Info, "N/A", "Couldn't be read on this PC");

        public static bool IsLaptop(SmbiosInfo s, PowerReading? p) =>
            s.ChassisTypes.Count > 0 ? s.ChassisTypes.Any(Smbios.IsPortable) : p?.HasBattery == true;

        // ── 1. display ───────────────────────────────────────────────────────
        public static CheckResult Display(IReadOnlyList<DisplayReading> displays)
        {
            const string id = "display", name = "Display refresh rate";
            if (displays.Count == 0) return Unreadable(id, name, Icons.Display);
            static string Label(DisplayReading d) =>
                (d.Name.Length > 0 ? d.Name : "Display") + (d.BuiltIn ? " (built-in)" : "");
            static bool Below(DisplayReading d) => d.MaxHz - d.CurrentHz > 1;   // 59 vs 60 is the same rate

            var main = displays.Where(Below).OrderByDescending(d => d.MaxHz - d.CurrentHz).FirstOrDefault() ?? displays[0];
            var lines = new List<DetailLine> { L("read from", "the current mode and every mode the driver lists at this resolution") };
            lines.AddRange(displays.Where(d => d != main).Select(d =>
                L("also", $"{Label(d)}, {d.Width} × {d.Height}: {d.CurrentHz} of {d.MaxHz} Hz{(Below(d) ? "" : ", OK")}")));
            lines.Add(L("can't tell", "whether you capped it on purpose, or whether the cable carries the top mode reliably"));

            string detail = $"{Label(main)} on {main.Adapter}, {main.Width} × {main.Height}";
            string value = $"{main.CurrentHz} Hz of {main.MaxHz} Hz";
            if (!Below(main))
                return new(id, name, Icons.Display, CheckStatus.Ok, value, detail, Lines: lines);
            string which = displays.Count > 1 ? $" › select {Label(main)}" : "";
            return new(id, name, Icons.Display, CheckStatus.WorthChecking, value, detail,
                Fix: $"Settings › System › Display › Advanced display{which} › Choose a refresh rate › {main.MaxHz} Hz.",
                Lines: lines);
        }

        // ── 2. memory ────────────────────────────────────────────────────────
        public static CheckResult Memory(SmbiosInfo s, bool laptop)
        {
            const string id = "memory", name = "Memory speed";
            var mods = s.Modules.Where(m => m.RatedMts > 0 && m.ConfiguredMts > 0).ToList();
            if (mods.Count == 0) return Unreadable(id, name, Icons.Memory);

            var slow = mods.Where(m => m.ConfiguredMts < m.RatedMts).ToList();
            var main = slow.FirstOrDefault() ?? mods[0];
            string kind = string.Join(" ", new[] { Smbios.MemoryTypeName(main.MemoryType), Smbios.FormFactorName(main.FormFactor) }
                .Where(x => x.Length > 0));
            string detail = $"{mods.Count} × {(kind.Length > 0 ? kind : "module")}";
            string value = $"{main.ConfiguredMts} of {main.RatedMts} MT/s";
            var lines = new List<DetailLine> { L("read from", "SMBIOS: configured speed and rated speed of each module") };
            lines.AddRange(mods.Where(m => m != main && m.Slot.Length > 0).Select(m => L("also", $"{m.Slot}: {m.ConfiguredMts} of {m.RatedMts} MT/s")));

            if (slow.Count > 0 && laptop)
            {
                lines.Add(L("note", "the CPU or the laptop may limit the memory speed; laptop memory has no XMP or EXPO setting"));
                return new(id, name, Icons.Memory, CheckStatus.Info, value, detail, Lines: lines);
            }
            if (slow.Count > 0)
            {
                lines.Add(L("can't tell", "the XMP/EXPO rating: Windows only sees the module's standard speed"));
                return new(id, name, Icons.Memory, CheckStatus.WorthChecking, value, detail,
                    Fix: "In the BIOS, set the memory to its rated speed or turn on its XMP or EXPO profile.", Lines: lines);
            }
            if (!laptop)
                lines.Add(L("xmp", $"if your memory was sold as faster than {main.RatedMts} MT/s, turn on XMP or EXPO in the BIOS"));
            return new(id, name, Icons.Memory, CheckStatus.Ok, value, detail, Lines: lines);
        }

        // ── 3. power ─────────────────────────────────────────────────────────
        public static string ModeName(int? mode) => mode switch
        {
            0 => "Energy saver", 1 => "Best power efficiency", 2 => "Balanced", 3 => "High performance",
            4 => "Best performance", 5 => "Game mode", 6 => "Mixed reality", _ => "unknown",
        };
        private static string ModeShort(int? mode) => mode switch
        {
            0 => "ENERGY SAVER", 1 => "EFFICIENCY", 2 => "BALANCED", 3 => "HIGH PERF.", 4 => "BEST PERF.",
            5 => "GAME MODE", 6 => "MIXED REALITY", _ => "MODE N/A",
        };

        public const string LaptopNote =
            "Your laptop maker's performance mode (for example in Armoury Crate or Lenovo Vantage) sets power limits and fans. Windows can't see it, and it often matters more than this.";

        public static CheckResult Power(PowerReading p, bool laptop, bool boostActive)
        {
            const string id = "power", name = "Power";
            string source = p.OnAc switch { true => "AC", false => "BATTERY", _ => "AC N/A" };
            string value = $"{source} · {ModeShort(p.EffectiveMode)}";
            string plugged = p.OnAc switch { true => "plugged in", false => "on battery", _ => "power source unknown" };
            string detail = $"{(laptop ? "Laptop" : "Desktop")}, {plugged} · power mode {ModeName(p.EffectiveMode)}";
            var lines = new List<DetailLine>
            {
                L("plan", p.Plan),
                L("read from", "power status (AC, battery, energy saver) and the effective power mode"),
            };
            if (boostActive) lines.Add(L("boost", "a boost is active: the power plan is PeakMode's until you restore"));

            string fix = p.OnAc == false && p.HasBattery
                    ? "Plug in: on battery, Windows and the laptop limit CPU and GPU power."
                : p.EnergySaver
                    ? "Turn off Energy saver: Settings › System › Power & battery › Energy saver."
                : p.OnAc == true && p.EffectiveMode == 1
                    ? "Settings › System › Power & battery › Power mode › Best performance."
                : "";
            return new(id, name, Icons.Power, fix.Length > 0 ? CheckStatus.WorthChecking : CheckStatus.Ok, value, detail,
                Fix: fix, NoteLabel: laptop ? "ON LAPTOPS" : "", Note: laptop ? LaptopNote : "", Lines: lines);
        }

        // ── 4. graphics ──────────────────────────────────────────────────────
        // NVIDIA's own number from the Windows driver version: 32.0.15.6607 → 566.07
        public static string DriverLabel(GpuReading g)
        {
            var parts = g.DriverVersion.Split('.');
            if (g.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) && parts.Length == 4 &&
                parts[2].Length > 0 && parts[3].Length is > 0 and <= 4 && parts.All(x => x.All(char.IsDigit)))
            {
                string d = parts[2][^1] + parts[3].PadLeft(4, '0');
                return $"{d[..3]}.{d[3..]}";
            }
            return g.DriverVersion.Length > 0 ? g.DriverVersion : "N/A";
        }

        private static string Vendor(string gpu) =>
            gpu.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA"
            : gpu.Contains("AMD", StringComparison.OrdinalIgnoreCase) || gpu.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ? "AMD"
            : gpu.Contains("Intel", StringComparison.OrdinalIgnoreCase) ? "Intel" : "the GPU maker";

        private static string MonthYear(DateTime d) => d.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

        public static CheckResult Gpu(IReadOnlyList<GpuReading> gpus, BoostGpu? last, DateTime now, bool laptop)
        {
            const string id = "gpu", name = "Graphics";
            if (gpus.Count == 0) return Unreadable(id, name, Icons.Gpu);
            bool hybrid = gpus.Any(g => g.Role == GpuRole.Discrete) && gpus.Any(g => g.Role == GpuRole.Integrated);
            var main = gpus.FirstOrDefault(g => g.Role == GpuRole.Discrete) ?? gpus[0];
            bool Old(GpuReading g) => g.DriverDate is { } d && now - d > DriverAgeLimit;
            var old = gpus.Where(Old).OrderByDescending(g => g == main).FirstOrDefault();
            bool ranOnIntegrated = hybrid && last?.Role == "integrated";

            string value = "DRIVER " + DriverLabel(main) +
                (main.DriverDate is { } md ? $" · {Math.Max(0, (int)((now - md).TotalDays / 30.44))} MO." : "");
            string detail = last != null && last.Adapter.Length > 0
                ? $"Last boost: {last.Process} ran on {last.Adapter}"
                : hybrid ? "Boost with a target process set to see which GPU your game uses" : main.Name;

            var lines = gpus.Select(g => L(g.Role switch { GpuRole.Discrete => "discrete", GpuRole.Integrated => "built-in", _ => "gpu" },
                $"{g.Name}: driver {DriverLabel(g)}{(g.DriverDate is { } d ? ", " + MonthYear(d) : "")}{(Old(g) ? ", older than 12 months" : "")}")).ToList();
            lines.Add(L("read from", "adapters and their hybrid role (D3DKMT), driver version and date (device properties); the GPU per process is sampled during a boost"));
            lines.Add(L("can't tell", "which GPU a game used outside a PeakMode boost, or whether a newer driver exists"));

            string maker = Vendor(old?.Name ?? main.Name) + (laptop ? " or your laptop maker" : "");
            string fix = ranOnIntegrated
                ? $"{last!.Process} ran on the built-in GPU. Settings › System › Display › Graphics › {last.Process} › High performance."
                : old != null ? $"The {old.Name} driver is from {MonthYear(old.DriverDate!.Value)}. Get the current one from {maker}."
                : "";
            return new(id, name, Icons.Gpu, fix.Length > 0 ? CheckStatus.WorthChecking : CheckStatus.Info, value, detail,
                Fix: fix, Lines: lines);
        }

        // ── 5. memory integrity: information only, never a recommendation ───
        public static CheckResult Hvci(HvciState h) => new(
            "hvci", "Memory integrity", Icons.Shield, CheckStatus.Info,
            h switch { HvciState.On => "ON", HvciState.Off => "OFF", _ => "N/A" },
            "Core isolation, a Windows security feature",
            NoteLabel: "ABOUT",
            Note: "Microsoft lists turning it off among the gaming performance options, with a security trade-off. PeakMode never changes it.",
            Lines: new[] { L("read from", "code integrity options (NtQuerySystemInformation)") },
            LinkText: "MICROSOFT", LinkUrl: GamePerfUrl);

        // ── 6. temperatures: information only ───────────────────────────────
        public const string ZoneLabel = "ACPI thermal zone, not the CPU core temperature";

        public static CheckResult Thermals(IReadOnlyList<GpuReading> gpus, IReadOnlyList<ThermalZone> zones)
        {
            const string id = "thermal", name = "Temperatures";
            var hot = gpus.Where(g => g.TemperatureC is not null).ToList();
            var main = hot.FirstOrDefault(g => g.Role == GpuRole.Discrete) ?? hot.FirstOrDefault();
            var parts = new List<string>();
            if (main != null) parts.Add($"GPU {main.TemperatureC:0} °C");
            if (zones.Count > 0) parts.Add($"ZONE {zones[0].Celsius:0} °C");
            if (parts.Count == 0) return Unreadable(id, name, Icons.Thermo);

            var lines = hot.Select(g => L("gpu", $"{g.Name}: {g.TemperatureC:0} °C, the reading Task Manager shows")).ToList();
            lines.AddRange(zones.Select(z => L("zone", $"{z.Name}: {z.Celsius:0} °C, {ZoneLabel}")));
            lines.Add(L("can't tell", "the CPU core temperature: Windows has no documented way to read it without a kernel driver"));
            string detail = zones.Count > 0 ? $"Right now. Zone: {ZoneLabel}" : "Right now. GPU only: the CPU core temperature can't be read";
            return new(id, name, Icons.Thermo, CheckStatus.Info, string.Join(" · ", parts), detail, Lines: lines);
        }

        // ── session log: newest "gpu" event ──────────────────────────────────
        public static BoostGpu? LastGpu(string sessionsJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(sessionsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
                BoostGpu? last = null;
                foreach (var ev in doc.RootElement.EnumerateArray())
                {
                    if (ev.ValueKind != JsonValueKind.Object || Str(ev, "type") != "gpu") continue;
                    string adapter = Str(ev, "adapter");
                    if (adapter.Length == 0) continue;
                    DateTime? time = DateTime.TryParse(Str(ev, "time"), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;
                    last = new BoostGpu(Str(ev, "process"), adapter, Str(ev, "role"), time);
                }
                return last;
            }
            catch (JsonException) { return null; }
        }

        private static string Str(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }
}
