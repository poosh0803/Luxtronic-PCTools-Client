using System.IO;
using Luxtronic.PCTools.Models;

namespace Luxtronic.PCTools.Services;

/// <summary>One RAM test log-poll cycle's parsed state - see
/// <see cref="TestSessionController.ParseRamLog"/>.</summary>
public sealed record RamTestProgress(double? TestedMb, int ErrorCount);

/// <summary>
/// Orchestrates one full CPU, GPU, or RAM test session end to end: fetch config -> create session
/// -> start test run -> connect telemetry -> run Prime95/FurMark/TM5 while streaming sensor
/// samples -> complete test run -> end session. RunCpuTestSessionAsync/RunGpuTestSessionAsync/
/// RunRamTestSessionAsync are standalone and mutually exclusive (share the same
/// <see cref="IsRunning"/> guard) - not CONTRACT.md §6's "together" mode (cpu+gpu running
/// concurrently), which the server already supports (concurrency.js) but this client doesn't
/// attempt yet; that would need reworking this class's single-run-at-a-time design and the UI's
/// single Start/Stop button pair. For RAM specifically, sharing IsRunning isn't just a
/// simplification - CONTRACT.md §6 requires RAM to reject if ANY other test_run is active, so this
/// already-existing guard is what makes that real server-enforced rule hold client-side too. SSD
/// orchestration (beyond the passive SubmitSsdSmartDataAsync below) is intentionally not built
/// here.
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
    private readonly TM5Runner _tm5;
    private readonly AppSettingsProvider _settings;
    private readonly ApiKeyProvider _apiKey;

    private TelemetryPublisher? _telemetry;
    private CancellationTokenSource? _stopCts;
    private bool _stopWasSensorFailure;

    public bool IsRunning { get; private set; }

    /// <summary>Fetches the current server config (CONTRACT.md §3) without starting a session or
    /// test run - used by the UI to show the configured CPU/GPU/RAM duration before the technician
    /// clicks Start, not just after. RunCpuTestSessionAsync/RunGpuTestSessionAsync/
    /// RunRamTestSessionAsync fetch their own fresh copy at test-run time regardless (config could
    /// change between an early UI display and an actual run), so this is purely informational.</summary>
    public Task<ServerConfig> GetConfigAsync(CancellationToken ct = default) => _api.GetConfigAsync(ct);

    public TestSessionController(
        AppSettingsProvider settings, ApiKeyProvider apiKey, SensorMonitor sensors,
        Prime95Runner prime95, FurMarkRunner furmark, TM5Runner tm5)
    {
        _settings = settings;
        _apiKey = apiKey;
        _sensors = sensors;
        _prime95 = prime95;
        _furmark = furmark;
        _tm5 = tm5;
        _api = new LuxApiClient(settings.ServerBaseUrl, apiKey.Key);
    }

    /// <param name="uiLifetime">Cancelled only if the app is shutting down - NOT used for the
    /// Stop button, which goes through <see cref="RequestStop"/> instead so a clean
    /// complete-test-run/end-session sequence still runs afterward.</param>
    /// <param name="onReading">Invoked once per sensor poll cycle (~1/sec) with the exact reading
    /// just sent as telemetry, so the UI can show live values instead of only log lines. Called
    /// from the background poll loop's thread - callers must marshal to the UI thread themselves.</param>
    public async Task RunCpuTestSessionAsync(
        CreateSessionRequest sessionInfo, Action<string> onLog, Action<SensorReadings>? onReading = null,
        CancellationToken uiLifetime = default)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("A test session is already running.");
        }

        IsRunning = true;
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
            var config = await _api.GetConfigAsync(uiLifetime).ConfigureAwait(false);
            var cpuCfg = config.Cpu ?? throw new InvalidOperationException(
                "Server config response had no 'cpu' subtree - check CONTRACT.md §3 shape matches.");

            onLog("Creating session (POST /api/sessions)...");
            sessionId = await _api.CreateSessionAsync(sessionInfo, uiLifetime).ConfigureAwait(false);
            onLog($"Session created: {sessionId}");

            onLog("Starting CPU test run (POST .../test-runs)...");
            testRunId = await _api.StartTestRunAsync(sessionId, Component.Cpu, uiLifetime).ConfigureAwait(false);
            onLog($"Test run started: {testRunId}");

            _telemetry = new TelemetryPublisher();
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

            onLog("Completing test run (PATCH .../test-runs/:id)...");
            await _api.CompleteTestRunAsync(sessionId, testRunId, new CompleteTestRunRequest
            {
                ToolExitCode = runResult.ExitCode,
                ToolOutputRaw = Truncate(runResult.RawOutput, 200_000),
                SummaryStats = summary,
                StopReason = effectiveStopReason,
            }, uiLifetime).ConfigureAwait(false);
            onLog("Test run completion recorded on the server.");

            await SubmitSsdSmartDataAsync(sessionId, onLog, uiLifetime).ConfigureAwait(false);
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

            if (_telemetry is not null)
            {
                await _telemetry.DisposeAsync().ConfigureAwait(false);
                _telemetry = null;
            }

            IsRunning = false;
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
    public async Task RunGpuTestSessionAsync(
        CreateSessionRequest sessionInfo, Action<string> onLog, Action<GpuReadings>? onReading = null,
        CancellationToken uiLifetime = default)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("A test session is already running.");
        }

        IsRunning = true;
        _stopCts = new CancellationTokenSource();
        _stopWasSensorFailure = false;

        string? sessionId = null;
        string? testRunId = null;
        var consecutiveSensorFailures = 0;
        double? maxTemp = null;

        try
        {
            onLog("Fetching server config (GET /api/config)...");
            var config = await _api.GetConfigAsync(uiLifetime).ConfigureAwait(false);
            var gpuCfg = config.Gpu ?? throw new InvalidOperationException(
                "Server config response had no 'gpu' subtree - check CONTRACT.md §3 shape matches.");

            onLog("Creating session (POST /api/sessions)...");
            sessionId = await _api.CreateSessionAsync(sessionInfo, uiLifetime).ConfigureAwait(false);
            onLog($"Session created: {sessionId}");

            onLog("Starting GPU test run (POST .../test-runs)...");
            testRunId = await _api.StartTestRunAsync(sessionId, Component.Gpu, uiLifetime).ConfigureAwait(false);
            onLog($"Test run started: {testRunId}");

            _telemetry = new TelemetryPublisher();
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

            var runResult = await _furmark.RunAsync(gpuCfg, _stopCts.Token, onLog, uiLifetime).ConfigureAwait(false);

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

            onLog("Completing test run (PATCH .../test-runs/:id)...");
            await _api.CompleteTestRunAsync(sessionId, testRunId, new CompleteTestRunRequest
            {
                ToolExitCode = runResult.ExitCode,
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

            if (_telemetry is not null)
            {
                await _telemetry.DisposeAsync().ConfigureAwait(false);
                _telemetry = null;
            }

            IsRunning = false;
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
        CancellationToken uiLifetime = default)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("A test session is already running.");
        }

        IsRunning = true;
        _stopCts = new CancellationTokenSource();
        _stopWasSensorFailure = false;

        string? sessionId = null;
        string? testRunId = null;
        var maxErrorsObserved = 0;
        var logPath = Path.Combine(_settings.RamToolsDirectory, "Log.txt");

        try
        {
            onLog("Fetching server config (GET /api/config)...");
            var config = await _api.GetConfigAsync(uiLifetime).ConfigureAwait(false);
            var ramCfg = config.Ram ?? throw new InvalidOperationException(
                "Server config response had no 'ram' subtree - check CONTRACT.md §3 shape matches.");

            onLog("Creating session (POST /api/sessions)...");
            sessionId = await _api.CreateSessionAsync(sessionInfo, uiLifetime).ConfigureAwait(false);
            onLog($"Session created: {sessionId}");

            onLog("Starting RAM test run (POST .../test-runs)...");
            testRunId = await _api.StartTestRunAsync(sessionId, Component.Ram, uiLifetime).ConfigureAwait(false);
            onLog($"Test run started: {testRunId}");

            _telemetry = new TelemetryPublisher();
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
                        if (File.Exists(logPath))
                        {
                            var content = await File.ReadAllTextAsync(logPath, pollCts.Token).ConfigureAwait(false);
                            var snapshot = ParseRamLog(content);
                            maxErrorsObserved = Math.Max(maxErrorsObserved, snapshot.ErrorCount);
                            onProgress?.Invoke(snapshot);

                            var ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                            var samples = new List<TelemetrySample>(2);
                            if (snapshot.TestedMb is double mb)
                            {
                                samples.Add(new TelemetrySample { TestRunId = capturedTestRunId, Ts = ts, SensorName = "ram_tested_mb", Value = mb });
                            }
                            samples.Add(new TelemetrySample { TestRunId = capturedTestRunId, Ts = ts, SensorName = "ram_errors", Value = snapshot.ErrorCount });

                            foreach (var s in samples)
                            {
                                await _telemetry.SendSampleAsync(s, pollCts.Token).ConfigureAwait(false);
                            }
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

            onLog("Completing test run (PATCH .../test-runs/:id)...");
            await _api.CompleteTestRunAsync(sessionId, testRunId, new CompleteTestRunRequest
            {
                ToolExitCode = runResult.ExitCode,
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

            if (_telemetry is not null)
            {
                await _telemetry.DisposeAsync().ConfigureAwait(false);
                _telemetry = null;
            }

            IsRunning = false;
            _stopCts?.Dispose();
            _stopCts = null;
        }
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

    /// <summary>
    /// Reports SMART data for every detected drive as its own SSD test_run, one
    /// start+complete pair per drive (SsdSmartInfo has no clean way to represent multiple
    /// drives in a single flat summary_stats object - see SSD_SMART_ADDENDUM.md in the shared
    /// planning repo for the full design/rationale, written for the server side to implement
    /// matching support against). Runs after the CPU test_run has already completed, so it can't
    /// trip CONTRACT.md §6's exclusive-concurrency rule for ssd. This is a passive read, not a
    /// benchmark - no CrystalDiskMark wrapper exists yet, so min_seq_read_mb_s/min_seq_write_mb_s
    /// simply go unevaluated server-side (per the server's own documented convention: thresholds
    /// with no matching summary_stats key aren't checked).
    ///
    /// Best-effort: a failure here (e.g. the server doesn't support component=ssd yet) is logged
    /// but does not fail the overall CPU test session, since CPU is this pass's actual scope.
    /// </summary>
    private async Task SubmitSsdSmartDataAsync(string sessionId, Action<string> onLog, CancellationToken ct)
    {
        IReadOnlyList<SsdSmartInfo> drives;
        try
        {
            drives = _sensors.ReadSsds();
        }
        catch (Exception ex)
        {
            onLog($"Skipping SSD SMART reporting - drive read failed: {ex.Message}");
            return;
        }

        foreach (var drive in drives)
        {
            try
            {
                onLog($"Reporting SMART data for {drive.Model} ({drive.SerialNumber ?? "no serial"})...");
                var ssdTestRunId = await _api.StartTestRunAsync(sessionId, Component.Ssd, ct).ConfigureAwait(false);
                await _api.CompleteTestRunAsync(sessionId, ssdTestRunId, new CompleteTestRunRequest
                {
                    ToolExitCode = null,
                    ToolOutputRaw = SsdSmartReader.FormatToolOutputRaw(drive),
                    SummaryStats = SsdSmartReader.BuildSummaryStats(drive),
                    StopReason = null,
                }, ct).ConfigureAwait(false);
                onLog($"SMART data reported for {drive.Model}.");
            }
            catch (Exception ex)
            {
                onLog($"WARNING: failed to report SMART data for {drive.Model}: {ex.Message}");
            }
        }
    }

    /// <summary>Signals the running test to stop (technician clicked Stop). The in-flight
    /// RunCpuTestSessionAsync call still completes the test run and ends the session cleanly
    /// with stop_reason=user_abort - this just requests that, it doesn't tear anything down
    /// itself.</summary>
    public void RequestStop() => _stopCts?.Cancel();

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
