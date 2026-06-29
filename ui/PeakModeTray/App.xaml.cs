using System;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace PeakModeTray
{
    public partial class App : Application
    {
        private TrayApp? _trayApp;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // WPF apps need this for NotifyIcon / WinForms interop
            System.Windows.Forms.Application.EnableVisualStyles();

            // Hide the WPF dispatcher window — we're tray-only
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _trayApp = new TrayApp();
            System.Windows.Forms.Application.Run(_trayApp);
        }
    }
}
