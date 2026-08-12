using System.Text.Json;
using System.Text.Json.Nodes;
using Luxtronic.PCTools.Models;
using Xunit;

namespace Luxtronic.PCTools.Tests;

// Guards against silent drift between Models/Contracts.cs and CONTRACT.md's §3/§4/§7 shapes.
// Every property-name assertion here was checked against CONTRACT.md directly, not against
// what Contracts.cs currently contains, so a typo/rename in either place shows up as a failure.
public class ContractsSerializationTests
{
    private static HashSet<string> PropertyNames(object value)
    {
        var node = JsonSerializer.SerializeToNode(value, value.GetType())!.AsObject();
        return node.Select(kv => kv.Key).ToHashSet();
    }

    // ---- POST /api/sessions (CONTRACT.md §4: mobo_serial, customer_name, session_type,
    // notes, ssd_serials, other_serials -> { session_id }) ------------------------------------

    [Fact]
    public void CreateSessionRequest_JsonPropertyNames_MatchContract()
    {
        var request = new CreateSessionRequest
        {
            MoboSerial = "ABC123",
            CustomerName = "Jane Doe",
            SessionType = Models.SessionType.NewBuild,
            Notes = "some notes",
            SsdSerials = new List<string> { "SSD1" },
            OtherSerials = new Dictionary<string, string> { ["gpu"] = "GPU1" },
        };

        Assert.Equal(
            new HashSet<string> { "mobo_serial", "customer_name", "session_type", "notes", "ssd_serials", "other_serials" },
            PropertyNames(request));
    }

    [Fact]
    public void CreateSessionRequest_RoundTrips()
    {
        var original = new CreateSessionRequest
        {
            MoboSerial = "ABC123",
            CustomerName = "Jane Doe",
            SessionType = Models.SessionType.Repair,
            Notes = "notes here",
            SsdSerials = new List<string> { "SSD1", "SSD2" },
            OtherSerials = new Dictionary<string, string> { ["gpu"] = "GPU1" },
        };

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<CreateSessionRequest>(json)!;

        Assert.Equal(original.MoboSerial, roundTripped.MoboSerial);
        Assert.Equal(original.CustomerName, roundTripped.CustomerName);
        Assert.Equal(original.SessionType, roundTripped.SessionType);
        Assert.Equal(original.Notes, roundTripped.Notes);
        Assert.Equal(original.SsdSerials, roundTripped.SsdSerials);
        Assert.Equal(original.OtherSerials, roundTripped.OtherSerials);
    }

    [Fact]
    public void CreateSessionResponse_JsonPropertyNames_MatchContract()
    {
        var response = new CreateSessionResponse { SessionId = "session-uuid" };

        Assert.Equal(new HashSet<string> { "session_id" }, PropertyNames(response));
    }

    // ---- POST /api/sessions/:id/test-runs (CONTRACT.md §4, §6: { component } -> { test_run_id }
    // or 409 { error, active_component }) ------------------------------------------------------

    [Fact]
    public void StartTestRunRequest_JsonPropertyNames_MatchContract()
    {
        var request = new StartTestRunRequest { Component = Models.Component.Cpu };

        Assert.Equal(new HashSet<string> { "component" }, PropertyNames(request));
    }

    [Fact]
    public void StartTestRunResponse_JsonPropertyNames_MatchContract()
    {
        var response = new StartTestRunResponse { TestRunId = "test-run-uuid" };

        Assert.Equal(new HashSet<string> { "test_run_id" }, PropertyNames(response));
    }

    [Fact]
    public void StartTestRunResponse_RoundTrips()
    {
        var original = new StartTestRunResponse { TestRunId = "test-run-uuid" };

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<StartTestRunResponse>(json)!;

        Assert.Equal(original.TestRunId, roundTripped.TestRunId);
    }

