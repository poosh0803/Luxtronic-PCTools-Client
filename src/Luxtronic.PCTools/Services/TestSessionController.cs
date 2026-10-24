using System.IO;
using System.Linq;
using Luxtronic.PCTools.Models;

namespace Luxtronic.PCTools.Services;

/// <summary>One RAM test log-poll cycle's parsed state - see
/// <see cref="TestSessionController.ParseRamLog"/>.</summary>
public sealed record RamTestProgress(double? TestedMb, int ErrorCount);

/// <summary>
/// Orchestrates one full CPU, GPU, RAM, or SSD test session end to end: fetch config -> create
/// session -> start test run -> (CPU/GPU/RAM: connect telemetry) -> run
/// Prime95/FurMark/TM5/DiskSpd -> complete test run -> end session.
/// RunCpuTestSessionAsync/RunGpuTestSessionAsync/RunRamTestSessionAsync/RunSsdTestSessionAsync are
/// standalone and mutually exclusive (share the same <see cref="IsRunning"/> guard) - not
/// CONTRACT.md §6's "together" mode (cpu+gpu running concurrently), which the server already
/// supports (concurrency.js) but this client doesn't attempt yet; that would need reworking this
/// class's single-run-at-a-time design and the UI's single Start/Stop button pair. For RAM and SSD
/// specifically, sharing IsRunning isn't just a simplification - CONTRACT.md §6 requires both to
/// reject if ANY other test_run is active, so this already-existing guard is what makes that real
/// server-enforced rule hold client-side too. Separately, SubmitSsdSmartDataAsync below is a
/// distinct, lower-stakes passive SMART-only report that runs automatically after every CPU test
/// (unaffected by this class's SSD benchmark addition).
///
/// Scope simplification (judgment call, flagged for reconciliation): this pass treats
/// "one session = one test run" and ends the session automatically right after the test
/// completes. CONTRACT.md's data model allows multiple test_runs per session (e.g. CPU then
/// GPU in the same visit), and a fuller UI would likely keep the session open with an
/// explicit "End Session" action. That's out of scope while tests are run one at a time.
/// </summary>
public sealed class TestSessionController : IAsyncDisposable
{
    private const int MaxConsecutiveSensorFailures = 3;

    private readonly LuxApiClient _api;
    private readonly SensorMonitor _sensors;
    private readonly Prime95Runner _prime95;
    private readonly FurMarkRunner _furmark;
    private readonly MemtestVulkanRunner _vram;
    private readonly TM5Runner _tm5;
    private readonly DiskSpdRunner _diskSpd;
    private readonly AppSettingsProvider _settings;
    private readonly ApiKeyProvider _apiKey;

    private TelemetryPublisher? _telemetry;
    private CancellationTokenSource? _stopCts;
    private bool _stopWasSensorFailure;

    private static int s_running;
    private bool _seriesCancelled;
    private readonly bool _offline;

    /// <summary>True while ANY test is running in this process - shared across controller instances,
    /// so a single-test tab and the Run Tests series can never run at once.</summary>
    public static bool AnyRunning => Volatile.Read(ref s_running) == 1;

    public bool IsRunning
    {
        get => AnyRunning;
        private set => Volatile.Write(ref s_running, value ? 1 : 0);
    }

    /// <summary>The most recent run's summary stats, set just before it is submitted (or, for a local
    /// controller, just before it is discarded). Null until a run completes.</summary>
    public LocalRunSummary? LastRun { get; private set; }

    private void RecordRun(string component, Dictionary<string, object> summary, string? stopReason, SeriesSession? series)
    {
        LastRun = new LocalRunSummary(component, new Dictionary<string, object>(summary), stopReason);
        series?.Results.Add(LastRun);
    }

    /// <summary>Fetches the current server config (CONTRACT.md §3) without starting a session or
    /// test run - used by the UI to show the configured CPU/GPU/RAM duration before the technician
    /// clicks Start, not just after. RunCpuTestSessionAsync/RunGpuTestSessionAsync/
    /// RunRamTestSessionAsync fetch their own fresh copy at test-run time regardless (config could
    /// change between an early UI display and an actual run), so this is purely informational.</summary>
    public Task<ServerConfig> GetConfigAsync(CancellationToken ct = default) => FetchConfigAsync(ct);

    /// <summary>Fetches the server config (thresholds etc.), then replaces the per-test run lengths with
    /// the ones set in this app's Settings - durations are a local choice, thresholds stay server-side.</summary>
    private async Task<ServerConfig> FetchConfigAsync(CancellationToken ct)
    {
        var config = await _api.GetConfigAsync(ct).ConfigureAwait(false);
        if (config.Cpu is { } cpu)
        {
            cpu.DurationMinutes = _settings.CpuDurationMinutes;
            cpu.Mode = _settings.CpuLoad == CpuLoadLevel.MaxHeat ? "small_fft" : "blend";
            cpu.DisableAvx = _settings.CpuLoad == CpuLoadLevel.BlendNoAvx;
        }
        if (config.Gpu is { } gpu) gpu.DurationMinutes = _settings.GpuDurationMinutes;
        if (config.Ram is { } ram) ram.DurationMinutes = _settings.RamDurationMinutes;
        return config;
    }

    public TestSessionController(
        AppSettingsProvider settings, ApiKeyProvider apiKey, SensorMonitor sensors,
        Prime95Runner prime95, FurMarkRunner furmark, TM5Runner tm5, DiskSpdRunner diskSpd,
        MemtestVulkanRunner vram, bool forceOffline = false)
    {
        _settings = settings;
        _apiKey = apiKey;
        _sensors = sensors;
        _prime95 = prime95;
        _furmark = furmark;
        _tm5 = tm5;
        _diskSpd = diskSpd;
        _vram = vram;
        _offline = forceOffline || settings.OfflineMode;
        _api = new LuxApiClient(settings.ServerBaseUrl, apiKey.Key, _offline);
    }

