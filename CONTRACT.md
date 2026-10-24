# API / Data Contract

This is the shared interface both the PC-side client and the server must build against. Written before either side, so both tracks can be implemented in parallel without touching each other's code. See [PROJECT_PLAN.md](PROJECT_PLAN.md) for the decisions this derives from.

## 1. Auth

- Every PC-client request carries `X-Api-Key: <key>`. One static key per technician.
- Server stores only a hash of each key (`technicians.api_key_hash`), never the raw value.
- Dashboard/browser endpoints: **no auth** (trusted LAN, per plan §5).
- WebSocket connections from the PC client authenticate the same way, via `X-Api-Key` on the upgrade request (or `?api_key=` query param if the client library can't set headers on a WS handshake — client implementer's choice, server must accept either).

## 2. Database schema (PostgreSQL)

```sql
CREATE TABLE technicians (
  id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name            TEXT NOT NULL,
  api_key_hash    TEXT NOT NULL UNIQUE,
  active          BOOLEAN NOT NULL DEFAULT TRUE,
  created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE machines (
  mobo_serial     TEXT PRIMARY KEY,
  first_seen_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
  last_seen_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TYPE session_type AS ENUM ('new_build', 'repair');

CREATE TABLE sessions (
  id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  mobo_serial     TEXT NOT NULL REFERENCES machines(mobo_serial),
  technician_id   UUID NOT NULL REFERENCES technicians(id),
  customer_name   TEXT,
  session_type    session_type NOT NULL,
  notes           TEXT,
  ssd_serials     JSONB NOT NULL DEFAULT '[]',   -- ["serial1", "serial2", ...]
  other_serials   JSONB NOT NULL DEFAULT '{}',   -- { "gpu": "...", ... } best-effort, may be empty
  system_info     JSONB,                         -- PC specification for the report; added by migrations/003 (see §4)
  started_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
  ended_at        TIMESTAMPTZ
);

CREATE TYPE component AS ENUM ('cpu', 'gpu', 'ram', 'ssd');   -- 'vram' added by migrations/002_vram_component.sql
CREATE TYPE test_result AS ENUM ('pass', 'fail', 'flagged', 'aborted');

CREATE TABLE test_runs (
  id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id      UUID NOT NULL REFERENCES sessions(id),
  component       component NOT NULL,
  config_snapshot JSONB NOT NULL,       -- the config.json subtree used for this run, frozen at start time
  started_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
  ended_at        TIMESTAMPTZ,
  tool_exit_code  INTEGER,
  tool_output_raw TEXT,                 -- raw stdout/log from Prime95/FurMark/TM5/CrystalDiskMark
  summary_stats   JSONB,                -- e.g. { "max_temp_c": 87, "error_count": 0, "avg_load_pct": 99 }
  result          test_result,
  stop_reason     TEXT                  -- null on a normal finish; see §7 for the non-null values
);

CREATE TABLE telemetry (
  id              BIGSERIAL PRIMARY KEY,
  test_run_id     UUID NOT NULL REFERENCES test_runs(id),
  ts              TIMESTAMPTZ NOT NULL,
  sensor_name     TEXT NOT NULL,        -- e.g. "cpu_package_temp_c", "gpu_fan_rpm"
  value           DOUBLE PRECISION NOT NULL
);
CREATE INDEX idx_telemetry_test_run_ts ON telemetry (test_run_id, ts);
```

Only one row per PC in `machines` — `sessions` accumulate over time against the same `mobo_serial`, giving you history per physical machine across repeat visits.

## 3. Server config file (thresholds/durations)

A JSON file on the server (v1 — no admin UI, per plan). Client fetches the relevant subtree at session start; server re-reads the same file when computing `result` on test-run completion, so both sides evaluate against the same numbers.

