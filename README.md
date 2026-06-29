# PeakMode

A Windows gaming performance optimizer that surgically applies system tweaks when you need them — and rolls everything back cleanly when you're done.

No permanent changes. No bloat. Just a tray icon toggle.

---

## What it does

By default, Windows shares CPU time fairly across all processes, runs background services constantly, keeps the GPU in adaptive power mode, and batches network packets to save energy. None of that is what you want mid-game.

PeakMode applies targeted tweaks across three areas:

**Windows**
- Raises game process priority to High
- Pins threads to P-cores on Intel hybrid CPUs
- Switches power plan to Ultimate Performance
- Sets timer resolution to 1ms (sub-millisecond via NtSetTimerResolution)
- Disables Nagle's algorithm (lower network latency)
- Stops SysMain (Superfetch) and Windows Search indexer

**NVIDIA** (via NVAPI)
- Forces Maximum Performance power state (P0)
- Disables adaptive VSync
- Sets pre-rendered frames to 1
- Enables Low Latency mode

**Interrupts**
- Moves NIC IRQ affinity off CPU core 0
- Disables NIC interrupt moderation (reduces ping variance)

Everything is snapshotted before activation and fully restored on toggle-off.

---

## Architecture

```
UI layer (C# WPF tray app)
        │  IPC / named pipe
        ▼
Core engine (C++ DLL)
  ├── ProfileManager   — loads peakmode.json
  ├── Snapshot         — captures system state before tweaks
  ├── Orchestrator     — coordinates apply / restore
  └── Modules
        ├── WindowsTweaks
        ├── NvidiaTweaks
        └── InterruptTweaks
```

---

## Requirements

- Windows 10/11 (64-bit)
- NVIDIA GPU (for NVAPI features)
- Administrator privileges (required for power plan, interrupt, and service tweaks)
- [NVAPI SDK](https://developer.nvidia.com/rtx/path-tracing/nvapi/get-started) — download and place in `core/third_party/nvapi/`
- CMake 3.20+
- Visual Studio 2022 (MSVC toolchain)
- .NET 8 SDK (for the tray UI)

---

## Building

### Core (C++ DLL)

```bash
cd core
cmake -B build -G "Visual Studio 17 2022" -A x64
cmake --build build --config Release
```

### UI (C# tray app)

```bash
cd ui/PeakModeTray
dotnet build -c Release
```

---

## Configuration

Edit `config/peakmode.json` to enable or disable individual tweaks.

```json
{
  "nvidia": {
    "disableHpet": false
  }
}
```

> `disableHpet` is off by default — it requires a reboot and is controversial on modern hardware.

---

## Roadmap

- [x] Project structure & architecture
- [ ] Windows tweaks (priority, affinity, power, timer, Nagle)
- [ ] NVAPI integration (power state, DRS settings)
- [ ] Interrupt affinity remapping
- [ ] Snapshot & restore system
- [ ] Named pipe IPC between C# UI and C++ core
- [ ] C# WPF tray app with toggle
- [ ] Auto-detect P-cores via `GetSystemCpuSetInformation()`
- [ ] Per-game profiles
- [ ] Auto-detect game launch (optional)

---

## License

MIT
