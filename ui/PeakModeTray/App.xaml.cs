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
                var preview = new MainWindow(new ViewModels.BoostViewModel(new PreviewCoreService(), TrayApp.FindConfig()))
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
                MessageBox.Show("PeakMode is already running (see the tray).", "PeakMode",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            _window ??= new MainWindow(new ViewModels.BoostViewModel(_core, TrayApp.FindConfig()));
            _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
        }

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
