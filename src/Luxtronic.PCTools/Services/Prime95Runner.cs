using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Luxtronic.PCTools.Models;

namespace Luxtronic.PCTools.Services;

public sealed class Prime95RunResult
{
    public int? ExitCode { get; init; }
    public string RawOutput { get; init; } = "";
    public int ErrorCount { get; init; }

    /// <summary>
    /// Null = normal finish (ran the full configured duration, or Prime95 itself reported a
    /// result and exited). Models.StopReason.ToolCrash = this runner detected an unexplained
    /// self-exit. Never set to UserAbort/ClientError here - this runner doesn't know *why*
    /// <c>externalStop</c> was signalled (Stop button vs. a sensor failure upstream), so when
    /// <see cref="WasExternallyStopped"/> is true, the caller decides the actual stop_reason.
    /// </summary>
    public string? StopReason { get; init; }

    /// <summary>True if RunAsync returned because externalStop was cancelled, rather than the
    /// configured duration elapsing or the process exiting on its own.</summary>
    public bool WasExternallyStopped { get; init; }
}

/// <summary>
/// Launches Prime95 in headless Torture Test mode ("-t") for a configured duration and
/// reports completion/errors. Looks for the binary at {ToolsDirectory}/prime95.exe -
/// intentionally never downloads it (see tools/prime95/README.md).
///
/// IMPORTANT - not yet validated against a real prime95.exe (none is bundled per the task's
/// instructions not to auto-fetch third-party binaries). Two things here are best-effort
/// judgment calls that need re-checking once a real binary is dropped in tools/prime95/:
///
/// 1. Config file format/keys in WritePrimeConfig() below - Prime95's local.txt/prime.txt
///    torture-test key names have drifted across versions and aren't documented in
///    CONTRACT.md beyond "-t flag + prime.txt config for FFT range/mode". This writes both
///    local.txt and prime.txt with the same best-known-good keys, since different Prime95
///    versions read torture-test settings from one or the other.
/// 2. Stop mechanism - Prime95 in "-t" headless mode has no documented graceful
///    stop-after-N-minutes flag; a burn-in duration is enforced externally. This wrapper
///    kills the process tree once the configured duration elapses. Results/error-scanning is
///    still done against whatever it wrote to results.txt/stdout up to that point.
/// </summary>
public sealed class Prime95Runner
{
    private static readonly Regex ErrorLinePattern = new(
        @"FATAL ERROR|ROUNDOFF ERROR|error occurred|Hardware failure",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _toolsDirectory;

    public Prime95Runner(string toolsDirectory)
    {
        _toolsDirectory = toolsDirectory;
    }

    public string ExePath => Path.Combine(_toolsDirectory, "prime95.exe");

    public bool IsExePresent => File.Exists(ExePath);

    /// <summary>
    /// Runs Prime95 for cfg.DurationMinutes, or until <paramref name="externalStop"/> is
    /// signalled (technician clicked Stop). <paramref name="onLog"/> receives human-readable
    /// progress lines for the UI log - it never receives pass/fail info, only operational
    /// status, per the "no results in the client UI" rule.
    /// </summary>
    public async Task<Prime95RunResult> RunAsync(
        CpuConfig cfg,
        CancellationToken externalStop,
        Action<string> onLog,
        CancellationToken ct = default)
    {
        if (!IsExePresent)
        {
            throw new FileNotFoundException(
                $"prime95.exe not found at '{ExePath}'. Drop the real binary there first - " +
                "see tools/prime95/README.md.", ExePath);
        }

        WritePrimeConfig(cfg);

        // Background mode starts Prime95 through the shell with a "hidden" show-state, which a
        // program normally applies to its first window - so the window may never appear at all. (Output
        // can't be redirected that way; Prime95 is a GUI program that writes nothing to stdout, and its
        // errors come from results.txt, which is read after the run.) The window-hiding loop below is the
        // fallback if Prime95 ignores the show-state.
        var redirect = !cfg.HideWindow;
        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = "-t",
            WorkingDirectory = _toolsDirectory,
            UseShellExecute = !redirect,
            WindowStyle = redirect ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
            RedirectStandardOutput = redirect,
            RedirectStandardError = redirect,
            CreateNoWindow = redirect,
        };

        var stdout = new StringBuilder();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        if (redirect)
        {
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (stdout) stdout.AppendLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (stdout) stdout.AppendLine(e.Data); } };
        }

        onLog($"Starting Prime95 (-t) from {ExePath}, mode={cfg.Mode}{(cfg.DisableAvx ? " (AVX off)" : "")}, duration={cfg.DurationMinutes}min");
        process.Start();
        if (redirect)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        // Prime95 is a normal Windows program with no hidden/silent switch, so "background" means
        // hiding its window from here - repeatedly, since it creates and re-shows windows while
        // starting up. A hidden window has no taskbar button either.
        using var hideCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (cfg.HideWindow)
        {
            _ = WindowHider.KeepHiddenAsync(process.Id, "Prime95", onLog, hideCts.Token);
        }

        var processExitTcs = new TaskCompletionSource();
        process.Exited += (_, _) => processExitTcs.TrySetResult();

        var durationTask = Task.Delay(TimeSpan.FromMinutes(cfg.DurationMinutes), ct);
        var stopTask = WaitForCancellationAsync(externalStop);

        var completed = await Task.WhenAny(processExitTcs.Task, durationTask, stopTask).ConfigureAwait(false);

        string? stopReason;
        var wasExternallyStopped = false;
        if (completed == processExitTcs.Task)
        {
            // Prime95 exited on its own before the configured duration elapsed. If it logged
            // an error pattern on the way out, that's a valid "the test found a problem"
            // outcome (§7: fail on error_count > 0) rather than a crash, so stop_reason stays
            // null and the error is reported via summary_stats.error_count instead. Only an
            // unexplained self-exit (no error text, unexpected) is treated as tool_crash.
            var outputSoFar = ReadAll(stdout);
            var errCount = CountErrors(outputSoFar);
            stopReason = errCount > 0 ? null : StopReason.ToolCrash;
            onLog(errCount > 0
                ? $"Prime95 exited on its own after logging {errCount} error line(s)."
                : "Prime95 exited on its own before the configured duration elapsed with no error lines detected - treating as a tool crash.");
        }
        else if (completed == stopTask)
        {
            stopReason = null; // caller (TestSessionController) decides: user_abort vs client_error
            wasExternallyStopped = true;
            onLog("Stop requested - terminating Prime95.");
            TryKill(process);
            await WaitBrieflyForExitAsync(process).ConfigureAwait(false);
        }
        else
        {
            stopReason = null; // normal finish: ran the full configured duration
            onLog($"Configured duration ({cfg.DurationMinutes} min) elapsed - stopping Prime95.");
            TryKill(process);
            await WaitBrieflyForExitAsync(process).ConfigureAwait(false);
        }

        var finalOutput = ReadAll(stdout);
        // Prime95 also writes a results.txt in its working directory - fold it in if present,
        // since some builds log errors there more reliably than to stdout in -t mode.
        var resultsTxtPath = Path.Combine(_toolsDirectory, "results.txt");
        if (File.Exists(resultsTxtPath))
        {
            try
            {
                finalOutput += Environment.NewLine + "--- results.txt ---" + Environment.NewLine +
                               await File.ReadAllTextAsync(resultsTxtPath, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // File may still be locked by the just-killed process - not fatal, stdout capture stands alone.
            }
        }

        hideCts.Cancel();

        return new Prime95RunResult
        {
            ExitCode = process.HasExited ? process.ExitCode : null,
            RawOutput = finalOutput,
            ErrorCount = CountErrors(finalOutput),
            StopReason = stopReason,
            WasExternallyStopped = wasExternallyStopped,
        };
    }

    /// <summary>
    /// Kills Prime95 / TestMem5 processes (by exe name, e.g. "prime95.exe") left behind by an app that crashed or was killed while a test ran -
    /// with the window hidden they would otherwise keep the CPU loaded with nothing to see. Only our
    /// own bundled copy is touched (matched by its folder), and only when its parent process is gone,
    /// so a second running copy of this app is never disturbed.
    /// </summary>
    public static void KillStrayProcesses(string exeName, string toolsDirectory, Action<string> onLog)
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT ProcessId, ParentProcessId, ExecutablePath FROM Win32_Process WHERE Name = '{exeName}'");
            foreach (var item in searcher.Get().Cast<System.Management.ManagementObject>())
            {
                var path = item["ExecutablePath"] as string;
                if (path is null || !path.StartsWith(toolsDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var pid = Convert.ToInt32(item["ProcessId"]);
                var parent = Convert.ToInt32(item["ParentProcessId"]);
                if (IsRunning(parent))
                {
                    continue; // still owned by a live app
                }

                using var stray = Process.GetProcessById(pid);
                stray.Kill(entireProcessTree: true);
                onLog($"Stopped a leftover {exeName} process (pid {pid}) from an earlier run.");
            }
        }
        catch (Exception ex)
        {
            onLog($"Could not check for leftover {exeName} processes: {ex.Message}");
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static int CountErrors(string output) => ErrorLinePattern.Matches(output).Count;

    private static string ReadAll(StringBuilder sb)
    {
        lock (sb) return sb.ToString();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Process may have exited between the HasExited check and Kill() - fine, that's the goal anyway.
        }
    }

    private static async Task WaitBrieflyForExitAsync(Process process)
    {
        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Gave it 10s to die after Kill(); moving on regardless so the UI doesn't hang.
        }
    }

    private static Task WaitForCancellationAsync(CancellationToken token)
    {
        var tcs = new TaskCompletionSource();
        token.Register(() => tcs.TrySetResult());
        return tcs.Task;
    }

    /// <summary>
    /// Pure decision logic for the torture-test config: which FFT min/max bracket to use for
    /// the given mode string, and how many threads to torture-test with. Split out from
    /// <see cref="WritePrimeConfig"/> so it can be unit tested without touching the filesystem -
    /// mode matching is intentionally case-insensitive (OrdinalIgnoreCase), matching what
    /// Prime95's mode config values look like in practice.
    /// </summary>
    internal static (int MinFft, int MaxFft, int Threads) GetTortureTestParams(string mode)
    {
        var (minFft, maxFft) = mode.Equals("small_fft", StringComparison.OrdinalIgnoreCase)
            ? (4, 32)     // small FFTs, fits in cache -> max heat, minimal RAM exercise
            : (8, 4096);  // "blend" - wide FFT range, exercises RAM too

        var threads = Math.Max(1, Environment.ProcessorCount);

        return (minFft, maxFft, threads);
    }

    /// <summary>
    /// Writes Prime95's torture-test config. See the class-level remarks: this is a
    /// best-known-good template, not yet verified against a real binary.
    /// </summary>
    private void WritePrimeConfig(CpuConfig cfg)
    {
        var (minFft, maxFft, threads) = GetTortureTestParams(cfg.Mode);

        var lines = new List<string>
        {
            "V24OptionsConverted=1",
            "WGUID_version=2",
            "[PrimeNet]",
            "Debug=0",
            "[PrimeNet]", // some versions read this pair twice under different sections; harmless if duplicated
            "[Test]",
            "StressTester=1",
            "UsePrimenet=0",
            $"TortureThreads={threads}",
            $"MinTortureFFT={minFft}",
            $"MaxTortureFFT={maxFft}",
            "TortureMem=0",       // 0 = in-place, standard default for "blend"-style torture
            "TortureTime=15",     // minutes per FFT size before rotating; independent of overall run duration, which this wrapper enforces externally
        };

        if (cfg.DisableAvx)
        {
            // Documented in prime95's undoc.txt: override detected CPU features, so the torture
            // test falls back to SSE2 code - much less heat/power than AVX2/FMA3/AVX-512.
            lines.InsertRange(0, new[] { "CpuSupportsAVX=0", "CpuSupportsFMA3=0", "CpuSupportsFMA4=0", "CpuSupportsAVX2=0", "CpuSupportsAVX512F=0" });
        }

        var content = string.Join(Environment.NewLine, lines) + Environment.NewLine;

        // Written to both filenames since Prime95 versions disagree on which file holds
        // torture-test settings vs license/PrimeNet settings - see class remarks.
        File.WriteAllText(Path.Combine(_toolsDirectory, "prime.txt"), content);
        File.WriteAllText(Path.Combine(_toolsDirectory, "local.txt"), content);
    }
}
