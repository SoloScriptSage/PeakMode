using PeakModeTray.Checkup;
using Xunit;

namespace PeakModeTray.Tests;

// PC Checkup status rules on fake readings. No system access.
public sealed class CheckupLogicTests
{
    private static readonly DateTime Now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

    // ── display ──────────────────────────────────────────────────────────────
    private static DisplayReading Disp(int cur, int max, string name = "Q27G4ZR", bool builtIn = false) =>
        new(name, builtIn, "NVIDIA GeForce RTX 3050 Ti Laptop GPU", 2560, 1440, cur, max);

    [Fact]
    public void DisplayBelowItsHighestRateIsWorthChecking()
    {
        var r = Checks.Display(new[] { Disp(165, 240) });
        Assert.Equal(CheckStatus.WorthChecking, r.Status);
        Assert.Equal("165 Hz of 240 Hz", r.Value);
        Assert.Contains("240 Hz", r.Fix);
        Assert.DoesNotContain("select", r.Fix);   // one display: nothing to pick
    }

    [Theory]
    [InlineData(60, 60)]
    [InlineData(59, 60)]     // 59.94 Hz modes report as 59
    [InlineData(144, 144)]
    public void DisplayAtItsHighestRateIsOk(int cur, int max)
    {
        var r = Checks.Display(new[] { Disp(cur, max) });
        Assert.Equal(CheckStatus.Ok, r.Status);
        Assert.Equal("", r.Fix);
    }

    [Fact]
    public void WithSeveralDisplaysTheSlowOneIsShownAndNamedInTheFix()
    {
        var r = Checks.Display(new[] { Disp(165, 165, "MSI MAG273R2"), Disp(165, 240, "Q27G4ZR"), Disp(60, 60, "Panel", builtIn: true) });
        Assert.Equal(CheckStatus.WorthChecking, r.Status);
        Assert.StartsWith("Q27G4ZR on ", r.Detail);
        Assert.Contains("select Q27G4ZR", r.Fix);
        Assert.Contains(r.AllLines, l => l.K == "also" && l.V.StartsWith("MSI MAG273R2") && l.V.EndsWith("OK"));
        Assert.Contains(r.AllLines, l => l.V.StartsWith("Panel (built-in)"));
    }

    [Fact]
    public void NoDisplayIsInfo()
    {
        var r = Checks.Display(Array.Empty<DisplayReading>());
        Assert.Equal(CheckStatus.Info, r.Status);
        Assert.Equal("N/A", r.Value);
    }

    // ── memory ───────────────────────────────────────────────────────────────
    private static SmbiosInfo Mem(int rated, int conf, int count = 2, int chassis = 3, int form = 0x09) =>
        new(Enumerable.Range(0, count).Select(i => new MemoryModule($"CHANNEL {(char)('A' + i)}", rated, conf, 0x1A, form)).ToList(),
            new[] { chassis });

    [Fact]
    public void SlowMemoryOnADesktopIsWorthChecking()
    {
        var r = Checks.Memory(Mem(3200, 2133), laptop: false);
        Assert.Equal(CheckStatus.WorthChecking, r.Status);
        Assert.Equal("2133 of 3200 MT/s", r.Value);
        Assert.Contains("XMP", r.Fix);
        Assert.Equal("2 × DDR4 DIMM", r.Detail);
    }

    [Fact]
    public void SlowMemoryOnALaptopIsOnlyInfo()
    {
        var r = Checks.Memory(Mem(5600, 5200, form: 0x0D), laptop: true);
        Assert.Equal(CheckStatus.Info, r.Status);
        Assert.Equal("", r.Fix);
        Assert.Contains(r.AllLines, l => l.K == "note" && l.V.Contains("may limit"));
    }

