# Tweak verdicts

Tweaks that popular "Windows tweaks for gaming" videos recommend, each checked against Microsoft, NVIDIA or AMD documentation and given a verdict for PeakMode. BIOS, peripheral and cosmetic tweaks are out of scope. Nothing here was applied to a machine. Checked 2026-11-24.

The videos (referred to as V1–V6 below):

| Key | Video |
|---|---|
| V1 | https://www.youtube.com/watch?v=Ntkc6PeImhU |
| V2 | https://www.youtube.com/watch?v=e6meKnEkzuU |
| V3 | https://www.youtube.com/watch?v=eSebSEeejOM |
| V4 | https://www.youtube.com/watch?v=3O5j2xyeddQ |
| V5 | https://www.youtube.com/watch?v=qRYr9VNQGhg |
| V6 | https://www.youtube.com/watch?v=z_quZzC9IYA |

The tweaks were collected from the videos' auto-generated captions. Registry paths and commands that the videos only showed on screen or in their descriptions were filled in from common usage and then checked here: see [Commonly cited locations](#commonly-cited-locations-confirmed-or-not).

## Rules used

- **ADD**: reversible, can be journaled in the snapshot, fits a boost session (no reboot, can be undone at restore), effect documented by the vendor, no security impact. The mechanism that changes it must also be documented, so the journal has a reliable path or API.
- **OPT-IN**: reversible, but the effect is disputed or depends on the hardware or game, *or* the documentation only covers part of it (for example a documented effect behind an undocumented registry location). Needs a before/after measurement before it becomes a default.
- **INFO ONLY**: PeakMode can show its state but should not change it: security features, reboot-only settings, things that are already the Windows default, and persistent user preferences.
- **REJECT**: irreversible, reduces security, maintenance rather than a tweak, the documentation contradicts the claim, or there is no evidence.

How much to trust each source:
- Pages marked *(snippet)* could not be fetched in full. The page fetch timed out or returned no article body, so the verdict relies on search-result excerpts of that page.
- Pages marked *(secondary)* are not vendor documentation.
- "No doc found" means a search and fetch found no vendor page. Those rows are judged as "no evidence".

---

## Summary

### ADD (5)

| Tweak | Mechanism (documented) | Why | Status in PeakMode |
|---|---|---|---|
| Processor performance boost mode = Aggressive (2) | Power setting `be337238-0d82-4146-a960-4f3749d470c7` (alias `PERFBOOSTMODE`, hidden) on the active scheme, AC value; journal the old value, restore it on exit [BOOST] | Documented: "selects the maximum boost performance when Aggressive". No reboot. No need to unhide it with `Attributes`, because hidden settings can be written by GUID | New |
| Energy performance preference = 0 | Power setting alias `PERFEPP` (0 = favor performance, 100 = favor energy) [EPP] | Documented register effect. Applies only to CPPC v2 with autonomous mode, so skip it and log when unsupported | New |
| NVIDIA Power management mode = Prefer maximum performance | NVAPI DRS `PREFERRED_PSTATE_ID` [NVCP] | "Use the GPU only at maximum performance when running most 3D applications" | Already implemented (`maxPerfMode`) |
| NVIDIA Low Latency Mode = On | NVAPI DRS `PRERENDERLIMIT_ID` = 1 [NVCP] | "limiting queued frames to one". Reflex overrides it in games that support Reflex [NVLAT] | Already implemented (`disablePrerenderFrames`) |
| NVIDIA Vertical sync = Off | NVAPI DRS `VSYNCMODE_ID` [NVCP] | NVIDIA: V-Sync "causes back pressure from the display"; "we highly recommend turning VSYNC OFF" [NVLAT] | Already implemented (`disableVSync`) |

### OPT-IN (17)