```jsonc
{
  "cpu": {
    "tool": "prime95",
    "mode": "blend",              // or "small_fft" for max heat
    "duration_minutes": 60,
    "max_temp_c": 95
  },
  "gpu": {
    "tool": "furmark",
    "duration_minutes": 20,
    "max_temp_c": 90
  },
  "vram": {                         // added with the VRAM test (memtest_vulkan); optional in the file
    "tool": "memtest_vulkan",
    "max_temp_c": 90
  },
  "ram": {
    "tool": "tm5",
    "config_profile": "anta777-extreme",
    "duration_minutes": 60,
    "max_errors": 0
  },
  "ssd": {
    "tool": "crystaldiskmark",
    "min_seq_read_mb_s": 400,          // CrystalDiskMark benchmark
    "min_seq_write_mb_s": 300,         // CrystalDiskMark benchmark
    "max_smart_temp_c": 70,            // SMART, both bus types
    "max_smart_reallocated_sectors": 0,        // SMART, ATA/SATA drives only
    "max_smart_percentage_used": 90,           // SMART, NVMe drives only
    "min_smart_available_spare_percent": 10,   // SMART, NVMe drives only
    "max_smart_media_errors": 0                // SMART, NVMe drives only
  },
  "concurrency": {
    "cpu_gpu_together_allowed": true,
    "exclusive_components": ["ram", "ssd"]
  }
}
```

**Durations, CPU mode and VRAM device are now chosen on the PC, not by this file.** The v2 client has its own Settings page: per-test run lengths (defaults 10 min CPU, 10 min GPU, 10 min VRAM, 30 min RAM), a CPU load level (Prime95 blend / small FFT / blend with AVX off) and the VRAM test's GPU number. It overrides `duration_minutes` and `mode` from this file when it fetches config, so those two keys are now only fallback defaults for older clients. Thresholds (`max_*` / `min_*`) are still read from here, by the server, when it computes `result`. The settings a run really used are sent with its completion as `run_settings` (§4) and merged into that run's `config_snapshot`, so the stored record shows the real duration instead of this file's. The `vram` section has no `duration_minutes`: the client never reads one.

**SSD SMART fields are bus-type-specific, not universal.** ATA/SATA and NVMe drives expose entirely different SMART data under the *same* attribute IDs (ATA attribute 5 = "Reallocated Sectors Count", NVMe attribute 5 = "Percentage Used" — unrelated metrics, same number). A single drive is only ever one bus type, so the client should only populate whichever `summary_stats` keys apply to the drive under test — per §7, a config threshold with no matching `summary_stats` key is simply skipped, which is what makes it safe to list both bus types' thresholds here at once. This replaces an earlier version of this config (`smart_reallocated_sectors_max`, "max" as a *suffix*) that had a naming bug in the server's threshold-matching implementation making it silently never evaluate, ever — every threshold key here must *start with* `max_`/`min_` for the server to pick it up (see `resultComputation.js` for the exact matching rule).

## 4. REST endpoints

### PC client (auth required)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/config` | Fetch current thresholds/durations JSON (§3) |
| POST | `/api/sessions` | Create a session. Body: `{ mobo_serial, customer_name?, session_type, notes?, ssd_serials?, other_serials?, system_info? }` → `{ session_id }`. `system_info` is the PC's specification so the report shows what machine was tested (object under 64KB, every key optional): `cpu {name, cores, threads, base_clock_mhz}`, `gpu [{name, vram_gb, driver_version}]`, `memory {total_gb, modules [{slot, size_gb, type, speed_mhz, manufacturer, part_number}]}`, `motherboard {name, serial, bios_version, bios_date}`, `storage [{model, serial, firmware, capacity_gb, bus, health_percent, power_on_hours, power_on_count, host_reads_gb, host_writes_gb}]`, `os {name, version, architecture}`. Stored in `sessions.system_info`; shown on the session page and at the top of the PDF report (formatted by `src/lib/systemInfo.js`) |
| POST | `/api/sessions/:id/test-runs` | Start a test run. Body: `{ component }` → `201 { test_run_id }`, or `409 { error: "exclusive_conflict", active_component }` if the concurrency rule (§6) rejects it |
| PATCH | `/api/sessions/:id/test-runs/:test_run_id` | Complete a test run. Body: `{ tool_exit_code, tool_output_raw, summary_stats, stop_reason?, run_settings? }` → server computes and returns `{ result }` (§7). `run_settings` is an optional object (under 4KB) of what the client actually ran with, e.g. `{ duration_minutes: 10, mode: "blend", avx_disabled: false }` for CPU, `{ duration_minutes, device_index }` for VRAM; it is merged over the run's `config_snapshot` (`config_snapshot \|\| run_settings`). Servers/clients that predate it simply ignore it / don't send it |
| PATCH | `/api/sessions/:id` | End session. Body: `{}` → sets `ended_at`; also auto-closes (§7) any test_run still open in this session |