    [Fact]
    public void DesktopsGetTheXmpHintAndLaptopsDont()
    {
        var desktop = Checks.Memory(Mem(3200, 3200), laptop: false);
        Assert.Equal(CheckStatus.Ok, desktop.Status);
        Assert.Contains(desktop.AllLines, l => l.K == "xmp" && l.V.Contains("3200 MT/s"));

        var laptop = Checks.Memory(Mem(3200, 3200, form: 0x0D), laptop: true);
        Assert.Equal(CheckStatus.Ok, laptop.Status);
        Assert.Equal("2 × DDR4 SODIMM", laptop.Detail);
        Assert.DoesNotContain(laptop.AllLines, l => l.V.Contains("XMP"));
    }

    [Fact]
    public void UnknownMemorySpeedIsInfo()
    {
        Assert.Equal(CheckStatus.Info, Checks.Memory(Mem(0, 0), laptop: false).Status);
        Assert.Equal(CheckStatus.Info, Checks.Memory(SmbiosInfo.Empty, laptop: false).Status);
    }

    [Fact]
    public void LaptopComesFromTheChassisOrTheBattery()
    {
        var power = new PowerReading(true, HasBattery: true, false, 2, "Balanced");
        Assert.True(Checks.IsLaptop(Mem(1, 1, chassis: 10), null));
        Assert.False(Checks.IsLaptop(Mem(1, 1, chassis: 3), power));       // desktop chassis with a UPS battery
        Assert.True(Checks.IsLaptop(SmbiosInfo.Empty, power));              // no chassis: battery decides
        Assert.False(Checks.IsLaptop(SmbiosInfo.Empty, power with { HasBattery = false }));
    }

    // ── power ────────────────────────────────────────────────────────────────
    private static PowerReading Pow(bool? ac = true, bool saver = false, int? mode = 4, bool battery = true) =>
        new(ac, battery, saver, mode, "Balanced");

    [Fact]
    public void PluggedInWithBestPerformanceIsOk()
    {
        var r = Checks.Power(Pow(), laptop: true, boostActive: false);
        Assert.Equal(CheckStatus.Ok, r.Status);
        Assert.Equal("AC · BEST PERF.", r.Value);
        Assert.Equal("ON LAPTOPS", r.NoteLabel);
        Assert.Contains("Armoury Crate", r.Note);
    }

    [Theory]
    [InlineData(false, false, 4, "Plug in")]
    [InlineData(true, true, 0, "Energy saver")]
    [InlineData(true, false, 1, "Best performance")]
    [InlineData(false, false, 1, "Plug in")]      // on battery comes first
    public void PowerProblemsAreWorthChecking(bool ac, bool saver, int mode, string fix)
    {
        var r = Checks.Power(Pow(ac, saver, mode), laptop: true, boostActive: false);
        Assert.Equal(CheckStatus.WorthChecking, r.Status);
        Assert.Contains(fix, r.Fix);
    }

    [Fact]
    public void DesktopsGetNoLaptopNoteAndBoostIsMentioned()
    {
        var r = Checks.Power(Pow(mode: 2, battery: false), laptop: false, boostActive: true);
        Assert.Equal(CheckStatus.Ok, r.Status);
        Assert.Equal("", r.Note);
        Assert.StartsWith("Desktop", r.Detail);
        Assert.Contains(r.AllLines, l => l.K == "boost");
        Assert.Equal("AC · MODE N/A", Checks.Power(Pow(mode: null), false, false).Value);
    }

    // ── graphics ─────────────────────────────────────────────────────────────
    private static GpuReading Nvidia(DateTime? date) =>
        new("NVIDIA GeForce RTX 3050 Ti Laptop GPU", GpuRole.Discrete, "32.0.15.6607", date, 52, 1);
    private static GpuReading Amd(DateTime? date) =>
        new("AMD Radeon(TM) Graphics", GpuRole.Integrated, "31.0.21916.2", date, 50, 2);