| Tweak | Mechanism | Why it isn't ADD | Status in PeakMode |
|---|---|---|---|
| Ultimate / High Performance power plan | `powercfg /duplicatescheme` (documented) [POWERCFG]. Ultimate GUID `e9a42962-…` is not on Microsoft Learn; only secondary sources describe the plan [ULT] | Microsoft pitched it for workstations ("micro-latencies") at higher power. V2 disputes using it. Gains depend on the hardware | **Already default-on** (`setUltimatePerf`). Should be measured |
| SysMain stopped for the session | Service `SysMain` [SVC] | Microsoft: "Maintains and improves system performance over time", "No guidance". The perf effect is disputed | **Already default-on** (`disableSysMain`). Should be measured |
| MMCSS Games task: `Scheduling Category`=High | `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games` [MMCSS] | Documented: High → priority 23–26, "designed for Pro Audio", and `Priority` is then "always treated as 2". It only affects threads that a game registers with MMCSS under "Games", so the effect depends on the game | New |
| `SystemResponsiveness` = 10 | `…\Multimedia\SystemProfile` [MMCSS] | Documented: the share of CPU reserved for low-priority tasks; "Values below 10 and above 100 are clamped to 20". Only matters while MMCSS-registered threads run. Default and whether a reboot is needed are not documented | New |
| Optimizations for windowed games | Settings → Display → Graphics [WINDOWED]. **Registry location undocumented** | Documented: DX10/11 windowed games move from blt to flip model, "reducing frame latency". But the storage location isn't documented (so it can't be journaled safely yet), the game must be restarted, and Auto HDR forces it on | New |
| Per-game GPU preference = High performance | Settings → Graphics → app options [WINDOWED]. Registry undocumented | Only matters on hybrid-GPU systems. Storage location undocumented | New |
| Captures / background recording off | Policy `HKLM\Software\Policies\Microsoft\Windows\GameDVR` `AllowGameDVR`=0 [GAMEDVR] | The policy is documented. Microsoft documents no perf effect, and the policy text says "only enforced in Windows 10 for desktop" | New |
| Visual effects: animations off | `SystemParametersInfo` `SPI_SETCLIENTAREAANIMATION` / `SPI_SETUIEFFECTS` [SPI] | The API is documented and reversible at runtime. No documented in-game effect | New |
| Transparency effects off | Undocumented (`EnableTransparency` unconfirmed) | Plausibly reduces compositor work; no doc | New |
| NIC checksum / LSO offloads off | `*TCPChecksumOffloadIPv4`, `*LsoV2IPv4`, … via `Set-NetAdapterAdvancedProperty` [NDIS-OFF] | Keywords documented; the latency effect isn't. Changing an advanced property may reset the link (unverified) | New (PeakMode already does NIC interrupt moderation) |
| NIC flow control off | `*FlowControl` = 0 (client default 3) [NDIS-ENUM] | Keyword documented; effect not documented | New |
| NVIDIA Low Latency Mode = Ultra | NVAPI DRS [NVCP] | "fully minimising queued frames". V1 says it can cost FPS in CPU-bound games | Variant of an existing feature |
| NVIDIA Texture filtering quality = High performance | NVAPI DRS [NVCP] | Trades image quality. "Quality" is the documented GeForce default | New |
| NVIDIA Anisotropic sample optimization on | NVAPI DRS [NVCP] | "only affects DirectX programs". Image quality trade; V1 says it is worse in some games | New |
| NVIDIA OpenGL rendering GPU = dGPU | NVAPI DRS [NVCP] | Multi-GPU only; the default "Auto select" lets the driver decide | New |
| AMD Texture filtering quality = Performance | Adrenalin; ADLX API not checked [AMD] *(snippet)* | AMD: "Standard option offers the best mix". Performance trades quality | New, needs an ADLX investigation |
| AMD Tessellation override, low max level | Adrenalin; ADLX not checked [AMD] *(snippet)* | AMD: lower tessellation "can reduce overhead allowing higher FPS". Visual cost | New, needs an ADLX investigation |

Ultimate Performance and SysMain are counted even though they already ship, because they need measuring.

---

## Findings that affect PeakMode 0.2