    /// <param name="uiLifetime">Cancelled only if the app is shutting down - NOT used for the
    /// Stop button, which goes through <see cref="RequestStop"/> instead so a clean
    /// complete-test-run/end-session sequence still runs afterward.</param>
    /// <param name="onReading">Invoked once per sensor poll cycle (~1/sec) with the exact reading
    /// just sent as telemetry, so the UI can show live values instead of only log lines. Called
    /// from the background poll loop's thread - callers must marshal to the UI thread themselves.</param>
    public async Task RunCpuTestSessionAsync(
        CreateSessionRequest sessionInfo, Action<string> onLog, Action<SensorReadings>? onReading = null,
        CancellationToken uiLifetime = default, SeriesSession? series = null)
    {
        if (series is null)
        {
            if (IsRunning)
            {
                throw new InvalidOperationException("A test session is already running.");
            }

            IsRunning = true;
            LastRun = null;
        }

        _stopCts = new CancellationTokenSource();
        _stopWasSensorFailure = false;

        string? sessionId = null;
        string? testRunId = null;
        var consecutiveSensorFailures = 0;
        double? maxTemp = null;
        double totalLoad = 0;
        var loadSamples = 0;

        try
        {
            onLog("Fetching server config (GET /api/config)...");
            var config = await FetchConfigAsync(uiLifetime).ConfigureAwait(false);
            var cpuCfg = config.Cpu ?? throw new InvalidOperationException(
                "Server config response had no 'cpu' subtree - check CONTRACT.md §3 shape matches.");

            onLog("Creating session (POST /api/sessions)...");
            sessionId = series?.SessionId ?? await _api.CreateSessionAsync(sessionInfo, uiLifetime).ConfigureAwait(false);
            onLog($"Session created: {sessionId}");

            onLog("Starting CPU test run (POST .../test-runs)...");
            testRunId = await _api.StartTestRunAsync(sessionId, Component.Cpu, uiLifetime).ConfigureAwait(false);
            onLog($"Test run started: {testRunId}");

            _telemetry = new TelemetryPublisher(_offline);
            await _telemetry.ConnectAsync(_settings.ServerBaseUrl, _apiKey.Key, uiLifetime).ConfigureAwait(false);
            onLog("Telemetry WebSocket connected (/ws/telemetry).");

            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(uiLifetime);
            var capturedTestRunId = testRunId;

            var pollTask = Task.Run(async () =>
            {
                while (!pollCts.IsCancellationRequested)
                {
                    try
                    {
                        var reading = _sensors.ReadCpu();
                        consecutiveSensorFailures = 0;
                        onReading?.Invoke(reading);

                        var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                        var samples = new List<TelemetrySample>(4);
                        if (reading.CpuPackageTempC is double t)
                        {
                            samples.Add(new TelemetrySample { TestRunId = capturedTestRunId, Ts = ts, SensorName = "cpu_package_temp_c", Value = t });
                            maxTemp = maxTemp is null ? t : Math.Max(maxTemp.Value, t);
                        }
                        if (reading.CpuLoadPct is double l)
                        {
                            samples.Add(new TelemetrySample { TestRunId = capturedTestRunId, Ts = ts, SensorName = "cpu_load_pct", Value = l });
                            totalLoad += l;
                            loadSamples++;
                        }
                        if (reading.CpuFanRpm is double f)
                        {
                            samples.Add(new TelemetrySample { TestRunId = capturedTestRunId, Ts = ts, SensorName = "cpu_fan_rpm", Value = f });
                        }
                        if (reading.CpuFrequencyMhz is double freq)
                        {
                            // Not folded into summary_stats (no config threshold exists for it,
                            // unlike max_temp_c) -- this is purely for the dashboard's live/
                            // historical telemetry chart, which already charts any sensor_name it
                            // sees without server-side changes.
                            samples.Add(new TelemetrySample { TestRunId = capturedTestRunId, Ts = ts, SensorName = "cpu_frequency_mhz", Value = freq });
                        }

                        if (samples.Count == 0)
                        {
                            onLog("Sensor poll returned no readable values this cycle.");
                        }

                        foreach (var s in samples)
                        {
                            await _telemetry.SendSampleAsync(s, pollCts.Token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        consecutiveSensorFailures++;
                        onLog($"Sensor/telemetry read failed ({consecutiveSensorFailures}/{MaxConsecutiveSensorFailures}): {ex.Message}");
                        if (consecutiveSensorFailures >= MaxConsecutiveSensorFailures)
                        {
                            onLog("Repeated sensor/telemetry failures - aborting test run as client_error.");
                            _stopWasSensorFailure = true;
                            _stopCts.Cancel();
                            return;
                        }
                    }

                    try
                    {
                        await Task.Delay(_settings.TelemetrySampleIntervalMs, pollCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }, uiLifetime);

            var runResult = await _prime95.RunAsync(cpuCfg, _stopCts.Token, onLog, uiLifetime).ConfigureAwait(false);

            pollCts.Cancel();
            try
            {
                await pollTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                onLog($"Sensor polling loop ended with an exception: {ex.Message}");
            }

            var effectiveStopReason = runResult.StopReason;
            if (runResult.WasExternallyStopped)
            {
                effectiveStopReason = _stopWasSensorFailure ? Models.StopReason.ClientError : Models.StopReason.UserAbort;
            }

            double? avgLoadPct = loadSamples > 0 ? totalLoad / loadSamples : null;
            var summary = BuildSummaryStats(runResult.ErrorCount, maxTemp, avgLoadPct);
            RecordRun(Component.Cpu, summary, effectiveStopReason, series);

            onLog("Completing test run (PATCH .../test-runs/:id)...");
            await _api.CompleteTestRunAsync(sessionId, testRunId, new CompleteTestRunRequest
            {
                ToolExitCode = runResult.ExitCode,
                RunSettings = BuildRunSettings(Component.Cpu),
                ToolOutputRaw = Truncate(runResult.RawOutput, 200_000),
                SummaryStats = summary,
                StopReason = effectiveStopReason,
            }, uiLifetime).ConfigureAwait(false);
            onLog("Test run completion recorded on the server.");

        }
        catch (Exception ex)
        {
            onLog($"ERROR: {ex.Message}");

            // Best-effort: if a test run was started but we're bailing out due to an
            // unexpected client-side exception, still tell the server so it doesn't sit
            // open forever (CONTRACT.md §7 has a session-end auto-close safety net too, but
            // closing it explicitly here gives a more accurate stop_reason than that fallback).
            if (sessionId is not null && testRunId is not null)
            {
                try
                {
                    await _api.CompleteTestRunAsync(sessionId, testRunId, new CompleteTestRunRequest
                    {
                        ToolExitCode = null,
                        ToolOutputRaw = ex.ToString(),
                        SummaryStats = new Dictionary<string, object>(),
                        StopReason = Models.StopReason.ClientError,
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Nothing more we can do client-side - session-end auto-close will catch it.
                }
            }

            throw;
        }
        finally
        {
            if (sessionId is not null && series is null) // a series ends its own session
            {
                try
                {
                    onLog("Ending session (PATCH /api/sessions/:id)...");
                    await _api.EndSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                    onLog("Session ended.");
                }
                catch (Exception ex)
                {
                    onLog($"Warning: failed to end session cleanly: {ex.Message}");
                }
            }

            if (_telemetry is not null)
            {
                await _telemetry.DisposeAsync().ConfigureAwait(false);
                _telemetry = null;
            }

            if (series is null)
            {
                IsRunning = false;
            }

            _stopCts?.Dispose();
            _stopCts = null;
        }
    }

    /// <summary>
    /// Mirrors <see cref="RunCpuTestSessionAsync"/> structurally, for GPU/FurMark instead of
    /// CPU/Prime95 - own session, own test_run (component=gpu), own telemetry samples. Shares
    /// <see cref="IsRunning"/> with the CPU path (throws the same way if either is already
    /// running), which is what actually enforces "one test at a time" - the UI's checkbox mutual
    /// exclusion is a second, belt-and-suspenders layer on top of this.
    /// </summary>
    /// <param name="uiLifetime">Cancelled only if the app is shutting down - see the identical
    /// remark on RunCpuTestSessionAsync.</param>
    /// <param name="onReading">Invoked once per sensor poll cycle with the exact GPU reading just
    /// sent as telemetry - same threading contract as RunCpuTestSessionAsync's onReading.</param>
    public Task RunGpuTestSessionAsync(
        CreateSessionRequest sessionInfo, Action<string> onLog, Action<GpuReadings?>? onReading = null,
        CancellationToken uiLifetime = default, SeriesSession? series = null) =>
        RunGpuSensorSessionAsync(sessionInfo, onLog, onReading, Component.Gpu, "GPU", async (config, stop) =>
        {
            var gpuCfg = config.Gpu ?? throw new InvalidOperationException(
                "Server config response had no 'gpu' subtree - check CONTRACT.md §3 shape matches.");
            var r = await _furmark.RunAsync(gpuCfg, stop, onLog, uiLifetime).ConfigureAwait(false);
            return new ToolOutcome(r.ExitCode, r.RawOutput, r.ErrorCount, r.StopReason, r.WasExternallyStopped);
        }, uiLifetime, series);

    /// <summary>VRAM test (memtest_vulkan). Same shape as the GPU test - GPU temperatures/clocks are
    /// streamed while it runs - but a separate component ("vram") with run length and device number
    /// taken from this app's Settings, since the server config has no vram section.</summary>
    public Task RunVramTestSessionAsync(
        CreateSessionRequest sessionInfo, Action<string> onLog, Action<GpuReadings?>? onReading = null,
        CancellationToken uiLifetime = default, SeriesSession? series = null) =>
        RunGpuSensorSessionAsync(sessionInfo, onLog, onReading, Component.Vram, "VRAM", async (_, stop) =>
        {
            var settings = new VramRunSettings(_settings.VramDurationMinutes, _settings.VramDeviceIndex);
            var r = await _vram.RunAsync(settings, stop, onLog, uiLifetime).ConfigureAwait(false);
            return new ToolOutcome(r.ExitCode, r.RawOutput, r.ErrorCount, r.StopReason, r.WasExternallyStopped);
        }, uiLifetime, series);

    /// <summary>The settings this app applied on top of the server config for a run, sent with the
    /// completion so the server's record shows what really ran (see CompleteTestRunRequest.RunSettings).</summary>
    private Dictionary<string, object>? BuildRunSettings(string component) => component switch
    {
        Component.Cpu => new Dictionary<string, object>
        {
            ["duration_minutes"] = _settings.CpuDurationMinutes,
            ["mode"] = _settings.CpuLoad == CpuLoadLevel.MaxHeat ? "small_fft" : "blend",
            ["avx_disabled"] = _settings.CpuLoad == CpuLoadLevel.BlendNoAvx,
        },
        Component.Gpu => new Dictionary<string, object> { ["duration_minutes"] = _settings.GpuDurationMinutes },
        Component.Vram => new Dictionary<string, object>
        {
            ["duration_minutes"] = _settings.VramDurationMinutes,
            ["device_index"] = _settings.VramDeviceIndex,
        },
        Component.Ram => new Dictionary<string, object> { ["duration_minutes"] = _settings.RamDurationMinutes },
        _ => null,
    };

    private sealed record ToolOutcome(int? ExitCode, string RawOutput, int ErrorCount, string? StopReason, bool WasExternallyStopped);

    private async Task RunGpuSensorSessionAsync(
        CreateSessionRequest sessionInfo, Action<string> onLog, Action<GpuReadings?>? onReading,
        string component, string label, Func<ServerConfig, CancellationToken, Task<ToolOutcome>> runTool,
        CancellationToken uiLifetime, SeriesSession? series = null)
    {
        if (series is null)
        {
            if (IsRunning)
            {
                throw new InvalidOperationException("A test session is already running.");
            }

            IsRunning = true;
            LastRun = null;
        }

        _stopCts = new CancellationTokenSource();
        _stopWasSensorFailure = false;

        string? sessionId = null;
        string? testRunId = null;
        var consecutiveSensorFailures = 0;
        double? maxTemp = null;

        try
        {
            onLog("Fetching server config (GET /api/config)...");
            var config = await FetchConfigAsync(uiLifetime).ConfigureAwait(false);

            onLog("Creating session (POST /api/sessions)...");
            sessionId = series?.SessionId ?? await _api.CreateSessionAsync(sessionInfo, uiLifetime).ConfigureAwait(false);
            onLog($"Session created: {sessionId}");

            onLog($"Starting {label} test run (POST .../test-runs)...");
            testRunId = await _api.StartTestRunAsync(sessionId, component, uiLifetime).ConfigureAwait(false);
            onLog($"Test run started: {testRunId}");

            _telemetry = new TelemetryPublisher(_offline);
            await _telemetry.ConnectAsync(_settings.ServerBaseUrl, _apiKey.Key, uiLifetime).ConfigureAwait(false);
            onLog("Telemetry WebSocket connected (/ws/telemetry).");

            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(uiLifetime);
            var capturedTestRunId = testRunId;

            var pollTask = Task.Run(async () =>
            {
                while (!pollCts.IsCancellationRequested)
                {
                    try
                    {
                        var reading = _sensors.ReadGpu();
                        consecutiveSensorFailures = 0;
                        if (reading is not null)
                        {
                            onReading?.Invoke(reading);
                        }

                        var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                        var samples = new List<TelemetrySample>(6);
                        if (reading?.CoreTempC is double t)
                        {
                            samples.Add(new TelemetrySample { TestRunId = capturedTestRunId, Ts = ts, SensorName = "gpu_core_temp_c", Value = t });
                            maxTemp = maxTemp is null ? t : Math.Max(maxTemp.Value, t);
                        }
                        if (reading?.HotSpotTempC is double hs)
                        {
                            samples.Add(new TelemetrySample { TestRunId = capturedTestRunId, Ts = ts, SensorName = "gpu_hot_spot_temp_c", Value = hs });
                        }
                        if (reading?.LoadPct is double l)
                        {
                            samples.Add(new TelemetrySample { TestRunId = capturedTestRunId, Ts = ts, SensorName = "gpu_load_pct", Value = l });
                        }
                        if (reading?.FanRpm is double f)
                        {
                            samples.Add(new TelemetrySample { TestRunId = capturedTestRunId, Ts = ts, SensorName = "gpu_fan_rpm", Value = f });
                        }
                        if (reading?.CoreClockMhz is double cc)
                        {
                            samples.Add(new TelemetrySample { TestRunId = capturedTestRunId, Ts = ts, SensorName = "gpu_core_clock_mhz", Value = cc });
                        }
                        if (reading?.PowerW is double p)
                        {
                            samples.Add(new TelemetrySample { TestRunId = capturedTestRunId, Ts = ts, SensorName = "gpu_power_w", Value = p });
                        }

                        if (samples.Count == 0)
                        {
                            onLog("Sensor poll returned no readable values this cycle.");
                        }

                        foreach (var s in samples)
                        {
                            await _telemetry.SendSampleAsync(s, pollCts.Token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        consecutiveSensorFailures++;
                        onLog($"Sensor/telemetry read failed ({consecutiveSensorFailures}/{MaxConsecutiveSensorFailures}): {ex.Message}");
                        if (consecutiveSensorFailures >= MaxConsecutiveSensorFailures)
                        {
                            onLog("Repeated sensor/telemetry failures - aborting test run as client_error.");
                            _stopWasSensorFailure = true;
                            _stopCts.Cancel();
                            return;
                        }
                    }

                    try
                    {
                        await Task.Delay(_settings.TelemetrySampleIntervalMs, pollCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }, uiLifetime);

            var runResult = await runTool(config, _stopCts.Token).ConfigureAwait(false);

            pollCts.Cancel();
            try
            {
                await pollTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                onLog($"Sensor polling loop ended with an exception: {ex.Message}");
            }

            var effectiveStopReason = runResult.StopReason;
            if (runResult.WasExternallyStopped)
            {
                effectiveStopReason = _stopWasSensorFailure ? Models.StopReason.ClientError : Models.StopReason.UserAbort;
            }

            var summary = BuildGpuSummaryStats(runResult.ErrorCount, maxTemp);
            RecordRun(component, summary, effectiveStopReason, series);

            onLog("Completing test run (PATCH .../test-runs/:id)...");
            await _api.CompleteTestRunAsync(sessionId, testRunId, new CompleteTestRunRequest
            {
                ToolExitCode = runResult.ExitCode,
                RunSettings = BuildRunSettings(component),
                ToolOutputRaw = Truncate(runResult.RawOutput, 200_000),
                SummaryStats = summary,
                StopReason = effectiveStopReason,
            }, uiLifetime).ConfigureAwait(false);
            onLog("Test run completion recorded on the server.");
        }
        catch (Exception ex)
        {
            onLog($"ERROR: {ex.Message}");

            if (sessionId is not null && testRunId is not null)
            {
                try
                {
                    await _api.CompleteTestRunAsync(sessionId, testRunId, new CompleteTestRunRequest
                    {
                        ToolExitCode = null,
                        ToolOutputRaw = ex.ToString(),
                        SummaryStats = new Dictionary<string, object>(),
                        StopReason = Models.StopReason.ClientError,
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Nothing more we can do client-side - session-end auto-close will catch it.
                }
            }

            throw;
        }
        finally
        {
            if (sessionId is not null && series is null) // a series ends its own session
            {
                try
                {
                    onLog("Ending session (PATCH /api/sessions/:id)...");
                    await _api.EndSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                    onLog("Session ended.");
                }
                catch (Exception ex)
                {
                    onLog($"Warning: failed to end session cleanly: {ex.Message}");
                }
            }

            if (_telemetry is not null)
            {
                await _telemetry.DisposeAsync().ConfigureAwait(false);
                _telemetry = null;
            }

            if (series is null)
            {
                IsRunning = false;
            }

            _stopCts?.Dispose();
            _stopCts = null;
        }
    }

    /// <summary>
    /// Mirrors RunCpuTestSessionAsync/RunGpuTestSessionAsync structurally, for RAM/TM5 instead of
    /// CPU/Prime95 or GPU/FurMark - own session, own test_run (component=ram), shares
    /// <see cref="IsRunning"/> with the other two (see class remarks - for RAM this is what makes
    /// CONTRACT.md §6's real exclusivity rule hold, not just a client-side simplification).
    ///
    /// Telemetry here is fundamentally different from CPU/GPU's sensor-poll model: RAM has no
    /// SensorMonitor reading to stream, so this polls TM5's Log.txt instead. CONFIRMED via real
    /// testing (see TM5Runner's class remarks): TM5 holds this file open exclusively for the whole
    /// run, so in practice this gets exactly one successful read (possibly stale) and then fails
    /// every cycle after - each failure just skips telemetry for that cycle rather than aborting
    /// the run, so the live readout freezes but the run itself isn't affected. Final
    /// summary_stats come from TM5Runner's own post-exit read, not this poll loop, so they're
    /// unaffected by any of this.
    /// </summary>
    /// <param name="uiLifetime">Cancelled only if the app is shutting down - see the identical
    /// remark on RunCpuTestSessionAsync.</param>
    /// <param name="onProgress">Invoked once per Log.txt poll cycle with whatever could be parsed
    /// - same threading contract as RunCpuTestSessionAsync's onReading.</param>
    public async Task RunRamTestSessionAsync(
        CreateSessionRequest sessionInfo, Action<string> onLog, Action<RamTestProgress>? onProgress = null,
        CancellationToken uiLifetime = default, SeriesSession? series = null, Action<SensorReadings>? onCpu = null)
    {
        if (series is null)
        {
            if (IsRunning)
            {
                throw new InvalidOperationException("A test session is already running.");
            }

            IsRunning = true;
            LastRun = null;
        }

        _stopCts = new CancellationTokenSource();
        _stopWasSensorFailure = false;

        string? sessionId = null;
        string? testRunId = null;
        var maxErrorsObserved = 0;
        var logReadWarned = false;
        var logPath = Path.Combine(_settings.RamToolsDirectory, "Log.txt");

        try
        {
            onLog("Fetching server config (GET /api/config)...");
            var config = await FetchConfigAsync(uiLifetime).ConfigureAwait(false);
            var ramCfg = config.Ram ?? throw new InvalidOperationException(
                "Server config response had no 'ram' subtree - check CONTRACT.md §3 shape matches.");

            onLog("Creating session (POST /api/sessions)...");
            sessionId = series?.SessionId ?? await _api.CreateSessionAsync(sessionInfo, uiLifetime).ConfigureAwait(false);
            onLog($"Session created: {sessionId}");

            onLog("Starting RAM test run (POST .../test-runs)...");
            testRunId = await _api.StartTestRunAsync(sessionId, Component.Ram, uiLifetime).ConfigureAwait(false);
            onLog($"Test run started: {testRunId}");

            _telemetry = new TelemetryPublisher(_offline);
            await _telemetry.ConnectAsync(_settings.ServerBaseUrl, _apiKey.Key, uiLifetime).ConfigureAwait(false);
            onLog("Telemetry WebSocket connected (/ws/telemetry).");

            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(uiLifetime);
            var capturedTestRunId = testRunId;

            var pollTask = Task.Run(async () =>
            {
                while (!pollCts.IsCancellationRequested)
                {
                    try
                    {
                        var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                        var samples = new List<TelemetrySample>(4);

                        if (File.Exists(logPath))
                        {
                            try
                            {
                                var snapshot = ParseRamLog(await ReadSharedAsync(logPath, pollCts.Token).ConfigureAwait(false));
                                maxErrorsObserved = Math.Max(maxErrorsObserved, snapshot.ErrorCount);
                                onProgress?.Invoke(snapshot);
                                samples.Add(new TelemetrySample { TestRunId = capturedTestRunId, Ts = ts, SensorName = "ram_errors", Value = snapshot.ErrorCount });
                            }
                            catch (IOException ex)
                            {
                                if (!logReadWarned)
                                {
                                    logReadWarned = true;
                                    onLog($"TM5's log can't be read right now ({ex.Message}) - the error count is taken from it when the test ends.");
                                }
                            }
                        }

                        // What actually changes during a RAM test: how much memory is in use (TM5
                        // allocating what it tests) and how hard the CPU/memory controller work.
                        if (SystemMemory.Read() is { } mem)
                        {
                            samples.Add(new TelemetrySample { TestRunId = capturedTestRunId, Ts = ts, SensorName = "ram_used_pct", Value = mem.UsedPercent });
                        }

                        var cpu = _sensors.ReadCpu();
                        onCpu?.Invoke(cpu);
                        if (cpu.CpuPackageTempC is double t)
                        {
                            samples.Add(new TelemetrySample { TestRunId = capturedTestRunId, Ts = ts, SensorName = "cpu_package_temp_c", Value = t });
                        }
                        if (cpu.CpuLoadPct is double l)
                        {
                            samples.Add(new TelemetrySample { TestRunId = capturedTestRunId, Ts = ts, SensorName = "cpu_load_pct", Value = l });
                        }

                        foreach (var s in samples)
                        {
                            await _telemetry.SendSampleAsync(s, pollCts.Token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // IOException from TM5 holding the file open exclusively that instant is
                        // expected/routine here, not the 3-strikes sensor-failure pattern
                        // CPU/GPU use - Log.txt access failing once says nothing about whether the
                        // next poll will succeed, unlike a genuinely dead sensor.
                        onLog($"RAM log poll failed this cycle: {ex.Message}");
                    }

                    try
                    {
                        await Task.Delay(_settings.TelemetrySampleIntervalMs, pollCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }, uiLifetime);

            var runResult = await _tm5.RunAsync(ramCfg, _stopCts.Token, onLog, uiLifetime).ConfigureAwait(false);

            pollCts.Cancel();
            try
            {
                await pollTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                onLog($"RAM log polling loop ended with an exception: {ex.Message}");
            }

            var effectiveStopReason = runResult.StopReason;
            if (runResult.WasExternallyStopped)
            {
                effectiveStopReason = _stopWasSensorFailure ? Models.StopReason.ClientError : Models.StopReason.UserAbort;
            }

            var summary = BuildRamSummaryStats(Math.Max(runResult.ErrorCount, maxErrorsObserved));
            RecordRun(Component.Ram, summary, effectiveStopReason, series);

            onLog("Completing test run (PATCH .../test-runs/:id)...");
            await _api.CompleteTestRunAsync(sessionId, testRunId, new CompleteTestRunRequest
            {
                ToolExitCode = runResult.ExitCode,
                RunSettings = BuildRunSettings(Component.Ram),
                ToolOutputRaw = Truncate(runResult.RawOutput, 200_000),
                SummaryStats = summary,
                StopReason = effectiveStopReason,
            }, uiLifetime).ConfigureAwait(false);
            onLog("Test run completion recorded on the server.");
        }
        catch (Exception ex)
        {
            onLog($"ERROR: {ex.Message}");

            if (sessionId is not null && testRunId is not null)
            {
                try
                {
                    await _api.CompleteTestRunAsync(sessionId, testRunId, new CompleteTestRunRequest
                    {
                        ToolExitCode = null,
                        ToolOutputRaw = ex.ToString(),
                        SummaryStats = new Dictionary<string, object>(),
                        StopReason = Models.StopReason.ClientError,
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Nothing more we can do client-side - session-end auto-close will catch it.
                }
            }

            throw;
        }
        finally
        {
            if (sessionId is not null && series is null) // a series ends its own session
            {
                try
                {
                    onLog("Ending session (PATCH /api/sessions/:id)...");
                    await _api.EndSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                    onLog("Session ended.");
                }
                catch (Exception ex)
                {
                    onLog($"Warning: failed to end session cleanly: {ex.Message}");
                }
            }

            if (_telemetry is not null)
            {
                await _telemetry.DisposeAsync().ConfigureAwait(false);
                _telemetry = null;
            }

            if (series is null)
            {
                IsRunning = false;
            }

            _stopCts?.Dispose();
            _stopCts = null;
        }
    }

    /// <summary>
    /// SSD equivalent of RunCpuTestSessionAsync/RunGpuTestSessionAsync/RunRamTestSessionAsync -
    /// own session, own test_run (component=ssd), shares <see cref="IsRunning"/> the same way
    /// (CONTRACT.md §6 requires ssd to reject if ANY other test_run is active, same real
    /// server-enforced rule RAM relies on this guard for).
    ///
    /// Structurally simpler than the other three: DiskSpdRunner's benchmark is a short (~20s
    /// total), one-shot subprocess call with no natural continuous stream to poll, unlike CPU/GPU's
    /// sensor telemetry or RAM's Log.txt tailing - so this doesn't open a telemetry WebSocket at
    /// all, matching the existing (passive, telemetry-free) SubmitSsdSmartDataAsync below. After
    /// the benchmark completes, this re-reads SMART for <paramref name="targetDrive"/> specifically
    /// (fresh temperatures post-load are more meaningful than the picker-time snapshot) and merges
    /// it with the throughput numbers into ONE test_run submission, rather than the two separate
    /// start+complete calls SubmitSsdSmartDataAsync uses for its passive per-drive loop - this flow
    /// already knows exactly which physical drive was tested (the technician picked it), so there's
    /// no "one row per drive" ambiguity to resolve here.
    /// </summary>
    /// <param name="targetDrive">Which physical drive the technician selected in the picker -
    /// used to re-match a fresh SsdSmartReader.ReadAll() snapshot after the benchmark by serial
    /// number; falls back to the originally-passed snapshot if the drive can't be re-found (e.g.
    /// external drive unplugged mid-run).</param>
    /// <param name="targetFolder">Filesystem folder (never a raw device) DiskSpdRunner writes its
    /// scratch test file into - see DiskSpdRunner's class remarks on why this must be a real path,
    /// not a physical drive index.</param>
    /// <param name="uiLifetime">Cancelled only if the app is shutting down - see the identical
    /// remark on RunCpuTestSessionAsync.</param>
    public async Task RunSsdTestSessionAsync(
        CreateSessionRequest sessionInfo, SsdSmartInfo targetDrive, string targetFolder, Action<string> onLog,
        CancellationToken uiLifetime = default, SeriesSession? series = null, Action<double?, double?>? onSpeed = null)
    {
        if (series is null)
        {
            if (IsRunning)
            {
                throw new InvalidOperationException("A test session is already running.");
            }

            IsRunning = true;
            LastRun = null;
        }

        _stopCts = new CancellationTokenSource();
        _stopWasSensorFailure = false;

        string? sessionId = null;
        string? testRunId = null;

        try
        {
            onLog("Fetching server config (GET /api/config)...");
            var config = await FetchConfigAsync(uiLifetime).ConfigureAwait(false);
            _ = config.Ssd ?? throw new InvalidOperationException(
                "Server config response had no 'ssd' subtree - check CONTRACT.md §3 shape matches.");

            onLog("Creating session (POST /api/sessions)...");
            sessionId = series?.SessionId ?? await _api.CreateSessionAsync(sessionInfo, uiLifetime).ConfigureAwait(false);
            onLog($"Session created: {sessionId}");

            onLog("Starting SSD test run (POST .../test-runs)...");
            testRunId = await _api.StartTestRunAsync(sessionId, Component.Ssd, uiLifetime).ConfigureAwait(false);
            onLog($"Test run started: {testRunId}");

            // Live read/write speed from Windows' disk counters while DiskSpd runs (DiskSpd itself
            // only reports at the end of each pass), shown on the PC and streamed as telemetry.
            _telemetry = new TelemetryPublisher(_offline);
            await _telemetry.ConnectAsync(_settings.ServerBaseUrl, _apiKey.Key, uiLifetime).ConfigureAwait(false);
            using var speedMonitor = DiskSpeedMonitor.TryCreate(targetFolder);
            if (speedMonitor is null)
            {
                onLog("Live disk speed isn't available for this drive (performance counters could not be opened) - final numbers only.");
            }

            using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(uiLifetime);
            var capturedTestRunId = testRunId;
            var pollTask = Task.Run(async () =>
            {
                while (speedMonitor is not null && !pollCts.IsCancellationRequested)
                {
                    try
                    {
                        var (readMbS, writeMbS) = speedMonitor.Read();
                        onSpeed?.Invoke(readMbS, writeMbS);
                        var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                        await _telemetry.SendSampleAsync(new TelemetrySample { TestRunId = capturedTestRunId, Ts = ts, SensorName = "ssd_read_mb_s", Value = readMbS }, pollCts.Token).ConfigureAwait(false);
                        await _telemetry.SendSampleAsync(new TelemetrySample { TestRunId = capturedTestRunId, Ts = ts, SensorName = "ssd_write_mb_s", Value = writeMbS }, pollCts.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A missed live sample is harmless - the final numbers come from DiskSpd itself.
                    }

                    try
                    {
                        await Task.Delay(_settings.TelemetrySampleIntervalMs, pollCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }, uiLifetime);

            var runResult = await _diskSpd.RunAsync(targetFolder, _stopCts.Token, onLog, uiLifetime).ConfigureAwait(false);

            pollCts.Cancel();
            try
            {
                await pollTask.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Poll loop swallows its own errors; nothing to report.
            }

            onSpeed?.Invoke(null, null);

            var effectiveStopReason = runResult.StopReason;
            if (runResult.WasExternallyStopped)
            {
                effectiveStopReason = Models.StopReason.UserAbort; // no sensor-poll loop here, so no client_error path to weigh against
            }

            var freshDrive = TryReadFreshDrive(targetDrive, onLog);
            var smartStats = SsdSmartReader.BuildSummaryStats(freshDrive);
            var summary = BuildSsdSummaryStats(smartStats, runResult.SeqReadMbS, runResult.SeqWriteMbS, runResult.ErrorCount);
            RecordRun(Component.Ssd, summary, effectiveStopReason, series);
            var rawOutput = runResult.RawOutput + Environment.NewLine + "--- SMART snapshot ---" + Environment.NewLine +
                             SsdSmartReader.FormatToolOutputRaw(freshDrive);

            onLog("Completing test run (PATCH .../test-runs/:id)...");
            await _api.CompleteTestRunAsync(sessionId, testRunId, new CompleteTestRunRequest
            {
                ToolExitCode = runResult.ExitCode,
                RunSettings = BuildRunSettings(Component.Ssd),
                ToolOutputRaw = Truncate(rawOutput, 200_000),
                SummaryStats = summary,
                StopReason = effectiveStopReason,
            }, uiLifetime).ConfigureAwait(false);
            onLog("Test run completion recorded on the server.");
        }
        catch (Exception ex)
        {
            onLog($"ERROR: {ex.Message}");

            if (sessionId is not null && testRunId is not null)
            {
                try
                {
                    await _api.CompleteTestRunAsync(sessionId, testRunId, new CompleteTestRunRequest
                    {
                        ToolExitCode = null,
                        ToolOutputRaw = ex.ToString(),
                        SummaryStats = new Dictionary<string, object>(),
                        StopReason = Models.StopReason.ClientError,
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Nothing more we can do client-side - session-end auto-close will catch it.
                }
            }

            throw;
        }
        finally
        {
            if (sessionId is not null && series is null) // a series ends its own session
            {
                try
                {
                    onLog("Ending session (PATCH /api/sessions/:id)...");
                    await _api.EndSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                    onLog("Session ended.");
                }
                catch (Exception ex)
                {
                    onLog($"Warning: failed to end session cleanly: {ex.Message}");
                }
            }

            if (_telemetry is not null)
            {
                await _telemetry.DisposeAsync().ConfigureAwait(false);
                _telemetry = null;
            }

            if (series is null)
            {
                IsRunning = false;
            }

            _stopCts?.Dispose();
            _stopCts = null;
        }
    }

    /// <summary>Best-effort re-read of <paramref name="originalDrive"/>'s current SMART snapshot
    /// (post-benchmark temperatures are more meaningful than the picker-time reading), matched by
    /// serial number against a fresh SensorMonitor.ReadSsds() call. Falls back to the original
    /// snapshot - not a thrown exception - if the drive can't be re-found or the re-read itself
    /// fails, since a stale-but-present SMART reading is more useful to the server than none at
    /// all (same "log what's available" philosophy as PROJECT_PLAN.md §8).</summary>
    private SsdSmartInfo TryReadFreshDrive(SsdSmartInfo originalDrive, Action<string> onLog)
    {
        try
        {
            var fresh = _sensors.ReadSsds();
            var match = originalDrive.SerialNumber is null
                ? null
                : fresh.FirstOrDefault(d => d.SerialNumber == originalDrive.SerialNumber);
            return match ?? originalDrive;
        }
        catch (Exception ex)
        {
            onLog($"WARNING: could not re-read SMART data after SSD benchmark, using pre-test snapshot: {ex.Message}");
            return originalDrive;
        }
    }

    /// <summary>Merges DiskSpdRunner's throughput numbers into SsdSmartReader's SMART
    /// summary_stats (CONTRACT.md §2/§7), split out so it's unit testable without real hardware.
    /// Overwrites the SMART dict's own always-0 "error_count" with <paramref name="errorCount"/>
    /// (DiskSpdRunner's own, also always 0 in practice - see DiskSpdRunResult.ErrorCount's
    /// remarks) rather than summing/duplicating the key. min_seq_read_mb_s/min_seq_write_mb_s use
    /// the exact key names CONTRACT.md's ssd config thresholds them against, same "same key name
    /// as the threshold it's compared against" convention as max_temp_c elsewhere - present only
    /// when DiskSpdRunner actually parsed a value (e.g. absent if a pass was stopped early).</summary>
    internal static Dictionary<string, object> BuildSsdSummaryStats(
        IReadOnlyDictionary<string, object> smartStats, double? seqReadMbS, double? seqWriteMbS, int errorCount)
    {
        var summary = new Dictionary<string, object>(smartStats) { ["error_count"] = errorCount };
        if (seqReadMbS is double r) summary["min_seq_read_mb_s"] = r;
        if (seqWriteMbS is double w) summary["min_seq_write_mb_s"] = w;
        return summary;
    }

    /// <summary>
    /// Pure parser for TM5's Log.txt, split out so it's unit testable without a real running TM5.
    /// Reuses TM5Runner.CountErrors against the whole file content each poll (files here are small
    /// - a handful of lines per session - so re-scanning from scratch each cycle rather than
    /// tracking a read position is simple and cheap). TestedMb comes from the last "Testing N MB
    /// x..." line found; null if no such line has been written yet.
    /// </summary>
    internal static RamTestProgress ParseRamLog(string logContent)
    {
        double? testedMb = null;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
            logContent, @"Testing (\d+(?:\.\d+)?) MB x"))
        {
            testedMb = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        return new RamTestProgress(testedMb, TM5Runner.CountErrors(logContent));
    }

    /// <summary>Signals the running test to stop (technician clicked Stop). The in-flight
    /// RunCpuTestSessionAsync call still completes the test run and ends the session cleanly
    /// with stop_reason=user_abort - this just requests that, it doesn't tear anything down
    /// itself.</summary>
    public void RequestStop()
    {
        _seriesCancelled = true; // also stops a series from starting its next test
        _stopCts?.Cancel();
    }

    /// <summary>
    /// Runs several tests one after another inside ONE server session, each as its own test run
    /// (CONTRACT.md §2 allows many test_runs per session, and §6's rule is satisfied because they
    /// never overlap). Results are submitted exactly as for a single test. A test that throws is
    /// logged and skipped so one failure doesn't discard the rest; Stop ends the current test and
    /// skips the remaining ones.
    /// </summary>
    public async Task RunSeriesAsync(
        CreateSessionRequest sessionInfo, IReadOnlyList<TestKind> tests, SsdDriveOption? ssdDrive, string ssdFolder,
        Action<string> onLog, Action<TestKind, int, int>? onStep,
        Action<SensorReadings>? onCpu, Action<GpuReadings?>? onGpu, Action<RamTestProgress>? onRam,
        Action<double?, double?>? onSsdSpeed = null, CancellationToken uiLifetime = default)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("A test session is already running.");
        }

        IsRunning = true;
        _seriesCancelled = false;
        string? sessionId = null;
        try
        {
            onLog("Creating session (POST /api/sessions)...");
            sessionId = await _api.CreateSessionAsync(sessionInfo, uiLifetime).ConfigureAwait(false);
            onLog($"Session created: {sessionId} - {tests.Count} test(s) will run in series.");
            var series = new SeriesSession(sessionId);

            for (var i = 0; i < tests.Count; i++)
            {
                if (_seriesCancelled)
                {
                    onLog("Series stopped - remaining tests skipped.");
                    break;
                }

                var test = tests[i];
                onStep?.Invoke(test, i + 1, tests.Count);
                onLog($"=== Test {i + 1}/{tests.Count}: {test} ===");
                try
                {
                    switch (test)
                    {
                        case TestKind.Cpu:
                            await RunCpuTestSessionAsync(sessionInfo, onLog, onCpu, uiLifetime, series).ConfigureAwait(false);
                            break;
                        case TestKind.Gpu:
                            await RunGpuTestSessionAsync(sessionInfo, onLog, onGpu, uiLifetime, series).ConfigureAwait(false);
                            break;
                        case TestKind.Vram:
                            await RunVramTestSessionAsync(sessionInfo, onLog, onGpu, uiLifetime, series).ConfigureAwait(false);
                            break;
                        case TestKind.Ram:
                            await RunRamTestSessionAsync(sessionInfo, onLog, onRam, uiLifetime, series, onCpu).ConfigureAwait(false);
                            break;
                        case TestKind.Ssd:
                            if (ssdDrive is null)
                            {
                                onLog("SSD test skipped: no drive selected.");
                                break;
                            }

                            await RunSsdTestSessionAsync(sessionInfo, ssdDrive.Drive, ssdFolder, onLog, uiLifetime, series, onSsdSpeed).ConfigureAwait(false);
                            break;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    onLog($"{test} test failed: {ex.Message} - continuing with the next test.");
                }
            }
        }
        finally
        {
            if (sessionId is not null)
            {
                try
                {
                    onLog("Ending session (PATCH /api/sessions/:id)...");
                    await _api.EndSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                    onLog("Session ended.");
                }
                catch (Exception ex)
                {
                    onLog($"Warning: failed to end session cleanly: {ex.Message}");
                }
            }

            IsRunning = false;
        }
    }

    /// <summary>Reads a file another process still has open for writing (TestMem5's Log.txt) - the
    /// default File.ReadAllText refuses that because it does not allow sharing with a writer.</summary>
    private static async Task<string> ReadSharedAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s[..maxLen] + "\n...[truncated]";

    /// <summary>
    /// Pure summary_stats builder (CONTRACT.md §2 test_runs.summary_stats / §7). Split out from
    /// the run loop so it can be unit tested without the rest of the orchestration. Mirrors the
    /// exact current logic: "error_count" is always present; "max_temp_c" is present only when
    /// a max temperature was observed during the run; "avg_load_pct" is present only when at
    /// least one load sample was averaged (avgLoadPct is expected to already be null in that
    /// case, computed by the caller as totalLoad / loadSamples guarded by loadSamples > 0).
    /// </summary>
    internal static Dictionary<string, object> BuildSummaryStats(int errorCount, double? maxTempObserved, double? avgLoadPct)
    {
        var summary = new Dictionary<string, object> { ["error_count"] = errorCount };
        if (maxTempObserved is double mt) summary["max_temp_c"] = mt;
        if (avgLoadPct is double al) summary["avg_load_pct"] = al;
        return summary;
    }

    /// <summary>
    /// GPU equivalent of <see cref="BuildSummaryStats"/> - no avg_load_pct, since CONTRACT.md
    /// §3's gpu config subtree only thresholds error_count/max_temp_c (no load-based threshold
    /// exists for gpu, same as cpu's avg_load_pct being informational-only there too - kept out
    /// here to match what's actually in the gpu config, rather than carrying an unused field).
    /// </summary>
    internal static Dictionary<string, object> BuildGpuSummaryStats(int errorCount, double? maxTempObserved)
    {
        var summary = new Dictionary<string, object> { ["error_count"] = errorCount };
        if (maxTempObserved is double mt) summary["max_temp_c"] = mt;
        return summary;
    }

    /// <summary>RAM equivalent of BuildSummaryStats/BuildGpuSummaryStats - CONTRACT.md §3's ram
    /// config subtree only thresholds error_count (max_errors), so that's the only key here.</summary>
    internal static Dictionary<string, object> BuildRamSummaryStats(int errorCount) =>
        new() { ["error_count"] = errorCount };

    public async ValueTask DisposeAsync()
    {
        _api.Dispose();
        if (_telemetry is not null)
        {
            await _telemetry.DisposeAsync().ConfigureAwait(false);
        }
    }
}