    [Fact]
    public void NvidiaDriverVersionUsesNvidiasNumber()
    {
        Assert.Equal("566.07", Checks.DriverLabel(Nvidia(null)));
        Assert.Equal("31.0.21916.2", Checks.DriverLabel(Amd(null)));
        Assert.Equal("N/A", Checks.DriverLabel(Nvidia(null) with { DriverVersion = "" }));
    }

    [Fact]
    public void DriverOlderThan12MonthsIsWorthChecking()
    {
        var r = Checks.Gpu(new[] { Nvidia(new DateTime(2024, 10, 20)), Amd(Now.AddMonths(-2)) }, null, Now, laptop: true);
        Assert.Equal(CheckStatus.WorthChecking, r.Status);
        Assert.Equal("DRIVER 566.07 · 25 MO.", r.Value);
        Assert.Contains("October 2024", r.Fix);
        Assert.Contains("NVIDIA or your laptop maker", r.Fix);
        Assert.Contains(r.AllLines, l => l.K == "discrete" && l.V.Contains("older than 12 months"));
    }

    [Fact]
    public void DriverWithin12MonthsIsInfo()
    {
        var r = Checks.Gpu(new[] { Nvidia(Now.AddDays(-364)) }, null, Now, laptop: false);
        Assert.Equal(CheckStatus.Info, r.Status);
        Assert.Equal("", r.Fix);
        Assert.Equal("NVIDIA GeForce RTX 3050 Ti Laptop GPU", r.Detail);
    }

    [Fact]
    public void GameOnTheBuiltInGpuIsWorthChecking()
    {
        var last = new BoostGpu("dota2.exe", "AMD Radeon(TM) Graphics", "integrated", Now.AddDays(-1));
        var r = Checks.Gpu(new[] { Nvidia(Now.AddDays(-30)), Amd(Now.AddDays(-30)) }, last, Now, laptop: true);
        Assert.Equal(CheckStatus.WorthChecking, r.Status);
        Assert.Equal("Last boost: dota2.exe ran on AMD Radeon(TM) Graphics", r.Detail);
        Assert.Contains("Graphics › dota2.exe › High performance", r.Fix);
    }

    [Fact]
    public void GameOnTheDiscreteGpuIsFineAndNoRecordGivesAHint()
    {
        var gpus = new[] { Nvidia(Now.AddDays(-30)), Amd(Now.AddDays(-30)) };
        var ran = Checks.Gpu(gpus, new BoostGpu("dota2.exe", "NVIDIA GeForce RTX 3050 Ti Laptop GPU", "discrete", null), Now, true);
        Assert.Equal(CheckStatus.Info, ran.Status);
        Assert.StartsWith("Last boost: dota2.exe ran on NVIDIA", ran.Detail);

        var none = Checks.Gpu(gpus, null, Now, true);
        Assert.Contains("target process", none.Detail);
        // "integrated" only matters on hybrid systems
        var single = Checks.Gpu(new[] { Amd(Now) with { Role = GpuRole.Single } },
            new BoostGpu("game.exe", "AMD Radeon(TM) Graphics", "integrated", null), Now, false);
        Assert.Equal(CheckStatus.Info, single.Status);
    }

    // ── memory integrity: never a recommendation ────────────────────────────
    [Theory]
    [InlineData((int)HvciState.On, "ON")]
    [InlineData((int)HvciState.Off, "OFF")]
    [InlineData((int)HvciState.Unknown, "N/A")]
    public void MemoryIntegrityIsAlwaysInfoWithTheMicrosoftLink(int state, string value)
    {
        var r = Checks.Hvci((HvciState)state);
        Assert.Equal(CheckStatus.Info, r.Status);
        Assert.Equal(value, r.Value);
        Assert.Equal("", r.Fix);
        Assert.Contains("never changes it", r.Note);
        Assert.StartsWith("https://support.microsoft.com/", r.LinkUrl);
    }