### Dashboard (no auth)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/technicians` | List technicians (`id`, `name`, `active`, `created_at` — never `api_key_hash`) |
| POST | `/api/technicians` | Provision a technician. Body: `{ name }` → `201 { id, name, api_key, key_file_path, overwrote_existing_file }` — `api_key` is the raw key, shown **once** (same rule as CONTRACT.md §1 — only its hash is stored after this response) |
| PATCH | `/api/technicians/:id` | Body: `{ active: boolean }` → revoke/reactivate. A `false` takes effect on that technician's very next authenticated request (§1) |
| DELETE | `/api/technicians/:id` | Permanently remove a technician. `409 { error: "has_sessions", session_count }` if any session references them — `sessions.technician_id` has no `ON DELETE CASCADE`, so this only ever succeeds for a technician with zero session history (e.g. a just-created duplicate/typo). Use `PATCH .../active=false` instead to revoke someone with real history |
| GET | `/api/sessions?mobo_serial=&customer_name=&from=&to=&technician_id=` | List/search sessions |
| GET | `/api/sessions/:id` | Session detail incl. all test_runs |
| GET | `/api/sessions/:id/telemetry?test_run_id=` | Historical telemetry for charts on a completed session |
| GET | `/api/sessions/:id/report.pdf` | Generate/download PDF report |
| POST | `/api/sessions/:id/test-runs/:test_run_id/stop` | Force-close one stuck test_run. `409 { error: "already_completed" }` if it already has an `ended_at`, `404` if not found. On success: `{ result: "aborted", session_ended_at }` — sets `result: "aborted"`, `stop_reason: "manual_stop"` (§7), and also closes the session if this was its last open test_run |
| POST | `/api/sessions/:id/end` | Force-close the whole session: closes every still-open test_run in it (same as `.../stop`, looped) AND unconditionally sets the session's `ended_at` if unset — even if nothing was open. `404` if the session doesn't exist. On success: `{ session_id, ended_at, stopped_test_run_ids }` |

The four technician endpoints are also not in the original table — added because there was previously no way to provision or revoke a technician except shell access to the server box (`scripts/create-technician.js`), and no way for the dashboard to even list them (a gap this same document used to flag under "no technicians-list endpoint"). No auth on these either, same as the rest of this table — anyone who can reach the dashboard can mint a new technician API key, which is consistent with the whole dashboard's LAN-trust model (PROJECT_PLAN.md), not a new trust boundary.

`.../stop` and `.../end` were added after the initial pass, not in the original table: every other way to close a test_run or a session (normal completion, the session-end auto-close) requires the PC client's API key, because normally it's the client reporting its own outcome. If that client crashes or loses network before calling any of those, nothing ever closes what it owns — the dashboard has no API key and had no way to intervene, so a "running" or "in progress" state could persist indefinitely. These give the dashboard its own escape hatch, deliberately with a distinct `stop_reason` (`manual_stop`) so it's clear in the data that this wasn't the client's own doing. `.../stop` is fine-grained (one test_run — useful when only one of several concurrently-running components is actually stuck); `.../end` is the blunt "just close this session" action, and is also the only one of the two that can fix a session stuck open with *zero* running test_runs (e.g. every test_run finished normally but the client crashed before its own end-session call) — `.../stop` has nothing to act on in that case since there's no open test_run to target.

## 5. WebSocket protocol

Two separate connection types — a client firehose in, a dashboard fan-out out. The server is the only thing that talks to both.

**`/ws/telemetry`** (PC client → server, authenticated)
Client sends one message per sensor sample:
```json
{ "test_run_id": "uuid", "ts": "2026-08-08T12:00:00.000Z", "sensor_name": "cpu_package_temp_c", "value": 78.4 }
```
Server persists each to `telemetry` and relays it to any dashboard subscribed to that session (see below). Default sample rate: ~1/sec per sensor — not enforced by the protocol, just the expected client behavior.

