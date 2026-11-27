using System.Runtime.InteropServices;
using System.Text;
using PeakModeTray.Checkup;
using Xunit;
using Xunit.Abstractions;

namespace PeakModeTray.Tests;

// PC Checkup readers: the SMBIOS parser on synthetic tables, P/Invoke struct
// sizes against the SDK headers, and the real readers. The real reads only
// query; they are also run without elevation (README test table).
public sealed class CheckupReaderTests
{
    private readonly ITestOutputHelper _out;
    public CheckupReaderTests(ITestOutputHelper output) { _out = output; }

    // ── SMBIOS parser ────────────────────────────────────────────────────────
    private static byte[] Structure(int type, byte[] formatted, params string[] strings)
    {
        var b = new List<byte> { (byte)type, (byte)(formatted.Length + 4), 0x34, 0x12 };
        b.AddRange(formatted);
        if (strings.Length == 0) b.AddRange(new byte[] { 0, 0 });
        else
        {
            foreach (var s in strings) { b.AddRange(Encoding.ASCII.GetBytes(s)); b.Add(0); }
            b.Add(0);
        }
        return b.ToArray();
    }

    // Type 17 formatted area after the 4-byte header, up to offset 0x5C
    private static byte[] MemoryDevice(int sizeMb, int rated, int configured, int type = 0x1A, int form = 0x09,
                                       long extRated = 0, long extConf = 0)
    {
        var f = new byte[0x5C - 4];
        void W(int off, int v) => BitConverter.GetBytes((ushort)v).CopyTo(f, off - 4);
        void D(int off, long v) => BitConverter.GetBytes((uint)v).CopyTo(f, off - 4);
        W(0x0C, sizeMb); f[0x0E - 4] = (byte)form; f[0x10 - 4] = 1; f[0x11 - 4] = 2; f[0x12 - 4] = (byte)type;
        W(0x15, rated); W(0x20, configured); D(0x54, extRated); D(0x58, extConf);
        return f;
    }

    private static byte[] Raw(params byte[][] structures)
    {
        var body = structures.SelectMany(s => s).Concat(Structure(127, Array.Empty<byte>())).ToArray();
        var raw = new byte[8 + body.Length];
        raw[1] = 3; raw[2] = 4;
        BitConverter.GetBytes((uint)body.Length).CopyTo(raw, 4);
        body.CopyTo(raw, 8);
        return raw;
    }

    [Fact]
    public void ParsesModulesAndChassis()
    {
        var enclosure = new byte[0x09 - 4];
        enclosure[0x05 - 4] = 0x80 | 10;   // notebook, with the lock bit set
        var info = Smbios.Parse(Raw(
            Structure(3, enclosure, "Maker"),
            Structure(17, MemoryDevice(8192, 3200, 2666, 0x1A, 0x0D), "DIMM 0", "P0 CHANNEL A"),
            Structure(17, MemoryDevice(0, 0, 0), "DIMM 1", "P0 CHANNEL B"),          // empty slot
            Structure(17, MemoryDevice(16384, 0xFFFF, 0xFFFF, 0x22, 0x09, 70000, 68000), "DIMM 2")));
        Assert.Equal(new[] { 10 }, info.ChassisTypes);
        Assert.Equal(2, info.Modules.Count);
        Assert.Equal(new MemoryModule("P0 CHANNEL A", 3200, 2666, 0x1A, 0x0D), info.Modules[0]);
        Assert.Equal(new MemoryModule("DIMM 2", 70000, 68000, 0x22, 0x09), info.Modules[1]);   // extended speeds, no bank
        Assert.True(Smbios.IsPortable(info.ChassisTypes[0]));
        Assert.Equal("DDR4", Smbios.MemoryTypeName(0x1A));
        Assert.Equal("SODIMM", Smbios.FormFactorName(0x0D));
    }

