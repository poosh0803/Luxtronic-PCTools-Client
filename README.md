# Luxtronic-PCTools-Client

The PC-side client of the Luxtronic PCTools hardware-testing system. Runs on the machine
under test (new build QC or customer repair), technician-operated only. See
[CLAUDE.md](CLAUDE.md), [PROJECT_PLAN.md](PROJECT_PLAN.md), and [CONTRACT.md](CONTRACT.md) for
the full design - this README only covers building/running this client.

## Current scope: CPU + GPU + RAM, standalone (not concurrent)

Per `PROJECT_PLAN.md` §9, this started as one complete vertical slice for the CPU test only
(Prime95), to prove the sensor-driver risk (see below) and the CONTRACT.md API/WebSocket shapes
work end-to-end - GPU (FurMark 2) and RAM (TestMem5/TM5) have since been wired up the same way.
**CPU, GPU, and RAM are mutually exclusive in this pass**, not CONTRACT.md §6's "together" mode
(cpu+gpu running concurrently) - the server already fully supports that (`concurrency.js`), but
the client doesn't attempt it yet; `TestSessionController.RunCpuTestSessionAsync`/
`RunGpuTestSessionAsync`/`RunRamTestSessionAsync` share a single `IsRunning` guard, and
`MainWindow`'s three checkboxes uncheck each other. For RAM specifically this isn't just a
client-side simplification - CONTRACT.md §6 requires RAM to never run concurrently with anything
else, a real server-enforced rule that the shared `IsRunning` guard happens to satisfy for free.
SSD's checkbox exists in the UI but is disabled/greyed ("coming soon") - not wired to anything.

Exceptions to the "one test at a time" scope:

- `Services/SsdSmartReader.cs` (drive identity + S.M.A.R.T. health via LibreHardwareMonitorLib)
  is wired up and reports to the server - see
  [SSD_SMART_ADDENDUM.md](../Luxtronic-PCTools/SSD_SMART_ADDENDUM.md) in the shared planning repo
  for the full contract (verified working end-to-end against a live server, no server-side
  changes needed). This happens automatically after every CPU test run, regardless of the (still
  disabled/"coming soon") SSD checkbox - `TestSessionController.SubmitSsdSmartDataAsync()`
  submits one `component: "ssd"` test_run per detected drive, and `ssd_serials` is now populated
  at session creation. What's still missing: CrystalDiskMark's actual throughput benchmark
  (sequential read/write, CONTRACT.md §3's `min_seq_*_mb_s`) isn't implemented at all, and
  there's no UI checkbox or exclusive-concurrency wiring for a technician-initiated SSD test -
  this is SMART health reporting only, piggybacking on the CPU test session.
- GPU sensors (temp, hot spot, core/memory clock, load, fan, power, VRAM) are always read live via
  `SensorMonitor.ReadGpu()` and shown in the UI's idle readout regardless of which test is
  selected - separate from whether a GPU *test* is actually running.

`Services/FurMarkRunner.cs` wraps FurMark 2 (`tools/FurMark_win64/` - see its own README for setup)
the same way `Prime95Runner.cs` wraps Prime95: `TestSessionController.RunGpuTestSessionAsync`
mirrors `RunCpuTestSessionAsync`'s fetch-config/create-session/start-test-run/poll-and-stream-
telemetry/complete-test-run/end-session flow, streaming `gpu_core_temp_c`, `gpu_hot_spot_temp_c`,
`gpu_load_pct`, `gpu_fan_rpm`, `gpu_core_clock_mhz`, `gpu_power_w` and using FurMark's
`--artifact-scanner` flag (GPU rendering-corruption detection) toward `summary_stats.error_count`
- see `FurMarkRunner`'s class remarks for what's ground-truthed there and what isn't (the artifact
*detection* log format specifically hasn't been observed against a real detected artifact).

`Services/TM5Runner.cs` wraps TestMem5/TM5 (`tools/TestMem5/` - see its own README, which also
covers the reverse-engineered config-selection mechanism in detail) the same way, with two real
differences from the CPU/GPU wrappers: (1) TM5 has no self-stopping duration flag like FurMark's
`--max-time`, so `TM5Runner` enforces the configured duration externally and kills the process,
the same shape `Prime95Runner` already uses; (2) `RamProfileSelector` auto-detects CPU vendor
(Intel/AMD) and RAM generation (DDR4/DDR5) via WMI and overrides the server's `config_profile` on
a detected DDR5 system - a deliberate deviation from CONTRACT.md's "config flows one direction"
model, listed in "Judgment calls" below. RAM telemetry (`ram_tested_mb`, `ram_errors`) comes from
polling TM5's `Log.txt`, not a `SensorMonitor` reading - **confirmed via real testing that TM5
holds this file open exclusively for the whole run**, so the live readout gets one value near the
start and then freezes there for the rest of the run (final `summary_stats` sent to the server are
unaffected - that comes from a post-exit read, after TM5 releases the lock). See `TM5Runner`'s
class remarks and `tools/TestMem5/README.md` for the full story and what's still unconfirmed (most
notably: the external-duration-kill path itself has never been observed firing - every real test
run was stopped manually first).

