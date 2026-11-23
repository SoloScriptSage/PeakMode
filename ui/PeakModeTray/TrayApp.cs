using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PeakModeTray
{
    // Tray icon and menu. Runs on the WPF dispatcher; all core calls go
    // through CoreService. Behavior matches v0.1.0: left-click toggles,
    // balloons on success, message box on error, restore prompt on start.
    internal sealed class TrayApp : IDisposable
    {
        private readonly NotifyIcon        _trayIcon;
        private readonly ToolStripMenuItem _toggleItem;
        private readonly ToolStripMenuItem _statusItem;
        private readonly Icon              _iconActive;
        private readonly Icon              _iconIdle;
        private readonly CoreService       _core;
        private readonly string?           _configPath;
        private readonly Action            _shutdown;
        private bool _exiting;

        private static readonly string StateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PeakMode");

        public TrayApp(CoreService core, Action shutdown)
        {
            _core       = core;
            _shutdown   = shutdown;
            _configPath = FindConfig();

            (_iconActive, _iconIdle) = LoadIcons();

            _statusItem = new ToolStripMenuItem("Status: IDLE")
            {
                Enabled = false,
                Font    = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            _toggleItem = new ToolStripMenuItem("Activate", null, OnToggle);

            var menu = new ContextMenuStrip();
            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_toggleItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Open config", null, (_, _) => OpenPath(_configPath)));
            menu.Items.Add(new ToolStripMenuItem("Open log", null,
                (_, _) => OpenPath(Path.Combine(StateDir, "peakmode.log"))));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit", null, OnExit));

            _trayIcon = new NotifyIcon
            {
                Icon             = _iconIdle,
                Text             = "PeakMode: IDLE",
                ContextMenuStrip = menu,
                Visible          = true
            };
            _trayIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) OnToggle(s, e);
            };

            // Restore on logoff/shutdown. If Windows kills us first, the
            // snapshot on disk is offered for restore on next start.
            SystemEvents.SessionEnding += OnSessionEnding;
            _core.StateChanged += UpdateUI;

            UpdateUI();
        }

        // Called once the dispatcher is running (after construction).
        public async void OfferLeftoverRestore()
        {
            if (_core.State != PeakModeCore.StateRestorePending) return;
            var answer = MessageBox.Show(
                "PeakMode was still active when it last closed (crash, kill or reboot), " +
                "so some system settings may still be tweaked.\n\n" +
                "Restore your original settings now?",
                "PeakMode", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer == DialogResult.Yes) await Restore(PeakModeCore.ReasonCrashPrompt);
        }

        private async void OnToggle(object? sender, EventArgs e)
        {
            // v0.1.0 blocked the UI thread during an operation; now clicks
            // while busy are ignored instead of queued.
            if (_core.IsBusy || _exiting) return;
            try
            {
                if (_core.State == PeakModeCore.StateIdle) await Activate();
                else                                       await Restore(PeakModeCore.ReasonTray);
            }
            catch (Exception ex)
            {
                ShowError($"Unexpected error:\n\n{ex.Message}");
            }
        }

        private async Task Activate()
        {
            if (_configPath == null)
            {
                ShowError("Config not found. Expected config\\peakmode.json next to PeakModeTray.exe.");
                return;
            }
            var r = await _core.ActivateAsync(_configPath);
            if (r.Code == PeakModeCore.OK)
                ShowBalloon("Gaming mode activated");
            else
                ShowError(r.Error);
        }

        private async Task Restore(int reason)
        {
            var r = await _core.RestoreAsync(reason);
            ReportRestore(r);
        }

        private void ReportRestore(CoreResult r)
        {
            if (r.Code == PeakModeCore.OK || r.Code == PeakModeCore.ErrNothing)
                ShowBalloon("Original settings restored");
            else
                ShowError(r.Error + "\n\nDetails are in the log (tray menu > Open log).");
        }

        private void UpdateUI()
        {
            if (_exiting) return;
            _toggleItem.Enabled = !_core.IsBusy;
            switch (_core.State)
            {
                case PeakModeCore.StateActive:
                    _trayIcon.Icon   = _iconActive;
                    _trayIcon.Text   = "PeakMode: ACTIVE";
                    _statusItem.Text = "Status: ACTIVE";
                    _toggleItem.Text = "Restore";
                    break;
                case PeakModeCore.StateRestorePending:
                    _trayIcon.Icon   = _iconActive;
                    _trayIcon.Text   = "PeakMode: restore pending";
                    _statusItem.Text = "Status: RESTORE PENDING";
                    _toggleItem.Text = "Restore settings from last session";
                    break;
                default:
                    _trayIcon.Icon   = _iconIdle;
                    _trayIcon.Text   = "PeakMode: IDLE";
                    _statusItem.Text = "Status: IDLE";
                    _toggleItem.Text = "Activate";
                    break;
            }
        }

        private void OnSessionEnding(object? sender, SessionEndingEventArgs e)
        {
            // Synchronous: Windows does not wait for async work here.
            if (_core.State == PeakModeCore.StateActive || _core.IsBusy)
            {
                try { _core.Restore(PeakModeCore.ReasonSessionEnd); } catch { }
            }
        }

        private async void OnExit(object? sender, EventArgs e)
        {
            if (_exiting) return;
            _exiting = true;
            _toggleItem.Enabled = false;
            try
            {
                // A running activate finishes first (CoreService serializes),
                // so check the state after it.
                if (_core.IsBusy || _core.State == PeakModeCore.StateActive)
                {
                    var r = await _core.RestoreAsync(PeakModeCore.ReasonExit);
                    if (r.Code != PeakModeCore.OK && r.Code != PeakModeCore.ErrNothing)
                        ShowError(r.Error + "\n\nDetails are in the log (tray menu > Open log).");
                }
            }
            catch { }
            Dispose();
            _shutdown();
        }

        public void Dispose()
        {
            SystemEvents.SessionEnding -= OnSessionEnding;
            _core.StateChanged -= UpdateUI;
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        // ── helpers ──────────────────────────────────────────────────────────
        private void ShowBalloon(string text)
        {
            if (!_exiting) _trayIcon.ShowBalloonTip(2000, "PeakMode", text, ToolTipIcon.Info);
        }

        private void ShowError(string message) =>
            MessageBox.Show(message, "PeakMode", MessageBoxButtons.OK, MessageBoxIcon.Error);

        private static void OpenPath(string? path)
        {
            if (path == null || !File.Exists(path)) return;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        // Release layout: <exe dir>\config\peakmode.json
        // Dev layout: walk up from bin\Release\net8.0-windows\... to the repo's config\
        private static string? FindConfig()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 7 && dir != null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "config", "peakmode.json");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static (Icon active, Icon idle) LoadIcons()
        {
            var size = SystemInformation.SmallIconSize;
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("PeakModeTray.peakmode.ico")
                ?? throw new InvalidOperationException("embedded icon missing");
            var active = new Icon(stream, size);

            // Idle: same artwork, desaturated and dimmed
            using var color = active.ToBitmap();
            using var gray  = new Bitmap(color.Width, color.Height, PixelFormat.Format32bppArgb);
            var matrix = new ColorMatrix(new[]
            {
                new[] { 0.30f, 0.30f, 0.30f, 0f, 0f },
                new[] { 0.59f, 0.59f, 0.59f, 0f, 0f },
                new[] { 0.11f, 0.11f, 0.11f, 0f, 0f },
                new[] { 0f,    0f,    0f,    0.75f, 0f },
                new[] { 0f,    0f,    0f,    0f, 1f },
            });
            using (var g = Graphics.FromImage(gray))
            using (var attrs = new ImageAttributes())
            {
                attrs.SetColorMatrix(matrix);
                g.DrawImage(color, new Rectangle(0, 0, color.Width, color.Height),
                            0, 0, color.Width, color.Height, GraphicsUnit.Pixel, attrs);
            }
            IntPtr hIcon = gray.GetHicon();
            var idle = (Icon)Icon.FromHandle(hIcon).Clone();
            DestroyIcon(hIcon);
            return (active, idle);
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);
    }
}