**`/ws/live?session_id=uuid`** (dashboard → server, no auth)
Server pushes, for the lifetime of the connection:
```json
{ "type": "telemetry", "test_run_id": "uuid", "ts": "...", "sensor_name": "...", "value": 78.4 }
{ "type": "test_run_status", "test_run_id": "uuid", "component": "cpu", "status": "started" | "completed", "result": "pass" | null }
```

## 6. Concurrency validation rule (server-enforced, on `POST .../test-runs`)

Given the active (`ended_at IS NULL`) test_runs already in this session:

- Requested component is `ram` or `ssd` → reject (`409`) if **any** active test_run exists.
- Requested component is `cpu`, `gpu` or `vram` → reject (`409`) if any active test_run is `ram` or `ssd`, or if a test_run for that same component is already active. Otherwise allowed (so `cpu` then `gpu` while `cpu` is still running is valid — that's the "together" mode).
- `gpu` and `vram` additionally reject each other: both load the same graphics card (FurMark vs memtest_vulkan), so they may run alongside `cpu` but never at the same time as one another.

The v2 client runs several tests **one after another inside a single session** (the Run Tests page): one `POST /api/sessions`, then for each test a `POST .../test-runs` → run → `PATCH .../test-runs/:id`, then one `PATCH /api/sessions/:id` at the end. Each test finishes before the next starts, so this never trips §6. Its per-test tabs run locally and send nothing at all.

This is the one piece of business logic that must match exactly between what the client expects and what the server enforces — implementers on both tracks should treat §6 as canonical, not re-derive it.

## 7. Result computation (server-side, on test-run completion)

Client submits raw `tool_exit_code`, `tool_output_raw`, and `summary_stats` (pre-parsed key metrics — max temp, error count, throughput, etc; exact keys per component are the parsing wrapper's responsibility on the client side), plus an optional `stop_reason`. For `vram` the client reports `error_count` (each "Error found" report from memtest_vulkan) and `max_temp_c` (GPU core temperature); telemetry uses the same `gpu_*` sensor names as the GPU test.

**If `stop_reason` is absent/null** — this was a normal finish. Server compares `summary_stats` against the matching §3 config subtree:

- `fail` — tool reported errors (`error_count > 0`) or a hard limit was exceeded (e.g. `max_temp_c` breached, `min_seq_read_mb_s` not met).
- `flagged` — no hard rule breached but borderline (e.g. within 5% of a threshold) — worth a human look on the dashboard.
- `pass` — otherwise.

**If `stop_reason` is present** — `result` is set to `aborted` unconditionally; `summary_stats` is still stored (whatever partial data exists) but is never compared against thresholds, since a partial run can't be fairly judged against a full-run limit. Valid values, client sets whichever applies:

| `stop_reason` | Meaning |
|---|---|
| `user_abort` | Technician clicked Stop before the test finished |
| `tool_crash` | The wrapped tool (Prime95/FurMark/TM5/CrystalDiskMark) exited unexpectedly / non-zero outside a normal failure path |
| `client_error` | The client app itself hit an error unrelated to the hardware under test (e.g. sensor read failure) |
| `manual_stop` | Set server-side (not by the client) when a technician force-stops a test_run from the dashboard via `POST .../stop` (§4) — used when the owning client is unreachable and never reports anything itself |
| `session_ended_early` | Set by the session-end auto-close below |

**Session-end auto-close**: if `PATCH /api/sessions/:id` is called while a test_run in that session still has `ended_at IS NULL` (technician closed the app, or it crashed, without a clean test-run completion call), the server closes it itself: sets `ended_at = now()`, `result = 'aborted'`, `stop_reason = 'session_ended_early'`. This guarantees no test_run is left open **as long as something eventually calls `PATCH /api/sessions/:id`** — if the client itself is what's gone (crashed, network-dead, force-quit) and nothing ever calls that either, this auto-close never fires, which is exactly the gap `POST .../stop` (§4) exists to cover from the dashboard side instead.