The client never computes pass/fail and never shows results locally - that's server/dashboard
only, by design (CONTRACT.md §7, PROJECT_PLAN.md §4).

## Prerequisites

- Windows 10/11.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (`dotnet --list-sdks` should
  show an `8.0.x` entry) - **only needed on whichever machine builds the app.** A technician's
  PC that just *runs* an already-built copy needs nothing installed at all if you deploy the
  self-contained publish (see "Deploying to a PC without .NET installed" below) - that's
  PROJECT_PLAN.md §4's original "single self-contained executable" goal.
- Must run **elevated (as Administrator)** - `LibreHardwareMonitorLib` needs elevation for
  Storage/SMART access (`SsdSmartReader.cs`). CPU/GPU sensors go through HWiNFO's shared memory
  instead and don't need this app itself to be elevated (confirmed: reading HWiNFO's shared memory
  works fine unelevated), but the requirement is left in place for Storage. The built exe requests
  this automatically via `app.manifest` (`requireAdministrator`), so Windows will prompt for
  elevation on launch.

## Build

```powershell
dotnet build LuxtronicPCTools.sln
```

## Deploying to a PC without .NET installed

Regular `dotnet build`/`dotnet run` (including `dev-menu.ps1` options 1/4/5) produce a
**framework-dependent** build - fast and small, but the target machine needs the .NET 8 Desktop
Runtime to run it. For an actual technician PC that shouldn't need anything installed, publish a
**self-contained, single-file** build instead - `dev-menu.ps1` option 10, or directly:

```powershell
dotnet publish src\Luxtronic.PCTools\Luxtronic.PCTools.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish\win-x64
```

