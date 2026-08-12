# Session Handoff

Written because the previous session's context window filled up. Read this before doing anything
else in this repo — it captures decisions and debugging context that aren't written down
anywhere else, including several things that would otherwise get silently re-broken or
re-investigated from scratch.

## State as of this handoff

- 17 commits ahead of `origin/master`, nothing pushed (no remote push has happened this whole
  project — confirm with the user before ever pushing).
- Build: `dotnet build LuxtronicPCTools.sln` — clean.
- Tests: `dotnet test LuxtronicPCTools.sln` — **73/73 passing**.
- `src/Luxtronic.PCTools/appsettings.json`'s `ServerBaseUrl` gets flipped back and forth between
  `http://localhost:7777` (local dev/testing) and `http://192.168.68.255:7777` (real LAN target)
  depending on what was being tested last — **check its current value before assuming**, and
  don't "fix" it back to one or the other without asking; it's deliberately mutable during dev.
- `tools/hwi/` (real `HWiNFO64.exe`, third-party, git-ignored) and `tools/CrystalDiskMark/`
  (also third-party, untracked, not yet integrated into anything) exist on disk but aren't
  committed - expected, matches `tools/prime95/`'s pattern.

## What's built (all wired up and working, per README.md's "Current scope")

- **CPU test**: Prime95 wrapper + full CONTRACT.md session/test-run/telemetry flow. This is the
  original "walking skeleton" scope and has been working since before this handoff.
