# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project status

This is the `Luxtronic-PCTools-Client` repo: the **rebuilt (v2) WPF client** in `src/Luxtronic.PCTools/`
(.NET 10, WPF-UI, MVVM + DI + Serilog, one self-contained exe with the test tools embedded). The previous
client lives in `v1/` for reference only (its README.md / HANDOFF.md are legacy; its 119 xUnit tests have
not been ported, so root `tests/` is empty).

Read [README.md](README.md) first (features, build/publish, tool pack, settings, known gaps and the repo
layout). [PROJECT_PLAN.md](PROJECT_PLAN.md) is the source of truth for architecture/scope (its last section
records what changed since it was written) and [CONTRACT.md](CONTRACT.md) is the API/DB/config contract
shared with the server repo - the server's copy is canonical and the two files must stay identical.

## Commands

```powershell
dotnet build LuxtronicPCTools.sln     # needs the .NET 10 SDK
publish.bat                           # single-file exe -> publish\win-x64\ (close the running app first)
```

No automated tests exist yet. Run the exe elevated (it requests admin itself); the **Offline mode** switch in
Settings lets you exercise the whole UI without a server.

## What this project is

Luxtronic PCTools: a two-part hardware testing system in **separate git repos**:

- **`Luxtronic-PCTools-Client`** (this repo) - runs on Windows 11/10 machines under test (new builds and
  customer repairs), operated by a technician. Runs Prime95 (CPU), FurMark (GPU), memtest_vulkan (VRAM),
  TestMem5 (RAM) and DiskSpd (SSD) as external processes unpacked from the exe's embedded tool pack, reads
  sensors from HWiNFO shared memory (+ LibreHardwareMonitor for SSD SMART), and streams to the server. It does
  **not** decide pass/fail.
- **`Luxtronic-PCTools-Server`** (`C:\Users\Admin\Documents\Github\Luxtronic-PCTools-Server`) - Node.js/Express +
  PostgreSQL (port `5434`), pm2 on the LAN box `192.168.68.255:7777`. Ingests telemetry, evaluates thresholds,
  serves the no-login dashboard and PDF reports.

## Apps / Drivers / Tools

The sidebar has Apps, Drivers and Tools tabs that download installers the server hosts (CONTRACT.md section 4,
"Apps, drivers and tools"; uploaded on the server's own pages). They share `UI/Controls/AppListView.xaml`, one
`AppListViewModel` base (Apps/Drivers/Tools subclasses differ only in category and wording) and
`Services/AppCatalogService.cs`. These endpoints need no API key. Downloads are checksum-verified and older
versions are deleted. Confirmation dialogs should use `Services/Shell/ThemedDialog`, not the classic MessageBox.
First start shows `UI/Windows/FirstRunWindow` (URL, API key, offline mode only).

## Architecture points that span both sides

- **Identity model**: a session is keyed by the PC's motherboard serial (read via WMI, never typed). Technicians
  are identified by a per-technician API key (stored in the client's `settings.json`).
- **Pass/fail lives on the server only.** The client fetches thresholds at session start. The exception is
  the *run parameters*: durations, CPU load mode/AVX and the VRAM GPU number are **local settings** that override
  the server config's values; the client reports what it really used in `run_settings` (POST test-run and PATCH
  completion) so the server/PDF show true values.
- **Two ways to run tests**: the per-test tabs (CPU/GPU/VRAM/RAM/SSD) run locally and submit nothing; **Run Tests**
  runs the ticked tests in series (fixed order CPU, GPU, VRAM, RAM, SSD) inside ONE server session and submits
  everything. Keep both paths behind the `TestSessionController` and the shared `AnyRunning` / `RunState` guard
  so two runs can never overlap.
- **Concurrency rule**: CPU and GPU may overlap in principle; GPU and VRAM never; RAM and SSD always exclusive.
  The client currently runs everything sequentially, which satisfies the rule.
- **`system_info`** (CPU, GPU, RAM modules, board/BIOS, drives, OS) is collected on the client and sent with the
  session so the report can show the PC spec.
- **No results on the PC side.** Single-test tabs show only a plain local "last run" outcome line, never
  pass/fail from a threshold. Review/export happens in the server dashboard.
- **Third-party tools are licensed for internal use only** (FurMark's free tier excludes commercial use and says
  "may not alter" - the bundle is trimmed). Don't redistribute or sell without re-checking licenses.

## Working notes

- Never overwrite or kill the user's running `Luxtronic.PCTools.exe`: publish to a side folder if it is running.
- The user's `Luxtronic.PCTools.exe - Shortcut.lnk` in the repo root is untracked and must stay out of git.
- Commit/push/deploy only when asked. After server-side changes, restart the local pm2 service.