This bundles the entire .NET runtime into one exe (~166MB - that size is normal and expected for
a bundled-runtime single file, not a bug). `dev-menu.ps1`'s version also auto-copies
`tools\prime95\` into the published folder, since `Prime95Runner`'s dev-mode "walk up parent
directories" fallback (see below) only works when this repo checkout is nearby - a folder copied
to another PC has no such parent repo, so `prime95.exe` has to ship directly alongside the
published exe instead. Still needed on the target PC after copying the published folder over,
same as every other build (not part of the publish output, deliberately - see "One-time local
setup" below): that technician's own `apikey.txt`, and a quick check that `appsettings.json`'s
`ServerBaseUrl` actually points at the real server.

Verified working: launched the published exe elevated on this dev machine and confirmed sensors
(CPU/GPU via HWiNFO, Storage via LHM's WinRing0 native driver) come up the same as a regular build
- the main risk with single-file + native libraries is exactly that class of failure, so this
wasn't assumed to work just because the publish command succeeded.

On the target PC, run **`Launch.bat`** (also copied into the published folder by `dev-menu.ps1`
option 10 - see `publish-assets/`), not the exe directly. It runs a pre-flight check (exe present,
`prime95.exe` present, `apikey.txt` present, `appsettings.json` valid), starts HWiNFO64.exe
unattended if present and waits for its shared memory to come up, checks the server is reachable,
and reports exactly what's missing before launching, rather than the app either failing
with a cryptic error or - the failure this was added to catch - coming up as a blank, permanently
unresponsive window with no diagnostic at all (seen on a real technician test machine; traced to a
LibreHardwareMonitorLib/WMI call able to hang instead of fail fast during startup, since fixed with
a bounded timeout in `MainWindow.InitializeSensorsAsync`/`SensorMonitor.TryReadMotherboardSerialViaWmi`
- Launch.bat's checks are a second line of defense, not a substitute for that fix).

The regular `[Bb]in/`/`[Oo]bj/` build output stays git-ignored as before; `publish/` is too - this
is a build artifact, produced on demand, never committed.

## Run (dev)

```powershell
dotnet run --project src\Luxtronic.PCTools\Luxtronic.PCTools.csproj
```

## Tests

```powershell
dotnet test LuxtronicPCTools.sln
```

`tests/Luxtronic.PCTools.Tests` is an xUnit project covering the pure/deterministic logic in
the CPU walking skeleton - Prime95 error-line counting and torture-test FFT/thread decisions,
the sensor driver-health verdict, the test-run summary_stats builder, and CONTRACT.md
JSON-shape round-trips for the DTOs in `Models/Contracts.cs`. It does **not** cover
`SensorMonitor.Initialize()`/`ReadCpu()`, `LuxApiClient`, or `TelemetryPublisher` - those need
real hardware/a running server and are exercised instead by the manual end-to-end checklist
above. A handful of methods that were previously `private` are `internal` (with
`InternalsVisibleTo` granted to the test assembly) specifically so this project can exercise
them without widening the public API surface.

Note: running via `dotnet run`/`dotnet exec` does **not** trigger the `app.manifest`
elevation prompt (that only applies when Windows launches the compiled `.exe` directly), so
sensor init may come up degraded unless your terminal itself is already elevated. To exercise
the real elevated path, build then run the exe directly from an elevated shell:

```powershell
dotnet build LuxtronicPCTools.sln
# from an Administrator PowerShell/cmd:
.\src\Luxtronic.PCTools\bin\Debug\net8.0-windows\Luxtronic.PCTools.exe
```

## One-time local setup

1. **API key** - create `apikey.txt` next to the built exe (i.e.
   `src\Luxtronic.PCTools\bin\Debug\net8.0-windows\apikey.txt` for a Debug build) containing
   just the technician's API key issued by the server side, no quotes/newlines needed. See
   `src/Luxtronic.PCTools/apikey.example.txt` for the expected format. This file is
   git-ignored - never commit a real key.
2. **Prime95** - drop the real `prime95.exe` (manually downloaded, see
   `tools/prime95/README.md`) into `tools/prime95/` at the repo root. The app resolves this
   folder relative to the exe first, then walks up parent directories looking for
   `tools\prime95` as a dev-mode convenience, so it finds the repo-root copy even when running
   from `bin\Debug\net8.0-windows\`.
3. **Server URL / other settings** - `src/Luxtronic.PCTools/appsettings.json` (copied to the
   build output automatically). Defaults to the LAN server at `http://192.168.68.255:7777`
   per `PROJECT_PLAN.md` §5 - change `ServerBaseUrl` if testing against a different host (e.g.
   a locally-running instance of `Luxtronic-PCTools-Server` during development).

## What a human needs to do to test this end-to-end

1. Have the `Luxtronic-PCTools-Server` companion repo running and reachable (locally or on
   the LAN box), with at least one technician row seeded in its `technicians` table so an API
   key is available.
2. Drop that API key into `apikey.txt` (step 1 above) and point `ServerBaseUrl` in
   `appsettings.json` at wherever that server is listening.
3. Drop a real `prime95.exe` into `tools/prime95/` (step 2 above).
4. Build and launch the exe **from an elevated (Administrator) shell/shortcut** - required for
   sensor access.
5. On launch, check the "Machine identity" panel: it should show a real motherboard serial, a
   live-updating CPU readout in green (e.g. "CPU: 40C   Load: 12%   4187 MHz   Fan: 2303 RPM" -
   updates every second) rather than a static message, a live GPU readout (or "(no GPU
   detected)"), and an SSD S.M.A.R.T. summary line per detected drive. If the CPU line instead
   shows a red WARNING, see "Known risk" below before going further. Also confirm the CPU
   checkbox shows a real "(duration: N min)" next to it, not "(duration: checking...)" stuck or
   an "unavailable" message - that's `GET /api/config` succeeding before Start is even clicked.