1. **Timer resolution (`setTimerResolution`, default on).** Microsoft: "Starting with Windows 10, version 2004, this function no longer affects global timer resolution." Since Windows 11, Windows also stops guaranteeing it for processes that are occluded or minimized [TIMER]. The page documents `timeBeginPeriod`. PeakMode calls the undocumented `NtSetTimerResolution`, which very likely has the same per-process scope. If so, PeakMode's 0.5 ms request only affects PeakMode's own process and not the game. This is unverified for `NtSetTimerResolution` and needs a test: query the resolution from inside the game's process while boosted.
2. **Ultimate Performance and SysMain** are default-on but only reach OPT-IN on the evidence. Both are candidates for the planned before/after measurement.
3. **Win32PrioritySeparation:** if it is ever added, note that 0x26 is exactly what Windows writes for "Adjust for best performance of: Programs" [W32PS][QUANTUM]. So it's a no-op on a normally configured client.

---

## Full verdict table

### 1. Security / virtualization

| Tweak | Verdict | Reason | Documented path / mechanism | Source |
|---|---|---|---|---|
| Memory Integrity off | **INFO ONLY** | Security feature; needs a reboot, so it can't be done in a session. Microsoft does list it as a gaming-performance option to turn off and back on | `HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity` `Enabled` (1/0). **Confirmed.** A UEFI lock (`Locked`=1) blocks turning it off from the registry | [HVCI], [GAMEPERF] *(snippet)* |
| LSA protection off | **REJECT** | Reduces security; no documented perf effect; can be UEFI-locked; needs a reboot | `HKLM\SYSTEM\CurrentControlSet\Control\Lsa` `RunAsPPL` (0/1/2). **Confirmed** | [LSA] |
| Defender: all off except real-time | **REJECT** | Reduces security. Microsoft: "We recommend keeping cloud protection turned on" | — | [CLOUD] |
| Defender periodic scanning off | **REJECT** | Misidentified feature: limited periodic scanning only runs "when another antivirus product is installed", so turning it off does nothing for a Defender-only PC | — | [LPS] |

### 2. Game Mode / graphics scheduling

| Tweak | Verdict | Reason | Documented path / mechanism | Source |
|---|---|---|---|---|
| Game Mode on | **INFO ONLY** | On by default; no Microsoft page describing its effect was found. Show it if the user has turned it off | `HKCU\Software\Microsoft\GameBar` `AutoGameModeEnabled`: **unconfirmed** | No doc found |
| Optimizations for windowed games on | **OPT-IN** | See the summary | Settings UI documented; `UserGpuPreferences`/`SwapEffectUpgradeEnable` **unconfirmed** | [WINDOWED], [DXBLOG22] |
| HAGS on | **INFO ONLY** | Needs a reboot. Microsoft: "users should not notice any significant changes" | `HwSchMode`: **unconfirmed** (not on any Microsoft page fetched) | [HAGS] |
| Per-game GPU preference | **OPT-IN** | See the summary | `GpuPreference=2;`: **unconfirmed** | [WINDOWED] |
| Game Bar off | **REJECT** | No documented perf effect for the overlay itself | — | No doc found |
| Game Bar removed (AppX) | **REJECT** | Hard to reverse; outside a session | `Remove-AppxPackage Microsoft.XboxGamingOverlay`: **unconfirmed** | No doc found |
| Captures off | **OPT-IN** | See the summary | `HKLM\Software\Policies\Microsoft\Windows\GameDVR` `AllowGameDVR`=0: **confirmed** (replaces the guessed `GameConfigStore\GameDVR_Enabled`, which is **unconfirmed**) | [GAMEDVR] |
| Timer resolution (third-party app) | **REJECT** | Since Windows 10 2004 another process can't raise the global timer resolution | `timeBeginPeriod` | [TIMER] |

### 3. Power

