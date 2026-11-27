using System.Text.Json;
using PeakModeTray.ViewModels;
using Xunit;

namespace PeakModeTray.Tests;

// History screen: parsing the session log as the core returns it
// (PeakMode_ReadSessions: JSON array of events, oldest first).
public class HistoryTests
{
    private static readonly DateTime Now = DateTime.Now;
    private static readonly DateTime T0  = DateTime.UtcNow.AddHours(-2);

    private static string Ev(string type, string session, DateTime utc, int pid, string extra = "") =>
        $"{{\"v\":1,\"type\":\"{type}\",\"session\":\"{session}\",\"time\":\"{utc:yyyy-MM-dd'T'HH:mm:ss'Z'}\",\"pid\":{pid}{extra}}}";

    private const string TwoEntries =
        ",\"entries\":[{\"kind\":\"powerScheme\",\"prevActive\":\"{381b4222-f694-41f0-9685-ff5bb260df2e}\"}," +
        "{\"kind\":\"service\",\"name\":\"WSearch\",\"wasRunning\":true}],\"powerScheme\":\"{8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c}\"";

    private static string End(string reason, string result = "ok", int restored = 2, int failed = 0) =>
        $",\"reason\":\"{reason}\",\"result\":\"{result}\",\"restored\":{restored},\"failed\":{failed}";

    private static string Log(params string[] events) => "[" + string.Join(",", events) + "]";

