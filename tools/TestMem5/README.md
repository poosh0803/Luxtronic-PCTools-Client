# TestMem5 / TM5 (drop-in, manual download required)

This folder is intentionally empty of any actual TM5 binary in the committed repo (a real install
may exist here locally, git-ignored). The client's `TM5Runner`
(`src/Luxtronic.PCTools/Services/TM5Runner.cs`) looks for the tool at:

```
tools/TestMem5/TM5.exe
```

(relative to `appsettings.json`'s `RamToolsDirectory` setting, which defaults to
`tools\TestMem5` resolved next to the built exe - same dev-mode "walk up from build output"
fallback `ToolsDirectory`/`GpuToolsDirectory` already use).

## What to do

1. Download TestMem5 manually: https://github.com/CoolCmd/TestMem5 (or a trusted mirror - the
   project distributes the tool itself plus community-authored `.cfg` profiles, commonly bundled
   together as a "TM5 with configs" pack; ground-truthed against v0.13.1).
2. Extract the full contents directly into this folder, so the layout is:
   ```
   tools/TestMem5/TM5.exe
   tools/TestMem5/TM5.dll
   tools/TestMem5/bin/*.cfg          (the profile files - keep all of them, not just one)
   tools/TestMem5/bin/MT0.dll
   ```
3. That's it - no code changes needed, and no need to manually select a profile through TM5's own
   UI. `TM5Runner` picks and activates one automatically before every run (see below).

## Why this isn't automated

TM5/its bundled `.cfg` profiles are third-party. Bundling a pinned version is a decision for a
human, not something an agent should auto-fetch from the internet - same reasoning as
`tools/prime95/README.md` and `tools/FurMark_win64/README.md`.

## How config selection actually works (read this before touching TM5Runner/RamProfileSelector)

This is **not** a documented mechanism - it was reverse-engineered through direct, real testing
(see `TM5Runner`'s and `RamProfileSelector`'s class remarks for the full detail), because no CLI
flag or documented config-selection API was found:

- TM5 looks for a **hardcoded filename**, `bin\Universal 2 @ LMhz.cfg`, every time it launches -
  confirmed by removing that exact file and getting `"The bin\Universal 2 @ LMhz.cfg
  configuration file was not found"`. It is **not** a "remembered last config" (TM5's own
  `TM5.ini` only stores window position) and **not** a specially-recognized name like `MT.cfg`
  some forum posts claim - that was tried directly and didn't work.
- `TM5Runner` copies whichever real profile `RamProfileSelector` picked onto that exact path
  before each run. Confirmed working by overwriting it with a different profile's content and
  seeing that profile's actual parameters run (a 31-step `Test Sequence` matched exactly) - even
  though TM5's on-screen "Configuration" label stayed cosmetically tied to the old filename text
  and did **not** update to reflect the copied-in content. **Don't trust that on-screen label for
  anything** - it's not reading the file's `Config Name=` field live.
- If you ever see TM5 running an unexpected profile, check `TM5Runner`'s log line (`"Selected RAM
  test profile: ..."`) rather than the app's own window - that's the authoritative record of what
  was actually requested.

## Confirmed: `Log.txt` is locked exclusively by TM5 for the whole run

`Log.txt` (written at this folder's root, not `bin/`) does **not** update incrementally during a
run - it's written once near the start and then TM5 holds it open exclusively until the process
exits. Confirmed directly during real testing: the app's status log repeated `"RAM log poll failed
this cycle: The process cannot access the file '...\Log.txt' because it is being used by another
process."` on every poll after the first, for the entire run. Practical effect:
`TestSessionController.RunRamTestSessionAsync`'s live `RamStatusText` readout gets exactly one
successful read - whatever the file contained at the instant polling started, which can even be
**stale data from the previous session** if TM5 hasn't opened/written its own new session's line
yet - and then stays frozen at that value for the rest of the run. **This does not affect
correctness of what's sent to the server** - `TM5Runner.RunAsync`'s own post-exit read happens
after TM5 has released the lock, so `summary_stats`/final telemetry are unaffected. Only the live
in-progress readout is unreliable - a real UX gap, not a functional bug, and not worth fixing by
adding retry/backoff complexity to the poll loop for a cosmetic readout.

## What's still NOT confirmed (flagged honestly, not assumed)

- What a real *detected memory error* looks like in `Log.txt` - no error occurred in any real test
  run done while building this wrapper, so `TM5Runner.CountErrors`'s pattern is a best guess (it
  is at least confirmed NOT to false-positive on TM5's own live `"Errors: 0"`-style healthy
  summary field).
- What a natural full-cycle completion (all of a profile's configured `Cycles` finishing before
  the app's own external duration limit) looks like in the log - every real test run done so far
  was stopped early by a technician, on purpose, since real RAM stress testing carries genuine
  risk on marginal hardware and wasn't left running unattended.
- **The external-duration-kill path itself** (`TM5Runner.RunAsync`'s `durationTask` branch -
  mirrors `Prime95Runner`'s "duration elapsed → kill → normal finish" logic) has never actually
  fired and been observed - every real run was stopped manually well before its configured
  duration elapsed. This is the single most important remaining thing to verify: start a run with
  a short configured duration and confirm the process is actually killed at the right time with
  `stop_reason: null` (not `tool_crash`).

If you're extending this wrapper and can safely verify any of the above, please update this file
and the class remarks with what you find.