| Tweak | Verdict | Reason | Documented path / mechanism | Source |
|---|---|---|---|---|
| Ultimate Performance plan | **OPT-IN** | See the summary | `powercfg /duplicatescheme` **confirmed**; GUID `e9a42962-a8a2-4b4f-a10f-6cc5f9bbc6ab` **unconfirmed** on Microsoft Learn | [POWERCFG], [ULT] *(secondary)* |
| High Performance instead of Ultimate | **OPT-IN** | Same row as above; the plan choice is what gets measured | — | [POWERCFG] |
| Processor performance time check interval 5000 | **REJECT** | Not on Microsoft Learn's processor power management pages; no evidence | GUID `4d2b0152-…`: **unconfirmed** | [PPM] (absent) |
| Processor idle time check 100000 | **REJECT** | Same as above | GUID `c4581c31-…`: **unconfirmed** | [PPM] (absent) |
| NVMe idle timeout | **REJECT** | The video itself doesn't apply it | — | — |
| Boost mode Aggressive | **ADD** | See the summary. The video's registry `Attributes`=2 unhide isn't needed and isn't documented on the pages fetched | GUID `be337238-0d82-4146-a960-4f3749d470c7`: **confirmed** | [BOOST] |
| EPP = max performance | **ADD** | See the summary | Alias `PERFEPP`: **confirmed** | [EPP] |
| Fast Startup off | **REJECT** | Only affects shutdown and boot; not a session tweak | What fast startup does is documented; `HiberbootEnabled`: **unconfirmed** | [FASTSTART] |

### 4. Registry: MMCSS / scheduler

| Tweak | Verdict | Reason | Documented path / mechanism | Source |
|---|---|---|---|---|
| Games `Scheduling Category`=High | **OPT-IN** | See the summary | `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games`: **confirmed** | [MMCSS] |
| Games `GPU Priority`=8 | **REJECT** | Microsoft: "This priority is not yet used" | same key | [MMCSS] |
| Games `Priority`=6 (with High) | **REJECT** | With Scheduling Category High, Priority "is always treated as 2" | same key | [MMCSS] |
| Games `SFIO Priority`=High | **REJECT** | Microsoft: "This value is not used" | same key | [MMCSS] |
| Win32PrioritySeparation = 26 (0x1A) | **REJECT** | The documentation contradicts the claim. 0x1A = long, *fixed* quanta, and "foreground boost is ignored when fixed", which gives the same scheduling as "Background services" (0x18). Also, no restart is needed, contrary to V1 | `HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl`: **confirmed** | [W32PS], [QUANTUM] |
| Win32PrioritySeparation = 38 (0x26) | **INFO ONLY** | 0x26 is exactly what Windows writes for "Programs". Show the state; only point it out if it's set to "Background services" | same | [W32PS], [QUANTUM] |
| SystemResponsiveness = 10 | **OPT-IN** | See the summary | `…\Multimedia\SystemProfile`: **confirmed** | [MMCSS] |
| SystemResponsiveness = 0 | **REJECT** | Documented: "Values below 10 … are clamped to 20", so 0 changes nothing. V1 was right on this point and V5 was wrong | same | [MMCSS] |

### 5. Network