6. Fill in customer name / new-build-or-repair / notes, leave the CPU checkbox ticked (it's
   the only one enabled), click **Start**. Watch the status log for each contract call
   (`GET /api/config`, `POST /api/sessions`, `POST .../test-runs`, WebSocket connect) and
   confirm each succeeds against the real server rather than throwing. Also confirm the log shows
   SSD SMART data being reported per drive right after the CPU test run completes (before the
   session ends) - that's `SubmitSsdSmartDataAsync` running.
7. Confirm telemetry is actually arriving server-side (e.g. via the server's own logs/DB, or
   its dashboard once that exists) while the CPU test runs.
8. Click **Stop** partway through and confirm the run completes cleanly with
   `stop_reason: user_abort` server-side, and that a full-duration run completes with no
   `stop_reason` and a computed `result`.
9. This is also the first real test of the Prime95 config-file generation (`prime.txt`/
   `local.txt` - see `tools/prime95/README.md`) - confirm Prime95 actually picks up the
   configured FFT range/duration rather than falling back to defaults or a first-run wizard.

## Known risk this pass is meant to catch (PROJECT_PLAN.md §8)

**Current architecture**: CPU and GPU sensors are read via HWiNFO64's Shared Memory interface
(`HwInfoSensorReader.cs`), not LibreHardwareMonitorLib. Storage/SMART still goes through LHM
(`SsdSmartReader.cs`) - see "Why HWiNFO, not LHM, for CPU/GPU" below for how this came about.
`SensorMonitor.Initialize()` (`src/Luxtronic.PCTools/Services/SensorMonitor.cs`) reads HWiNFO's
shared memory once at startup and reports an explicit degraded state (visible in the UI's "Sensor
status" line and the log) if it isn't reachable, rather than assuming success just because nothing
threw. If you see that warning on a real customer/shop machine: HWiNFO64 either isn't running, or
Settings > "Shared Memory Support" isn't enabled (`publish-assets/Launch.ps1` starts HWiNFO
automatically before launching the app in a real deployment - if you're running the exe directly
instead, launch `tools/hwi/HWiNFO64.exe` yourself first).

### Why HWiNFO, not LHM, for CPU/GPU

