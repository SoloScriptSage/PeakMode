using PeakModeTray.Checkup;
using PeakModeTray.ViewModels;
using Xunit;

namespace PeakModeTray.Tests;

// PC Checkup screen on a fake core and a fake reader. Links go through the
// injected opener; nothing is started and nothing on the system is read.
public sealed class CheckupViewModelTests
{
    private sealed class FakeCore : ICoreService
    {
        public int  State  { get; set; } = PeakModeCore.StateIdle;
        public bool IsBusy => false;
        public string Sessions = "[]";
        public event Action<CoreProgress>? Progress { add { } remove { } }
        public event Action? StateChanged { add { } remove { } }
        public event Action<CoreResult>? Completed { add { } remove { } }
        public Task<CoreResult> ActivateAsync(string configPath) => throw new NotSupportedException();
        public Task<CoreResult> RestoreAsync(int reason) => throw new NotSupportedException();
        public string ReadSessions() => Sessions;
    }

    private sealed class FakeReader : ISystemReader
    {
        public int MaxHz = 240;
        public int Calls;
        public IReadOnlyList<DisplayReading> Displays() { Calls++; return new[] { new DisplayReading("Q27G4ZR", false, "GPU", 2560, 1440, 165, MaxHz) }; }
        public SmbiosInfo Smbios() => new(new[] { new MemoryModule("A", 3200, 3200, 0x1A, 0x0D) }, new[] { 10 });
        public PowerReading Power() => new(true, true, false, 4, "Balanced");
        public IReadOnlyList<GpuReading> Gpus() => new[]
        {
            new GpuReading("NVIDIA GeForce RTX 3050 Ti Laptop GPU", GpuRole.Discrete, "32.0.15.6607", new DateTime(2026, 9, 1), 52, 1),
            new GpuReading("AMD Radeon(TM) Graphics", GpuRole.Integrated, "31.0.21916.2", new DateTime(2026, 9, 1), 50, 2),
        };
        public HvciState Hvci() => HvciState.Off;
        public IReadOnlyList<ThermalZone> ThermalZones() => new[] { new ThermalZone(@"\_TZ.TZ01", 70) };
    }

    private static readonly DateTime Now = new(2026, 11, 27, 14, 32, 0);
    private readonly List<string> _opened = new();
    private readonly FakeCore _core = new();
    private readonly FakeReader _reader = new();

    private CheckupViewModel Create() => new(_core, _opened.Add, _reader, () => Now);

    [Fact]
    public async Task ChecksRunAndAreCounted()
    {
        var vm = Create();
        Assert.Empty(vm.Rows);
        Assert.Equal("Not checked yet.", vm.CheckedText);
        await vm.RefreshAsync();
        Assert.Equal(6, vm.Rows.Count);
        Assert.Equal("2", vm.OkCount);      // memory, power
        Assert.Equal("1", vm.WarnCount);    // display
        Assert.Equal("3", vm.InfoCount);    // graphics, memory integrity, temperatures
        Assert.True(vm.WarnHot);
        Assert.StartsWith("Checked 14:32", vm.CheckedText);
        Assert.False(vm.IsBusy);
        Assert.True(vm.RecheckCommand.CanExecute(null));
    }

    [Fact]
    public async Task WorthCheckingRowsStartOpenAndShowTheirFix()
    {
        var vm = Create();
        await vm.RefreshAsync();
        var display = vm.Rows.Single(r => r.Id == "display");
        Assert.True(display.IsOpen && display.IsWarn && display.HasFix);
        Assert.Equal("WORTH CHECKING", display.Tag);
        Assert.Contains("How to fix: Settings", display.SpokenStatus);
        Assert.All(vm.Rows.Where(r => !r.IsWarn), r => Assert.False(r.IsOpen));
        Assert.Equal("Hide details for Display refresh rate", display.ExpandName);
        display.ToggleOpen.Execute(null);
        Assert.Equal("Show details for Display refresh rate", display.ExpandName);
    }

    [Fact]
    public async Task RecheckKeepsOpenRowsWhileTheirStatusStaysTheSame()
    {
        var vm = Create();
        await vm.RefreshAsync();
        vm.Rows.Single(r => r.Id == "memory").IsOpen = true;
        vm.Rows.Single(r => r.Id == "display").IsOpen = false;
        await vm.RefreshAsync();
        Assert.True(vm.Rows.Single(r => r.Id == "memory").IsOpen);
        Assert.False(vm.Rows.Single(r => r.Id == "display").IsOpen);

        _reader.MaxHz = 165;             // fixed: display is OK now, back to closed
        vm.Rows.Single(r => r.Id == "display").IsOpen = true;
        await vm.RefreshAsync();
        var display = vm.Rows.Single(r => r.Id == "display");
        Assert.True(display.IsOk);
        Assert.False(display.IsOpen);
        Assert.Equal("0", vm.WarnCount);
        Assert.Equal(3, _reader.Calls);
    }

    [Fact]
    public async Task MemoryIntegrityLinkGoesThroughTheOpener()
    {
        var vm = Create();
        await vm.RefreshAsync();
        var hvci = vm.Rows.Single(r => r.Id == "hvci");
        Assert.True(hvci.HasLink && hvci.HasNote);
        Assert.Equal("ABOUT", hvci.NoteLabel);
        hvci.OpenLink.Execute(null);
        Assert.Equal(new[] { Checks.GamePerfUrl }, _opened);
        Assert.All(vm.Rows.Where(r => r.Id != "hvci"), r => Assert.False(r.OpenLink.CanExecute(null)));
    }

    [Fact]
    public async Task LastBoostGpuAndBoostStateComeFromTheCore()
    {
        _core.State = PeakModeCore.StateActive;
        _core.Sessions = """
            [ { "type": "gpu", "session": "s1", "process": "dota2.exe", "adapter": "AMD Radeon(TM) Graphics", "role": "integrated" } ]
            """;
        var vm = Create();
        await vm.RefreshAsync();
        var gpu = vm.Rows.Single(r => r.Id == "gpu");
        Assert.True(gpu.IsWarn);
        Assert.Equal("Last boost: dota2.exe ran on AMD Radeon(TM) Graphics", gpu.Detail);
        Assert.Contains(vm.Rows.Single(r => r.Id == "power").Lines, l => l.K == "boost");
    }

    [Fact]
    public async Task UnreadableSessionLogStillRunsTheChecks()
    {
        _core.Sessions = "not json";
        var vm = Create();
        await vm.RefreshAsync();
        Assert.Equal(6, vm.Rows.Count);
        Assert.Contains("target process", vm.Rows.Single(r => r.Id == "gpu").Detail);
    }
}
