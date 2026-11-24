using System.IO;
using System.Text.Json.Nodes;
using Xunit;

namespace PeakModeTray.Tests;

// ProfileStore edits a copy of a config in a temp folder, never the real one.
public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _dir  = Directory.CreateTempSubdirectory("peakmode-tests-").FullName;
    private string File(string name = "peakmode.json") => Path.Combine(_dir, name);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ReadsCommentsAndTrailingCommas()
    {
        System.IO.File.WriteAllText(File(), "{ // profile\n \"name\": \"gaming\", \"windows\": { \"disableNagle\": false, }, }");
        var store = new ProfileStore(File());
        Assert.True(store.CanEdit);
        Assert.Equal("gaming", store.GetString(null, "name"));
        Assert.False(store.GetBool("windows", "disableNagle", true));
        Assert.True(store.GetBool("windows", "missing", true));
    }

    [Fact]
    public void WriteKeepsOtherKeysAndOrder()
    {
        System.IO.File.WriteAllText(File(), "{\"name\":\"gaming\",\"windows\":{\"a\":1,\"disableNagle\":true,\"z\":\"keep\"},\"unknown\":[1]}");
        var store = new ProfileStore(File());
        int changed = 0;
        store.Changed += () => changed++;

        Assert.True(store.SetBool("windows", "disableNagle", false));
        Assert.True(store.SetMany(("nvidia", "disableVSync", false), (null, "interruptTweaks", true)));

        var root = JsonNode.Parse(System.IO.File.ReadAllText(File()))!.AsObject();
        Assert.Equal(new[] { "name", "windows", "unknown", "nvidia", "interruptTweaks" }, root.Select(p => p.Key));
        Assert.Equal(new[] { "a", "disableNagle", "z" }, root["windows"]!.AsObject().Select(p => p.Key));
        Assert.False((bool)root["windows"]!["disableNagle"]!);
        Assert.Equal("keep", (string)root["windows"]!["z"]!);
        Assert.False((bool)root["nvidia"]!["disableVSync"]!);
        Assert.Equal(2, changed);
        Assert.False(System.IO.File.Exists(File() + ".tmp"));
    }

    [Fact]
    public void MalformedConfigIsNotEditable()
    {
        System.IO.File.WriteAllText(File(), "{ \"name\": ");
        var store = new ProfileStore(File());
        Assert.False(store.CanEdit);
        Assert.NotEqual("", store.Error);
        Assert.False(store.SetBool(null, "windowsTweaks", false));
        Assert.Equal("{ \"name\": ", System.IO.File.ReadAllText(File()));
    }

    [Fact]
    public void MissingConfig()
    {
        var store = new ProfileStore(null);
        Assert.False(store.CanEdit);
        Assert.Contains("not found", store.Error);
    }
}
