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

/// <summary>
/// Mirrors the server config JSON (CONTRACT.md §3). Only "cpu" and "concurrency" are modeled
/// since gpu/ram/ssd wrappers aren't built this pass; System.Text.Json ignores the unmodeled
/// subtrees on deserialize, so fetching the full config document still works fine.
/// </summary>
public sealed class ServerConfig
{
    [JsonPropertyName("cpu")]
    public CpuConfig? Cpu { get; set; }

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
