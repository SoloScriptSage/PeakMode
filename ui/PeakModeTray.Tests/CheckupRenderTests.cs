using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PeakModeTray.Checkup;
using PeakModeTray.ViewModels;
using Xunit;

namespace PeakModeTray.Tests;

// Builds the real MainWindow XAML with the Checkup page on fake readings and
// renders it off-screen. The window is never shown. Catches XAML, resource
// and binding-path mistakes; set PEAKMODE_TEST_PNG_DIR to save the image.
public sealed class CheckupRenderTests
{
    private sealed class FakeCore : ICoreService
    {
        public int  State  => PeakModeCore.StateIdle;
        public bool IsBusy => false;
        public event Action<CoreProgress>? Progress { add { } remove { } }
        public event Action? StateChanged { add { } remove { } }
        public event Action<CoreResult>? Completed { add { } remove { } }
        public Task<CoreResult> ActivateAsync(string configPath) => throw new NotSupportedException();
        public Task<CoreResult> RestoreAsync(int reason) => throw new NotSupportedException();
        public string ReadSessions() => """
            [ { "type": "gpu", "session": "s1", "process": "dota2.exe", "adapter": "NVIDIA GeForce RTX 3050 Ti Laptop GPU", "role": "discrete" } ]
            """;
    }

    // The values the test PC reported, with an old NVIDIA driver
    private sealed class FakeReader : ISystemReader
    {
        public IReadOnlyList<DisplayReading> Displays() => new[]
        {
            new DisplayReading("Q27G4ZR", false, "NVIDIA GeForce RTX 3050 Ti Laptop GPU", 2560, 1440, 165, 240),
            new DisplayReading("MSI MAG273R2", false, "AMD Radeon(TM) Graphics", 1920, 1080, 165, 165),
        };
        public SmbiosInfo Smbios() => new(new[]
        {
            new MemoryModule("P0 CHANNEL A", 3200, 3200, 0x1A, 0x0D), new MemoryModule("P0 CHANNEL B", 3200, 3200, 0x1A, 0x0D),
        }, new[] { 10 });
        public PowerReading Power() => new(true, true, false, 4, "Balanced");
        public IReadOnlyList<GpuReading> Gpus() => new[]
        {
            new GpuReading("NVIDIA GeForce RTX 3050 Ti Laptop GPU", GpuRole.Discrete, "32.0.15.6607", new DateTime(2024, 10, 20), 52, 1),
            new GpuReading("AMD Radeon(TM) Graphics", GpuRole.Integrated, "31.0.21916.2", new DateTime(2026, 6, 18), 50, 2),
        };
        public HvciState Hvci() => HvciState.Off;
        public IReadOnlyList<ThermalZone> ThermalZones() => new[] { new ThermalZone(@"\_TZ.TZ01", 72.9) };
    }

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

    // MainWindow resolves its StaticResources from the application resources
    private static void LoadAppResources()
    {
        var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        if (app.Resources.MergedDictionaries.Count > 0) return;
        foreach (var d in new[] { "Tokens", "Type", "Controls", "Motion" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/PeakModeTray;component/Themes/{d}.xaml") });
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var d in Descendants<T>(c)) yield return d;
        }
    }

    [Fact]
    public void CheckupPageBuildsAndRenders()
    {
        var (texts, inked) = OnSta(() =>
        {
            LoadAppResources();
            var core = new FakeCore();
            var checkup = new CheckupViewModel(core, _ => { }, new FakeReader(), () => new DateTime(2026, 11, 27, 14, 32, 0));
            checkup.RefreshAsync().GetAwaiter().GetResult();
            var main = new MainViewModel(
                new BoostViewModel(core, null, () => "Balanced", () => 1.0, _ => false),
                new TweaksViewModel(core, new ProfileStore(null)),
                checkup, new HistoryViewModel(core), new SettingsViewModel("v0.3.0", null, "peakmode.log", ".", _ => { }));

            var w = new MainWindow(main);
            w.BoostPage.Visibility = Visibility.Collapsed;
            w.CheckupPage.Visibility = Visibility.Visible;
            var root = (FrameworkElement)w.Content;
            w.Content = null;                       // render the content without showing the window
            root.DataContext = main;
            var size = new Size(1280, 1000);
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
                System.Windows.Threading.DispatcherPriority.ContextIdle);
            root.UpdateLayout();

            var bmp = new RenderTargetBitmap(1280, 1000, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(root);
            var px = new byte[1280 * 1000 * 4];
            bmp.CopyPixels(px, 1280 * 4, 0);
            int lit = 0;
            for (int i = 0; i < px.Length; i += 4) if (px[i] + px[i + 1] + px[i + 2] > 300) lit++;

            string? dir = Environment.GetEnvironmentVariable("PEAKMODE_TEST_PNG_DIR");
            if (dir != null)
            {
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using var f = File.Create(Path.Combine(dir, "checkup-page.png"));
                enc.Save(f);
            }
            // IsVisible needs a shown window: check Visibility up the tree instead
            static bool Shown(DependencyObject? e)
            {
                for (; e != null; e = VisualTreeHelper.GetParent(e))
                    if (e is UIElement u && u.Visibility != Visibility.Visible) return false;
                return true;
            }
            var visible = Descendants<TextBlock>(w.CheckupPage).Where(Shown).Select(t => t.Text).ToList();
            return (visible, lit);
        });

        Assert.True(inked > 5000, $"page looks empty ({inked} light pixels)");
        foreach (var name in new[] { "PC CHECKUP", "Display refresh rate", "Memory speed", "Power", "Graphics",
                                     "Memory integrity", "Temperatures", "165 Hz of 240 Hz", "WORTH CHECKING", "HOW TO FIX" })
            Assert.Contains(name, texts);
    }
}
