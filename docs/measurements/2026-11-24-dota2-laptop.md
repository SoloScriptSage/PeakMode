# Dota 2 on a laptop: PeakMode 0.2.0 on and off

First real-game measurement of PeakMode. Measured 2026-11-24.

**Result: no measurable difference.** The spread between runs with the same setting (about 10 FPS average) is larger than any difference between PeakMode on and off.

## Setup

| | |
|---|---|
| PeakMode | 0.2.0 |
| CPU | AMD Ryzen 7 4800H |
| GPU | NVIDIA RTX 3050 Ti Laptop GPU |
| RAM | 16 GB, 3200 MT/s |
| Game | Dota 2, replay of a teamfight |
| Capture | CapFrameX, 60 s per run |

Every run used the same replay section. "Off" means PeakMode was not active. "PeakMode" means a boost session was active for the whole run.

## Results

| Run | Avg FPS | P1 FPS | P0.1 FPS |
|---|---:|---:|---:|
| Off #1 | 80.6 | 44.3 | 32.0 |
| Off #2 | 71.0 | 37.5 | 21.9 |
| PeakMode #1 | 77.3 | 39.5 | 21.4 |
| PeakMode #2 | 69.3 | 37.7 | 22.6 |
| Off (control) | 77.2 | 42.8 | 28.5 |

Ranges per setting:

| | Avg FPS | P1 FPS | P0.1 FPS |
|---|---|---|---|
| Off (3 runs) | 71.0 – 80.6 | 37.5 – 44.3 | 21.9 – 32.0 |
| PeakMode (2 runs) | 69.3 – 77.3 | 37.7 – 39.5 | 21.4 – 22.6 |

Both PeakMode runs fall inside the range of the Off runs on every metric. The PeakMode mean is slightly lower (73.3 vs 76.3 average FPS). That gap is smaller than the 9.6 FPS between Off #1 and Off #2, and with two and three runs it cannot be told apart from noise. The runs follow the run number more than the setting: both #1 runs are 77–81 FPS, both #2 runs 69–71.

## Sensors

- **CPU clock about 4.29 GHz in every run**, with or without PeakMode. The CPU was already at its maximum boost clock, so the power plan change has no headroom to add anything here.
- **GPU load about 40%.** The game is CPU-bound, limited by its main thread, so the NVIDIA driver settings cannot help either.

## What this means for PeakMode

- On this machine and this game, the default tweaks do not change FPS or 1% / 0.1% lows by more than the noise.
- The power plan tweak is one of the default-on tweaks rated OPT-IN in [tweak-verdicts.md](../tweak-verdicts.md). This run is evidence against it on a CPU that already reaches full boost on the stock plan. Other hardware (CPUs that park cores or drop clocks under the balanced plan) is not covered.
- The timer resolution request probably does not reach the game process at all on Windows 10 2004 and later ([#9](https://github.com/SoloScriptSage/PeakMode/issues/9)), so this run does not test it.
- Five 60 s runs are too few to detect small effects. A useful next measurement needs more runs per setting, alternating on and off, and frametime data rather than summary numbers.
