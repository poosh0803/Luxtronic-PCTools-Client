using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Luxtronic.PCTools.Models;

namespace Luxtronic.PCTools.Services;

public sealed class FurMarkRunResult
{
    public int? ExitCode { get; init; }
    public string RawOutput { get; init; } = "";
    public int ErrorCount { get; init; }

    /// <summary>Null = normal finish (FurMark's own --max-time elapsed and it exited with code 0).
    /// Models.StopReason.ToolCrash = it exited on its own with a non-zero exit code. Never set to
    /// UserAbort/ClientError here - see Prime95RunResult's identical remark, same reasoning.</summary>
    public string? StopReason { get; init; }

    /// <summary>True if RunAsync returned because externalStop was cancelled, rather than the
    /// process exiting on its own.</summary>
    public bool WasExternallyStopped { get; init; }
}

/// <summary>
/// Runs FurMark 2's GPU stress-test demo for a configured duration and reports completion/errors.
/// Looks for the binary at {ToolsDirectory}/furmark.exe - intentionally never downloads it (see
/// tools/FurMark_win64/README.md).
///
/// Simpler than Prime95Runner in one respect: FurMark 2 has a real --max-time flag (seconds) that
/// makes it exit on its own when the configured duration elapses - confirmed by a real timed run
/// (no zombie process, clean shutdown log lines). Prime95's "-t" torture mode has no equivalent, so
/// Prime95Runner has to enforce the duration externally and kill the process; this wrapper only
/// needs to Kill() for the "technician clicked Stop early" case.
///
/// IMPORTANT - not fully validated against a real detected artifact. --artifact-scanner is enabled
/// on every run (confirmed via a real run: its startup line "[ArtifactScanner] Artifact scanner
/// for GPU stability checking." appears in _furmark_log.txt), but the short clean test run used to
/// ground-truth this class found no artifacts, as expected - the exact log line format when one
/// *is* detected has not been observed. CountArtifacts's pattern is a reasonable best guess (any
/// "[ArtifactScanner]" line beyond the startup announcement), not a confirmed match - re-check
/// against a real detection if GPU test error_count ever looks suspiciously always-zero.
/// </summary>
public sealed class FurMarkRunner
{
    private static readonly Regex ArtifactLinePattern = new(
        @"\[ArtifactScanner\](?!\s*Artifact scanner for GPU stability checking\.)",
        RegexOptions.Compiled);

    private readonly string _toolsDirectory;

    public FurMarkRunner(string toolsDirectory)
    {
        _toolsDirectory = toolsDirectory;
    }

    public string ExePath => Path.Combine(_toolsDirectory, "furmark.exe");

    public bool IsExePresent => File.Exists(ExePath);

    /// <summary>
    /// Runs FurMark's furmark-gl stress-test demo for cfg.DurationMinutes, or until
    /// <paramref name="externalStop"/> is signalled (technician clicked Stop). <paramref
    /// name="onLog"/> receives human-readable progress lines for the UI log only, per the "no
    /// results in the client UI" rule.
    /// </summary>
    public async Task<FurMarkRunResult> RunAsync(
        GpuConfig cfg,
        CancellationToken externalStop,
        Action<string> onLog,
        CancellationToken ct = default)
    {
        if (!IsExePresent)
        {
            throw new FileNotFoundException(
                $"furmark.exe not found at '{ExePath}'. Drop the real FurMark 2 install there first - " +
                "see tools/FurMark_win64/README.md.", ExePath);
        }

        var maxTimeSeconds = Math.Max(1, cfg.DurationMinutes * 60);
        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = $"--demo furmark-gl --max-time {maxTimeSeconds} --artifact-scanner",
            WorkingDirectory = _toolsDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var stdout = new StringBuilder();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (stdout) stdout.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (stdout) stdout.AppendLine(e.Data); } };

        onLog($"Starting FurMark (furmark-gl) from {ExePath}, duration={cfg.DurationMinutes}min");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var processExitTcs = new TaskCompletionSource();
        process.Exited += (_, _) => processExitTcs.TrySetResult();

        var stopTask = WaitForCancellationAsync(externalStop);
        var completed = await Task.WhenAny(processExitTcs.Task, stopTask).ConfigureAwait(false);

        string? stopReason;
        var wasExternallyStopped = false;
        int? exitCode = null;
        if (completed == stopTask)
        {
            stopReason = null; // caller (TestSessionController) decides: user_abort vs client_error
            wasExternallyStopped = true;
            onLog("Stop requested - terminating FurMark.");
            TryKill(process);
            await WaitBrieflyForExitAsync(process).ConfigureAwait(false);
            if (process.HasExited)
            {
                exitCode = process.ExitCode;
            }
        }
        else
        {
            // FurMark exited on its own - either --max-time elapsed normally (exit code 0,
            // confirmed by a real timed run) or it crashed/errored before completing. Standard
            // process-exit-code convention, not a timing heuristic - simpler and doesn't need an
            // unvalidated "was this close enough to the configured duration" guess.
            exitCode = process.ExitCode;
            if (exitCode == 0)
            {
                stopReason = null;
                onLog($"FurMark finished (configured duration reached, exit code 0).");
            }
            else
            {
                stopReason = StopReason.ToolCrash;
                onLog($"FurMark exited on its own with exit code {exitCode} - treating as a tool crash.");
            }
        }

        var finalOutput = ReadAll(stdout);
        var logPath = Path.Combine(_toolsDirectory, "_furmark_log.txt");
        if (File.Exists(logPath))
        {
            try
            {
                finalOutput += Environment.NewLine + "--- _furmark_log.txt ---" + Environment.NewLine +
                               await File.ReadAllTextAsync(logPath, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // File may still be locked by the just-killed process - not fatal, stdout capture stands alone.
            }
        }

        return new FurMarkRunResult
        {
            ExitCode = exitCode,
            RawOutput = finalOutput,
            ErrorCount = CountArtifacts(finalOutput),
            StopReason = stopReason,
            WasExternallyStopped = wasExternallyStopped,
        };
    }

    internal static int CountArtifacts(string output) => ArtifactLinePattern.Matches(output).Count;

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
}
