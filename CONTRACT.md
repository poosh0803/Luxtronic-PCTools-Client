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
  started_at      TIMESTAMPTZ NOT NULL DEFAULT now(),
  ended_at        TIMESTAMPTZ
);

CREATE TYPE component AS ENUM ('cpu', 'gpu', 'ram', 'ssd');
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
  "ram": {
    "tool": "tm5",
    "config_profile": "anta777-extreme",
    "duration_minutes": 60,
    "max_errors": 0
  },
  "ssd": {
    "tool": "crystaldiskmark",
    "min_seq_read_mb_s": 400,
    "min_seq_write_mb_s": 300,
    "smart_reallocated_sectors_max": 0
  },
  "concurrency": {
    "cpu_gpu_together_allowed": true,
    "exclusive_components": ["ram", "ssd"]
  }
}
```

## 4. REST endpoints

### PC client (auth required)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/config` | Fetch current thresholds/durations JSON (§3) |
| POST | `/api/sessions` | Create a session. Body: `{ mobo_serial, customer_name?, session_type, notes?, ssd_serials?, other_serials? }` → `{ session_id }` |
| POST | `/api/sessions/:id/test-runs` | Start a test run. Body: `{ component }` → `201 { test_run_id }`, or `409 { error: "exclusive_conflict", active_component }` if the concurrency rule (§6) rejects it |
| PATCH | `/api/sessions/:id/test-runs/:test_run_id` | Complete a test run. Body: `{ tool_exit_code, tool_output_raw, summary_stats, stop_reason? }` → server computes and returns `{ result }` (§7) |
| PATCH | `/api/sessions/:id` | End session. Body: `{}` → sets `ended_at`; also auto-closes (§7) any test_run still open in this session |

### Dashboard (no auth)

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/sessions?mobo_serial=&customer_name=&from=&to=&technician_id=` | List/search sessions |
| GET | `/api/sessions/:id` | Session detail incl. all test_runs |
| GET | `/api/sessions/:id/telemetry?test_run_id=` | Historical telemetry for charts on a completed session |
| GET | `/api/sessions/:id/report.pdf` | Generate/download PDF report |

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
- Requested component is `cpu` or `gpu` → reject (`409`) if any active test_run is `ram` or `ssd`, or if a test_run for that same component is already active. Otherwise allowed (so `cpu` then `gpu` while `cpu` is still running is valid — that's the "together" mode).

This is the one piece of business logic that must match exactly between what the client expects and what the server enforces — implementers on both tracks should treat §6 as canonical, not re-derive it.

## 7. Result computation (server-side, on test-run completion)

Client submits raw `tool_exit_code`, `tool_output_raw`, and `summary_stats` (pre-parsed key metrics — max temp, error count, throughput, etc; exact keys per component are the parsing wrapper's responsibility on the client side), plus an optional `stop_reason`.

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

**Session-end auto-close**: if `PATCH /api/sessions/:id` is called while a test_run in that session still has `ended_at IS NULL` (technician closed the app, or it crashed, without a clean test-run completion call), the server closes it itself: sets `ended_at = now()`, `result = 'aborted'`, `stop_reason = 'session_ended_early'`. This guarantees no test_run is ever left permanently open.
