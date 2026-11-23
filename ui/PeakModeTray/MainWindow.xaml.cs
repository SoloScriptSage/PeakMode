using System.ComponentModel;
using System.Reflection;
using System.Windows;

namespace PeakModeTray;

// Main window (Boost, Tweaks, History, Settings), opened from the tray.
// Closing it only hides it; the tray keeps running.
public partial class MainWindow : Window
{
    internal MainWindow(object viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v == null ? "PeakMode" : $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    internal bool AllowClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }
}
