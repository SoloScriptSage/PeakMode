using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PeakModeTray
{
    internal class TrayApp : ApplicationContext
    {
        private readonly NotifyIcon _trayIcon;
        private readonly ToolStripMenuItem _toggleItem;
        private readonly ToolStripMenuItem _statusItem;

        private bool _isActive = false;

        // exe lives at: ui/PeakModeTray/bin/Release/net8.0-windows/
        // config lives at: config/peakmode.json (5 levels up)
        private static readonly string ConfigPath = Path.GetFullPath(Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "..", "..", "..", "..", "..",
            "config", "peakmode.json"));

        public TrayApp()
        {
            _statusItem = new ToolStripMenuItem("Status: IDLE")
            {
                Enabled = false,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };

            _toggleItem = new ToolStripMenuItem("Activate", null, OnToggle);

            var exitItem = new ToolStripMenuItem("Exit", null, OnExit);

            var menu = new ContextMenuStrip();
            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_toggleItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _trayIcon = new NotifyIcon
            {
                Icon             = CreateIcon(active: false),
                Text             = "PeakMode — IDLE",
                ContextMenuStrip = menu,
                Visible          = true
            };

            _trayIcon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                    OnToggle(s, e);
            };
        }

        private void OnToggle(object? sender, EventArgs e)
        {
            try
            {
                if (!_isActive) Activate();
                else            Restore();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"PeakMode error:\n\n{ex.Message}\n\nMake sure PeakModeCore.dll is next to the .exe and you're running as Administrator.",
                    "PeakMode", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void Activate()
        {
            if (!File.Exists(ConfigPath))
            {
                MessageBox.Show($"Config not found:\n{ConfigPath}", "PeakMode",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var inst = PeakModeCore.Orchestrator_GetInstance();
            PeakModeCore.Orchestrator_Activate(inst, ConfigPath);

            _isActive = true;
            UpdateUI();

            _trayIcon.ShowBalloonTip(2000, "PeakMode",
                "Gaming mode activated", ToolTipIcon.Info);
        }

        private void Restore()
        {
            var inst = PeakModeCore.Orchestrator_GetInstance();
            PeakModeCore.Orchestrator_Restore(inst);

            _isActive = false;
            UpdateUI();

            _trayIcon.ShowBalloonTip(2000, "PeakMode",
                "System restored to defaults", ToolTipIcon.Info);
        }

        private void UpdateUI()
        {
            if (_isActive)
            {
                _trayIcon.Icon   = CreateIcon(active: true);
                _trayIcon.Text   = "PeakMode — ACTIVE";
                _statusItem.Text = "Status: ACTIVE";
                _toggleItem.Text = "Restore";
            }
            else
            {
                _trayIcon.Icon   = CreateIcon(active: false);
                _trayIcon.Text   = "PeakMode — IDLE";
                _statusItem.Text = "Status: IDLE";
                _toggleItem.Text = "Activate";
            }
        }

        private static Icon CreateIcon(bool active)
        {
            using var bmp = new Bitmap(16, 16);
            using var g   = Graphics.FromImage(bmp);
            g.Clear(Color.Transparent);

            Color fill   = active ? Color.FromArgb(0, 200, 80)  : Color.FromArgb(120, 120, 120);
            Color border = active ? Color.FromArgb(0, 140, 50)  : Color.FromArgb(80, 80, 80);

            using var brush = new SolidBrush(fill);
            using var pen   = new Pen(border, 1.5f);

            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillEllipse(brush, 2, 2, 11, 11);
            g.DrawEllipse(pen,   2, 2, 11, 11);

            if (active)
            {
                using var boltBrush = new SolidBrush(Color.White);
                g.FillPolygon(boltBrush, new Point[]
                {
                    new(8, 3), new(5, 8), new(7, 8), new(6, 13), new(10, 7), new(8, 7)
                });
            }

            return Icon.FromHandle(bmp.GetHicon());
        }

        private void OnExit(object? sender, EventArgs e)
        {
            if (_isActive)
            {
                try { Restore(); } catch { }
            }
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            Application.Exit();
        }
    }
}