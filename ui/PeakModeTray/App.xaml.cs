using System;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using Application = System.Windows.Application;
using MessageBox = System.Windows.Forms.MessageBox;

namespace PeakModeTray
{
    // The WPF dispatcher is the only message loop (the tray's NotifyIcon
    // runs on it too), so WPF windows get normal keyboard input.
    public partial class App : Application
    {
        private TrayApp?     _trayApp;
        private CoreService? _core;
        private Mutex?       _singleInstance;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
#if DEBUG
            // Visual test bench for the gauge; no core, tray or mutex
            if (Array.IndexOf(e.Args, "--gauge-lab") >= 0)
            {
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                MainWindow = new Controls.GaugeLabWindow();
                return;
            }
            // Main window on a simulated core: nothing is changed on the system
            if (Array.IndexOf(e.Args, "--window-preview") >= 0)
            {
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                var core = new PreviewCoreService(pending: Array.IndexOf(e.Args, "--preview-pending") >= 0);
                bool On() => core.State == PeakModeTray.PeakModeCore.StateActive;
                // The preview never stops real services, so it can't read them:
                // --preview-restarted simulates Windows restarting WSearch (#7)
                bool restarted = Array.IndexOf(e.Args, "--preview-restarted") >= 0;
                var preview = new MainWindow(CreateMainViewModel(core, PreviewConfig(),
                    () => On() ? "High performance" : "Balanced", () => On() ? 0.5 : 15.625,
                    name => restarted && name == "WSearch"))
                {
                    AllowClose = true, Title = "PeakMode (preview, simulated core)",
                };
                MainWindow = preview;
                preview.Show();
                return;
            }
#endif
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Two tray instances would apply and journal tweaks twice.
            _singleInstance = new Mutex(true, @"Global\PeakModeTray", out bool isFirst);
            if (!isFirst)
            {
                // A hidden topmost owner keeps the box in front of whatever has
                // focus; without an owner it could open behind other windows and
                // leave this second process running unseen (#4).
                using (var owner = new Form { TopMost = true, ShowInTaskbar = false, Opacity = 0,
                                              StartPosition = FormStartPosition.CenterScreen })
                {
                    owner.Show();
                    owner.Activate();
                    MessageBox.Show(owner, "PeakMode is already running (see the tray).", "PeakMode",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                Shutdown();
                return;
            }

            System.Windows.Forms.Application.EnableVisualStyles();
            try
            {
                _core    = new CoreService(Dispatcher);
                _trayApp = new TrayApp(_core, ShutdownAll, OpenWindow);
            }
            catch (DllNotFoundException)
            {
                MessageBox.Show("PeakModeCore.dll was not found next to PeakModeTray.exe.",
                    "PeakMode", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Shutdown();
                return;
            }
            // After startup returns and the dispatcher is pumping
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                new Action(() => _trayApp?.OfferLeftoverRestore()));
        }

        private MainWindow? _window;

        private void OpenWindow()
        {
            if (_core == null) return;
            _window ??= new MainWindow(CreateMainViewModel(_core, TrayApp.FindConfig()));
            _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
        }

        private static ViewModels.MainViewModel CreateMainViewModel(ICoreService core, string? configPath,
            Func<string>? powerPlan = null, Func<double>? timerMs = null, Func<string, bool>? serviceRunning = null)
        {
            var store  = new ProfileStore(configPath);
            var boost  = new ViewModels.BoostViewModel(core, configPath, powerPlan, timerMs, serviceRunning);
            var tweaks = new ViewModels.TweaksViewModel(core, store);
            store.Changed += boost.ProfileChanged;
            var settings = new ViewModels.SettingsViewModel(VersionText(), configPath, TrayApp.LogPath,
                AppContext.BaseDirectory, TrayApp.ShellOpen);
            return new ViewModels.MainViewModel(boost, tweaks, new ViewModels.HistoryViewModel(core), settings);
        }

        internal static string VersionText()
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "PeakMode" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }

#if DEBUG
        // The preview edits a copy of the config in %TEMP%, never the real one
        private static string? PreviewConfig()
        {
            string? real = TrayApp.FindConfig();
            if (real == null) return null;
            string copy = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "peakmode-preview-config.json");
            System.IO.File.Copy(real, copy, overwrite: true);
            return copy;
        }
#endif

        private void ShutdownAll()
        {
            if (_window != null) { _window.AllowClose = true; _window.Close(); }
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _core?.Dispose();
            _singleInstance?.Dispose();
            base.OnExit(e);
        }
    }
}
