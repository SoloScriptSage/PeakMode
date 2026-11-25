using System.IO;
using PeakModeTray.ViewModels;
using Xunit;

namespace PeakModeTray.Tests;

// The Settings screen never opens anything itself: the opener is recorded.
public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("peakmode-settings-").FullName;
    private readonly List<string> _opened = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private SettingsViewModel Create(string? config, string log) =>
        new("v0.2.1", config, log, _dir, _opened.Add);

    [Fact]
    public void MissingFilesDisableTheirButtons()
    {
        var vm = Create(null, Path.Combine(_dir, "peakmode.log"));
        Assert.False(vm.OpenConfigCommand.CanExecute(null));
        Assert.False(vm.OpenLogCommand.CanExecute(null));
        Assert.Contains("Not found", vm.ConfigText);
        Assert.All(vm.Licenses, l => Assert.False(l.Open.CanExecute(null)));
    }

    [Fact]
    public void ExistingFilesOpenThroughTheOpener()
    {
        string config = Path.Combine(_dir, "peakmode.json"), log = Path.Combine(_dir, "peakmode.log");
        File.WriteAllText(config, "{}");
        File.WriteAllText(log, "");
        File.WriteAllText(Path.Combine(_dir, "LICENSE"), "MIT");
        var vm = Create(config, log);

        vm.OpenConfigCommand.Execute(null);
        vm.OpenLogCommand.Execute(null);
        vm.OpenGitHubCommand.Execute(null);
        var own = vm.Licenses.Single(l => l.Name == "PeakMode");
        own.Open.Execute(null);

        Assert.Equal(new[] { config, log, SettingsViewModel.RepoUrl, Path.Combine(_dir, "LICENSE") }, _opened);
        Assert.False(vm.Licenses.Single(l => l.Name == "Inter").CanOpen);
    }

    [Fact]
    public void LicenseListMatchesThePackageScript()
    {
        // scripts/package.ps1 is the source of truth for what ships next to the exe
        string script = File.ReadAllText(FindRepoFile("scripts", "package.ps1"));
        foreach (var (_, _, file) in SettingsViewModel.KnownLicenses)
        {
            if (file == "LICENSE") continue;
            if (file.StartsWith("THIRD-PARTY-FONT-"))
            {
                string font = file["THIRD-PARTY-FONT-".Length..^"-OFL.txt".Length];
                Assert.Contains(font, script, StringComparison.OrdinalIgnoreCase);
            }
            else Assert.Contains(file, script);
        }
    }

    [Fact]
    public void EveryLicenseHasAnAutomationName()
    {
        var vm = Create(null, Path.Combine(_dir, "x.log"));
        Assert.All(vm.Licenses, l => Assert.StartsWith("Open the ", l.AutomationName));
    }

    private static string FindRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (; dir != null; dir = dir.Parent)
        {
            string p = Path.Combine(new[] { dir.FullName }.Concat(parts).ToArray());
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException(string.Join("/", parts));
    }
}
