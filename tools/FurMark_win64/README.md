# FurMark 2 (drop-in, manual download required)

This folder is intentionally empty of any actual FurMark binary in the committed repo (a real
install may exist here locally, git-ignored). The client's `FurMarkRunner`
(`src/Luxtronic.PCTools/Services/FurMarkRunner.cs`) looks for the tool at:

```
tools/FurMark_win64/furmark.exe
```

(relative to `appsettings.json`'s `GpuToolsDirectory` setting, which defaults to
`tools\FurMark_win64` resolved next to the built exe - see `AppSettingsProvider.cs` for the exact
resolution logic, including the same dev-mode "walk up from build output" fallback
`ToolsDirectory`/Prime95 already uses).

## What to do

1. Download FurMark 2 manually from the official site: https://www.geeks3d.com/furmark/
   - Pick a specific pinned version (do not auto-update). Note the version number somewhere
     (e.g. this README) so test runs are reproducible. Ground-truthed against FurMark 2.10.2.0.
2. Read `EULA.txt` once dropped in - FurMark 2's license is "free for any use (private or
   commercial)," notably more permissive than the FurMark 1-era license this project's planning
   docs originally assumed. Restrictions worth knowing: don't claim authorship, can't sell/rent
   it, don't modify the software or repackage it into a new installer.
3. Extract the full FurMark 2 zip contents (not just `furmark.exe` - it depends on several DLLs
   and resource files alongside it) directly into this folder, so the layout is:
   ```
   tools/FurMark_win64/furmark.exe
   tools/FurMark_win64/(everything else from the FurMark 2 zip)
   ```
4. That's it - no code changes needed. `FurMarkRunner` will find it automatically next time the
   app starts.

## Why this isn't automated

FurMark is a third-party redistributable binary. Bundling a pinned version is a decision for a
human, not something an agent should auto-fetch from the internet - same reasoning as
`tools/prime95/README.md`.

## What the wrapper runs

`FurMarkRunner` launches `furmark.exe --demo furmark-gl --max-time <seconds> --artifact-scanner`
- the classic OpenGL 3.2 stress-test demo, not the newer Vulkan (`furmark-vk`) or knot variants,
picked for broadest GPU/driver compatibility. `--max-time` is FurMark's own built-in duration
flag (confirmed via a real timed run: the process exits cleanly on its own when it elapses, no
external kill needed - unlike Prime95, which has no equivalent and gets killed externally by
`Prime95Runner`).

`--artifact-scanner` enables GPU rendering-corruption detection during the run - a real stability
signal beyond just temperature, counted toward `summary_stats.error_count`. **Not fully
ground-truthed**: the exact log line format when an artifact is actually detected has not been
observed (a short clean test run found none, as expected). See `FurMarkRunner`'s class remarks and
`FurMarkRunnerTests.cs` before trusting `error_count` on a GPU test that's ever come back
suspiciously always-zero - the detection regex may need adjusting against a real captured
detection.

## Files the wrapper reads here after each run

`_furmark_log.txt` (overwritten each run, same "read it back after the process exits" pattern
`Prime95Runner` uses for `results.txt`) is read and folded into `tool_output_raw` sent to the
server, on a best-effort basis. `_scores_maxtime.csv` (also written per-run) is deliberately not
parsed - redundant with this app's own HWiNFO-sourced GPU temperature telemetry, and not worth the
added parsing fragility.
