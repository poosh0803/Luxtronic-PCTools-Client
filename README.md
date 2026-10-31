# Luxtronic PCTools - Client

The PC-side app of the Luxtronic hardware-testing system. It runs on the machine under test (new
build QC or a customer repair), operated by a technician, and ships as **one standalone exe**.
Pass/fail is decided by the server, never here. See [CLAUDE.md](CLAUDE.md), [PROJECT_PLAN.md](PROJECT_PLAN.md)
and [CONTRACT.md](CONTRACT.md) for the design and the API contract shared with the server repo
(`Luxtronic-PCTools-Server`).

> `v1/` holds the previous client (single-window WPF, config files, PowerShell launchers). It is kept
> for reference only; everything at the repo root is the current app.

## What it is

- **.NET 10 WPF** app on the **WPF-UI** (Fluent) library: left sidebar, Mica window, light/dark themes.
  MVVM with CommunityToolkit.Mvvm, Generic Host dependency injection and Serilog file logging.
- **Single self-contained exe** (~118 MB): the .NET runtime and every test tool are inside it.
- Must run **as Administrator** (the manifest requests it) - the SSD SMART reader loads a kernel driver
  and HWiNFO is started by the app.

### Sidebar

| Page | What it does |
|---|---|
| **Dashboard** | This PC's identity and live sensors: CPU and GPU tiles with the model names, motherboard card (name, serial, BIOS), one card per drive (model, serial, firmware, health, temperature, hours, host reads/writes), server/API-key status and whether each bundled tool was found. |
| **Run Tests** | Tick any combination of CPU / GPU / VRAM / RAM / SSD. They run **one after another in one server session**, every result is **submitted**. Shows the running test's own progress (percent, elapsed/remaining) and an overall bar. Customer name, new-build/repair and notes go here. |
| **CPU, GPU, VRAM, RAM, SSD** | One tab per test. Runs that single test **on this PC only - nothing is sent to the server**. Shows live sensors, a progress card and a plain "last run" outcome line (not saved anywhere). SSD has the drive/folder picker. |
| **Apps, Drivers, Tools** | Installers the server hosts, one list per category. Each entry shows its icon, name, version, description and file size; **Download** fetches it (progress, cancel, checked against the server's SHA-256) into `%LocalAppData%\LuxtronicPCTools\apps\`, `drivers\` or `tools\`, then the button becomes **Run** (launched through Windows, so UAC works) plus **Folder**. A newer upload on the server counts as not downloaded yet, and finishing the download deletes the older version. **Remove downloaded apps/drivers/tools** clears that category from this PC. A **Grid / List** switch changes the layout (shared by the three tabs for the session). Managed on the server's Apps / Drivers / Tools pages; none of this needs the API key. |
| **Settings** | Server URL and API key, Offline mode, per-test durations, CPU load level, VRAM GPU number, background (hidden-window) switch, telemetry interval, theme, "Reset to defaults" and "Reset everything and restart". |
| **Log / About** | The live status log (copy / clear / open log folder), version and logo. |

### The tests

| Test | Tool | Notes |
|---|---|---|
| CPU | Prime95 `-t` torture test | Load level in Settings: Blend, Max heat (small FFT) or **Blend with AVX off (default)**. Errors are read from `results.txt`. |
| GPU | FurMark 2 (`--max-time`, artifact scanner) | Trimmed bundle (no GPU-Z / GPU Shark / CPU Burner). |
| VRAM | memtest_vulkan v0.5.0 | Started as `memtest_vulkan.exe <gpu number>` (non-interactive); each "Error found" report counts as an error. Sent to the server as its own component, `vram`. |
| RAM | TestMem5 (TM5) | Config profile is copied over the file TM5 always loads; progress/errors from `Log.txt`. Telemetry also streams system memory in use and CPU temp/load. |
| SSD | DiskSpd sequential read/write benchmark + SMART | Scratch file on the chosen drive (never a raw device), deleted afterwards. Live read/write speed from the Windows LogicalDisk counters. |

Prime95 and TestMem5 have no silent mode, so with the **background** switch on (default) they are started
with a hidden show-state and their windows are hidden by a fast watcher. A hidden tool left behind by a
crash is stopped at the next startup (only our own bundled copy, only if its parent app is gone).

### What gets sent to the server (Run Tests series only)

`POST /api/sessions` with the motherboard serial, customer, type, notes and a **`system_info`** block
(CPU, GPU, memory modules, motherboard + BIOS, drives, OS - collected once at startup via WMI and the
sensors); then per test `POST .../test-runs` (with `run_settings` - the duration/mode this PC really uses),
telemetry over `/ws/telemetry`, and `PATCH .../test-runs/:id` with `summary_stats`; finally
`PATCH /api/sessions/:id`. The server's config still supplies the pass **thresholds**, but **durations,
CPU mode/AVX and the VRAM GPU number are local settings** that override the server config's values.

## Build and run

Prerequisites: the **.NET 10 SDK** (`dotnet --list-sdks`).

```
dotnet build LuxtronicPCTools.sln
publish.bat                      # single-file exe -> publish\win-x64\Luxtronic.PCTools.exe
```

`publish.bat` runs `dotnet publish ... -p:PublishProfile=Single` (self-contained, ReadyToRun, compressed
single file; settings in `src/Luxtronic.PCTools/Properties/PublishProfiles/Single.pubxml`). The exe cannot
be overwritten while it is running - close the app first.

### The tool pack

Third-party tools live in `tools/` at the repo root (git-ignored binaries; each folder has a README on what
to drop in): `prime95`, `FurMark_win64`, `TestMem5`, `DiskSpd`, `hwi` (HWiNFO64 + `HWiNFO64.INI`),
`memtest_vulkan`. At build time the `BuildToolPack` target in the csproj zips a **trimmed** selection into
`obj/toolpack/toolpack.zip` and embeds it in the exe. If `tools/prime95/prime95.exe` is missing the build
warns and produces an exe **without** a pack (dev fallback: it then looks for a `tools` folder above the
exe). On first run the pack is unpacked to `%LocalAppData%\LuxtronicPCTools\tools\pack-<size>\` (a new
build with different tools unpacks into a fresh folder and removes the old one).

### Where things live on a PC

`%LocalAppData%\LuxtronicPCTools\` holds `settings.json` (server URL, **API key in plain text**, durations,
...), `ui.json` (theme), `logs\` (daily Serilog files) and `tools\`. "Reset everything" in Settings deletes
this whole folder and relaunches the exe.

### First run

1. Launch the exe (accept the UAC prompt). It unpacks the tools, starts HWiNFO (minimized) and waits for its
   shared memory, then reads the sensors.
2. On a fresh install (no `settings.json` yet) a small **Server connection** window asks for just
   a few things: the server URL (defaults to `http://192.168.68.255:7777`), the technician's API key (issued by the
   server; optional when Offline mode is on), the **Offline mode** switch and the **theme** (Dark / Light /
   System; it previews live). **Finish** saves and opens the
   Dashboard; **Skip for now** (or closing it) saves the defaults and opens **Settings** instead, where the key
   can be entered later. The window is not shown again unless the settings file is deleted ("Reset everything").

