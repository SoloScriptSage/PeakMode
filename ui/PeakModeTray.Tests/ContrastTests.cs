using System.Globalization;
using System.IO;
using System.Xml.Linq;
using Xunit;

namespace PeakModeTray.Tests;

// DESIGN.md: text contrast 4.5:1 or more. Reads the real token file, so a
// color change that breaks contrast fails the build.
public class ContrastTests
{
    private static readonly Dictionary<string, string> Colors = LoadColors();

    private static Dictionary<string, string> LoadColors()
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var doc = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Tokens.xaml"));
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "Color" && e.Attribute(x + "Key") != null)
            .ToDictionary(e => e.Attribute(x + "Key")!.Value, e => e.Value.Trim());
    }

    // Text tokens and the surfaces they are drawn on
    private static readonly string[] TextColors =
        { "TextColor", "TextSoftColor", "MutedColor", "FaintColor", "AccentColor", "AccentSoftColor", "AmberColor", "GreenColor" };
    private static readonly string[] Surfaces =
        { "BgColor", "SidebarColor", "SurfaceColor", "SurfaceDimColor", "SurfaceHotColor", "SelectedColor" };

    public static IEnumerable<object[]> Pairs()
    {
        foreach (var fg in TextColors)
            foreach (var bg in Surfaces)
                yield return new object[] { fg, bg };
        // Restore-pending banner and its button
        foreach (var fg in new[] { "TextColor", "AmberColor", "AmberTextColor" })
            yield return new object[] { fg, "AmberBgColor" };
        yield return new object[] { "OnAmberColor", "AmberColor" };
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void TextTokenMeetsAA(string fg, string bg)
    {
        double ratio = Ratio(Colors[fg], Colors[bg]);
        Assert.True(ratio >= 4.5, $"{fg} on {bg}: {ratio:0.00}:1, needs 4.5:1");
    }

    [Fact]
    public void RatioMatchesKnownValues()
    {
        Assert.Equal(21.0, Ratio("#000000", "#FFFFFF"), 2);
        Assert.Equal(1.0, Ratio("#777777", "#777777"), 2);
    }

    private static double Ratio(string a, string b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    // WCAG 2 relative luminance of #RRGGBB (or #AARRGGBB, alpha ignored)
    private static double Luminance(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length == 8) hex = hex[2..];
        double Channel(int i)
        {
            double v = int.Parse(hex.Substring(i, 2), NumberStyles.HexNumber) / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }
}
