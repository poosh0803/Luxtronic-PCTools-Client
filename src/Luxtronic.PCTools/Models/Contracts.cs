using System.Text.Json.Serialization;

namespace Luxtronic.PCTools.Models;

// All types in this file are direct mirrors of the shapes specified in CONTRACT.md.
// Property names use [JsonPropertyName] to match the contract's snake_case exactly,
// rather than relying on a naming-policy convention, so a diff against CONTRACT.md
// is easy for whoever reconciles client/server later.

/// <summary>String constants for the "component" enum used throughout the contract.</summary>
public static class Component
{
    public const string Cpu = "cpu";
    public const string Gpu = "gpu";
    public const string Ram = "ram";
    public const string Ssd = "ssd";
}

/// <summary>String constants for the "session_type" enum (CONTRACT.md §2).</summary>
public static class SessionType
{
    public const string NewBuild = "new_build";
    public const string Repair = "repair";
}

/// <summary>String constants for "stop_reason" (CONTRACT.md §7).</summary>
public static class StopReason
{
    public const string UserAbort = "user_abort";
    public const string ToolCrash = "tool_crash";
    public const string ClientError = "client_error";
}

// ---- GET /api/config (CONTRACT.md §3) ----------------------------------------------------

/// <summary>Mirrors the server config JSON (CONTRACT.md §3) - "cpu", "gpu", "ram", "ssd", and
/// "concurrency" are all modeled.</summary>
public sealed class ServerConfig
{
    [JsonPropertyName("cpu")]
    public CpuConfig? Cpu { get; set; }

    [JsonPropertyName("gpu")]
    public GpuConfig? Gpu { get; set; }

    [JsonPropertyName("ram")]
    public RamConfig? Ram { get; set; }

    [JsonPropertyName("ssd")]
    public SsdConfig? Ssd { get; set; }

    [JsonPropertyName("concurrency")]
    public ConcurrencyConfig? Concurrency { get; set; }
}

public sealed class CpuConfig
{
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "prime95";

    /// <summary>"blend" or "small_fft" - see CONTRACT.md §3.</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "blend";

    [JsonPropertyName("duration_minutes")]
    public int DurationMinutes { get; set; } = 60;

    [JsonPropertyName("max_temp_c")]
    public double MaxTempC { get; set; } = 95;
}

/// <summary>No "mode" field, unlike CpuConfig - CONTRACT.md §3's gpu subtree is just tool/duration/
/// max_temp_c.</summary>
public sealed class GpuConfig
{
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "furmark";

    [JsonPropertyName("duration_minutes")]
    public int DurationMinutes { get; set; } = 20;

    [JsonPropertyName("max_temp_c")]
    public double MaxTempC { get; set; } = 90;
}

/// <summary>
/// config_profile is honored as-is only on DDR4 (or when platform/RAM-generation detection fails)
/// - on a detected DDR5 system, RamProfileSelector overrides it with a platform-specific profile
/// instead, since the shipped profile pack has no per-intensity DDR5 variants. See
/// RamProfileSelector's class remarks and README's "Judgment calls against CONTRACT.md".
/// </summary>
public sealed class RamConfig
{
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "tm5";

    [JsonPropertyName("config_profile")]
    public string ConfigProfile { get; set; } = "anta777-extreme";

    [JsonPropertyName("duration_minutes")]
    public int DurationMinutes { get; set; } = 60;

    [JsonPropertyName("max_errors")]
    public int MaxErrors { get; set; } = 0;
}

/// <summary>
/// No duration_minutes field, unlike Cpu/Gpu/Ram - CONTRACT.md §3's ssd subtree has no
/// duration/test-size concept at all (min_seq_read_mb_s/min_seq_write_mb_s are throughput
/// thresholds, not a run length). DiskSpdRunner's test file size and per-direction duration are
/// hardcoded client-side instead of server-configured - a deliberate scope decision (touching the
/// frozen CONTRACT.md to add fields wasn't warranted for a first pass) - see DiskSpdRunner's class
/// remarks.
///
/// Five SMART fields, not one - matches the server's ACTUAL config/default.json shape (confirmed
/// by reading Luxtronic-PCTools-Server directly), not this doc's older example. An earlier version
/// of this contract used a single `smart_reallocated_sectors_max` field ("max" as a suffix); the
/// server's own resultComputation.js only treats a config key as a threshold if it *starts with*
/// `max_`/`min_`, so that field silently never evaluated, ever - already fixed server-side (and in
/// SsdSmartReader.BuildSummaryStats, which already emits the correct key names below) before this
/// class existed. CONTRACT.md here and in the shared planning repo were still showing the stale
/// shape until this was caught while wiring up the DiskSpd benchmark - now synced to match the
/// server. None of these five are actually read by client code (the server is the only place
/// that evaluates thresholds, by design) - modeled here purely for parity with CpuConfig/
/// GpuConfig/RamConfig's "fully mirror the contract subtree" convention.
/// </summary>
public sealed class SsdConfig
{
    [JsonPropertyName("tool")]
    public string Tool { get; set; } = "crystaldiskmark";

