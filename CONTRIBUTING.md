# Contributing to PeakMode

Thanks for helping. PeakMode changes system settings on real machines, so the bar for a change is
"it can always be put back". Please read this before opening a pull request.

## The rules for a tweak

A new tweak or a change to an existing one must be:

1. **Reversible within a boost.** No reboot needed to apply it or to undo it.
2. **Journaled before it is made.** The original value goes into the write-ahead snapshot first
   (`core/engine/Snapshot.cpp`). If the snapshot write fails, the change is not made.
3. **Restorable from a fresh process.** Restore reads only the snapshot file, so it must work after
   a crash or a reboot, not just from the process that applied it.
4. **Documented by the vendor.** Microsoft, NVIDIA or AMD documentation for both what it does and
   how it is set (registry path, API, service name). [docs/tweak-verdicts.md](docs/tweak-verdicts.md)
   shows how tweaks are judged; a tweak marked REJECT there needs new evidence, not a new PR.
5. **No security trade.** PeakMode does not turn off security features, even for a session.

Registry writes go through `core/engine/Registry.cpp`, which only allows known keys. Adding a key to
that allowlist is part of the review.

## Building and testing

Requirements: Visual Studio 2022 with the C++ desktop workload and CMake, .NET 8 SDK.

```powershell
cmake -S core -B core/build -A x64
cmake --build core/build --config Release
core/build/Release/peakmode_test.exe selftest config/peakmode.json   # no system changes
dotnet test ui/PeakModeTray.Tests -c Release                          # no core, no system changes
```

CI runs the same steps on every pull request. Both test suites must pass and must stay free of
system changes: the self-test uses temp folders, the UI tests use fake cores.

For UI work, a Debug build has `PeakModeTray.exe --window-preview`, which runs the window on a
simulated core and edits a copy of the config in `%TEMP%`.

### On-machine cycles

Anything that touches the system (activate, restore, a new tweak) needs an on-machine cycle before
it is called tested. Use a machine you can recover:

1. `scripts/verify-state.ps1` captures every setting PeakMode touches. Run it before activating,
   while active, and after restore.
2. The cycle passes only if the state after restore is identical to the state before.
3. Put the result in the README's test status table, including what was **not** tested.
   `scripts/RECOVERY.md` describes manual recovery if a restore fails.

## Pull requests

- One change per pull request, with a short description of what changed and how it was tested.
- Say which tests ran on a real machine and which did not.
- UI changes: keep every control a real button or toggle with an automation name, and keep text
  contrast at 4.5:1 or more (`ContrastTests` checks the color tokens).
- Reference the issue (`Fixes #123`) when there is one.

## Reporting a bug

Use the bug report template. The most useful things to include are `peakmode.log` and
`sessions.jsonl` from `C:\ProgramData\PeakMode` (readable by administrators; the Settings screen
has an OPEN button for the log), your Windows version, GPU and network adapter.

If a restore failed and your settings are still changed, start with `scripts/RECOVERY.md`.

## License

By contributing you agree that your contribution is licensed under the MIT license of this
project.