| Tweak | Verdict | Reason | Documented path / mechanism | Source |
|---|---|---|---|---|
| NetworkThrottlingIndex = 0xFFFFFFFF | **REJECT** | The documented mechanism caps NDIS at "10 packets per millisecond (10,000 packets per second)" while MMCSS throttles. Game traffic is orders of magnitude below that, so there's no evidence it helps ping. The value isn't on the current MMCSS page, and secondary sources say a reboot is needed | `…\Multimedia\SystemProfile` `NetworkThrottlingIndex`: **unconfirmed** on Microsoft Learn (cited to KB948066, not retrieved) | [RUSS], [MMCSS] (absent) |
| DNS → 8.8.8.8 / Cloudflare | **REJECT** | Only affects name lookups, not game latency; changes the resolver (a privacy/policy choice) | — | No doc found |
| `ipconfig /flushdns` | **REJECT** | Maintenance: "Flushes and resets the contents of the DNS client resolver cache" | **confirmed** | [IPCONFIG] |
| NIC power management off | **REJECT** | No Microsoft doc found for the "Allow the computer to turn off this device" effect; revisit if one turns up | `PnPCapabilities`: **unconfirmed** | No doc found |
| NIC ARP / NS offload off | **REJECT** | Documented as "when the system enters a sleep state", so no effect while gaming | `*PMARPOffload`, `*PMNSOffload`: **confirmed** | [NDIS-PM] |
| NIC checksum / LSO offloads off | **OPT-IN** | See the summary | `*IPChecksumOffloadIPv4`, `*TCPChecksumOffloadIPv4/6`, `*UDPChecksumOffloadIPv4/6`, `*LsoV2IPv4/6`: **confirmed** | [NDIS-OFF] |
| NIC flow control off | **OPT-IN** | See the summary | `*FlowControl`: **confirmed** | [NDIS-ENUM] |
| NIC jumbo packet off | **REJECT** | Already the default per V3; no change | `*JumboPacket`: **unconfirmed** (not on the page fetched) | [NDIS-ENUM] (absent) |
| NIC wake-on off | **REJECT** | Wake features have no effect while awake | `*WakeOnMagicPacket`, `*WakeOnPattern`: **confirmed** | [NDIS-PM] |
| Realtek driver + tweaks | **REJECT** | Not specified | — | — |
| Delivery Optimization P2P off | **REJECT** | Persistent preference; no documented gaming effect | `DODownloadMode` (0 = HTTP only) via Group Policy/MDM: **confirmed**; registry path not documented | [DO] |

### 6. Services

