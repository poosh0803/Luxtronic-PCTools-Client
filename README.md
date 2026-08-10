# Luxtronic-PCTools-Client

The PC-side client of the Luxtronic PCTools hardware-testing system. Runs on the machine
under test (new build QC or customer repair), technician-operated only. See
[CLAUDE.md](CLAUDE.md), [PROJECT_PLAN.md](PROJECT_PLAN.md), and [CONTRACT.md](CONTRACT.md) for
the full design - this README only covers building/running this client.

## Current scope: CPU-only walking skeleton

Per `PROJECT_PLAN.md` §9, this first pass implements one complete vertical slice for the CPU
test only (Prime95), to prove the sensor-driver risk (see below) and the CONTRACT.md
API/WebSocket shapes work end-to-end before GPU/RAM/SSD wrappers are added. GPU/RAM/SSD
checkboxes exist in the UI but are disabled/greyed ("coming soon") - not wired to anything.

One exception: `Services/SsdSmartReader.cs` (drive identity + S.M.A.R.T. health via
LibreHardwareMonitorLib) is wired up and reports to the server - see
[SSD_SMART_ADDENDUM.md](../Luxtronic-PCTools/SSD_SMART_ADDENDUM.md) in the shared planning repo
for the full contract (verified working end-to-end against a live server, no server-side changes
needed). This happens automatically after every CPU test run, regardless of the (still
disabled/"coming soon") SSD checkbox - `TestSessionController.SubmitSsdSmartDataAsync()` submits
one `component: "ssd"` test_run per detected drive, and `ssd_serials` is now populated at session
creation. What's still missing: CrystalDiskMark's actual throughput benchmark (sequential
read/write, CONTRACT.md §3's `min_seq_*_mb_s`) isn't implemented at all, and there's no UI
checkbox or exclusive-concurrency wiring for a technician-initiated SSD test - this is SMART
health reporting only, piggybacking on the CPU test session.

The client never computes pass/fail and never shows results locally - that's server/dashboard
only, by design (CONTRACT.md §7, PROJECT_PLAN.md §4).

## Prerequisites

- Windows 10/11.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (`dotnet --list-sdks` should
  show an `8.0.x` entry).
- Must run **elevated (as Administrator)** - `LibreHardwareMonitorLib` needs to load/start the
  WinRing0 kernel driver to read CPU sensors. The built exe requests this automatically via
  `app.manifest` (`requireAdministrator`), so Windows will prompt for elevation on launch.

## Build

```powershell
dotnet build LuxtronicPCTools.sln
```

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
5. On launch, check the "Machine identity" panel: it should show a real motherboard serial and
   a green "Sensors OK - N sensor(s) found" status. If it instead shows the red WARNING about
   0 sensors, see "Known risk" below before going further.
6. Fill in customer name / new-build-or-repair / notes, leave the CPU checkbox ticked (it's
   the only one enabled), click **Start**. Watch the status log for each contract call
   (`GET /api/config`, `POST /api/sessions`, `POST .../test-runs`, WebSocket connect) and
   confirm each succeeds against the real server rather than throwing.
7. Confirm telemetry is actually arriving server-side (e.g. via the server's own logs/DB, or
   its dashboard once that exists) while the CPU test runs.
8. Click **Stop** partway through and confirm the run completes cleanly with
   `stop_reason: user_abort` server-side, and that a full-duration run completes with no
   `stop_reason` and a computed `result`.
9. This is also the first real test of the Prime95 config-file generation (`prime.txt`/
   `local.txt` - see `tools/prime95/README.md`) - confirm Prime95 actually picks up the
   configured FFT range/duration rather than falling back to defaults or a first-run wizard.

## Known risk this pass is meant to catch (PROJECT_PLAN.md §8)

`LibreHardwareMonitorLib` depends on the WinRing0 kernel driver for sensor access. On Windows
11 with Secure Boot + Memory Integrity (HVCI) enabled, that driver can silently fail to load -
sensors just return nothing, no exception. `SensorMonitor.Initialize()`
(`src/Luxtronic.PCTools/Services/SensorMonitor.cs`) checks the actual sensor count after the
first poll and reports an explicit degraded state (visible in the UI's "Sensor status" line
and the log) rather than assuming success just because nothing threw. If you see that warning
on a real customer/shop machine, that's the risk materializing - check admin elevation first,
then Windows Security > Device security > Core isolation > Memory integrity.

**What happened in this dev environment**: sensors initialized cleanly - 39 sensors found
(CPU temps, per-core/package, load, etc.) and the motherboard serial read correctly via WMI,
even when the app was launched *unelevated* (via `dotnet exec`, which bypasses the manifest's
elevation prompt - see "Run (dev)" above). That's a reasonable dev-box result but is not a
substitute for confirming this on real target hardware with Secure Boot/HVCI enabled, which is
exactly the scenario PROJECT_PLAN.md §8 flags as needing validation on real machines, not
assumed from a dev box.

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
   `result`. The ATA-side
   mapping (IDs 5 and 9) is the standard, widely-documented SMART table but **not yet validated
   against a real ATA/SATA drive** in this codebase.

## Repo layout

```
src/Luxtronic.PCTools/        WPF client app (net8.0-windows)
  Models/Contracts.cs          Typed mirrors of every CONTRACT.md request/response/DB-adjacent shape
  Services/
    AppSettingsProvider.cs     Loads appsettings.json, resolves relative paths
    ApiKeyProvider.cs          Reads the technician API key file
    LuxApiClient.cs            REST calls from CONTRACT.md §4
    TelemetryPublisher.cs      /ws/telemetry client (CONTRACT.md §5)
    SensorMonitor.cs           LibreHardwareMonitorLib + WMI mobo-serial wrapper
    SsdSmartReader.cs          LibreHardwareMonitorLib Storage/SMART wrapper (drive identity + health -
                                 not yet wired into TestSessionController/UI, see "Current scope" below)
    Prime95Runner.cs           Prime95 process wrapper
    TestSessionController.cs   Orchestrates one full CPU test session end-to-end
  MainWindow.xaml(.cs)         The one screen: session form, test checkboxes, Start/Stop, log
  app.manifest                 requireAdministrator
tools/prime95/README.md        Placeholder - drop prime95.exe here manually (not auto-downloaded)
```