    [Fact]
    public void ExclusiveConflictResponse_JsonPropertyNames_MatchContract()
    {
        var response = new ExclusiveConflictResponse { Error = "exclusive_conflict", ActiveComponent = Models.Component.Ram };

        Assert.Equal(new HashSet<string> { "error", "active_component" }, PropertyNames(response));
    }

    // ---- PATCH /api/sessions/:id/test-runs/:test_run_id (CONTRACT.md §4, §7: { tool_exit_code,
    // tool_output_raw, summary_stats, stop_reason } -> { result }) -----------------------------

    [Fact]
    public void CompleteTestRunRequest_JsonPropertyNames_MatchContract()
    {
        var request = new CompleteTestRunRequest
        {
            ToolExitCode = 0,
            ToolOutputRaw = "raw output",
            SummaryStats = new Dictionary<string, object> { ["error_count"] = 0, ["max_temp_c"] = 87 },
            StopReason = Models.StopReason.UserAbort,
        };

        Assert.Equal(
            new HashSet<string> { "tool_exit_code", "tool_output_raw", "summary_stats", "stop_reason" },
            PropertyNames(request));
    }

    [Fact]
    public void CompleteTestRunRequest_RoundTrips()
    {
        var original = new CompleteTestRunRequest
        {
            ToolExitCode = 1,
            ToolOutputRaw = "some output",
            SummaryStats = new Dictionary<string, object> { ["error_count"] = 2 },
            StopReason = Models.StopReason.ToolCrash,
        };

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<CompleteTestRunRequest>(json)!;

        Assert.Equal(original.ToolExitCode, roundTripped.ToolExitCode);
        Assert.Equal(original.ToolOutputRaw, roundTripped.ToolOutputRaw);
        Assert.Equal(original.StopReason, roundTripped.StopReason);
        Assert.NotNull(roundTripped.SummaryStats);
        Assert.True(roundTripped.SummaryStats!.ContainsKey("error_count"));
    }

    [Fact]
    public void CompleteTestRunResponse_JsonPropertyNames_MatchContract()
    {
        var response = new CompleteTestRunResponse { Result = "pass" };

        Assert.Equal(new HashSet<string> { "result" }, PropertyNames(response));
    }

    // ---- PATCH /api/sessions/:id (CONTRACT.md §4: body is always "{}") -----------------------

    [Fact]
    public void EndSessionRequest_SerializesToEmptyObject()
    {
        var request = new EndSessionRequest();

        var json = JsonSerializer.Serialize(request);

        Assert.Equal("{}", json);
    }

    // ---- /ws/telemetry (CONTRACT.md §5: test_run_id, ts, sensor_name, value) -----------------

    [Fact]
    public void TelemetrySample_JsonPropertyNames_MatchContract()
    {
        var sample = new TelemetrySample
        {
            TestRunId = "test-run-uuid",
            Ts = "2026-08-08T12:00:00.000Z",
            SensorName = "cpu_package_temp_c",
            Value = 78.4,
        };

        Assert.Equal(
            new HashSet<string> { "test_run_id", "ts", "sensor_name", "value" },
            PropertyNames(sample));
    }

    // ---- GET /api/config (CONTRACT.md §3: cpu.tool, cpu.mode, cpu.duration_minutes,
    // cpu.max_temp_c, concurrency.cpu_gpu_together_allowed, concurrency.exclusive_components) --

    [Fact]
    public void CpuConfig_JsonPropertyNames_MatchContract()
    {
        var cpuConfig = new CpuConfig
        {
            Tool = "prime95",
            Mode = "blend",
            DurationMinutes = 60,
            MaxTempC = 95,
        };

        Assert.Equal(
            new HashSet<string> { "tool", "mode", "duration_minutes", "max_temp_c" },
            PropertyNames(cpuConfig));
    }

