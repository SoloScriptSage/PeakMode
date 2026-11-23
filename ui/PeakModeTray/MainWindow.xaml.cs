using System;
using System.ComponentModel;
using System.Windows.Controls;
using Size = System.Windows.Size;
using HAlign = System.Windows.HorizontalAlignment;
using VAlign = System.Windows.VerticalAlignment;
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

    // Sidebar: show one page (Checked fires during InitializeComponent too)
    private void OnNavBoost(object sender, RoutedEventArgs e)  => ShowPage(boost: true);
    private void OnNavTweaks(object sender, RoutedEventArgs e) => ShowPage(boost: false);
    private void ShowPage(bool boost)
    {
        if (BoostPage == null || TweaksPage == null) return;
        BoostPage.Visibility  = boost ? Visibility.Visible : Visibility.Collapsed;
        TweaksPage.Visibility = boost ? Visibility.Collapsed : Visibility.Visible;
    }

    // Hero layout: counter and hint under the gauge, or beside it when that
    // gives a bigger gauge (wide, short card at small window heights).
    private const double SideTextWidth = 200, SideGap = 20, MaxGauge = 290;
    private bool? _heroSide;

    private void OnHeroSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double w = HeroGrid.ActualWidth, h = HeroGrid.ActualHeight;
        HeroText.Measure(new Size(SideTextWidth, double.PositiveInfinity));
        double textSide = HeroText.DesiredSize.Height;
        HeroText.Measure(new Size(w, double.PositiveInfinity));
        double textUnder = HeroText.DesiredSize.Height;

        double under = Math.Min(MaxGauge, Math.Min(w, h - textUnder));
        double side  = Math.Min(MaxGauge, Math.Min(w - SideTextWidth - SideGap, Math.Max(h, textSide)));
        bool useSide = side > under + 8;
        if (_heroSide == useSide) return;
        _heroSide = useSide;

        Grid.SetRowSpan(HeroGauge, useSide ? 2 : 1);
        Grid.SetColumnSpan(HeroGauge, 1);
        Grid.SetRow(HeroText, useSide ? 0 : 1);
        Grid.SetRowSpan(HeroText, useSide ? 2 : 1);
        Grid.SetColumn(HeroText, useSide ? 1 : 0);
        Grid.SetColumnSpan(HeroText, useSide ? 1 : 2);
        HeroText.Width             = useSide ? SideTextWidth : double.NaN;
        HeroText.Margin            = useSide ? new Thickness(SideGap, 0, 0, 0) : new Thickness(0);
        HeroText.VerticalAlignment = useSide ? VAlign.Center : VAlign.Stretch;
        CounterRow.HorizontalAlignment = useSide ? HAlign.Left : HAlign.Center;
        CounterRow.Margin          = useSide ? new Thickness(0) : new Thickness(0, 10, 0, 0);
        HintText.TextAlignment     = useSide ? TextAlignment.Left : TextAlignment.Center;
        HintText.HorizontalAlignment = useSide ? HAlign.Left : HAlign.Center;
    }

    protected override void OnSourceInitialized(System.EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this, captionRgb: 0x07080C, borderRgb: 0x1C1F2A);   // bg, border tokens
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }
}
