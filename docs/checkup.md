# PC Checkup: what it reads and how

The Checkup screen (0.3.0) is read-only. It reads system state with documented Windows APIs,
shows a status for each check and a one-line fix the user makes themselves. It changes nothing,
writes nothing to the snapshot, and sends nothing anywhere.

| Status | Meaning |
|---|---|
| OK | Nothing to do |
| WORTH CHECKING | Likely costs performance; the row says how to fix it |
| INFO | Shown for context; no recommendation |

## Spike: what works without admin

`scripts/checkup-probe.ps1` was run on the test PC (Windows 11 Pro 22631, Ryzen 7 4800H with
Radeon graphics, RTX 3050 Ti Laptop GPU, 2560 × 1440 240 Hz external display) twice on
2026-11-27: from an elevated prompt (High integrity) and started through `explorer.exe`
(Medium integrity, filtered token without the Administrators role, as a standard user gets).

| Read | API | Elevated | Not elevated |
|---|---|---|---|
| Display modes | `EnumDisplayDevices`, `EnumDisplaySettingsEx` | Yes | Yes |
| Memory speed | `Win32_PhysicalMemory` (probe); the app reads SMBIOS type 17 itself | Yes | Yes |
| AC / battery / energy saver | `GetSystemPowerStatus` | Yes | Yes |
| Effective power mode | `PowerRegisterForEffectivePowerModeNotifications` (V2) | Yes | Yes |
| Chassis type | `Win32_SystemEnclosure` (probe); the app reads SMBIOS type 3 | Yes | Yes |
| GPU driver version and date | `Win32_VideoController` (probe); the app uses SetupAPI device properties | Yes | Yes |
| GPU per process | "GPU Engine" performance counters | Yes | Yes |
| GPU name, hybrid role, temperature | `D3DKMTEnumAdapters2`, `D3DKMTQueryAdapterInfo` | Yes | Yes |
| Memory Integrity | `Win32_DeviceGuard` (probe); the app uses `NtQuerySystemInformation` | Yes | Yes |
| Thermal zone | "Thermal Zone Information" performance counter | Yes | Yes |
| Thermal zone | `MSAcpi_ThermalZoneTemperature` (WMI) | Yes | **Access denied** |

The app reads the same things with its own P/Invoke readers (SMBIOS, SetupAPI,
`NtQuerySystemInformation`, D3DKMT, PDH; `ui/PeakModeTray/Checkup/`), with no new packages. Their
read-only tests (`CheckupReaderTests`) pass both elevated and at Medium integrity on the test PC.
Not tried: a separate standard user account.

Findings that shaped the checks:

- `Win32_VideoController.MaxRefreshRate` is per adapter, not per monitor (240 on the NVIDIA
  adapter, 165 on the AMD one here), so the display check uses the mode list of each display.
- The HVCI registry key `…\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity` does not exist
  when Memory Integrity was never configured. A missing key is not an error.
- D3DKMT reports three adapters here: the NVIDIA GPU (`HybridDiscrete`), the AMD GPU
  (`HybridIntegrated`) and the software adapter, whose performance query fails (`0xC000000D`).
  Software adapters are skipped.
- `D3DKMT_ADAPTER_PERFDATA.PowerStateOverride` read 0 for the NVIDIA GPU while it drove a display,
  so it is not used.
- SMBIOS reports the SODIMMs as form factor 0x0D; `Win32_PhysicalMemory` reports 12, because WMI
  uses its own CIM values. The app uses the SMBIOS values.
- Both displays on the test PC are external monitors; their names come from `QueryDisplayConfig`.
- Enum values come from the SDK headers (10.0.22621), not from the documentation pages, which
  list no numbers: `KMTQAITYPE_ADAPTERPERFDATA` = 62, `KMTQAITYPE_ADAPTERTYPE` = 15,
  `KMTQAITYPE_ADAPTERREGISTRYINFO` = 8 (`shared/d3dkmthk.h`); `EFFECTIVE_POWER_MODE` 0 to 6
  (`um/powersetting.h`); `SystemCodeIntegrityInformation` = 103,
  `CODEINTEGRITY_OPTION_HVCI_KMCI_ENABLED` = 0x400 (`um/winternl.h`).