### Offline (dev) mode

Settings > Offline mode: no config fetch, session, test-run or telemetry call leaves the PC - everything is
answered locally. Takes effect the moment the switch is flipped. For development and demos; nothing is
recorded anywhere.

## Sensors

CPU and GPU readings come from **HWiNFO64's shared memory** (the app starts the bundled copy; it needs
"Shared Memory Support", pre-set in `tools/hwi/HWiNFO64.INI`). The earlier LibreHardwareMonitorLib CPU
path was dropped because its WinRing0 driver failed on real machines. **LibreHardwareMonitorLib is still
used for storage/SMART** (HWiNFO doesn't expose power-on hours/count), together with DiskInfoToolkit's
parsed SMART block for health, host reads/writes and firmware.

## Tests

`tests/` is empty: the xUnit suite for the old client lives in `v1/tests` and has **not been ported** to
this project yet. Anything that needs hardware, a server or a window was only verified by hand.

## Known gaps / unverified

- A real detected memory error in TM5's `Log.txt` and a natural full TM5 cycle were never observed
  (`TM5Runner.CountErrors` is a best-guess pattern).
- memtest_vulkan's exit-code handling and GPU numbering come from reading its source; the
  "device lost" path was never exercised. On a PC with an iGPU the GPU number may need changing in Settings.
- Hiding TestMem5's window is untested on a real run (Prime95's was confirmed).
- FurMark's EULA says "may not alter": the bundle is trimmed - fine for internal use, re-check before any
  wider distribution. All bundled tools are licensed for internal use only.
- Passes through a series are sequential; CPU+GPU "together" mode (allowed by the server) is not used.

## Repo layout

```
src/Luxtronic.PCTools/
  App.xaml(.cs)              host, DI registration, Serilog, theme at startup
  Models/Contracts.cs        typed mirrors of the CONTRACT.md shapes (+ local-only fields, [JsonIgnore])
  Services/
    AppSettingsProvider.cs   settings.json (URL, key, durations, load level, switches)
    TestSessionController.cs one server session per call, or RunSeriesAsync for several tests
    Prime95Runner / FurMarkRunner / TM5Runner / DiskSpdRunner / MemtestVulkanRunner   tool wrappers
    SensorMonitor / HwInfoSensorReader / SsdSmartReader   sensors and SMART
    AppCatalogService.cs     lists/downloads the server's apps, drivers and tools (checksum check, old-version cleanup)
    SystemInfoCollector.cs   the system_info block;  MotherboardInfoReader.cs, SystemProbes.cs (RAM, disk speed)
    LuxApiClient / TelemetryPublisher   REST + WebSocket (both honour Offline mode)
    WindowHider.cs           hides tool windows;  Shell/ : MachineService, ControllerProvider, RunState,
                             LogService, AppThemeService, ToolPack, HwInfoLauncher, AppReset, DriveCard, LiveStat, ThemedDialog
  UI/
    Windows/MainWindow       sidebar shell;  FirstRunWindow (first-start connection wizard)
    Pages/                   Dashboard, RunTests, Settings, LogAbout + one tab page per test + Apps, Drivers, Tools
    Controls/                SingleTestView (shared tab layout), AppListView (list/grid UI shared by Apps/Drivers/Tools),
                             GeometryIcon + HardwareIcons (sidebar icons)
    ViewModels/              one per page; SingleTestViewModel + CPU/GPU/VRAM/RAM/SSD subclasses
  Resources/                 Logo.png, Emblem.png (+ source art);  Logo.ico (exe icon)
publish.bat                  builds the single-file exe
tools/                       third-party tool folders (binaries git-ignored)
v1/                          the previous client, kept for reference
```
