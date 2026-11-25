using System.IO;
using System.Xml.Linq;
using Xunit;

namespace PeakModeTray.Tests;

// DESIGN.md: every control is a real button or toggle with an automation
// name, and Tab only stops on things that show focus. Reads MainWindow.xaml
// as XML; nothing is rendered.
public sealed class AccessibilityTests
{
    private static readonly XNamespace P = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XDocument Window =
        XDocument.Load(Path.Combine(AppContext.BaseDirectory, "MainWindow.xaml"));

    private static string Where(XElement e) =>
        $"<{e.Name.LocalName}> {(string?)e.Attribute("Content") ?? (string?)e.Attribute(X + "Name") ?? (string?)e.Attribute("Command") ?? ""}";

    private static bool NotFocusable(XElement e) =>
        (string?)e.Attribute("Focusable") == "False" || (string?)e.Attribute("IsHitTestVisible") == "False";

    [Theory]
    [InlineData("Button")]
    [InlineData("ToggleButton")]
    [InlineData("RadioButton")]
    [InlineData("TextBox")]
    public void EveryControlHasAnAutomationName(string type)
    {
        var missing = Window.Descendants(P + type)
            .Where(e => e.Parent?.Name.LocalName != $"{type}.Template")
            .Where(e => e.Attribute("AutomationProperties.Name") == null)
            .Select(Where)
            .ToList();
        Assert.True(missing.Count == 0, "No automation name: " + string.Join(", ", missing));
    }

    [Fact]
    public void ScrollViewersAndListsAreNotTabStops()
    {
        // A focusable ScrollViewer or ItemsControl is a tab stop with no visible focus
        var stops = Window.Descendants()
            .Where(e => e.Name == P + "ScrollViewer" || e.Name == P + "ItemsControl")
            .Where(e => !NotFocusable(e))
            .Select(Where)
            .ToList();
        Assert.True(stops.Count == 0, "Focusable container: " + string.Join(", ", stops));
    }

    [Fact]
    public void KeyboardFocusStartsInTheSidebar()
    {
        Assert.Equal("{Binding ElementName=NavBoost}", (string?)Window.Root!.Attribute("FocusManager.FocusedElement"));
        Assert.NotNull(Window.Descendants(P + "RadioButton").SingleOrDefault(e => (string?)e.Attribute(X + "Name") == "NavBoost"));
    }

    [Fact]
    public void EverySidebarPageIsReachable()
    {
        // Four nav items, none disabled, each with a Checked handler
        var nav = Window.Descendants(P + "RadioButton").Where(e => (string?)e.Attribute("GroupName") == "nav").ToList();
        Assert.Equal(new[] { "Boost", "Tweaks", "History", "Settings" }, nav.Select(e => (string?)e.Attribute("Content")));
        Assert.All(nav, e => Assert.Null(e.Attribute("IsEnabled")));
        Assert.All(nav, e => Assert.NotNull(e.Attribute("Checked")));
    }

    [Fact]
    public void ModuleCardSwitchExposesItsStatus()
    {
        // The Boost card's read-only switch carries the status tag and detail for screen readers
        var sw = Window.Descendants(P + "ToggleButton")
            .Single(e => (string?)e.Attribute("IsEnabled") == "False");
        Assert.Equal("{Binding Status}", (string?)sw.Attribute("AutomationProperties.ItemStatus"));
        Assert.Equal("{Binding Detail}", (string?)sw.Attribute("AutomationProperties.HelpText"));
    }
}
