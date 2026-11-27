using PeakModeTray.Checkup;
using Xunit;

namespace PeakModeTray.Tests;

// PC Checkup: recording which GPU the boosted game uses. Fake core and fake
// sampler with no delays; the real sampler is only queried read-only.
public sealed class BoostGpuRecorderTests
{
    private static readonly GpuReading Nvidia = new("NVIDIA GeForce RTX 3050 Ti Laptop GPU", GpuRole.Discrete, "", null, null, 0x10585);
    private static readonly GpuReading Amd    = new("AMD Radeon(TM) Graphics", GpuRole.Integrated, "", null, null, 0xF6DC);

    // ── counter instance names ───────────────────────────────────────────────
    [Theory]
    [InlineData("pid_10552_luid_0x00000000_0x00010585_phys_0_eng_0_engtype_3D", 10552, 0x10585L)]
    [InlineData("pid_4_luid_0x00000001_0x0000F6DC_phys_0_eng_12_engtype_3D", 4, 0x1_0000F6DCL)]
    public void ParsesGpuEngineInstances(string instance, int pid, long luid)
    {
        Assert.True(GpuUsageSampler.TryParse(instance, out int p, out long l));
        Assert.Equal(pid, p);
        Assert.Equal(luid, l);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pid_x_luid_0x0_0x1")]
    [InlineData("pid_12_phys_0")]
    [InlineData("luid_0x00000000_0x00010585_pid")]
    public void RejectsOtherNames(string instance) => Assert.False(GpuUsageSampler.TryParse(instance, out _, out _));

    [Fact]
    public void PicksTheAdapterWithTheMostUseByThisProcess()
    {
        var usage = new[]
        {
            ("pid_100_luid_0x00000000_0x00010585_phys_0_eng_0_engtype_3D", 30.0),
            ("pid_101_luid_0x00000000_0x00010585_phys_0_eng_1_engtype_3D", 12.0),
            ("pid_100_luid_0x00000000_0x0000F6DC_phys_0_eng_0_engtype_3D", 25.0),
            ("pid_100_luid_0x00000000_0x0000F6DC_phys_0_eng_1_engtype_3D", 8.0),
            ("pid_999_luid_0x00000000_0x0000F6DC_phys_0_eng_0_engtype_3D", 90.0),   // another process
        };
        Assert.Equal(Amd, GpuUsageSampler.Pick(usage, new HashSet<int> { 100 }, new[] { Nvidia, Amd }));
        Assert.Equal(Nvidia, GpuUsageSampler.Pick(usage, new HashSet<int> { 100, 101 }, new[] { Nvidia, Amd }));
        Assert.Null(GpuUsageSampler.Pick(usage, new HashSet<int> { 5 }, new[] { Nvidia, Amd }));
        Assert.Null(GpuUsageSampler.Pick(new[] { ("pid_100_luid_0x0_0x10585_engtype_3D", 0.0) }, new HashSet<int> { 100 }, new[] { Nvidia }));
        Assert.Null(GpuUsageSampler.Pick(usage, new HashSet<int> { 100 }, Array.Empty<GpuReading>()));   // unknown adapter
    }

    [Fact]
    public void RealSamplerIsReadOnlyAndQuietForAMissingProcess()
    {
        Assert.Null(new GpuUsageSampler(new SystemReader()).Sample("PeakModeNoSuchProcess.exe"));
    }

    // ── recorder ─────────────────────────────────────────────────────────────
    private sealed class FakeCore : ICoreService
    {
        public int  State  { get; set; } = PeakModeCore.StateIdle;
        public bool IsBusy { get; set; }
        public readonly List<(string, string, string)> Recorded = new();
        public event Action<CoreProgress>? Progress { add { } remove { } }
        public event Action? StateChanged;
        public event Action<CoreResult>? Completed { add { } remove { } }
        public Task<CoreResult> ActivateAsync(string configPath) => throw new NotSupportedException();
        public Task<CoreResult> RestoreAsync(int reason) => throw new NotSupportedException();
        public string ReadSessions() => "[]";
        public bool RecordGpu(string process, string adapter, string role)
        {
            lock (Recorded) Recorded.Add((process, adapter, role));
            return true;
        }
        public void Set(int state, bool busy = false) { State = state; IsBusy = busy; StateChanged?.Invoke(); }
    }

    private sealed class FakeSampler : IGpuUsageSampler
    {
        public readonly Queue<GpuReading?> Results = new();
        public int Calls;
        public Func<GpuReading?>? OnSample;
        public GpuReading? Sample(string exeName)
        {
            Calls++;
            if (OnSample != null) return OnSample();
            return Results.Count > 0 ? Results.Dequeue() : null;
        }
    }

    private static BoostGpuRecorder Recorder(FakeCore core, FakeSampler sampler, string target = "dota2.exe", int attempts = 3) =>
        new(core, () => target, sampler, TimeSpan.Zero, TimeSpan.Zero, attempts);

    [Fact]
    public async Task RecordsOncePerBoostWithTheRole()
    {
        var core = new FakeCore();
        var sampler = new FakeSampler();
        sampler.Results.Enqueue(null);
        sampler.Results.Enqueue(Nvidia);
        using var rec = Recorder(core, sampler);
        Assert.Equal(0, sampler.Calls);              // idle: nothing sampled

        core.Set(PeakModeCore.StateActive, busy: true);
        await rec.Running;
        Assert.Equal(0, sampler.Calls);              // still activating
        core.Set(PeakModeCore.StateActive);
        await rec.Running;
        Assert.Equal(new[] { ("dota2.exe", Nvidia.Name, "discrete") }, core.Recorded);
        Assert.Equal(2, sampler.Calls);              // one retry after "nothing seen"

        core.Set(PeakModeCore.StateActive);          // another state event during the same boost
        await rec.Running;
        Assert.Single(core.Recorded);

        core.Set(PeakModeCore.StateIdle);            // restored; next boost records again
        sampler.Results.Enqueue(Amd);
        core.Set(PeakModeCore.StateActive);
        await rec.Running;
        Assert.Equal(("dota2.exe", Amd.Name, "integrated"), core.Recorded[1]);
    }

    [Fact]
    public async Task GivesUpAfterTheAttemptsWhenNothingIsSeen()
    {
        var core = new FakeCore();
        var sampler = new FakeSampler();
        using var rec = Recorder(core, sampler, attempts: 3);
        core.Set(PeakModeCore.StateActive);
        await rec.Running;
        Assert.Equal(3, sampler.Calls);
        Assert.Empty(core.Recorded);
    }

    [Fact]
    public async Task NoTargetProcessMeansNoSampling()
    {
        var core = new FakeCore { State = PeakModeCore.StateActive };
        var sampler = new FakeSampler();
        using var rec = Recorder(core, sampler, target: "  ");
        core.Set(PeakModeCore.StateActive);
        await rec.Running;
        Assert.Equal(0, sampler.Calls);
    }

    [Fact]
    public async Task ActiveAtStartupStartsSampling()
    {
        // The tray restarted while a boost was active
        var core = new FakeCore { State = PeakModeCore.StateActive };
        var sampler = new FakeSampler();
        sampler.Results.Enqueue(Nvidia);
        using var rec = Recorder(core, sampler);
        await rec.Running;
        Assert.Single(core.Recorded);
    }

    [Fact]
    public async Task RestoreCancelsSamplingAndNothingIsRecorded()
    {
        var core = new FakeCore();
        var sampler = new FakeSampler();
        using var rec = Recorder(core, sampler);
        sampler.OnSample = () =>
        {
            // Restore happens while the sample is being taken
            core.Set(PeakModeCore.StateIdle);
            return Nvidia;
        };
        core.Set(PeakModeCore.StateActive);
        await rec.Running;
        Assert.Empty(core.Recorded);
    }

    [Fact]
    public async Task ASamplerErrorCountsAsNothingSeen()
    {
        var core = new FakeCore();
        int n = 0;
        var sampler = new FakeSampler { OnSample = () => ++n == 1 ? throw new InvalidOperationException() : Nvidia };
        using var rec = Recorder(core, sampler);
        core.Set(PeakModeCore.StateActive);
        await rec.Running;
        Assert.Single(core.Recorded);
    }
}
