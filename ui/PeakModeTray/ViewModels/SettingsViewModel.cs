using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PeakModeTray.ViewModels
{
    // Settings screen: version, where the config and log live, the licenses
    // shipped next to the exe, and the project page. Opening goes through
    // the injected opener so tests never start a process.
    internal sealed class SettingsViewModel
    {
        public const string RepoUrl = "https://github.com/SoloScriptSage/PeakMode";

        public SettingsViewModel(string version, string? configPath, string logPath, string appDir,
            Action<string> open)
        {
            Version    = version;
            ConfigPath = configPath;
            LogPath    = logPath;
            OpenConfigCommand = new RelayCommand(() => open(ConfigPath!), () => ConfigPath != null && File.Exists(ConfigPath));
            OpenLogCommand    = new RelayCommand(() => open(LogPath), () => File.Exists(LogPath));
            OpenGitHubCommand = new RelayCommand(() => open(RepoUrl));
            Licenses = KnownLicenses
                .Select(l => new LicenseItem(l.Name, l.Kind, Path.Combine(appDir, l.File), open))
                .ToList();
        }

        public string  Version    { get; }
        public string? ConfigPath { get; }
        public string  LogPath    { get; }
        public string  ConfigText => ConfigPath ?? "Not found next to PeakModeTray.exe";

        public RelayCommand OpenConfigCommand { get; }
        public RelayCommand OpenLogCommand    { get; }
        public RelayCommand OpenGitHubCommand { get; }
        public IReadOnlyList<LicenseItem> Licenses { get; }

        // The files scripts/package.ps1 puts next to the exe
        internal static readonly (string Name, string Kind, string File)[] KnownLicenses =
        {
            ("PeakMode",       "MIT",          "LICENSE"),
            ("NVIDIA NVAPI",   "MIT",          "THIRD-PARTY-NVAPI-LICENSE.txt"),
            ("nlohmann/json",  "MIT",          "THIRD-PARTY-NLOHMANN-JSON-LICENSE.txt"),
            ("Chakra Petch",   "SIL OFL 1.1",  "THIRD-PARTY-FONT-CHAKRAPETCH-OFL.txt"),
            ("Inter",          "SIL OFL 1.1",  "THIRD-PARTY-FONT-INTER-OFL.txt"),
            ("JetBrains Mono", "SIL OFL 1.1",  "THIRD-PARTY-FONT-JETBRAINSMONO-OFL.txt"),
        };
    }

    internal sealed class LicenseItem
    {
        public LicenseItem(string name, string kind, string path, Action<string> open)
        {
            Name = name; Kind = kind; Path = path;
            Open = new RelayCommand(() => open(Path), () => CanOpen);
        }
        public string Name { get; }
        public string Kind { get; }
        public string Path { get; }
        public bool CanOpen => File.Exists(Path);
        public RelayCommand Open { get; }
        public string AutomationName => $"Open the {Name} license ({Kind})";
    }
}
