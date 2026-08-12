# HWiNFO64 (drop-in, manual download required)

This folder holds a real HWiNFO64 install (the `.exe` files are git-ignored; `HWiNFO64.INI` is
committed - see below for why). `HwInfoSensorReader`
(`src/Luxtronic.PCTools/Services/HwInfoSensorReader.cs`) is the app's **sole CPU and GPU sensor
source** - see the main [README.md](../../README.md)'s "Known risk" section for how that came
about (LibreHardwareMonitorLib's WinRing0-based MSR reads are unreliable on some real CPU/board
combinations; HWiNFO reads the same values correctly on the very same affected machines). Storage/
SMART still goes through LibreHardwareMonitorLib, unaffected by anything in this folder.

## What to do

1. Download HWiNFO64 manually from the official site: https://www.hwinfo.com/download/
   - Pick a specific pinned version (do not auto-update). Ground-truthed against HWiNFO64 v8.51.
   - **License not re-verified for this internal-use case** - HWiNFO is commonly described as
     freeware for personal use, but that hasn't been checked against Geeks3D/FurMark-style
     rigor the way `tools/FurMark_win64/README.md` did for FurMark. Worth confirming before this
     ships beyond internal shop use, same caveat as `CLAUDE.md`'s general third-party-tools note.
2. Extract the full HWiNFO64 zip contents (not just `HWiNFO64.exe`) directly into this folder:
   ```
   tools/hwi/HWiNFO64.exe
   tools/hwi/(everything else from the HWiNFO64 zip)
   ```
3. Enable **Settings > "Shared Memory Support"** in HWiNFO64's own UI - a one-time, GUI-only
   toggle (no confirmed silent/INI-based way to enable it without a human clicking it at least
   once - it has to actually be toggled through the app, not just present in the INI beforehand).
   **Restart HWiNFO64 after enabling it** - toggling it while already running does not
   retroactively create the shared-memory block; see `HwInfoSensorReader.cs`'s class remarks.
4. That's it - no code changes needed. `HwInfoSensorReader` reads HWiNFO's shared memory
   automatically once it's running with that setting on.

## Why `HWiNFO64.INI` is committed (unlike the `.exe` files)

Unlike Prime95/FurMark, this one config file *is* tracked - it's not a copyrighted binary, and
committing it makes the drop-in genuinely turnkey instead of requiring a human to click through
HWiNFO's settings UI on every fresh machine. Current settings and why:

- `SensorsSM=1` - Shared Memory Support, enabled (see step 3 above - this is the setting that
  matters most; having it pre-set here saves the one-time manual toggle, though HWiNFO still
  needs an initial run to actually create the mapping).
- `SensorsOnly=1` - starts straight into the sensor monitor, not the full HWiNFO GUI.
- `ShowWelcomeAndProgress=0` - skips HWiNFO's startup welcome/progress dialog, so
  `publish-assets/Launch.ps1` can start it unattended (no dialog to dismiss) ahead of launching
  the app - see that script's remarks.
- `SensorInterval=500` - HWiNFO's own sensor poll interval, matched to the app's
  `TelemetrySampleIntervalMs` (`appsettings.json`) so telemetry graphs get one real HWiNFO sample
  per client poll rather than the client polling faster than HWiNFO actually refreshes.
- `Theme=1` - cosmetic only.

If HWiNFO's shared memory ever comes up inactive despite this INI being in place, it almost always
means HWiNFO hasn't been *launched* at all yet on that machine (the INI alone doesn't create the
mapping - only running the app with the setting on does) - `dev-menu.ps1`'s status panel and
`publish-assets/Launch.ps1`'s pre-flight check both report this directly (`Global\HWiNFO_SENS_SM2`).

## Why this isn't automated

HWiNFO64 is a third-party redistributable binary. Bundling a pinned version is a decision for a
human, not something an agent should auto-fetch from the internet - same reasoning as
`tools/prime95/README.md` and `tools/FurMark_win64/README.md`.

## Label-matching ground-truth

`HwInfoSensorReader`'s CPU and GPU field extraction is ground-truthed against a real, live HWiNFO64
shared-memory dump on the project's dev machine (Intel Core i5-11400F + NVIDIA GTX 1080 Ti - 342
CPU-side readings, 76 GPU-side readings) - see `HwInfoSensorReaderTests.cs` for the exact fixtures.
Only validated against this one CPU/GPU/HWiNFO version combination - other hardware may use
different label text for the same metrics; `SensorMonitor.EvaluateSensorHealth` reports "HWiNFO
reachable but no CPU temperature reading matched" as a distinct message from "HWiNFO not
reachable at all" for exactly this reason.
