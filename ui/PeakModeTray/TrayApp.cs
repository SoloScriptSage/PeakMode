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
    internal class TrayApp : ApplicationContext
    {
        private readonly NotifyIcon        _trayIcon;
        private readonly ToolStripMenuItem _toggleItem;
        private readonly ToolStripMenuItem _statusItem;
        private readonly Icon              _iconActive;
        private readonly Icon              _iconIdle;
        private readonly IntPtr            _core;
        private readonly string?           _configPath;

        private static readonly string StateDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PeakMode");

        public TrayApp()
        {
            _core       = PeakModeCore.Orchestrator_GetInstance();
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

            UpdateUI();
            OfferLeftoverRestore();
        }

        // ── state ────────────────────────────────────────────────────────────
        private int State => PeakModeCore.Orchestrator_GetState(_core);

        private void OfferLeftoverRestore()
        {
            if (State != PeakModeCore.StateRestorePending) return;
            var answer = MessageBox.Show(
                "PeakMode was still active when it last closed (crash, kill or reboot), " +
                "so some system settings may still be tweaked.\n\n" +
                "Restore your original settings now?",
                "PeakMode", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer == DialogResult.Yes) Restore();
        }

        private void OnToggle(object? sender, EventArgs e)
        {
            try
            {
                if (State == PeakModeCore.StateIdle) Activate();
                else                                 Restore();
            }
            catch (Exception ex)
            {
                ShowError($"Unexpected error:\n\n{ex.Message}");
            }
        }

        private void Activate()
        {
            if (_configPath == null)
            {
                ShowError("Config not found. Expected config\\peakmode.json next to PeakModeTray.exe.");
                return;
            }
            Cursor.Current = Cursors.WaitCursor;
            int rc = PeakModeCore.Orchestrator_Activate(_core, _configPath);
            Cursor.Current = Cursors.Default;
            UpdateUI();

            if (rc == PeakModeCore.OK)
                _trayIcon.ShowBalloonTip(2000, "PeakMode", "Gaming mode activated", ToolTipIcon.Info);
            else
                ShowError(PeakModeCore.LastError());
        }

        private void Restore()
        {
            Cursor.Current = Cursors.WaitCursor;
            int rc = PeakModeCore.Orchestrator_Restore(_core);
            Cursor.Current = Cursors.Default;
            UpdateUI();

            if (rc == PeakModeCore.OK || rc == PeakModeCore.ErrNothing)
                _trayIcon.ShowBalloonTip(2000, "PeakMode", "Original settings restored", ToolTipIcon.Info);
            else
                ShowError(PeakModeCore.LastError() +
                          "\n\nDetails are in the log (tray menu > Open log).");
        }

        private void UpdateUI()
        {
            switch (State)
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
            if (State == PeakModeCore.StateActive)
            {
                try { PeakModeCore.Orchestrator_Restore(_core); } catch { }
            }
        }

        private void OnExit(object? sender, EventArgs e)
        {
            if (State == PeakModeCore.StateActive)
            {
                try { Restore(); } catch { }
            }
            SystemEvents.SessionEnding -= OnSessionEnding;
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            Application.Exit(); // returns from Application.Run in App.OnStartup
        }

        // ── helpers ──────────────────────────────────────────────────────────
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
