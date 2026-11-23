#if DEBUG
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
// ImplicitUsings + UseWindowsForms imports System.Drawing / System.Windows.Forms
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace PeakModeTray.Controls
{
    // Debug-only test bench for BoostGauge: PeakModeTray.exe --gauge-lab
    // (Debug build). Touches nothing on the system: no core, no tray.
    internal sealed class GaugeLabWindow : Window
    {
        private const int Steps = 6;               // like the default profile
        private readonly BoostGauge      _gauge = new() { Width = 290, Height = 290 };
        private readonly TextBlock       _info  = new();
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(650) };
        private int  _done;
        private bool _simRestore;

        public GaugeLabWindow()
        {
            Title = "PeakMode gauge lab (debug)";
            Width = 760; Height = 520;
            Background = (Brush)FindResource("DotGridBrush");
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            _info.Style = (Style)FindResource("MonoText");
            _info.Foreground = (Brush)FindResource("TextSoftBrush");
            _info.Margin = new Thickness(0, 16, 0, 0);

            var buttons = new StackPanel { Margin = new Thickness(24, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            void Add(string text, Action a)
            {
                var b = new Button { Content = text, Style = (Style)FindResource("OutlineButton"), Margin = new Thickness(0, 0, 0, 8) };
                b.Click += (_, _) => { a(); Show(); };
                buttons.Children.Add(b);
            }
            Add("Idle", () => { Stop(); _done = 0; _gauge.Mode = GaugeMode.Idle; _gauge.Progress = 0; });
            Add("Step +1 (applying)", () => { Stop(); Step(+1, GaugeMode.Applying); });
            Add("Step -1 (restoring)", () => { Stop(); Step(-1, GaugeMode.Restoring); });
            Add("Active (rev loop)", () => { Stop(); _done = Steps; _gauge.Progress = 1; _gauge.Mode = GaugeMode.Active; });
            Add("Simulate boost, 650 ms/step", () => { _simRestore = false; _done = 0; _gauge.Progress = 0; _gauge.Mode = GaugeMode.Applying; _timer.Start(); });
            Add("Simulate restore, 455 ms/step", () => { _simRestore = true; _done = Steps; _gauge.Progress = 1; _gauge.Mode = GaugeMode.Restoring; _timer.Interval = TimeSpan.FromMilliseconds(455); _timer.Start(); });
            Add("Restore pending", () => { Stop(); _done = 0; _gauge.Progress = 0; _gauge.Mode = GaugeMode.RestorePending; });
            Add("Animations: system / on / off", () =>
                _gauge.AnimationsOverride = _gauge.AnimationsOverride switch { null => true, true => false, _ => null });

            _timer.Tick += (_, _) =>
            {
                if (!_simRestore)
                {
                    _done++;
                    _gauge.Progress = (double)_done / Steps;
                    if (_done >= Steps) { Stop(); _gauge.Mode = GaugeMode.Active; }
                }
                else
                {
                    _done--;
                    _gauge.Progress = (double)_done / Steps;
                    if (_done <= 0) { Stop(); _gauge.Mode = GaugeMode.Idle; }
                }
                Show();
            };

            var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(_gauge);
            left.Children.Add(_info);
            var root = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(32) };
            root.Children.Add(left);
            root.Children.Add(buttons);
            Content = root;
            Show();
        }

        private void Step(int delta, GaugeMode mode)
        {
            _done = Math.Clamp(_done + delta, 0, Steps);
            _gauge.Mode = mode;
            _gauge.Progress = (double)_done / Steps;
        }

        private void Stop()
        {
            _timer.Stop();
            _timer.Interval = TimeSpan.FromMilliseconds(650);
        }

        private new void Show()
        {
            string anim = _gauge.AnimationsOverride switch
            {
                null  => $"system ({(SystemParameters.ClientAreaAnimation ? "on" : "off")})",
                true  => "forced on",
                false => "forced off",
            };
            _info.Text = $"mode {_gauge.Mode}   step {_done} / {Steps}\n" +
                         $"needle target {_gauge.NeedleTarget:0.#} deg   animations {anim}";
            if (!IsVisible) base.Show();
        }
    }
}
#endif