## The checks

### Display refresh rate
- **Reads:** for each active display, the current mode and every mode the driver lists at the
  current resolution (`EnumDisplaySettingsEx`); the monitor name and whether it is built in
  (`QueryDisplayConfig`, `DisplayConfigGetDeviceInfo`).
- **Worth checking:** a display runs more than 1 Hz below the highest rate at its resolution.
- **Fix:** Settings › System › Display › Advanced display › Choose a refresh rate.
- **Can't tell:** whether the rate is capped on purpose, whether the top mode is stable over the
  cable, or variable refresh rate.

### Memory speed
- **Reads:** SMBIOS type 17 (`GetSystemFirmwareTable('RSMB')`): speed and configured speed per
  module, memory type, form factor. SMBIOS type 3: chassis type (laptop or not).
- **Worth checking (desktops):** a module runs slower than its own rated speed.
- **Info (laptops):** the same case, because the CPU or laptop may limit the speed.
- **OK:** configured speed equals rated speed. Desktops get the line "if your memory was sold as
  faster, turn on XMP/EXPO in the BIOS". Laptops don't (SODIMM or soldered memory).
- **Can't tell:** the XMP/EXPO rating. SMBIOS has the module's standard speed only; the XMP profile
  is in the module's SPD chip, which needs a kernel driver to read.

### Power
- **Reads:** `GetSystemPowerStatus` (AC, battery present, energy saver), the effective power mode
  (`PowerRegisterForEffectivePowerModeNotifications`, unregistered after the first callback), the
  active power plan (`PowerGetActiveScheme`), chassis type.
- **Worth checking:** on battery, energy saver on, or "Best power efficiency" while plugged in.
- **Laptops:** a fixed note that the laptop maker's performance mode (for example in Armoury Crate
  or Lenovo Vantage) sets power limits and fans, and Windows can't see it.
- **During a boost:** the power plan is PeakMode's; the row says so.

### Graphics
- **Reads:** adapters with their hybrid role and LUID (D3DKMT), driver version and date
  (SetupAPI `DEVPKEY_Device_DriverVersion`, `DEVPKEY_Device_DriverDate`), and the newest `gpu`
  event in the session log.
- **Recording during a boost:** while a boost with a target process is active, the tray waits
  10 s, then samples the "GPU Engine" 3D utilization of that process for 1 s and sums it per
  adapter LUID. The adapter with the most use is recorded once per boost: the core appends a `gpu`
  event to `sessions.jsonl` (process, adapter name, hybrid role) through `PeakMode_RecordGpu`. If no
  3D use is seen yet, it tries again every 30 s, up to 10 times. Restore or exit stops it.
- **Worth checking:** the last boosted game ran on the integrated GPU while a discrete one exists,
  or a driver is older than 12 months.
- **Can't tell:** which GPU a game used outside a boost; whether a newer driver exists (the date is
  the driver's own date; laptop makers' drivers often lag).

### Memory Integrity
- **Reads:** `NtQuerySystemInformation(SystemCodeIntegrityInformation)`,
  `CODEINTEGRITY_OPTION_HVCI_KMCI_ENABLED`.
- **Always info.** Microsoft lists turning it off as a gaming-performance option with a security
  trade-off; PeakMode never changes it ([CONTRIBUTING](../CONTRIBUTING.md) rule 5,
  [tweak verdicts](tweak-verdicts.md)).

### Temperatures
- **Reads:** GPU temperature from `D3DKMT_ADAPTER_PERFDATA.Temperature` (tenths of a degree, the
  value Task Manager shows); the "Thermal Zone Information" counter (Kelvin).
- **Always info.** The thermal zone is shown only with the label "ACPI thermal zone, not the CPU
  core temperature". Windows has no documented way to read the CPU core temperature without a
  kernel driver, and PeakMode does not install one.