    [Fact]
    public void ShortOrBrokenTablesDoNotThrow()
    {
        Assert.Empty(Smbios.Parse(Array.Empty<byte>()).Modules);
        Assert.Empty(Smbios.Parse(new byte[8]).Modules);
        // An old type 17 without the configured-speed field (SMBIOS 2.3, 0x1B bytes)
        var old = MemoryDevice(4096, 1600, 0).Take(0x1B - 4).ToArray();
        var info = Smbios.Parse(Raw(Structure(17, old)));
        Assert.Equal(new MemoryModule("", 1600, 0, 0x1A, 0x09), Assert.Single(info.Modules));
        // Length field larger than the buffer, and a structure cut in half
        var raw = Raw(Structure(17, MemoryDevice(4096, 1600, 1600)));
        Smbios.Parse(raw.Take(raw.Length - 30).ToArray());
    }

    // ── struct layouts (x64, SDK 10.0.22621) ─────────────────────────────────
    [Fact]
    public void StructSizesMatchTheHeaders()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(72,  Marshal.SizeOf<Native.DISPLAYCONFIG_PATH_INFO>());
        Assert.Equal(64,  Marshal.SizeOf<Native.DISPLAYCONFIG_MODE_INFO>());
        Assert.Equal(84,  Marshal.SizeOf<Native.DISPLAYCONFIG_SOURCE_DEVICE_NAME>());
        Assert.Equal(420, Marshal.SizeOf<Native.DISPLAYCONFIG_TARGET_DEVICE_NAME>());
        Assert.Equal(20,  Marshal.SizeOf<Native.D3DKMT_ADAPTERINFO>());
        Assert.Equal(64,  Marshal.SizeOf<Native.D3DKMT_ADAPTER_PERFDATA>());
        Assert.Equal(24,  Marshal.SizeOf<Native.D3DKMT_QUERYADAPTERINFO>());
        Assert.Equal(32,  Marshal.SizeOf<Native.SP_DEVINFO_DATA>());
        Assert.Equal(20,  Marshal.SizeOf<Native.DEVPROPKEY>());
        Assert.Equal(24,  Marshal.SizeOf<Native.PDH_FMT_COUNTERVALUE_ITEM_W>());
        Assert.Equal(12,  Marshal.SizeOf<Native.SYSTEM_POWER_STATUS>());
    }

    // ── real readers: read-only, must not throw ──────────────────────────────
    private readonly SystemReader _reader = new();

    [Fact]
    public void RealDisplaysAreReadable()
    {
        var displays = _reader.Displays();
        foreach (var d in displays) _out.WriteLine($"display: {d}");
        Assert.All(displays, d => Assert.True(d.MaxHz >= d.CurrentHz && d.Width > 0 && d.Height > 0));
    }

    [Fact]
    public void RealSmbiosIsReadable()
    {
        var info = _reader.Smbios();
        foreach (var m in info.Modules) _out.WriteLine($"memory: {m}");
        _out.WriteLine("chassis: " + string.Join(",", info.ChassisTypes));
        Assert.All(info.Modules, m => Assert.True(m.RatedMts >= 0 && m.ConfiguredMts >= 0));
    }

    [Fact]
    public void RealPowerIsReadable()
    {
        var p = _reader.Power();
        _out.WriteLine($"power: {p}");
        Assert.False(string.IsNullOrEmpty(p.Plan));
        Assert.True(p.EffectiveMode is null or >= 0 and <= 6);
    }

    [Fact]
    public void RealGpusAreReadable()
    {
        var gpus = _reader.Gpus();
        foreach (var g in gpus) _out.WriteLine($"gpu: {g}");
        Assert.All(gpus, g => Assert.False(string.IsNullOrEmpty(g.Name)));
        Assert.All(gpus, g => Assert.True(g.TemperatureC is null or > 0 and < 130));
    }

    [Fact]
    public void RealHvciAndThermalsAreReadable()
    {
        var hvci = _reader.Hvci();
        _out.WriteLine($"hvci: {hvci}");
        Assert.NotEqual(HvciState.Unknown, hvci);
        foreach (var z in _reader.ThermalZones()) _out.WriteLine($"zone: {z}");
        Assert.All(_reader.ThermalZones(), z => Assert.InRange(z.Celsius, -40, 150));
    }
}