    // ── temperatures ─────────────────────────────────────────────────────────
    [Fact]
    public void ThermalZoneIsAlwaysLabeledNotCpuCore()
    {
        var r = Checks.Thermals(new[] { Nvidia(null), Amd(null) }, new[] { new ThermalZone(@"\_TZ.TZ01", 72.9) });
        Assert.Equal(CheckStatus.Info, r.Status);
        Assert.Equal("GPU 52 °C · ZONE 73 °C", r.Value);
        Assert.Contains("not the CPU core temperature", r.Detail);
        Assert.All(r.AllLines.Where(l => l.K == "zone"), l => Assert.Contains("not the CPU core temperature", l.V));
    }

    [Fact]
    public void WithoutAZoneOnlyTheGpuIsShown()
    {
        var r = Checks.Thermals(new[] { Amd(null) with { TemperatureC = 48.4 } }, Array.Empty<ThermalZone>());
        Assert.Equal("GPU 48 °C", r.Value);
        Assert.Contains("GPU only", r.Detail);
        Assert.Equal("N/A", Checks.Thermals(new[] { Amd(null) with { TemperatureC = null } }, Array.Empty<ThermalZone>()).Value);
    }

    // ── session log ──────────────────────────────────────────────────────────
    [Fact]
    public void LastGpuIsTheNewestGpuEventWithAnAdapter()
    {
        string json = """
            [ { "type": "begin", "session": "s1" },
              { "type": "gpu", "session": "s1", "process": "a.exe", "adapter": "GPU A", "role": "discrete", "time": "2026-11-20T10:00:00Z" },
              { "type": "gpu", "session": "s2", "process": "b.exe", "adapter": "GPU B", "role": "integrated", "time": "2026-11-21T10:00:00Z" },
              { "type": "gpu", "session": "s3", "process": "c.exe", "adapter": "", "role": "" },
              { "type": "end", "session": "s2" } ]
            """;
        var last = Checks.LastGpu(json);
        Assert.Equal(new BoostGpu("b.exe", "GPU B", "integrated", new DateTime(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc)), last);
        Assert.Null(Checks.LastGpu("[]"));
        Assert.Null(Checks.LastGpu("{ broken"));
        Assert.Null(Checks.LastGpu("{}"));
    }

    // ── all checks ───────────────────────────────────────────────────────────
    private sealed class FakeReader : ISystemReader
    {
        public Func<IReadOnlyList<DisplayReading>> D = () => new[] { Disp(165, 240) };
        public Func<SmbiosInfo> S = () => Mem(3200, 3200, chassis: 10);
        public Func<PowerReading> P = () => Pow();
        public Func<IReadOnlyList<GpuReading>> G = () => new[] { Nvidia(new DateTime(2024, 10, 20)) };
        public IReadOnlyList<DisplayReading> Displays() => D();
        public SmbiosInfo Smbios() => S();
        public PowerReading Power() => P();
        public IReadOnlyList<GpuReading> Gpus() => G();
        public HvciState Hvci() => throw new InvalidOperationException("no access");
        public IReadOnlyList<ThermalZone> ThermalZones() => Array.Empty<ThermalZone>();
    }

    [Fact]
    public void AllSixChecksRunAndAFailingReadOnlyAffectsItsRow()
    {
        var reader = new FakeReader { P = () => throw new UnauthorizedAccessException() };
        var rows = Checks.RunAll(reader, new CheckContext(Now, false, null));
        Assert.Equal(new[] { "display", "memory", "power", "gpu", "hvci", "thermal" }, rows.Select(r => r.Id));
        Assert.Equal(CheckStatus.WorthChecking, rows[0].Status);
        Assert.Equal(CheckStatus.Ok, rows[1].Status);
        Assert.Equal(CheckStatus.Info, rows[2].Status);
        Assert.Equal("N/A", rows[2].Value);
        Assert.Equal("N/A", rows[4].Value);
        Assert.Equal("GPU 52 °C", rows[5].Value);
        Assert.DoesNotContain(rows[1].AllLines, l => l.K == "xmp");   // chassis 10: laptop
    }
}