`LibreHardwareMonitorLib` depends on the WinRing0 kernel driver for CPU sensor access. On Windows
11 with Secure Boot + Memory Integrity (HVCI) enabled, that driver can silently fail to load -
sensors just return nothing, no exception. This was originally handled with a narrow fallback
(HWiNFO used only when LHM's own CPU temp/clock came back null); HWiNFO has since become the sole
CPU/GPU source, since it's proven reliably more available than LHM on affected hardware and is now
started unattended alongside the app.

**What happened in this dev environment, back when LHM was still primary**: sensors initialized
cleanly - 39 sensors found (CPU temps, per-core/package, load, etc.) and the motherboard serial
read correctly via WMI, even when the app was launched *unelevated* (via `dotnet exec`, which
bypasses the manifest's elevation prompt - see "Run (dev)" above).

**Confirmed materializing on real technician PCs/laptops**: multiple other machines showed CPU
temp/clock/fan all null while GPU sensors (when a GPU was present) worked fine. This uncovered two
things, one at a time:

1. **A real bug in the (now-removed) LHM health check itself.** The "is temperature actually
   readable" test was scoped to *any* hardware's temperature sensor, not specifically the CPU's -
   so once GPU/Storage sensor support was added, a working GPU or SSD temperature sensor silently
   masked a completely dead CPU temperature path, reporting a false green "Sensors OK" while CPU
   temp/clock/fan stayed null the whole time.
2. **HVCI turned out not to be the actual cause.** The health-check message initially named Secure
   Boot/HVCI as the likely explanation, on the reasoning that GPU vendor APIs don't need WinRing0
   the way CPU MSR reads do. Further field testing disproved that: on one affected machine, Memory
   Integrity was confirmed ON, no other monitoring app was running, and CPU temp *still* came back
   null via this app - but HWiNFO64, launched separately on the very same machine, read CPU temp
   correctly. If HVCI were blocking WinRing0-style drivers generically, HWiNFO's own kernel driver
   would have failed too. It didn't, which points at a LibreHardwareMonitorLib-specific limitation
   for this CPU/board combination instead - matching an open, unresolved upstream GitHub issue for
   the same CPU family, not an HVCI policy question a technician can toggle their way out of. This
   is the concrete evidence behind moving CPU/GPU off LHM entirely rather than continuing to patch
   around it.

**Current implementation**: `HwInfoSensorReader.cs` reads CPU temperature/clock/load/fan and the
full GPU reading set from HWiNFO64's Shared Memory. Requires HWiNFO64 running in the background
with Settings > "Shared Memory Support" enabled (GUI-only toggle, restart HWiNFO after enabling it
- see the class remarks for why; `tools/hwi/HWiNFO64.INI` in this repo already has it set) -
`tools/hwi/` is where a technician/developer drops the real `HWiNFO64.exe`, same drop-in pattern as
`tools/prime95/`. Label-matching is ground-truthed against a real, live HWiNFO shared-memory dump
on the same Intel i5-11400F + NVIDIA GTX 1080 Ti used throughout this README (342 CPU-side
readings, 76 GPU-side readings) - see `HwInfoSensorReaderTests.cs` for the exact fixtures. Only
validated against this one Intel CPU / NVIDIA GPU / HWiNFO version combination - AMD/Intel GPUs and
other CPU vendors may use different label text for the same metrics, worth re-confirming if CPU or
GPU readings are null on other hardware even with HWiNFO installed, configured, and reachable
(distinct from "HWiNFO isn't reachable at all" - `SensorMonitor.EvaluateSensorHealth` reports these
as two different messages for exactly this reason).

**Storage stays on LHM.** HWiNFO's shared memory exposes drive temperature, remaining life %,
available spare %, failure/warning flags, and total host writes/reads, but not Power-On Hours,
Power-On Count, or Reallocated Sectors Count - which `SsdSmartReader.cs` already reports and which
are already sent to the server per SSD_SMART_ADDENDUM.md. Storage was never the reliability problem
(CPU temp/clock was), so it wasn't worth the regression to switch it too.

## Judgment calls made against CONTRACT.md (flag for reconciliation with the server side)

CONTRACT.md is precise about endpoint/message shapes but silent on a few operational
questions this client had to resolve on its own. Listed here so they can be reconciled with
whoever built the server:

1. **Prime95 has no documented "stop after N minutes" flag.** `-t` torture-test mode runs
   until stopped externally. This wrapper (`Prime95Runner.RunAsync`) enforces the configured
   `duration_minutes` itself and kills the process tree when it elapses, treating that as a
   **normal finish** (`stop_reason: null`) rather than an abort, since the test ran the full
   configured duration without errors. Only a technician-initiated Stop is `user_abort`; an
   *unexplained* self-exit before the duration elapses (no error text logged) is treated as
   `tool_crash`; a self-exit *with* error text logged is treated as a normal finish whose
   `summary_stats.error_count > 0` lets the server's existing fail logic (CONTRACT.md §7) do
   its job, rather than the client pre-judging it as a crash.
2. **Prime95's config file name/location is ambiguous across versions.** CONTRACT.md and
   PROJECT_PLAN.md both say "`-t` + `prime.txt`", but historically some Prime95 versions read
   torture-test settings from `local.txt` instead. `WritePrimeConfig()` writes both files with
   identical content as a hedge. **Not yet validated against a real binary** - flagged clearly
   in `tools/prime95/README.md` as the first thing to check once a real `prime95.exe` is
   dropped in.
3. **`client_error` scope.** CONTRACT.md's example for `client_error` is "sensor read
   failure". This client also uses `client_error` for a telemetry WebSocket send failure (3
   consecutive failures of either sensor read or telemetry send aborts the run) - reasoned as
   fitting the general definition ("the client app itself hit an error unrelated to the
   hardware under test"), but it's a slight extrapolation beyond the literal example.
4. **`tool_output_raw` size.** CONTRACT.md doesn't cap this. The client truncates to 200KB
   before sending, to avoid pathological payloads from a long-running verbose tool. Worth
   confirming the server doesn't have a stricter (or looser) limit that should match.
5. **One session = one test run, this pass.** CONTRACT.md's data model supports multiple
   `test_runs` per session (e.g. CPU then GPU in the same visit) and an explicit end-of-visit
   action. Since only CPU is wired up here, this client creates a session, runs the one CPU
   test, and ends the session automatically right after - it doesn't yet support "run CPU,
   then also run GPU, then end session" in one sitting. That'll need revisiting once GPU/RAM/
   SSD land.
6. **Motherboard serial can come back as WMI placeholder junk** (e.g. "Default string", "To
   Be Filled By O.E.M.") on some boards, or `null` if WMI is blocked/unavailable entirely.
   The client surfaces `null` as an explicit blocking warning (Start is disabled without a
   serial) but does *not* attempt to detect/filter known placeholder strings - a board
   reporting "Default string" would currently be sent as-is and become that literal string's
   row in the server's `machines` table, which is probably not desired long-term.
7. **WebSocket auth**: implemented via the `X-Api-Key` request header on the `/ws/telemetry`
   upgrade request (one of the two options CONTRACT.md §1 allows). The `?api_key=` query-param
   fallback is not exercised client-side - the server must still accept both per contract, but
   nothing in this repo tests that path.
8. **`smart_reallocated_sectors_max` has no NVMe equivalent.** CONTRACT.md §3's SSD threshold
   is a classic ATA/SATA SMART attribute (ID 5) - NVMe drives don't have "reallocated sectors"
   as a concept at all. Ground-truthed against two real NVMe drives on this dev machine:
   `SsdSmartReader` correctly leaves `ReallocatedSectorsCount` null for NVMe and instead
   populates NVMe-specific `PercentageUsed`/`AvailableSparePercent` (see
   `Services/SsdSmartReader.cs` class remarks for the full attribute-ID mapping, including the
   gotcha that NVMe attribute ID 5 means "Percentage Used" while ATA ID 5 means "Reallocated
   Sectors Count" - same number, unrelated meaning). **Update:** this is now resolved in practice
   - see [SSD_SMART_ADDENDUM.md](../Luxtronic-PCTools/SSD_SMART_ADDENDUM.md). The server's
   already-generic `max_`/`min_` convention-based threshold matching handles the NVMe-specific
   `summary_stats` keys (`max_smart_percentage_used` etc.) with no server-side code changes -
   confirmed via a live end-to-end test against a running server that returned the correct
   `result`. The ATA-side mapping (IDs 5, 9, 12) is now also confirmed against a real ATA/SATA
   drive (a WD SATA SSD in a USB enclosure) rather than just the documented SMART spec - see
   `SsdSmartReader.cs` class remarks and SSD_SMART_ADDENDUM.md §4 for the exact values.
9. **CPU duration is fetched twice.** The "Tests to run" panel shows the server-configured CPU
   duration (`TestSessionController.GetConfigAsync()`) as soon as the app opens, so a technician
   can see it before deciding to click Start - `RunCpuTestSessionAsync` then fetches its own
   fresh copy at actual test-run time regardless, per CONTRACT.md's "client fetches thresholds at
   session start" model. If `config/default.json` is hand-edited on the server between app open
   and clicking Start, the displayed duration could theoretically be stale for a few seconds/
   minutes until Start re-fetches - the *test itself* always uses the fresh value, so this is a
   display-only edge case, not a correctness one.
10. **RAM's `config_profile` is overridden client-side on detected DDR5 systems.** CONTRACT.md
    §3's `ram.config_profile` is a single server-wide value, same as CPU's `mode`/GPU's `tool` -
    the server has no idea what CPU platform or RAM generation is actually in the PC under test.
    The shipped TestMem5 profile pack only has one DDR5-tuned `.cfg` per CPU platform (no
    per-intensity DDR5 variants the way DDR4 has Absolut/Extreme/Heavy/etc.), so
    `RamProfileSelector` detects CPU vendor (Intel/AMD) and RAM generation (DDR4/DDR5) via WMI and
    substitutes the matching DDR5 profile when one is detected, ignoring `config_profile`'s exact
    value in that case only - on DDR4 (or when detection fails/is ambiguous), `config_profile` is
    honored normally. This is a deliberate, one-off deviation from "config flows one direction,
    client just executes" - flagged here rather than silently deviating.

## Repo layout

```
src/Luxtronic.PCTools/        WPF client app (net8.0-windows)
  Models/Contracts.cs          Typed mirrors of every CONTRACT.md request/response/DB-adjacent shape
  Services/
    AppSettingsProvider.cs     Loads appsettings.json, resolves relative paths
    ApiKeyProvider.cs          Reads the technician API key file
    LuxApiClient.cs            REST calls from CONTRACT.md §4
    TelemetryPublisher.cs      /ws/telemetry client (CONTRACT.md §5)
    SensorMonitor.cs           CPU/GPU via HwInfoSensorReader, Storage via LibreHardwareMonitorLib
                                 (one shared Computer - see its class remarks for why) + WMI
                                 mobo-serial wrapper - see "Known risk" above for why CPU/GPU and
                                 Storage use different sources
    SsdSmartReader.cs          Storage/SMART attribute extraction + summary_stats/tool_output_raw
                                 builders, called by SensorMonitor.ReadSsds() and
                                 TestSessionController.SubmitSsdSmartDataAsync() - see "Current scope" above
    HwInfoSensorReader.cs      HWiNFO64 shared-memory reader - the sole CPU + GPU sensor source
                                 (temp/load/fan/clock for CPU, full reading set for GPU) - see
                                 "Known risk" above
    Prime95Runner.cs           Prime95 process wrapper
    FurMarkRunner.cs           FurMark 2 process wrapper - mirrors Prime95Runner, simpler in one
                                 respect (--max-time makes FurMark exit on its own, no external
                                 kill-after-duration needed like Prime95 requires)
    TM5Runner.cs               TestMem5/TM5 process wrapper - external duration enforcement like
                                 Prime95Runner (no self-stopping flag like FurMark's --max-time);
                                 config selection works by overwriting a hardcoded file path, not a
                                 CLI flag - see its class remarks and tools/TestMem5/README.md
    RamProfileSelector.cs      Picks which TM5 .cfg profile to run - WMI-detected DDR5 systems
                                 override the server's config_profile (see "Judgment calls" above)
    TestSessionController.cs   Orchestrates one full CPU, GPU, or RAM test session end-to-end
                                 (standalone, not concurrent - see "Current scope" above), plus the
                                 per-drive SSD SMART reporting described above
  MainWindow.xaml(.cs)         The one screen: session form (customer/type/notes), test checkboxes
                                 with the server-configured CPU duration shown next to the CPU one,
                                 machine identity panel (mobo serial, live CPU sensor readout, live
                                 GPU readout, SSD SMART summary), Start/Stop, status log
  app.manifest                 requireAdministrator
dev-menu.ps1                   PowerShell dev launcher/menu (build, test, launch elevated or via
                                 dotnet run, set API key/server URL, set CPU test duration in the
                                 Server repo's config, publish a self-contained build) - stands in
                                 for a proper installer/shortcut during development, not part of
                                 the shipped app
dev-menu.bat                    Double-click wrapper for dev-menu.ps1 - bypasses the default
                                 PowerShell execution policy that otherwise blocks it outright
tools/prime95/README.md        Placeholder - drop prime95.exe here manually (not auto-downloaded)
tools/hwi/README.md            Drop the real HWiNFO64.exe here manually (not auto-downloaded,
                                 the .exe itself not committed - see "Known risk" above). Sole
                                 CPU/GPU sensor source now, not optional. HWiNFO64.INI *is*
                                 committed (turnkey Shared Memory Support + other settings - see
                                 tools/hwi/README.md for why each one is set)
tools/FurMark_win64/README.md  Placeholder - drop the real FurMark 2 install here manually (not
                                 auto-downloaded, not committed)
tools/TestMem5/README.md       Placeholder - drop the real TestMem5 install here manually (not
                                 auto-downloaded, not committed). Covers the reverse-engineered
                                 config-selection mechanism in detail - read before touching
                                 TM5Runner/RamProfileSelector
publish-assets/                Launch.ps1/Launch.bat - copied into publish/win-x64/ by dev-menu.ps1
                                 option 10 (not produced by dotnet publish itself). What a
                                 technician actually double-clicks on the target PC: runs a
                                 pre-flight check (exe/prime95/apikey/appsettings), starts HWiNFO64
                                 if present (unattended - HWiNFO64.INI's ShowWelcomeAndProgress=0
                                 skips its startup dialog) and waits for its shared memory to come
                                 up, checks server reachability, then launches the app - so missing
                                 prerequisites show up as a clear checklist instead of the app
                                 failing cryptically or hanging with no explanation
publish/win-x64/               Self-contained single-file build output (dev-menu.ps1 option 10) -
                                 git-ignored, produced on demand, this is what gets copied to a
                                 technician PC that has no .NET installed
```
