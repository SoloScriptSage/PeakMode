using System.Text.Json.Nodes;
using PeakModeTray.ViewModels;
using Xunit;

namespace PeakModeTray.Tests;

// #7: a service PeakMode stopped is running again while the boost is active.
// Runs on a fake core and a fake service reader; nothing touches real services.
public sealed class ServiceRestartTests
{
    private sealed class FakeCore : ICoreService
    {
        public int  State  { get; set; } = PeakModeCore.StateActive;
        public bool IsBusy => false;
        public string Sessions = "[]";
        public event Action<CoreProgress>? Progress;
        public event Action? StateChanged;
        public event Action<CoreResult>? Completed { add { } remove { } }
        public Task<CoreResult> ActivateAsync(string configPath) => throw new NotSupportedException();
        public Task<CoreResult> RestoreAsync(int reason) => throw new NotSupportedException();
        public string ReadSessions() => Sessions;
        public void Raise(CoreProgress p) => Progress?.Invoke(p);
        public void SetState(int s) { State = s; StateChanged?.Invoke(); }
    }

    private static string ActiveLog(params (string name, bool wasRunning)[] services)
    {
        var entries = new JsonArray(new JsonObject { ["kind"] = "powerScheme", ["module"] = "power" });
        foreach (var (name, was) in services)
            entries.Add(new JsonObject { ["kind"] = "service", ["name"] = name, ["wasRunning"] = was });
        return new JsonArray(
            new JsonObject { ["type"] = "begin", ["session"] = "s1", ["profile"] = "default" },
            new JsonObject { ["type"] = "active", ["session"] = "s1", ["entries"] = entries }).ToJsonString();
    }

    private readonly HashSet<string> _running = new();

    private (FakeCore core, BoostViewModel vm, ModuleCardViewModel card) Create(string sessions)
    {
        var core = new FakeCore { Sessions = sessions };
        var vm = new BoostViewModel(core, null, () => "Balanced", () => 15.625, n => _running.Contains(n));
        return (core, vm, vm.Modules.Single(m => m.Step == PeakModeCore.StepServices));
    }

    [Fact]
    public void StaysAppliedWhileStopped()
    {
        var (_, vm, card) = Create(ActiveLog(("SysMain", true), ("WSearch", true)));
        vm.CheckServices();
        Assert.Equal("APPLIED", card.Status);
        Assert.False(card.IsWarn);
    }

    [Fact]
    public void RestartedServiceIsShownOnTheCard()
    {
        var (_, vm, card) = Create(ActiveLog(("SysMain", true), ("WSearch", true)));
        _running.Add("WSearch");
        vm.CheckServices();
        Assert.Equal("RESTARTED", card.Status);
        Assert.True(card.IsWarn);
        Assert.False(card.IsHot);
        Assert.Contains("WSearch", card.Detail);
        Assert.DoesNotContain("SysMain", card.Detail);
    }

    [Fact]
    public void ServicesThisSessionDidNotStopAreIgnored()
    {
        // SysMain was already stopped before the boost, so PeakMode didn't stop it
        var (_, vm, card) = Create(ActiveLog(("WSearch", true), ("SysMain", false)));
        _running.Add("SysMain");
        vm.CheckServices();
        Assert.Equal("APPLIED", card.Status);
    }

    [Fact]
    public void KeepsSayingRestartedIfTheServiceStopsAgain()
    {
        var (_, vm, card) = Create(ActiveLog(("WSearch", true)));
        _running.Add("WSearch");
        vm.CheckServices();
        _running.Clear();
        vm.CheckServices();
        Assert.Equal("RESTARTED", card.Status);
    }

    [Fact]
    public void RestoreEndsAsRestoredAndIdleResetsTheDetail()
    {
        var (core, vm, card) = Create(ActiveLog(("WSearch", true)));
        _running.Add("WSearch");
        vm.CheckServices();

        core.Raise(new CoreProgress(PeakModeCore.EvRestoreBegin, -1, 0, 1));
        core.Raise(new CoreProgress(PeakModeCore.EvDone, -1, 0, 0));
        Assert.Equal("RESTORED", card.Status);

        core.SetState(PeakModeCore.StateIdle);
        Assert.Equal("READY", card.Status);
        Assert.DoesNotContain("running again", card.Detail);
        vm.CheckServices();   // not active: no change
        Assert.Equal("READY", card.Status);
    }

    [Fact]
    public void RealServiceQueryIsReadOnlyAndWorks()
    {
        // Query access only. RpcSs (Remote Procedure Call) always runs on Windows.
        Assert.True(SystemInfo.ServiceRunning("RpcSs"));
        Assert.False(SystemInfo.ServiceRunning("PeakModeNoSuchService"));
    }

    [Fact]
    public void NoActiveEventMeansNothingToWatch()
    {
        var (_, vm, card) = Create("[]");
        _running.Add("WSearch");
        vm.CheckServices();
        Assert.Equal("APPLIED", card.Status);
    }
}
