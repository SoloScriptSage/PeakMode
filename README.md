<p align="center">
  <img src="assets/peakmode-logo-1024.png" alt="PeakMode logo" width="160">
</p>

# PeakMode

A Windows tray app that applies a set of gaming performance tweaks with one click and undoes every one of them with the next click.

Every change is recorded on disk before it is made, so PeakMode can put your system back even after a crash, a killed process or a reboot.

---

## What it does

**Windows**
- Raises the priority of your game process to High (`windows.targetProcess`)
- Pins the game to one thread per physical core (P-cores only on hybrid Intel CPUs)
- Requests 0.5 ms timer resolution while PeakMode runs
- Switches to the High Performance or Ultimate Performance power plan
- Disables Nagle's algorithm (`TcpAckFrequency=1`, `TCPNoDelay=1`) on all network interfaces
- Stops SysMain (Superfetch) and Windows Search while active

**NVIDIA** (global driver profile, via NVAPI)
- Power management: prefer maximum performance
- Max pre-rendered frames: 1 (the driver's "Low Latency Mode: On")
- Vertical sync: force off
- Threaded optimization: on

**Network adapter interrupts** (off by default, see below)
- Routes the Ethernet adapter's interrupts away from CPU core 0
- Turns off interrupt moderation, Energy Efficient Ethernet, power saving and Wake-on-LAN on the adapter

### Why interrupt tweaks are off by default

Adapter settings only take effect after the adapter restarts, so with `interruptTweaks` enabled PeakMode **disables and re-enables your Ethernet adapter on activate and again on restore**. Your connection drops for a few seconds each time, and anything online (a game lobby, a call, a download) may disconnect. Turn it on in the config only if that is acceptable. Wi-Fi adapters are never touched.

---

## Install and use

1. Download `PeakMode-<version>-win-x64.zip` from the [releases](../../releases) and extract it anywhere.
2. Optionally edit `config\peakmode.json` (for example set `windows.targetProcess` to your game's exe name).
3. Run `PeakModeTray.exe` and accept the administrator prompt. The icon appears in the tray (grey = idle).
   The exe is not code-signed, so on first start Windows SmartScreen may show "Windows protected your PC". Click **More info**, then **Run anyway**.
4. Left-click the icon to activate (icon turns colored), left-click again to restore. Right-click for the menu: open config, open log, exit.

The release is self-contained: no .NET runtime needs to be installed. Exiting the tray while active restores everything first.

### Requirements

- Windows 10 or 11, 64-bit (tested on Windows 11 23H2 only)
- Administrator rights (services, power plan, registry and driver settings)
- NVIDIA GPU and driver for the NVIDIA tweaks (skipped automatically otherwise)

---

## Safety and recovery

- **Write-ahead snapshot.** Before each individual change, PeakMode writes the original value to `C:\ProgramData\PeakMode\snapshot.json`. If the write fails, the change is not made. Values that did not exist before are recorded as such and deleted again on restore.
- **Restore works from a fresh process.** Restore uses only the snapshot file, in reverse order. The file is deleted only when every item was restored; failed items stay in it so you can retry.
- **Crash or reboot while active.** On the next start the tray asks whether to restore the original settings. Until then it shows "restore pending".
- **Logoff and shutdown.** The tray restores on session end. If Windows ends it first, you get the restore prompt on the next start.
- **Locked-down state folder.** `C:\ProgramData\PeakMode` is accessible to Administrators and SYSTEM only. PeakMode repairs that ACL on startup, refuses a folder or snapshot file not owned by Administrators/SYSTEM, and only restores registry values under the three keys it changes.
- **Log.** `C:\ProgramData\PeakMode\peakmode.log` (also reachable from the tray menu).

If something is ever left tweaked, see [RECOVERY.md](scripts/RECOVERY.md) (also included in the release zip).

---

## Configuration

`config\peakmode.json` next to `PeakModeTray.exe`. Missing keys use the defaults below; unknown keys are logged and ignored; a malformed file stops activation with an error instead of falling back silently.

| Key | Default | Effect |
|---|---|---|
| `windowsTweaks` | `true` | Enable the Windows section |
| `nvidiaTweaks` | `true` | Enable the NVIDIA section |
| `interruptTweaks` | `false` | Enable the network adapter section (restarts the adapter) |
| `windows.targetProcess` | `""` | Game exe name, e.g. `"cs2.exe"`. Must be running when you activate. Empty skips priority and affinity |
| `windows.setProcPriorityHigh` | `true` | High priority for the target process |
| `windows.pinToPCores` | `true` | Target process affinity: one thread per physical core |
| `windows.setUltimatePerf` | `true` | High/Ultimate Performance power plan |
| `windows.disableNagle` | `true` | Nagle off on all interfaces (new connections) |
| `windows.disableSysMain` | `true` | Stop SysMain while active |
| `windows.disableSearchIndex` | `true` | Stop Windows Search while active |
| `windows.setTimerResolution` | `true` | 0.5 ms timer resolution while PeakMode runs |
| `nvidia.maxPerfMode` | `true` | Prefer maximum performance |
| `nvidia.disableVSync` | `true` | Force VSync off |
| `nvidia.disablePrerenderFrames` | `true` | Max pre-rendered frames = 1 |
| `nvidia.threadedOptimization` | `true` | Threaded optimization on |
| `interrupt.nicInstanceId` | `""` | Adapter PnP instance id. Empty = first connected physical Ethernet adapter |
| `interrupt.redirectNicIrq` | `true` | Interrupts on all physical cores except core 0 |
| `interrupt.disableNicInterruptMod` | `true` | Adapter properties listed above; only values the driver declares valid |

---

## Architecture

```
PeakModeTray.exe  (C# WPF/WinForms tray app, .NET 8, self-contained)
        |
        |  P/Invoke (C ABI, UTF-8 strings, int status codes)
        v
PeakModeCore.dll  (C++20)
  Orchestrator     activate / restore, status and error reporting
  ProfileManager   loads config\peakmode.json (nlohmann/json)
  Snapshot         write-ahead journal in C:\ProgramData\PeakMode
  Registry         journaled HKLM writes, restricted to known keys
  Modules
    WindowsTweaks    priority, affinity, timer, power plan, Nagle, services
    NvidiaTweaks     NVAPI driver settings (DRS)
    InterruptTweaks  adapter IRQ policy and advanced properties
```

`peakmode_test.exe` is a command line front end to the same DLL, used for testing and manual recovery: `activate`, `restore`, `status`, `dump`, `selftest`.

---

## Building

Requirements: Visual Studio 2022 with the C++ desktop workload and CMake, .NET 8 SDK.

```powershell
cmake -S core -B core/build -A x64
cmake --build core/build --config Release
core/build/Release/peakmode_test.exe selftest config/peakmode.json

# Release zip in dist\ (same script CI uses)
./scripts/package.ps1 -Version 0.1.0 -CoreDll core/build/Release/PeakModeCore.dll
```

CI (`.github/workflows/build.yml`) builds, runs the self-test and packages on every push and pull request; tags `v*` publish the zip as a GitHub release.

The NVAPI headers and import library in `core/third_party/nvapi` are a vendored subset of the NVIDIA NVAPI SDK (MIT).

---

## Test status

All on-machine tests ran on one PC: Windows 11 Pro 23H2 (22631), AMD Ryzen 7 4800H, NVIDIA RTX 3050 Ti Laptop GPU, Realtek RTL8168 Ethernet. Each cycle captures every setting PeakMode touches before activating, while active, and after restore, and passes only if the state after restore is identical to before (`scripts/run-cycle.ps1`, `scripts/tray-cycle.ps1`). In every cycle the restore ran in a separate, fresh process, the same path used after a crash.

| Area | Applied and verified | Restored to identical state | Notes |
|---|---|---|---|
| Target process priority | Yes | Yes | Tested with Notepad as the target |
| Target process affinity | Yes | Yes | `0xFFFF` to `0x5555` on 8 cores / 16 threads |
| Timer resolution | Yes (0.5 ms while active) | Yes (released) | Measured in the tray cycle |
| Power plan | Yes (High Performance) | Yes | Creating a temporary plan copy (fallback path): **untested** |
| Nagle | Yes | Yes | 6 interfaces |
| SysMain / Windows Search | Yes | Yes | Services already stopped before activation: **untested** |
| NVIDIA driver settings | Yes | Yes | Settings at driver default before activation: **untested** |
| Adapter IRQ policy | Yes (registry) | Yes | Actual interrupt routing not measured |
| Adapter properties | Yes | Yes | Buffer sizes skipped: this driver's maximum is already set; applying them: **untested** |
| Adapter restart | Yes | Yes | |
| Everything at once (default config with interrupt tweaks on) | Yes | Yes | 28 snapshot entries |
| Crash during activation | | Yes | Killed after entry 15, 21 and 28 |
| Crash while the adapter is disabled | | **Untested** | Covered by the same restore path, not reproduced |
| Tray: toggle, kill + restore prompt, exit while active | Yes | Yes | |
| Tray icon colors and notifications | **Untested** | | Not visually confirmed yet |
| Reboot with a pending snapshot, restore prompt after login | | Yes | |
| Restart with the tray active (restore on shutdown) | | **Untested** | |
| State folder ACL | Yes (self-test) | | Also repaired on the test machine |
| Intel hybrid CPUs (P-core detection) | **Untested** | | |
| PCs without an NVIDIA GPU | **Untested** | | Module is skipped when NVAPI is unavailable |
| Windows 10 | **Untested** | | |
| GitHub Actions build, self-test and packaging | Yes | | [Run 45060641184](https://github.com/SoloScriptSage/PeakMode/actions/runs/45060641184) on windows-latest, including the state folder ACL self-test |
| GitHub Actions release on a `v*` tag | Yes | | [Run 45062573365](https://github.com/SoloScriptSage/PeakMode/actions/runs/45062573365) published the v0.1.0 release |

---

## Roadmap

- [x] Windows, NVIDIA and network adapter tweaks
- [x] Write-ahead snapshot with crash and reboot recovery
- [x] Config file with per-tweak flags
- [x] C# tray app with P/Invoke to the C++ core
- [x] CI build and release packaging
- [ ] Apply priority/affinity when the game starts after activation (today it must already be running)
- [ ] Per-game profiles
- [ ] Optional auto-activate on game launch
- [ ] Start with Windows (optional)
- [ ] Processor groups (more than 64 logical CPUs)
- [ ] Choice of adapter when several Ethernet adapters are connected

---

## License

MIT, see [LICENSE](LICENSE). Third-party: NVIDIA NVAPI (MIT), nlohmann/json (MIT).