    [JsonPropertyName("min_seq_read_mb_s")]
    public double MinSeqReadMbS { get; set; } = 400;

    [JsonPropertyName("min_seq_write_mb_s")]
    public double MinSeqWriteMbS { get; set; } = 300;

    /// <summary>Both bus types.</summary>
    [JsonPropertyName("max_smart_temp_c")]
    public double MaxSmartTempC { get; set; } = 70;

    /// <summary>ATA/SATA drives only.</summary>
    [JsonPropertyName("max_smart_reallocated_sectors")]
    public int MaxSmartReallocatedSectors { get; set; } = 0;

    /// <summary>NVMe drives only.</summary>
    [JsonPropertyName("max_smart_percentage_used")]
    public int MaxSmartPercentageUsed { get; set; } = 90;

    /// <summary>NVMe drives only.</summary>
    [JsonPropertyName("min_smart_available_spare_percent")]
    public int MinSmartAvailableSparePercent { get; set; } = 10;

    /// <summary>NVMe drives only.</summary>
    [JsonPropertyName("max_smart_media_errors")]
    public int MaxSmartMediaErrors { get; set; } = 0;
}

public sealed class ConcurrencyConfig
{
    [JsonPropertyName("cpu_gpu_together_allowed")]
    public bool CpuGpuTogetherAllowed { get; set; }

    [JsonPropertyName("exclusive_components")]
    public List<string> ExclusiveComponents { get; set; } = new();
}

// ---- POST /api/sessions (CONTRACT.md §4) --------------------------------------------------

public sealed class CreateSessionRequest
{
    [JsonPropertyName("mobo_serial")]
    public string MoboSerial { get; set; } = "";

    [JsonPropertyName("customer_name")]
    public string? CustomerName { get; set; }

    [JsonPropertyName("session_type")]
    public string SessionType { get; set; } = Models.SessionType.Repair;

    [JsonPropertyName("notes")]
    public string? Notes { get; set; }

    [JsonPropertyName("ssd_serials")]
    public List<string>? SsdSerials { get; set; }

    [JsonPropertyName("other_serials")]
    public Dictionary<string, string>? OtherSerials { get; set; }
}

public sealed class CreateSessionResponse
{
    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = "";
}

// ---- POST /api/sessions/:id/test-runs (CONTRACT.md §4, §6) -------------------------------

public sealed class StartTestRunRequest
{
    [JsonPropertyName("component")]
    public string Component { get; set; } = Models.Component.Cpu;
}

public sealed class StartTestRunResponse
{
    [JsonPropertyName("test_run_id")]
    public string TestRunId { get; set; } = "";
}

/// <summary>409 body shape when the concurrency rule (CONTRACT.md §6) rejects a start request.</summary>
public sealed class ExclusiveConflictResponse
{
    [JsonPropertyName("error")]
    public string Error { get; set; } = "";

    [JsonPropertyName("active_component")]
    public string? ActiveComponent { get; set; }
}

// ---- PATCH /api/sessions/:id/test-runs/:test_run_id (CONTRACT.md §4, §7) -----------------

public sealed class CompleteTestRunRequest
{
    [JsonPropertyName("tool_exit_code")]
    public int? ToolExitCode { get; set; }

    [JsonPropertyName("tool_output_raw")]
    public string? ToolOutputRaw { get; set; }

    [JsonPropertyName("summary_stats")]
    public Dictionary<string, object>? SummaryStats { get; set; }

    /// <summary>Null/absent = normal finish. See Models.StopReason for valid non-null values.</summary>
    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; set; }
}

public sealed class CompleteTestRunResponse
{
    /// <summary>
    /// "pass" | "fail" | "flagged" | "aborted". The client deliberately does not surface this
    /// anywhere in the UI (PROJECT_PLAN.md §4 / CLAUDE.md: no results/pass-fail shown client-side)
    /// - it's parsed here only so the HTTP call has a typed response to await.
    /// </summary>
    [JsonPropertyName("result")]
    public string? Result { get; set; }
}

// ---- PATCH /api/sessions/:id (CONTRACT.md §4) ---------------------------------------------

/// <summary>Body is always "{}" per contract - no fields to set.</summary>
public sealed class EndSessionRequest
{
}

// ---- /ws/telemetry (CONTRACT.md §5) --------------------------------------------------------

public sealed class TelemetrySample
{
    [JsonPropertyName("test_run_id")]
    public string TestRunId { get; set; } = "";

    /// <summary>ISO-8601 with milliseconds, e.g. "2026-08-08T12:00:00.000Z".</summary>
    [JsonPropertyName("ts")]
    public string Ts { get; set; } = "";

    [JsonPropertyName("sensor_name")]
    public string SensorName { get; set; } = "";

    [JsonPropertyName("value")]
    public double Value { get; set; }
}