    private static List<SessionItem> Parse(string json, int state = PeakModeCore.StateIdle) =>
        HistoryViewModel.Parse(json, state, Now);

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("{\"type\":\"begin\"}")]
    public void EmptyOrBrokenLogGivesNoSessions(string json) => Assert.Empty(Parse(json));

    [Fact]
    public void CleanSessionFromTheWindow()
    {
        var s = Assert.Single(Parse(Log(
            Ev("begin",  "A", T0, 10, ",\"profile\":\"gaming\""),
            Ev("active", "A", T0.AddSeconds(1), 10, TwoEntries),
            Ev("end",    "A", T0.AddMinutes(3), 10, End("window")))));
        Assert.Equal("CLEAN", s.Badge);
        Assert.True(s.BadgeGreen);
        Assert.Equal("Boost screen", s.RestoredBy);
        Assert.Equal("All 2 back", s.Result);
        Assert.True(s.ResultGood);
        Assert.Equal("3 min", s.ActiveFor);
        Assert.Equal("Gaming profile · 2 snapshot entries", s.DetailMeta);
        Assert.StartsWith("2 settings · restored ", s.Line);
        Assert.All(s.Entries, e => Assert.True(e.Restored));
    }

    [Fact]
    public void GpuEventsDoNotChangeTheSession()
    {
        // PC Checkup records the boosted game's GPU while active
        string gpu = Ev("gpu", "A", T0.AddMinutes(1), 10,
            ",\"process\":\"game.exe\",\"adapter\":\"NVIDIA GeForce RTX 3050 Ti Laptop GPU\",\"role\":\"discrete\"");
        var s = Assert.Single(Parse(Log(
            Ev("begin",  "A", T0, 10, ",\"profile\":\"gaming\""),
            Ev("active", "A", T0.AddSeconds(1), 10, TwoEntries),
            gpu,
            Ev("end",    "A", T0.AddMinutes(3), 10, End("window")))));
        Assert.Equal("CLEAN", s.Badge);
        Assert.Equal("3 min", s.ActiveFor);
        Assert.Equal(2, s.Entries.Count);
    }

    [Fact]
    public void EndFromAnotherProcessIsAfterCrash()
    {
        var s = Assert.Single(Parse(Log(
            Ev("begin",  "A", T0, 10),
            Ev("active", "A", T0, 10, TwoEntries),
            Ev("end",    "A", T0.AddHours(1), 22, End("crashPrompt")))));
        Assert.Equal("AFTER CRASH", s.Badge);
        Assert.False(s.BadgeGreen);
        Assert.Equal("2 settings · restored on next start", s.Line);
        Assert.Equal("Crash prompt", s.RestoredBy);
        Assert.True(s.ResultGood);
    }

    [Fact]
    public void PartialRestoreThenRetryLastEndWins()
    {
        string begin  = Ev("begin",  "A", T0, 10);
        string active = Ev("active", "A", T0, 10, TwoEntries);
        string partial = Ev("end", "A", T0.AddMinutes(5), 10, End("tray", "partial", 1, 1));

        var s = Assert.Single(Parse(Log(begin, active, partial)));
        Assert.Equal("PARTIAL", s.Badge);
        Assert.Equal("1 failed", s.Result);
        Assert.False(s.ResultGood);
        Assert.All(s.Entries, e => Assert.False(e.Restored));

        s = Assert.Single(Parse(Log(begin, active, partial,
            Ev("end", "A", T0.AddMinutes(6), 10, End("banner")))));
        Assert.Equal("CLEAN", s.Badge);
        Assert.Equal("History banner", s.RestoredBy);
    }

    [Theory]
    [InlineData(PeakModeCore.StateActive,         "ACTIVE",  "Still active")]
    [InlineData(PeakModeCore.StateRestorePending, "PENDING", "Waiting")]
    [InlineData(PeakModeCore.StateIdle,           "NO END",  "Not recorded")]
    public void OpenNewestSessionFollowsCoreState(int state, string badge, string result)
    {
        var list = Parse(Log(
            Ev("begin",  "A", T0, 10),
            Ev("active", "A", T0, 10, TwoEntries)), state);
        var s = Assert.Single(list);
        Assert.Equal(badge, s.Badge);
        Assert.Equal(result, s.Result);
        Assert.Equal("Not yet", s.RestoredBy);
        Assert.Equal(state == PeakModeCore.StateActive, s.BadgeHot);
    }

    [Fact]
    public void OpenOlderSessionIsNeverShownAsActive()
    {
        var list = Parse(Log(
            Ev("begin", "OLD", T0, 10),
            Ev("begin", "NEW", T0.AddMinutes(10), 11),
            Ev("end",   "NEW", T0.AddMinutes(20), 11, End("tray", restored: 0))), PeakModeCore.StateActive);
        Assert.Equal(new[] { "NEW", "OLD" }, list.Select(s => s.Id));
        Assert.Equal("NO END", list[1].Badge);
    }

    [Fact]
    public void SessionsAreNewestFirstAndEventsGroupedById()
    {
        var list = Parse(Log(
            Ev("begin", "A", T0, 10),
            Ev("begin", "B", T0.AddMinutes(1), 10),
            Ev("end",   "A", T0.AddMinutes(2), 10, End("tray", restored: 0)),
            Ev("end",   "B", T0.AddMinutes(3), 10, End("exit", restored: 0))));
        Assert.Equal(new[] { "B", "A" }, list.Select(s => s.Id));
        Assert.Equal("Tray exit", list[0].RestoredBy);
        Assert.Equal("Tray icon", list[1].RestoredBy);
    }

    [Fact]
    public void CrashDuringActivationHasNoEntries()
    {
        var s = Assert.Single(Parse(Log(
            Ev("begin", "A", T0, 10),
            Ev("end",   "A", T0.AddMinutes(1), 22, End("crashPrompt", restored: 3)))));
        Assert.Empty(s.Entries);
        Assert.Equal("3 settings · restored on next start", s.Line);
        Assert.Contains("ended during activation", s.EntriesNote);
    }

    [Fact]
    public void UnknownFieldsAndKindsDoNotBreakParsing()
    {
        var s = Assert.Single(Parse(Log(
            Ev("begin",  "A", T0, 10, ",\"future\":{\"x\":[1,2]}"),
            Ev("active", "A", T0, 10, ",\"entries\":[{\"kind\":\"newThing\"},42,{\"kind\":\"drs\",\"settingId\":\"bad\"}]"),
            Ev("end",    "A", T0, 10, End("somethingNew", restored: 2)))));
        Assert.Equal(2, s.Entries.Count);
        Assert.Equal("newThing", s.Entries[0].What);
        Assert.Equal("Unknown", s.RestoredBy);
    }

    // ── one snapshot entry ───────────────────────────────────────────────
    private static EntryItem Describe(string json, string scheme = "") =>
        HistoryViewModel.Describe(JsonDocument.Parse(json).RootElement, 1, scheme);

    [Fact]
    public void DescribeNagleValue()
    {
        var e = Describe(@"{""kind"":""registryValue"",""existed"":false,""name"":""TCPNoDelay"",
            ""path"":""SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\{34460a70-6c3b-4baf-be0d-6d0e9568cc18}""}");
        Assert.Equal("01", e.N);
        Assert.Equal("Nagle · 34460a70 · TCPNoDelay", e.What);
        Assert.Equal("not set", e.Before);
        Assert.Equal("1", e.During);
    }

    [Fact]
    public void DescribeRegistryValuesDecodeDwordAndString()
    {
        var dword = Describe(@"{""kind"":""registryValue"",""existed"":true,""type"":4,""data"":""02000000"",
            ""name"":""TcpAckFrequency"",""path"":""SYSTEM\\CurrentControlSet\\Services\\Tcpip\\Parameters\\Interfaces\\{x}""}");
        Assert.Equal("2", dword.Before);
        var sz = Describe(@"{""kind"":""registryValue"",""existed"":true,""type"":1,""data"":""310000000000"",
            ""name"":""*EEE"",""path"":""SYSTEM\\CurrentControlSet\\Control\\Class\\{x}\\0001""}");
        Assert.Equal("Adapter · EEE", sz.What);
        Assert.Equal("1", sz.Before);
    }

    [Fact]
    public void DescribePowerServiceNvidiaProcess()
    {
        var p = Describe(@"{""kind"":""powerScheme"",""prevActive"":""{381B4222-F694-41F0-9685-FF5BB260DF2E}""}",
                         "{e9a42b02-d5df-448d-aa00-03f14749eb61}");
        Assert.Equal(("Power plan", "Balanced", "Ultimate perf."), (p.What, p.Before, p.During));

        var s = Describe(@"{""kind"":""service"",""name"":""WSearch""}");
        Assert.Equal(("Service · Windows Search", "running", "stopped"), (s.What, s.Before, s.During));

        var d = Describe(@"{""kind"":""drs"",""settingId"":11041231,""existed"":true,""value"":1620202130}");
        Assert.Equal(("NVIDIA · vertical sync", "1620202130", "force off"), (d.What, d.Before, d.During));
        Assert.Equal("default", Describe(@"{""kind"":""drs"",""settingId"":8102046,""existed"":false}").Before);

        var pr = Describe(@"{""kind"":""process"",""exe"":""game.exe"",""prevPriority"":32}");
        Assert.Equal(("Process · game.exe", "Normal", "High"), (pr.What, pr.Before, pr.During));
    }

    // ── formatting ───────────────────────────────────────────────────────
    [Fact]
    public void FormatWhenIsRelativeToToday()
    {
        var now = new DateTime(2026, 11, 24, 7, 0, 0);
        Assert.Equal("Today, 06:28",     HistoryViewModel.FormatWhen(new DateTime(2026, 11, 24, 6, 28, 0), now));
        Assert.Equal("Yesterday, 23:59", HistoryViewModel.FormatWhen(new DateTime(2026, 11, 23, 23, 59, 0), now));
        Assert.Equal("Sat, 18:02",       HistoryViewModel.FormatWhen(new DateTime(2026, 11, 21, 18, 2, 0), now));
        Assert.Equal("Nov 10, 09:05",    HistoryViewModel.FormatWhen(new DateTime(2026, 11, 10, 9, 5, 0), now));
        Assert.Equal("Dec 31 2025, 20:00", HistoryViewModel.FormatWhen(new DateTime(2025, 12, 31, 20, 0, 0), now));
    }

    [Theory]
    [InlineData(-5,     "1 s")]
    [InlineData(9,      "9 s")]
    [InlineData(180,    "3 min")]
    [InlineData(3600,   "1 h")]
    [InlineData(4320,   "1 h 12 min")]
    [InlineData(93600,  "1 d 2 h")]
    public void DurationText(int seconds, string expected) =>
        Assert.Equal(expected, HistoryViewModel.Duration(TimeSpan.FromSeconds(seconds)));
}
