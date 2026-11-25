using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PeakModeTray.Controls;
using Xunit;

namespace PeakModeTray.Tests;

// Letter spacing via TextEffects. WPF elements need an STA thread; the text is
// rendered off-screen to a bitmap, nothing is shown.
public sealed class TrackingTests
{
    private static T OnSta<T>(Func<T> f)
    {
        T result = default!;
        Exception? error = null;
        var t = new Thread(() => { try { result = f(); } catch (Exception e) { error = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Xunit.Sdk.XunitException(error.ToString());
        return result;
    }

    private static TextBlock Label(string text, double spacing)
    {
        var tb = new TextBlock
        {
            Text = text, FontFamily = new FontFamily("Consolas"), FontSize = 11,
            Foreground = Brushes.Black, Background = Brushes.White,
        };
        Tracking.SetSpacing(tb, spacing);
        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        tb.Arrange(new Rect(tb.DesiredSize));
        return tb;
    }

    [Fact]
    public void WidthGrowsBySpacingPerGap()
    {
        var (plain, spaced) = OnSta(() => (Label("CHANGES APPLIED", 0).DesiredSize.Width,
                                           Label("CHANGES APPLIED", 1.5).DesiredSize.Width));
        Assert.Equal(14 * 1.5, spaced - plain, 3);
    }

    [Fact]
    public void TextIsUnchangedForScreenReaders()
    {
        var (text, effects) = OnSta(() => { var tb = Label("SNAPSHOT", 2); return (tb.Text, tb.TextEffects.Count); });
        Assert.Equal("SNAPSHOT", text);
        Assert.Equal(7, effects);
    }

    [Fact]
    public void ChangingTextOrSpacingDoesNotAccumulatePadding()
    {
        var pad = OnSta(() =>
        {
            var tb = Label("AB", 2);
            Tracking.SetSpacing(tb, 3);
            Tracking.SetSpacing(tb, 2);
            Tracking.Apply(tb);
            return tb.Padding.Right;
        });
        Assert.Equal(2, pad, 3);   // one gap x 2 px, not stacked
    }

    [Fact]
    public void RenderedInkIsWider()
    {
        // The TextEffects must reach the renderer, not only the layout width
        var (plainInk, spacedInk) = OnSta(() => (InkWidth(Label("RESTARTED", 0)), InkWidth(Label("RESTARTED", 2))));
        Assert.InRange(spacedInk - plainInk, 8 * 2 - 2, 8 * 2 + 2);
    }

    private static int InkWidth(TextBlock tb)
    {
        int w = (int)Math.Ceiling(tb.ActualWidth), h = (int)Math.Ceiling(tb.ActualHeight);
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(tb);
        var px = new byte[w * h * 4];
        bmp.CopyPixels(px, w * 4, 0);
        int min = int.MaxValue, max = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (px[(y * w + x) * 4] < 128) { min = Math.Min(min, x); max = Math.Max(max, x); }

        string? dir = Environment.GetEnvironmentVariable("PEAKMODE_TEST_PNG_DIR");
        if (dir != null)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var f = File.Create(Path.Combine(dir, $"tracking-{tb.Text}-{Tracking.GetSpacing(tb)}.png"));
            enc.Save(f);
        }
        return max < 0 ? 0 : max - min + 1;
    }
}
