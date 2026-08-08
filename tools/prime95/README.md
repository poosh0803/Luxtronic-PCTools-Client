# Prime95 (drop-in, manual download required)

This folder is intentionally empty of any actual Prime95 binary. The client's `Prime95Runner`
(`src/Luxtronic.PCTools/Services/Prime95Runner.cs`) looks for the tool at:

```
tools/prime95/prime95.exe
```

(relative to `appsettings.json`'s `ToolsDirectory` setting, which defaults to `tools\prime95`
resolved next to the built exe - see `AppSettingsProvider.cs` for the exact resolution logic,
including a dev-mode fallback that walks up from the build output looking for this folder).

## What to do

1. Download Prime95 manually from the official site: https://www.mersenne.org/download/
   - Pick a specific pinned version (do not auto-update). Note the version number somewhere
     (e.g. this README, or the eventual installer/release notes) so test runs are reproducible.
2. Confirm you're OK with Prime95's license/terms for this internal use case (same spirit as
   the FurMark/TM5/CrystalDiskMark license notes in PROJECT_PLAN.md - re-check if usage ever
   expands beyond internal shop use).
3. Extract `prime95.exe` (and whatever else ships in the zip - DLLs etc. if any) directly into
   this folder, so the layout is:
   ```
   tools/prime95/prime95.exe
   tools/prime95/(any other files from the Prime95 zip)
   ```
4. That's it - no code changes needed. `Prime95Runner` will find it automatically next time
   the app starts.

## Why this isn't automated

Prime95 is a third-party redistributable binary. Bundling a pinned version and clearing its
license terms for redistribution is a decision for a human, not something an agent should
auto-fetch from the internet. See `CLAUDE.md` / the task instructions this repo was built
from for the reasoning.

## Config files the wrapper writes here at test-run time

`Prime95Runner` writes `prime.txt` and `local.txt` into this folder before each run, built
from the server's `cpu` config subtree (CONTRACT.md §3: `mode`, `duration_minutes`). It
writes both filenames because Prime95's torture-test settings have moved between `local.txt`
and `prime.txt` across versions - **this has not been validated against a real binary yet**.
Once you drop `prime95.exe` in here, the first real test run is also the first real test of
whether the generated config is being read correctly - watch for Prime95 falling back to
default/GUI-prompted settings instead of the configured FFT range and duration, and adjust
`WritePrimeConfig()` in `Prime95Runner.cs` if so.

`results.txt` (if Prime95 writes one here during a run) is read back and folded into the
`tool_output_raw` sent to the server, on a best-effort basis.
