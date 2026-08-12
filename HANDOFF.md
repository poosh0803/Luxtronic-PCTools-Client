# Session Handoff

Written because the previous session's context window filled up. Read this before doing anything
else in this repo — it captures decisions and debugging context that aren't written down anywhere
else, including several things that would otherwise get silently re-broken or re-investigated from
scratch. This supersedes the previous `HANDOFF.md` entirely (that one's content is now either
committed history or folded into this note) — do not try to reconcile the two.

## State as of this handoff

- Build: `dotnet build LuxtronicPCTools.sln` — clean, 0 warnings/errors.
- Tests: `dotnet test LuxtronicPCTools.sln` — **119/119 passing**.
- Client repo: 1 commit ahead of `origin/master` (nothing pushed all project — confirm with the
  user before ever pushing). **A large chunk of this session's work is uncommitted** — see "What's
  uncommitted" below. The user hasn't asked for a commit on the RAM/TM5 work yet.
- Server repo: clean except two pre-existing unrelated modified files (`public/css/style.css`,
  `public/js/session.js`) — not this session's work, believed to be the other agent mentioned in
  the previous handoff ("main agent is working on the server side"). Don't touch/revert those
  without checking with the user first.
- `appsettings.json`'s `ServerBaseUrl` is currently `http://192.168.68.255:7777` (the real LAN
  server, not localhost) — same "check before assuming, don't silently flip it" rule as always.
- Nothing is running right now (app, TM5, HWiNFO, FurMark all confirmed stopped) — the user closed
  everything themselves due to time pressure, not a crash. Safe starting state.

## What's uncommitted (this session's RAM/TM5 work)

New files: `src/Luxtronic.PCTools/Services/TM5Runner.cs`, `RamProfileSelector.cs`,
`tests/Luxtronic.PCTools.Tests/TM5RunnerTests.cs`, `RamProfileSelectorTests.cs`,
`tools/TestMem5/README.md`, `tools/hwi/README.md` (this last one is unrelated leftover from an
earlier small ask this session — "write read.md in hwi folder" — never committed either).

Modified: `.gitignore`, `README.md`, `MainWindow.xaml(.cs)`, `Models/Contracts.cs`,
`Services/AppSettingsProvider.cs`, `Services/TestSessionController.cs`, `appsettings.json`,
`ContractsSerializationTests.cs`, `TestSessionControllerTests.cs`.

All of it builds clean and passes tests (119/119 includes the new RAM/TM5 tests). **If the user
asks for a commit, this is all one coherent feature (RAM test wiring) plus one small unrelated
doc file (`tools/hwi/README.md`) that should probably go in its own tiny commit** — same
separation-of-concerns approach used for every other commit this session (see `git log`).

## What's built (all committed except RAM/TM5, see above)

- **CPU test**: Prime95 wrapper + full CONTRACT.md session/test-run/telemetry flow. Working,
  long-established, the original walking skeleton.