    [Fact]
    public void GpuConfig_JsonPropertyNames_MatchContract()
    {
        var gpuConfig = new GpuConfig
        {
            Tool = "furmark",
            DurationMinutes = 20,
            MaxTempC = 90,
        };

        Assert.Equal(
            new HashSet<string> { "tool", "duration_minutes", "max_temp_c" },
            PropertyNames(gpuConfig));
    }

    [Fact]
    public void RamConfig_JsonPropertyNames_MatchContract()
    {
        var ramConfig = new RamConfig
        {
            Tool = "tm5",
            ConfigProfile = "anta777-extreme",
            DurationMinutes = 60,
            MaxErrors = 0,
        };

        Assert.Equal(
            new HashSet<string> { "tool", "config_profile", "duration_minutes", "max_errors" },
            PropertyNames(ramConfig));
    }

    [Fact]
    public void ConcurrencyConfig_JsonPropertyNames_MatchContract()
    {
        var concurrencyConfig = new ConcurrencyConfig
        {
            CpuGpuTogetherAllowed = true,
            ExclusiveComponents = new List<string> { "ram", "ssd" },
        };

        Assert.Equal(
            new HashSet<string> { "cpu_gpu_together_allowed", "exclusive_components" },
            PropertyNames(concurrencyConfig));
    }

    [Fact]
    public void ServerConfig_JsonPropertyNames_MatchContract()
    {
        var serverConfig = new ServerConfig
        {
            Cpu = new CpuConfig(),
            Gpu = new GpuConfig(),
            Ram = new RamConfig(),
            Concurrency = new ConcurrencyConfig(),
        };

        Assert.Equal(new HashSet<string> { "cpu", "gpu", "ram", "concurrency" }, PropertyNames(serverConfig));
    }

    [Fact]
    public void ServerConfig_DeserializesFullContractExample_IgnoringUnmodeledSubtrees()
    {
        // The full CONTRACT.md §3 example. ssd's subtree is deliberately not modeled yet (comment
        // in Contracts.cs explains why) - this confirms deserialization tolerates that unmodeled
        // subtree rather than throwing, and that the modeled cpu/gpu/ram/concurrency subtrees all
        // come through correctly.
        const string json = """
        {
          "cpu": { "tool": "prime95", "mode": "blend", "duration_minutes": 60, "max_temp_c": 95 },
          "gpu": { "tool": "furmark", "duration_minutes": 20, "max_temp_c": 90 },
          "ram": { "tool": "tm5", "config_profile": "anta777-extreme", "duration_minutes": 60, "max_errors": 0 },
          "ssd": { "tool": "crystaldiskmark", "min_seq_read_mb_s": 400, "min_seq_write_mb_s": 300, "smart_reallocated_sectors_max": 0 },
          "concurrency": { "cpu_gpu_together_allowed": true, "exclusive_components": ["ram", "ssd"] }
        }
        """;

        var config = JsonSerializer.Deserialize<ServerConfig>(json)!;

        Assert.NotNull(config.Cpu);
        Assert.Equal("prime95", config.Cpu!.Tool);
        Assert.Equal("blend", config.Cpu.Mode);
        Assert.Equal(60, config.Cpu.DurationMinutes);
        Assert.Equal(95, config.Cpu.MaxTempC);

        Assert.NotNull(config.Gpu);
        Assert.Equal("furmark", config.Gpu!.Tool);
        Assert.Equal(20, config.Gpu.DurationMinutes);
        Assert.Equal(90, config.Gpu.MaxTempC);

        Assert.NotNull(config.Ram);
        Assert.Equal("tm5", config.Ram!.Tool);
        Assert.Equal("anta777-extreme", config.Ram.ConfigProfile);
        Assert.Equal(60, config.Ram.DurationMinutes);
        Assert.Equal(0, config.Ram.MaxErrors);

        Assert.NotNull(config.Concurrency);
        Assert.True(config.Concurrency!.CpuGpuTogetherAllowed);
        Assert.Equal(new List<string> { "ram", "ssd" }, config.Concurrency.ExclusiveComponents);
    }
}