- **CPU sensors**: temp, load, clock (frequency), fan — streamed as telemetry during a CPU test,
  shown live in the UI (updates ~1/sec). Falls back to HWiNFO shared memory for temp/clock
  specifically on hardware where LibreHardwareMonitorLib's own reads don't work (see "CPU temp/
  clock reliability" below — this is the single biggest debugging story of this session).
- **GPU sensors**: temp, hot spot temp, core/memory clock, load, fan, power, VRAM — read and
  shown live in the UI. **Display only, not sent to the server** — no telemetry, no GPU test_run,
  no FurMark wrapper. Only validated against one NVIDIA GPU.
- **SSD S.M.A.R.T.**: drive identity + health (temp, wear indicators, reallocated sectors,
  power-on hours/count) — read at app open (shown in the UI) AND **sent to the server** as a
  `component: "ssd"` test_run automatically after every CPU test run completes, one test_run per
  detected drive. Full contract written up in `SSD_SMART_ADDENDUM.md` in the shared planning repo
  (`Luxtronic-PCTools`, sibling directory) — **verified end-to-end against a live running server,
  no server-side changes needed**, the server's existing generic threshold-matching convention
  already handles it. This is NOT the CrystalDiskMark benchmark (no throughput data) — SMART
  health only.
- **CPU test duration**: shown in the UI before Start is even clicked (`TestSessionController.
  GetConfigAsync()`, separate from the fetch `RunCpuTestSessionAsync` does at actual run time).
- **Self-contained publish**: `dev-menu.ps1` option 10 produces a single ~166MB exe with the
  .NET runtime bundled in — target PCs need nothing installed. Auto-bundles `tools/prime95/` and
  `tools/hwi/` into the published folder. Verified by actually launching the published exe
  elevated and confirming sensors work (single-file + native driver libraries is a real failure
  class, not assumed safe just because the publish command exited 0).
- **Dev tooling**: `dev-menu.ps1` (PowerShell menu: build/test/launch/publish/API key/server URL/
  CPU duration/status checks) + `dev-menu.bat` (double-click wrapper, since PowerShell's default
  execution policy blocks the `.ps1` outright on a fresh PC — hit this on a second technician PC
  this session).

## CPU temp/clock reliability — the big debugging story

This took most of this session and has several layers. If CPU temp/fan/clock issues come up
again, read this in full before re-diagning from scratch.

1. **LibreHardwareMonitorLib only supports ONE `Computer` instance per process.** Opening a
   second one (an earlier version of the SSD SMART reader owned its own) corrupts the first
   one's internal CPU hardware state and makes the next `ReadCpu()` throw a
   `NullReferenceException` deep inside LHM's own code. Fixed by making `SensorMonitor` the sole
   owner of the one `Computer` instance (CPU+GPU+Storage all enabled on it); `SsdSmartReader` is
   now a stateless static class operating on an already-updated hardware collection, not owning
   its own `Computer`. **Don't ever give any new sensor-reading code its own `Computer` instance.**

2. **The sensor-health check must be scoped to CPU hardware specifically, not "any hardware".**
   `SensorMonitor.Initialize()`'s "is temperature actually readable" check used to query across
   *all* hardware types. That was correct when only CPU+Motherboard sensors were enabled, but
   silently broke once GPU/Storage support was added: a working GPU or SSD temperature sensor
   would satisfy the check and mask a completely dead CPU temperature path, reporting a false
   green "Sensors OK" while CPU temp/clock/fan were actually all null. This was found by testing
   on real technician PCs/laptops, not caught by unit tests (there's no automated coverage for
   this specific regression class — if hardware-scoped sensor code changes again, think hard
   about whether a health check needs re-scoping).

3. **Secure Boot/HVCI is NOT the dominant real-world cause of CPU temp failing** — an earlier
   version of the warning message said it was; that was wrong and has been corrected. Field
   evidence: on an affected machine, Memory Integrity (HVCI) was confirmed ON, no other
   hardware-monitoring app was running, and CPU temp still came back null via this app — but
   HWiNFO64, launched separately on the *same* machine, read CPU temp correctly. If HVCI blocked
   WinRing0-style drivers generically, HWiNFO's own driver would have failed too; it didn't. This
   points at a **LibreHardwareMonitorLib-specific limitation** for certain CPU/board
   combinations (matches an open, unresolved upstream GitHub issue for the same CPU family — not
   something fixable by toggling a Windows setting).

4. **The fix: `HwInfoSensorReader.cs`**, a narrow fallback that reads CPU temp/clock from
   HWiNFO64's Shared Memory interface, used only when LHM's own values come back null (both in
   the health check and every `ReadCpu()` poll). Ground-truthed against a real 342-reading HWiNFO
   dump — the label-matching logic is confirmed correct, not a guess. Requires:
   - `HWiNFO64.exe` running in the background (`tools/hwi/`, git-ignored, drop-in like Prime95)
   - Settings → "Shared Memory Support" enabled in HWiNFO — **GUI-only toggle, no confirmed
     silent/INI way to enable it**
   - **HWiNFO restarted after enabling that setting** — toggling it while already running does
     NOT retroactively create the shared-memory block
   - `dev-menu.ps1`'s status panel (option 3, or just look at the top of any menu screen) shows
     whether HWiNFO's shared memory is actually active right now (`Global\HWiNFO_SENS_SM2` check)
     — this is the only way to know if the fallback would work, independent of whether it's
     actually needed on the current machine.

5. **What's NOT yet confirmed**: the full "LHM fails → HWiNFO fallback kicks in → UI shows real
   values" loop has never been exercised end-to-end from this agent's side — this dev machine's
   LHM already works, so the fallback path is provably correct in isolation
   (`HwInfoSensorReader` tested directly against real data) but never naturally triggers here.
   Needs testing on one of the actually-affected machines, with HWiNFO set up there too.

## Other non-obvious facts worth knowing

- **`appsettings.json`'s `ServerBaseUrl` needs a full URI with scheme.** A bare `"localhost"`
  (no `http://`, no port) makes `new Uri(...)` throw `Invalid URI: The format of the URI could
  not be determined.` inside `LuxApiClient`'s constructor — this surfaces as a confusing "ERROR:
  Invalid URI" in the app's log right after "API key loaded", with Start silently disabled
  afterward. Always `http://host:port`.
- **NVMe and ATA/SATA SMART attribute IDs are NOT unified** — e.g. raw attribute ID 5 means
  "Percentage Used" on NVMe but "Reallocated Sectors Count" on ATA/SATA. `SsdSmartReader` gates
  every attribute lookup on bus type for exactly this reason. Ground-truthed against two real
  NVMe drives and one real ATA/SATA drive (a WD SATA SSD in a USB enclosure).
- **The exe locks during rebuild if the app is currently running.** `dotnet build`/`publish` will
  fail with `MSB3027`/file-in-use errors — always check `tasklist /FI "IMAGENAME eq
  Luxtronic.PCTools.exe"` (or just ask the user to close it) before rebuilding.
- **`dev-menu.ps1`'s menu option numbers shift** every time an option is added before "Exit" —
  don't assume "9 = exit" or similar from memory; always check the current menu text. This
  caused a real self-inflicted testing bug earlier this session (piped "9" expecting Exit, got
  "Open tools folder" instead, then an infinite empty-input loop).
- **Git Bash path quirks**: `.\script.ps1` sometimes gets mis-parsed as `.script.ps1` by
  PowerShell invoked from this Bash tool — use `./script.ps1` (forward slash) instead.
- **UAC prompts need the actual human** — background PowerShell tasks that hit `Start-Process
  -Verb RunAs` will sit waiting; the agent cannot click "Yes" itself. Always tell the user a
  prompt is pending rather than assuming it'll resolve.
- **Credential files should never be read directly by the agent** — API keys, even when the task
  seems to require inspecting them. Use file-to-file copies, or compare SHA-256 hashes (safe,
  one-way) instead of `cat`-ing the raw file, when debugging auth mismatches.

## What's explicitly NOT built yet

Same as README.md's "Current scope" section states, but worth restating: no GPU test
orchestration (FurMark wrapper doesn't exist), no RAM test (TestMem5), no CrystalDiskMark
throughput benchmark (`tools/CrystalDiskMark/` has binaries dropped in but nothing uses them
yet), no exclusive-concurrency wiring for RAM/SSD, no multi-test-run-per-session UI (one CPU test
= one full session, start to end, still). GPU/RAM/SSD checkboxes in the UI are still disabled.

## Server-side coordination

The user said "the main agent is working on the server side" early this session — the server repo
(`Luxtronic-PCTools-Server`, sibling directory) has independently gained substantial
functionality (dashboard, PDF export, full REST API, generic threshold-matching config) during
the same period this client repo was being worked on. `SSD_SMART_ADDENDUM.md` in the shared
planning repo (`Luxtronic-PCTools`, not a git repo itself — just docs) is the one deliberate
cross-repo coordination artifact from this session, written specifically so whoever's on the
server side doesn't need to reverse-engineer the SSD SMART payload shape from client code.

## If picking this up next: recommended first checks

1. `git log --oneline -20` to see what's landed since this note (if the user's continued without
   an agent in between, unlikely but worth checking).
2. `dotnet build LuxtronicPCTools.sln && dotnet test LuxtronicPCTools.sln` to confirm the
   baseline still holds (73/73 as of this note).
3. Check `appsettings.json`'s current `ServerBaseUrl` before assuming which server the app is
   pointed at.
4. If the conversation continues a CPU-temp-related thread, read the "CPU temp/clock reliability"
   section above in full before touching `SensorMonitor.cs`/`HwInfoSensorReader.cs` again.
