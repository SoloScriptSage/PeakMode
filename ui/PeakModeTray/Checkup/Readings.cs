using System;
using System.Collections.Generic;

namespace PeakModeTray.Checkup
{
    // What the PC Checkup reads. Every reader is read-only; SystemReader is
    // the real one, tests pass fakes. See docs/checkup.md for the APIs.
    internal interface ISystemReader
    {
        IReadOnlyList<DisplayReading> Displays();
        SmbiosInfo Smbios();
        PowerReading Power();
        IReadOnlyList<GpuReading> Gpus();
        HvciState Hvci();
        IReadOnlyList<ThermalZone> ThermalZones();
    }

    // One active display: current mode and the highest refresh rate the
    // driver lists at the same resolution
    internal sealed record DisplayReading(string Name, bool BuiltIn, string Adapter,
                                          int Width, int Height, int CurrentHz, int MaxHz);

    // SMBIOS type 17 (one per installed module) and type 3 (chassis)
    internal sealed record MemoryModule(string Slot, int RatedMts, int ConfiguredMts, int MemoryType, int FormFactor);
    internal sealed record SmbiosInfo(IReadOnlyList<MemoryModule> Modules, IReadOnlyList<int> ChassisTypes)
    {
        public static readonly SmbiosInfo Empty = new(Array.Empty<MemoryModule>(), Array.Empty<int>());
    }

    // EffectiveMode: EFFECTIVE_POWER_MODE (powersetting.h), null if unknown
    internal sealed record PowerReading(bool? OnAc, bool HasBattery, bool EnergySaver, int? EffectiveMode, string Plan);

    internal enum GpuRole { Single, Discrete, Integrated }

    // One hardware adapter (software adapters are left out)
    internal sealed record GpuReading(string Name, GpuRole Role, string DriverVersion, DateTime? DriverDate,
                                      double? TemperatureC, long Luid);

    internal enum HvciState { Unknown, Off, On }

    internal sealed record ThermalZone(string Name, double Celsius);
}
