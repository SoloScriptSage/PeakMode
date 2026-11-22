using System;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;
using MessageBox = System.Windows.Forms.MessageBox;

namespace PeakModeTray
{
    public partial class App : Application
    {
        private TrayApp? _trayApp;
        private Mutex?   _singleInstance;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
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
                _trayApp = new TrayApp();
            }
            catch (DllNotFoundException)
            {
                MessageBox.Show("PeakModeCore.dll was not found next to PeakModeTray.exe.",
                    "PeakMode", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Shutdown();
                return;
            }
            System.Windows.Forms.Application.Run(_trayApp);
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _singleInstance?.Dispose();
            base.OnExit(e);
        }
    }
}
