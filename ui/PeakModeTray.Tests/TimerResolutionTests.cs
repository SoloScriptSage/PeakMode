using System.IO;
using PeakModeTray.ViewModels;
using Xunit;

namespace PeakModeTray.Tests;

// #9: the timer request does not reach other processes, so the tweak is
// opt-in and the Boost screen's Timer card only reports the global value.
// Fake core, configs in a temp folder; nothing touches the system.
public sealed class TimerResolutionTests : IDisposable
{
    private sealed class FakeCore : ICoreService
    {
        public int  State  { get; set; } = PeakModeCore.StateIdle;
        public bool IsBusy => false;
        public event Action<CoreProgress>? Progress { add { } remove { } }
        public event Action? StateChanged { add { } remove { } }
        public event Action<CoreResult>? Completed { add { } remove { } }
        public Task<CoreResult> ActivateAsync(string configPath) => throw new NotSupportedException();
        public Task<CoreResult> RestoreAsync(int reason) => throw new NotSupportedException();
        public string ReadSessions() => "[]";
    }

    private readonly string _dir = Directory.CreateTempSubdirectory("peakmode-timer-").FullName;
    private readonly List<string> _opened = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Config(string json)
    {
        string path = Path.Combine(_dir, "peakmode.json");
        File.WriteAllText(path, json);
        return path;
    }

    // The config shipped next to the exe, copied so nothing edits the original
    private string ShippedConfig()
    {
        string path = Path.Combine(_dir, "peakmode.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "config", "peakmode.json"), path, overwrite: true);
        return path;
    }

    private static BoostViewModel Boost(string? config, double timerMs, int state = PeakModeCore.StateIdle) =>
        new(new FakeCore { State = state }, config, () => "Balanced", () => timerMs, _ => false);

    private TweaksViewModel Tweaks(string config) =>
        new(new FakeCore(), new ProfileStore(config), _opened.Add);

    private static ModuleCardViewModel TimerCard(BoostViewModel vm) =>
        vm.Modules.Single(m => m.Step == PeakModeCore.StepTimer);

    private static TweakRowViewModel TimerRow(TweaksViewModel vm) =>
        vm.Rows.Single(r => r.Step == PeakModeCore.StepTimer);

    // ── Boost: Timer stat card is information only ───────────────────────────
    [Theory]
    [InlineData(0.5, PeakModeCore.StateActive)]
    [InlineData(0.5, PeakModeCore.StateIdle)]
    [InlineData(15.625, PeakModeCore.StateIdle)]
    public void TimerCardShowsTheGlobalValueAndIsNeverHot(double ms, int state)
    {
        var vm = Boost(null, ms, state);
        Assert.Equal("GLOBAL TIMER", vm.TimerStat.Label);
        Assert.Equal($"{ms:0.##} MS", vm.TimerStat.Value);
        Assert.False(vm.TimerStat.Hot);
        Assert.Equal("Games set their own timer", vm.TimerStat.Note);
    }

    [Fact]
    public void TimerCardSaysNotAvailableWhenTheQueryFails()
    {
        var vm = Boost(null, 0);
        Assert.Equal("N/A", vm.TimerStat.Value);
        Assert.False(vm.TimerStat.Hot);
    }

    [Fact]
    public void RealTimerQueryIsReadOnlyAndWorks()
    {
        // NtQueryTimerResolution only reads; Windows' range is 0.5 to 15.625 ms
        double ms = SystemInfo.TimerResolutionMs();
        Assert.InRange(ms, 0.49, 15.7);
    }

    // ── off by default, opt-in still works ───────────────────────────────────
    [Fact]
    public void ShippedConfigLeavesTheTimerOff()
    {
        string config = ShippedConfig();
        var boost = Boost(config, 1);
        Assert.False(TimerCard(boost).Included);
        Assert.Equal("SKIPPED", TimerCard(boost).Status);
        Assert.False(TimerRow(Tweaks(config)).Included);
    }

    [Fact]
    public void MissingKeyMeansOff()
    {
        // Same default as the core (ProfileManager.h)
        string config = Config("{ \"windows\": { \"disableNagle\": true } }");
        Assert.False(TimerCard(Boost(config, 1)).Included);
        var row = TimerRow(Tweaks(config));
        Assert.False(row.Included);
        Assert.False(row.Flags.Single().Value);
    }

    [Fact]
    public void OptingInIncludesTheStep()
    {
        string config = Config("{ \"windows\": { \"setTimerResolution\": true } }");
        Assert.True(TimerCard(Boost(config, 1)).Included);
        Assert.True(TimerRow(Tweaks(config)).Included);
    }

    [Fact]
    public void TheTweaksSwitchWritesTheOptIn()
    {
        string config = Config("{ \"windows\": { \"setTimerResolution\": false } }");
        var row = TimerRow(Tweaks(config));
        row.Included = true;
        Assert.True(new ProfileStore(config).GetBool("windows", "setTimerResolution", false));
        row.Included = false;
        Assert.False(new ProfileStore(config).GetBool("windows", "setTimerResolution", true));
    }

    // ── Tweaks: one-line note and the link to #9 ─────────────────────────────
    [Fact]
    public void TimerRowExplainsWhyAndLinksTheIssue()
    {
        var vm = Tweaks(ShippedConfig());
        var row = TimerRow(vm);
        Assert.True(row.HasNote);
        Assert.StartsWith("Opt-in:", row.Note);
        Assert.DoesNotContain('\n', row.Note);
        Assert.Equal("https://github.com/SoloScriptSage/PeakMode/issues/9", row.NoteUrl);
        Assert.Contains("#9", row.OpenNoteName);

        row.OpenNote!.Execute(null);
        Assert.Equal(new[] { row.NoteUrl }, _opened);
    }

    [Fact]
    public void OnlyTheTimerRowHasANote()
    {
        var vm = Tweaks(ShippedConfig());
        Assert.All(vm.Rows.Where(r => r.Step != PeakModeCore.StepTimer), r =>
        {
            Assert.False(r.HasNote);
            Assert.Null(r.OpenNote);
        });
    }
}