- **CPU + GPU sensors**: Now **100% HWiNFO-sourced**, not LibreHardwareMonitorLib. This was a
  deliberate full rewrite this session (see "Known risk" in `README.md` for the full story) —
  LHM's WinRing0 driver is unreliable on some real hardware; HWiNFO reads the same values
  correctly on the same affected machines. **Storage/SMART stayed on LHM** (HWiNFO's shared memory
  doesn't expose Power-On Hours/Count, which the server already consumes). This is
  **committed and stable** (`a14cdab`).
- **GPU test**: FurMark 2 wrapper, fully wired (`Services/FurMarkRunner.cs`,
  `TestSessionController.RunGpuTestSessionAsync`), standalone (not concurrent with CPU per a
  scope decision this session — see README's "Current scope"). **Committed and
  live-verified** (`a8395a9`) — real FurMark window opened, genuinely stressed the GPU (98% usage,
  74°C/88.8°C hotspot), stopped cleanly.
- **RAM test**: TestMem5 (TM5) wrapper, fully wired the same way — **THIS IS THE UNCOMMITTED
  WORK**. Also live-verified for real (see below), just not yet committed.
- **SSD S.M.A.R.T.**: unchanged, still LHM-based, still auto-submitted after every CPU run.
  Committed, stable, untouched this session.
- **Publish tooling**: `Launch.ps1`/`Launch.bat` pre-flight check + auto-start HWiNFO, `dev-menu.ps1`
  publish step. Committed, stable.

## RAM/TM5 — the big new debugging story this session

TestMem5 (TM5) turned out **meaningfully harder to automate** than Prime95/FurMark - no confirmed
CLI arguments, no self-stopping duration flag, and (now **confirmed**, not just inferred - see
below) it locks its own log file for the whole run. Everything below was learned through real,
live testing (with the user's explicit awareness each time - RAM stress testing carries genuine
risk on marginal hardware, treated with the same courtesy as a "heads up, real test running" each
time throughout this session).

1. **Config selection works by overwriting a hardcoded file path**, not a CLI flag or a
   "remembered last config." TM5 always looks for `bin\Universal 2 @ LMhz.cfg` on launch -
   confirmed by removing that exact file and getting a "file was not found" error naming it
   specifically. `TM5Runner.ActivateConfigProfile()` copies whichever real profile
   `RamProfileSelector` picked onto that path before each run. **The on-screen "Configuration"
   label in TM5's own window does NOT reflect the copied-in content** - it stays cosmetically tied
   to the filename text, confirmed by overwriting it with a different profile and watching that
   profile's actual parameters run while the label stayed unchanged. Every live test this session
   showed "Configuration: Universal 2 @ LMhz" regardless of which profile was actually active -
   **that label is not evidence of anything, don't trust it when debugging**. If you need to know
   which profile actually ran, check the app's own log line ("Selected RAM test profile: ...").
2. **`Log.txt` (at the TestMem5 root, not `bin/`) is locked exclusively by TM5 for the entire
   run - now CONFIRMED, not just inferred.** The user's own screenshot during a live run showed
   the app's status log repeating `"RAM log poll failed this cycle: The process cannot access the
   file '...\Log.txt' because it is being used by another process."` on every single poll cycle
   after the first. Practical effect: `TestSessionController.RunRamTestSessionAsync`'s live
   `RamStatusText` readout gets exactly ONE successful read (whatever the file contained at the
   instant polling started, which can be **stale data from the previous session** if TM5 hasn't
   opened/written its own new session's line yet) and then **freezes at that stale value for the
   rest of the run** - confirmed directly: a run showing "Tested: 672 MB" in the live UI the whole
   time, while `Log.txt` itself already had "656 MB" (correct for that run) by the time it was
   checked directly. **This does NOT affect correctness of what's sent to the server** -
   `TM5Runner.RunAsync`'s own post-exit read of `Log.txt` happens after the process has exited and
   released the lock, so `summary_stats`/final telemetry should still be accurate. Only the *live*
   in-progress readout is unreliable. This should be called out explicitly in code comments
   (`TM5Runner`'s class remarks, `TestSessionController.RunRamTestSessionAsync`'s remarks,
   `tools/TestMem5/README.md`'s "not confirmed" section) - **as of this handoff those still say
   "not confirmed" / "likely" and need updating to reflect this is now directly confirmed**, not
   done yet due to running out of context.
3. **No confirmed CLI arguments** - a raw config-path positional argument produced `"Something is
   wrong on the command line. See help for correct usage."` Not investigated further since the
   file-overwrite method works.
4. **Duration is TM5's own `Cycles` count, not wall-clock time** - confirmed directly this
   session: a real run configured for 60 minutes (external enforcement) showed TM5's own window
   estimating **"Remaining: 1:24:46"** (~1.5 hours) at the same moment, because TM5 has no idea
   about the app's external 60-minute limit - it's just estimating based on its own internal
   Cycles setting and current throughput. This is **expected, not a bug** - `TM5Runner` mirrors
   `Prime95Runner`'s exact pattern (external timer, kill the process tree when it elapses,
   regardless of TM5's own internal progress).
5. **NOT YET VERIFIED: the external-duration-kill path itself.** Every real test run done this
   session (four of them) was stopped by a technician clicking Stop (or closing the app) well
   before any configured duration elapsed - the longest ran ~6.5 minutes before being stopped. The
   `durationTask` branch in `TM5Runner.RunAsync` (mirrors `Prime95Runner`'s "duration elapsed →
   kill → normal finish" logic) has **never actually fired and been observed** in this project. A
   session was scheduled to wait for a real 60-minute run to reach its natural external kill, but
   was cancelled by the user before it completed (time pressure, not a finding). **This is the
   single most important thing for whoever picks this up to verify** - start a RAM test with a
   short configured duration (same trick used for CPU/GPU verification earlier - temporarily edit
   the *deployed* server's `config/default.json` `ram.duration_minutes` to something like 1, NOT
   the local server checkout at `192.168.68.70` which has zero effect on the actual
   `192.168.68.255` server the client talks to - this cost real time to figure out during the GPU
   pass, don't repeat that mistake) and confirm: the process actually gets killed at the right
   time, `stop_reason` comes through as `null` (normal finish) not `tool_crash`, and the app
   doesn't hang waiting on anything.
6. **What's NOT confirmed and likely can't be safely forced**: what a real detected memory error
   looks like in `Log.txt` (no error occurred in any test run this session - `TM5Runner.CountErrors`
   is a best-guess regex, already unit-tested against the one *negative* case that matters - TM5's
   own live "Errors: 0" field would false-positive a naive bare-substring match, confirmed and
   fixed during development, see `TM5RunnerTests.cs`); what a natural full-cycle completion looks
   like (every run was stopped early, on purpose - letting 60+ minutes of real RAM stress run
   unattended to find out isn't something this session did lightly, and shouldn't be treated as
   trivial by whoever picks this up either).
7. **`RamProfileSelector`'s DDR5 auto-detection has not been exercised on real DDR5 hardware** -
   this dev machine is DDR4 (11th Gen Intel + B560 chipset, doesn't support DDR5 at all), so only
   the DDR4/`config_profile`-fallback branch has run for real. The DDR5+Intel/DDR5+AMD branches
   are unit-tested (`RamProfileSelectorTests.cs`) but not live-verified - flag this if a DDR5
   machine becomes available to test on.

## Other non-obvious facts worth knowing (new this session, beyond the previous handoff's list)

- **HWiNFO's `SensorInterval` was changed to 500ms** (from its 1000ms default) to match the
  client's `TelemetrySampleIntervalMs` (also changed to 500ms this session, from 1000ms) - "more
  datapoints for the graph" was the ask. Both changes are committed. `tools/hwi/HWiNFO64.INI` is
  one of the few tracked (not gitignored) files in a `tools/` subfolder - see its own remarks (or
  the new `tools/hwi/README.md`, uncommitted) for why each setting in there is set the way it is.
- **Elevation boundary blocks a LOT of automated verification this session.** The app always runs
  elevated (`app.manifest`). This dev environment's automation tools (PowerShell tool calls) run
  *unelevated*. Windows blocks cross-integrity-level UI Automation and window
  focus/move/foreground calls in both directions - confirmed repeatedly: can't `Stop-Process` the
  elevated app from here, can't `SetForegroundWindow`/`SetWindowPos` its window, `UIAutomationClient`
  enumeration returns zero elements against it. **Screenshots (raw screen capture) still work
  fine** regardless of elevation - that's the only reliable way to see the elevated app's state
  from here. For anything requiring an actual click (checking a checkbox, clicking Start/Stop),
  **the user has to do it themselves** - this was true all session for both the GPU and RAM
  verification passes, not a one-off inconvenience.
- **TM5 itself does NOT need elevation to functionally run** (spawned real worker processes,
  tested real MB, unelevated, confirmed early in the RAM research) - but since the app that
  launches it is always elevated anyway, this never mattered in practice; TM5 inherits the
  elevated launch.
- **Multiple app instances can coexist.** Launching a second `Luxtronic.PCTools.exe` while an
  earlier one is still open (e.g. because the first one initialized before HWiNFO was ready, and a
  fresh instance is needed) works fine - no singleton enforcement. Used repeatedly this session to
  work around HWiNFO startup-timing races when verifying UI state.
- **HWiNFO needs a few real seconds after `Start-Process` before its shared memory is actually
  live** - launching the app immediately after starting HWiNFO (e.g. 2-3s gap) can catch it before
  `Global\HWiNFO_SENS_SM2` exists, producing a real (if temporary) "HWiNFO isn't reachable" state
  in the app. Wait and positively confirm the shared-memory check succeeds (see `dev-menu.ps1`'s
  `Test-HwInfoSharedMemoryActive` or just retry the raw `MemoryMappedFile.OpenExisting` check)
  before launching the app, not just a fixed sleep.

## If picking this up next: recommended first checks

1. `git log --oneline -5` and `git status` in the client repo - confirm nothing's changed since
   this note (the state described above should still hold).
2. `dotnet build LuxtronicPCTools.sln && dotnet test LuxtronicPCTools.sln` - confirm 119/119 still
   holds.
3. Decide with the user: commit the uncommitted RAM/TM5 work (and separately, the small
   `tools/hwi/README.md`) now, or continue verifying first (item 5 above - the external-duration-
   kill path - is the one real gap left).
4. Update the three "not confirmed" spots (`TM5Runner.cs` class remarks,
   `TestSessionController.RunRamTestSessionAsync`'s remarks, `tools/TestMem5/README.md`) to reflect
   that the `Log.txt` file-lock behavior is now confirmed, not just inferred (see item 2 above) -
   small doc-only change, safe to do without further live testing.
5. If continuing RAM verification: remember to edit the **deployed** server's
   `config/default.json` (`192.168.68.255`, not the local checkout) for a short test duration, and
   revert it afterward - same as the GPU pass's lesson.