| Tweak | Verdict | Reason | Documented path / mechanism | Source |
|---|---|---|---|---|
| DiagTrack disabled | **REJECT** | No documented perf effect; Microsoft: "No guidance" | `DiagTrack`: **confirmed** | [SVC] |
| SysMain disabled | **OPT-IN** | See the summary | `SysMain`: **confirmed** *(non-verbatim part of the page)* | [SVC] |
| TrkWks disabled | **REJECT** | No perf evidence; "No guidance" | `TrkWks`: **confirmed** | [SVC] |
| Print Spooler manual | **REJECT** | No perf evidence (Microsoft's advice is about attack surface, not speed) | `Spooler`: **confirmed** *(non-verbatim)* | [SVC] |
| Geolocation disabled | **REJECT** | No perf evidence; "Disabling breaks apps that rely on the service" | `lfsvc`: **confirmed** | [SVC] |
| PcaSvc disabled | **REJECT** | No perf evidence | `PcaSvc`: **confirmed** *(non-verbatim)* | [SVC] |
| WpnService disabled | **REJECT** | No perf evidence | `WpnService`: **confirmed** *(non-verbatim)* | [SVC] |
| WalletService disabled | **REJECT** | No perf evidence | `WalletService`: **confirmed** *(non-verbatim)* | [SVC] |
| Time sync off | **REJECT** | "essential to the successful operation of Kerberos V5 authentication" | `W32Time`: **confirmed** | [W32TIME] |
| Scheduled defrag off | **REJECT** | Maintenance; persistent | — | No doc found |
| System Protection off | **REJECT** | Removes restore points; persistent | — | No doc found |
| Remote Assistance off | **REJECT** | No perf effect; persistent | `fAllowToGetHelp`: **unconfirmed** | No doc found |

Note: the service guidance page [SVC] covers Windows Server 2016 with Desktop Experience, not client Windows. It confirms service names and descriptions, but its recommendations are about attack surface, not performance.

### 7. Windows settings / debloat

| Tweak | Verdict | Reason | Documented path / mechanism | Source |
|---|---|---|---|---|
| Chris Titus WinUtil | **REJECT** | Third-party bundle; parts can't be reversed; individual items not documented | — | — |
| Visual effects: best performance | **OPT-IN** (animations part only) | See the summary. The full "best performance" preset is a persistent preference | `SPI_SETCLIENTAREAANIMATION`, `SPI_SETUIEFFECTS`: **confirmed**; `VisualFXSetting`: **unconfirmed** | [SPI] |
| Transparency off | **OPT-IN** | See the summary | `EnableTransparency`: **unconfirmed** | No doc found |
| Animation effects off | **OPT-IN** | Same as the visual effects row | `SPI_SETCLIENTAREAANIMATION`: **confirmed** | [SPI] |
| Notifications off / DND | **REJECT** | No documented perf effect | — | No doc found |
| Bluetooth off | **REJECT** | No evidence | — | No doc found |
| Dynamic Lighting off | **REJECT** | No evidence | — | No doc found |
| Share across devices off | **REJECT** | No evidence (claimed bug not documented) | — | No doc found |
| Startup apps | **REJECT** | Persistent preference, takes effect at boot | — | — |
| Background apps off | **REJECT** | Persistent preference; no documented gaming effect | — | No doc found |
| Uninstall apps / Edge / WebView | **REJECT** | Irreversible | — | — |
| Privacy toggles | **REJECT** | Privacy preference, not performance | — | — |
| Search permissions / indexing | **REJECT** | Preference. (PeakMode already stops WSearch for the session) | — | — |
| Windows Update options / pause | **REJECT** | Persistent; security updates | — | — |
| Disk Cleanup, temp/prefetch, defrag | **REJECT** | Maintenance | — | — |
| SFC / DISM | **REJECT** | Maintenance | `sfc /scannow`, `DISM … /RestoreHealth`: **unconfirmed** (not checked; maintenance anyway) | — |
| OS list timeout | **REJECT** | Boot only | — | — |
| Discord / Steam overlays | **REJECT** | Third-party apps, out of scope | — | — |
| Scaling 100% / max refresh | **INFO ONLY** | Show it if the display isn't at its highest refresh rate; changing the display mode mid-session is disruptive | — | No doc found |
| Audio settings | **REJECT** | Preference; no evidence | — | — |
| Enhance pointer precision off | **REJECT** | Personal input preference, not a boost | `HKCU\Control Panel\Mouse` values: **unconfirmed** | No doc found |

### 8. NVIDIA

| Tweak | Verdict | Reason | Documented path / mechanism | Source |
|---|---|---|---|---|
| OpenGL rendering GPU | **OPT-IN** | See the summary | NVAPI DRS | [NVCP] |
| Shader cache size 10 GB | **INFO ONLY** | Disk budget that pays off over time, not per session; "Installing a new driver will delete the cache" | NVAPI DRS (setting ID not checked) | [NVCP] |
| V-Sync off | **ADD** | See the summary | `VSYNCMODE_ID` | [NVCP], [NVLAT] |
| AA gamma correction | **REJECT** | Image quality, OpenGL only; no perf effect | — | [NVCP] |
| Low Latency Mode On | **ADD** | See the summary | `PRERENDERLIMIT_ID` | [NVCP], [NVLAT] |
| Low Latency Mode Ultra | **OPT-IN** | See the summary | NVAPI DRS | [NVCP] |
| Power mgmt Prefer max performance | **ADD** | See the summary | `PREFERRED_PSTATE_ID` | [NVCP] |
| Texture filtering High performance | **OPT-IN** | See the summary | NVAPI DRS | [NVCP] |
| Anisotropic sample optimization | **OPT-IN** | See the summary | NVAPI DRS | [NVCP] |
| "Copy these settings" | **REJECT** | Not specified | — | — |
| Reset NVCP defaults | **REJECT** | Not a tweak | — | — |
| GPU scaling | **REJECT** | Display preference; not described in the NVCP help | — | [NVCP] (absent) |
| Digital vibrance | **REJECT** | Cosmetic; not documented | — | [NVCP] (absent) |
| NVIDIA App auto-OC | **REJECT** | Overclocking; outside our snapshot; no NVIDIA warranty or safety statement found | — | [NVAPP] |
| NVCleanInstall | **REJECT** | Third-party driver install; not a session tweak | — | — |
| Latest drivers | **REJECT** | Maintenance | — | — |
| Afterburner undervolt | **REJECT** | Out of scope | — | — |

### 9. AMD

ADLX (AMD's settings API) was not checked. Every AMD OPT-IN needs that check first.

| Tweak | Verdict | Reason | Source |
|---|---|---|---|
| Adrenalin preferences / overlays / hotkeys off | **REJECT** | No documented perf effect | No doc found |
| Texture filtering quality = Performance | **OPT-IN** | See the summary | [AMD] *(snippet)* |
| Surface format optimization on | **INFO ONLY** | On by default (per an AMD Community post, not the FAQ) | [AMD] *(snippet)* |
| Tessellation override, max off | **OPT-IN** | See the summary | [AMD] *(snippet)* |
| Driver-only install | **REJECT** | Not a session tweak | — |

---

## Commonly cited locations: confirmed or not

| Location or command | Result |
|---|---|
| HVCI `…\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity` `Enabled`=0 | **Confirmed** [HVCI] |
| `…\Control\Lsa` `RunAsPPL`=0 | **Confirmed** [LSA] |
| `GameBar` `AutoGameModeEnabled` | **Unconfirmed** |
| `UserGpuPreferences` `SwapEffectUpgradeEnable=1;` | **Unconfirmed** |
| `GraphicsDrivers` `HwSchMode`=2 | **Unconfirmed** |
| `UserGpuPreferences` `GpuPreference=2;` | **Unconfirmed** |
| `Remove-AppxPackage Microsoft.XboxGamingOverlay` | **Unconfirmed** |
| `GameConfigStore` `GameDVR_Enabled`=0 | **Unconfirmed**. Documented alternative: policy `HKLM\Software\Policies\Microsoft\Windows\GameDVR` `AllowGameDVR` [GAMEDVR] |
| Ultimate plan `powercfg -duplicatescheme e9a42962-…` | Command **confirmed** [POWERCFG]; GUID **unconfirmed** on Microsoft Learn |
| Perf time check interval GUID `4d2b0152-…` | **Unconfirmed** |
| Idle time check GUID `c4581c31-…` | **Unconfirmed** |
| `PERFEPP 0` | Alias and meaning **confirmed** [EPP] |
| `HiberbootEnabled` | **Unconfirmed** |
| `Tasks\Games` key | **Confirmed** [MMCSS] |
| `Control\PriorityControl` | **Confirmed** [W32PS] |
| `Multimedia\SystemProfile` (SystemResponsiveness) | **Confirmed** [MMCSS] |
| `Multimedia\SystemProfile` (NetworkThrottlingIndex) | **Unconfirmed** on Microsoft Learn (secondary sources only) |
| `ipconfig /flushdns` | **Confirmed** [IPCONFIG] |
| Service names DiagTrack, TrkWks, lfsvc | **Confirmed** verbatim [SVC] |
| Service names SysMain, Spooler, PcaSvc, WpnService, WalletService | **Confirmed** (non-verbatim part of the fetch) [SVC] |
| W32Time | **Confirmed** [W32TIME] |
| Delivery Optimization user setting | **Unconfirmed** registry path; documented control is `DODownloadMode` policy [DO] |
| `Remote Assistance` `fAllowToGetHelp` | **Unconfirmed** |
| `VisualFXSetting`, `EnableTransparency` | **Unconfirmed**. Documented runtime APIs: `SPI_SETCLIENTAREAANIMATION`, `SPI_SETUIEFFECTS` [SPI] |
| `HKCU\Control Panel\Mouse` accel values | **Unconfirmed** |
| `sfc` / `DISM` commands | **Unconfirmed** (not checked; rows rejected as maintenance) |

---

## Sources

[MMCSS]: https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service
[W32PS]: https://learn.microsoft.com/en-us/previous-versions/cc976120(v=technet.10)
[QUANTUM]: https://learn.microsoft.com/en-us/archive/blogs/embedded/master-your-quantum
[BOOST]: https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/options-for-perf-state-engine-perfboostmode
[EPP]: https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/options-for-perf-state-engine-perfenergypreference
[PPM]: https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/configure-processor-power-management-options
[POWERCFG]: https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/powercfg-command-line-options
[ULT]: https://www.techpowerup.com/241586/microsoft-adding-ultimate-performance-mode-to-windows-10
[FASTSTART]: https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/distinguishing-fast-startup-from-wake-from-hibernation
[HAGS]: https://devblogs.microsoft.com/directx/hardware-accelerated-gpu-scheduling/
[WINDOWED]: https://support.microsoft.com/en-us/topic/3f006843-2c7e-4ed0-9a5e-f9389e535952
[DXBLOG22]: https://devblogs.microsoft.com/directx/updates-in-graphics-and-gaming/
[GAMEPERF]: https://support.microsoft.com/en-us/windows/options-to-optimize-gaming-performance-in-windows-11-a255f612-2949-4373-a566-ff6f3f474613
[GAMEDVR]: https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-applicationmanagement
[TIMER]: https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod
[HVCI]: https://learn.microsoft.com/en-us/windows/security/hardware-security/enable-virtualization-based-protection-of-code-integrity
[LSA]: https://learn.microsoft.com/en-us/windows-server/security/credentials-protection-and-management/configuring-additional-lsa-protection
[SVC]: https://learn.microsoft.com/en-us/windows-server/security/windows-services/security-guidelines-for-disabling-system-services-in-windows-server
[LPS]: https://learn.microsoft.com/en-us/defender-endpoint/limited-periodic-scanning-microsoft-defender-antivirus
[CLOUD]: https://learn.microsoft.com/en-us/defender-endpoint/cloud-protection-microsoft-defender-antivirus
[W32TIME]: https://learn.microsoft.com/en-us/windows-server/networking/windows-time-service/windows-time-service-top
[SPI]: https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow
[NDIS-PM]: https://learn.microsoft.com/en-us/windows-hardware/drivers/network/standardized-inf-keywords-for-power-management
[NDIS-OFF]: https://learn.microsoft.com/en-us/windows-hardware/drivers/network/using-registry-values-to-enable-and-disable-task-offloading
[NDIS-ENUM]: https://learn.microsoft.com/en-us/windows-hardware/drivers/network/enumeration-keywords
[DO]: https://learn.microsoft.com/en-us/windows/deployment/do/waas-delivery-optimization-reference
[IPCONFIG]: https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/ipconfig
[RUSS]: https://learn.microsoft.com/en-us/archive/blogs/markrussinovich/vista-multimedia-playback-and-network-throughput
[NVCP]: https://www.nvidia.com/content/Control-Panel-Help/vLatest/en-gb/mergedProjects/nv3dENG/Manage_3D_Settings_(reference).htm
[NVLAT]: https://www.nvidia.com/en-us/geforce/guides/system-latency-optimization-guide/
[NVAPP]: https://www.nvidia.com/en-us/software/nvidia-app/faq/
[AMD]: https://www.amd.com/en/resources/support-articles/faqs/dh3-012.html

| Key | Notes on the source |
|---|---|
| ULT | *Secondary*: reporting on Microsoft's Feb 2018 Insider announcement; the Microsoft post itself wasn't retrieved |
| GAMEPERF | *Snippet*: the page returned no article body when fetched; content comes from search excerpts and press coverage of it |
| QUANTUM | Archived Microsoft blog, 2006 |
| W32PS | Archived TechNet reference, 2008 |
| RUSS | Archived Microsoft blog (Mark Russinovich), 2007 |
| NVLAT | NVIDIA guide dated 2020 |
| AMD | *Snippet*: the page fetch timed out twice; content comes from search excerpts |
