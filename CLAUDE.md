# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project status

**Active development — real code exists here.** This is the `Luxtronic-PCTools-Client` repo
specifically (WPF app, `src/Luxtronic.PCTools/`), not the shared planning repo this file's
boilerplate below still describes. **Read [HANDOFF.md](HANDOFF.md) first** — it's a
session-by-session account of what's built, what's pending, and hard-won debugging context from
testing on real hardware (multiple technician PCs/laptops, not just this dev box) that isn't
written down anywhere else. [README.md](README.md) has full build/run/test/deploy instructions.
[PROJECT_PLAN.md](PROJECT_PLAN.md) is still the source-of-truth for architecture/scope decisions,
and [CONTRACT.md](CONTRACT.md) is the frozen API/DB/config contract shared with the server side —
treat it as canonical, not a suggestion.

Quick commands (see README.md for the full picture, `dev-menu.ps1` for an interactive menu
covering all of this plus API key / server URL / CPU duration setup):

```powershell
dotnet build LuxtronicPCTools.sln
dotnet test LuxtronicPCTools.sln
powershell -ExecutionPolicy Bypass -File .\dev-menu.ps1   # or double-click dev-menu.bat
```

## What this project is

Luxtronic PCTools: a two-part hardware testing system, split into two **separate git repos** (not subdirectories of this one — this repo is planning/contract docs only):

- **`Luxtronic-PCTools-Client`** (`C:\Users\Admin\Documents\Github\Luxtronic-PCTools-Client`, git-initialized, no commits yet) — C#/.NET WPF, runs on Windows 11/10 machines under test (new builds and customer repairs). Technician-operated, no customer-facing UI. Runs Prime95 (CPU), FurMark (GPU), TestMem5 (RAM), CrystalDiskMark/CrystalDiskInfo (SSD) as external processes bundled in its own install folder (pinned versions, no runtime download), reads sensors via LibreHardwareMonitorLib, and streams everything to the server. It does **not** decide pass/fail — that's server-side.
- **`Luxtronic-PCTools-Server`** (`C:\Users\Admin\Documents\Github\Luxtronic-PCTools-Server`, git-initialized, no commits yet) — Node.js/Express + PostgreSQL (own instance on port `5434`, separate from the other services' Postgres on `5432`/`5433`), deployed via the same pm2 pattern as the other Luxtronic LAN services (`lan-portal-deploy`: git pull + pm2 restart), listening on `192.168.68.255:7777`. Ingests telemetry, evaluates results against a config file of thresholds, and serves a live-updating web dashboard (no login — LAN-trust only) for reviewing sessions and exporting PDF reports.

**Build order**: a single CPU-only vertical slice end-to-end first (client → server → live dashboard view → PDF export), to prove the sensor driver works on real Windows 11 hardware and that CONTRACT.md holds up, before parallelizing the GPU/RAM/SSD wrappers. See PROJECT_PLAN.md §9 for why.

## Architecture points that span both sides

- **Identity model**: a test session is keyed by the PC's motherboard serial number (read on the client, not user-entered). Technicians are identified by a static API key file, one per technician — this key is how the server attributes a session to whoever ran it.
- **Config flows one direction**: from server to client. The client fetches thresholds/durations at session start and just executes; the server is the only place that holds/evaluates pass/fail logic. Don't add client-side pass/fail logic — it belongs server-side by design.
- **Test concurrency rule**: CPU and GPU tests may run simultaneously or individually; RAM and SSD tests must always run exclusively (never concurrently with anything else). This is a real constraint from test validity, not an arbitrary UI limitation — preserve it in any orchestration code.
- **No results on the PC side.** All review/export happens through the server's web dashboard. Don't add report-viewing or result UI to the PC-side client.
- **Third-party test tools are licensed for internal use only** (notably FurMark's free tier explicitly excludes commercial applications — acceptable here only because usage stays internal/unshipped). Do not bundle, redistribute, or sell any part of this tool without re-checking those licenses.
