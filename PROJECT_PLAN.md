# Luxtronic PCTools — Project Plan

Status: **planning complete, no code written yet.** This is the shared understanding reached via interview — confirm before implementation starts.

## 1. What this is

A two-part system for hardware testing on Windows PCs (new builds and customer repairs):

- **PC-side app** — runs on the machine being tested (your shop's LAN, both newly built PCs and customer PCs brought in for repair). Technician-operated only; customers never touch it.
- **Server-side service** — runs on your existing LAN box (`192.168.68.255`), receives live + final test data, stores it, and serves a web dashboard for reviewing and exporting reports.

Target OS: Windows 11 (primary, 95%), Windows 10 (secondary, 5%).

## 2. Components tested

| Component | Method | Notes |
|---|---|---|
| CPU | **Prime95** Torture Test | freeware, scriptable headless (`-t` + `prime.txt`) |
| GPU | **FurMark** (free version) | internal-use only, not redistributed/sold — acceptable per free license |
| RAM | **TestMem5 (TM5)** | free, config-based, runs in Windows (no reboot) |
| SSD | **CrystalDiskMark** (benchmark) + **CrystalDiskInfo/SMART** (health) | MIT license, no destructive writes — customer data safety |
| Sensors (all) | **LibreHardwareMonitorLib** | temps, fan RPM, load %, component serial numbers; requires admin rights |

Dropped from current workflow: Cinebench R23 (optional secondary benchmark score only, not the stress mechanism), MemTest86 bootable, HDTune Pro/WinPE — all replaced with in-Windows equivalents so nothing requires a reboot into bootable media.

**Note on FurMark licensing:** the free license text excludes "commercial applications." You've decided this is acceptable since the tool isn't shipped/sold, only used internally. Worth revisiting if usage ever expands (e.g. reselling the tool, or an audit concern) — Geeks3D sells a commercial PRO Pack if that changes.

## 3. Test orchestration rules

- CPU and GPU may run **together** (combined thermal/power load) or **individually**.
- RAM and SSD always run **exclusively** — never concurrently with each other or with CPU/GPU.
- Technician selects which test(s) to run per session on the PC app.
- Both **burn-in** (new build QC) and **diagnostic** (customer-reported issue) use the same test engine — just different presets/durations/thresholds pulled from server config.

## 4. PC-side app

- **Stack:** C#/.NET, WPF, single self-contained executable.
- **UI:** minimal — enter/confirm PC identifier (motherboard serial, auto-read), customer name, new-build vs. repair flag, optional notes; checkboxes for which test(s) to run; Start/Stop; a bare "test running, do not power off" indicator. **No results shown locally** — all review happens on the dashboard.
- **Behavior:** fetches run config (thresholds, durations, FFT ranges, etc.) from the server at session start, executes the selected test(s), streams live sensor + tool telemetry to the server continuously, does **not** make pass/fail decisions itself — that's server-side, against server config.
- **Tool binaries:** Prime95, FurMark, TM5, and CrystalDiskMark ship bundled inside the client's install folder with pinned versions — not pre-installed on target PCs, not downloaded at runtime. Keeps every test run reproducible and working offline.
- **Identification:** motherboard serial number as primary PC identity; SSD serial and other available component serials logged alongside.
- **Auth:** small API key file per technician (you + one other currently), sent with every request — lets the server attribute each session to the technician who ran it.

## 5. Server-side service

- **Stack:** Node.js/Express (or Fastify), PostgreSQL — matches your existing pm2-managed LAN service pattern (same deploy workflow as `luxtronic-portal` etc. via git pull + pm2 restart).
- **Host:** `192.168.68.255:7777` — single port handles REST ingestion, live telemetry (WebSocket), and the dashboard, no need for multiple ports.
- **Database:** PostgreSQL on the same box, port `5434` (that server already runs other Postgres instances on `5432`/`5433` for the other Luxtronic services — this one gets its own instance/port rather than sharing).
- **Network:** LAN-only for v1.
- **Responsibilities:**
  - Ingest and store all raw telemetry (time-series: temps, fan RPM, load, tool output) per session.
  - Evaluate results against configurable thresholds (temp limits, error counts, duration) — pass/fail/flagged lives here, not on the PC.
  - Serve a **web dashboard** (mobile- and browser-friendly) showing:
    - **Live view** of a test in progress (charts update as data streams in — lets you catch a dangerous spike and abort early).
    - Historical sessions, searchable/filterable by PC identifier, customer, date, technician.
    - **PDF export** per session — charts (temp/load over time) + summary tables, styled similarly to your existing GPU-bench reports.
  - **Config:** thresholds/durations/test parameters live in a config file (YAML/JSON) on the server for v1 — no admin UI yet, given it's a 2-person team who can edit a file directly.
- **Auth:**
  - PC → server: API key per technician (see above).
  - Dashboard: **no login** — network is isolated/trusted, so LAN access alone is the boundary for v1.

## 6. Data model (high level)

- `sessions` — id, pc_identifier (mobo serial), customer_name, session_type (new-build/repair), technician (from API key), started_at, ended_at, ssd_serial(s), other component serials.
- `test_runs` — id, session_id, component (cpu/gpu/ram/ssd), config used (thresholds/duration), result (pass/fail/flagged), started_at, ended_at.
- `telemetry` — time-series: test_run_id, timestamp, sensor name, value (temp/fan RPM/load/etc.).
- `tool_output` — raw output/logs from Prime95/FurMark/TM5/CrystalDiskMark per test_run, for audit/debugging.

## 7. Explicitly out of scope for v1 (backlog)

- Internet/remote access (LAN-only for now).
- Integration with `luxtronic-service-form` / `luxtronic-quotation-form` (a free-text `ticket_ref` field is left open for future linking).
- Admin UI for editing thresholds (config file only for now).
- Dashboard login/accounts (revisit if network trust boundary changes).
- Destructive SSD write-stress testing (would need explicit per-job consent design).
- Code signing / installer polish for the PC-side exe (functional distribution first).

## 8. Open engineering notes (not decisions, just facts to keep in mind)

- LibreHardwareMonitorLib requires the PC app to run elevated (admin) — expected, since technicians are already doing hardware work.
- Not all components reliably expose a serial number via standard sensor APIs (e.g., some GPUs) — log what's available, don't block on missing serials.
- Third-party tool licenses (Prime95, FurMark, TM5, CrystalDiskMark) should be re-checked if this tool's usage ever expands beyond internal use.
- **Risk to validate early:** LibreHardwareMonitorLib's sensor access depends on a kernel driver (WinRing0), which can silently fail to load on Windows 11 machines with Secure Boot + memory integrity (HVCI) enabled — sensors just return nothing rather than an obvious error. Since this runs on arbitrary customer PCs with unknown security settings, this needs to be confirmed working on a real machine as part of the first build, not assumed. This is the main reason the build order below front-loads a single end-to-end slice before parallelizing.

## 9. Repo layout & build order

- **Two separate repos**: `Luxtronic-PCTools-Client` (the WPF app) and `Luxtronic-PCTools-Server` (the Node/Postgres service), both under `C:\Users\Admin\Documents\Github`, already `git init`'d. This matches the existing convention where `lan-portal-deploy` git-pulls and pm2-restarts each service by its own repo name — the server plugs straight into that workflow unchanged.
- **Build order: walking skeleton first.** Before building all four test-tool wrappers and the full dashboard, build one complete vertical slice end-to-end for CPU only: client reads sensors + runs Prime95 → streams telemetry to the server over the CONTRACT.md WebSocket → server stores it and evaluates pass/fail → dashboard shows the live view and exports a PDF. This proves the sensor-driver risk above, and proves the whole contract works, while it's still cheap to fix. GPU/RAM/SSD wrappers and dashboard polish are added afterward, in parallel, once the skeleton works on a real machine.
