using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Luxtronic.PCTools.Models;

namespace Luxtronic.PCTools.Services;

public sealed class TM5RunResult
{
    public int? ExitCode { get; init; }
    public string RawOutput { get; init; } = "";
    public int ErrorCount { get; init; }

    /// <summary>Null = normal finish (configured duration elapsed, killed cleanly). Models.
    /// StopReason.ToolCrash = TM5 exited on its own before the configured duration with no error
    /// lines found - an unexplained self-exit. Never set to UserAbort/ClientError here - same
    /// reasoning as Prime95RunResult/FurMarkRunResult, the caller decides which applies.</summary>
    public string? StopReason { get; init; }

    public bool WasExternallyStopped { get; init; }
}

/// <summary>
/// Runs TestMem5 (TM5) for a configured duration and reports completion/errors. Looks for the
/// binary at {ToolsDirectory}/TM5.exe - intentionally never downloads it (see
/// tools/TestMem5/README.md).
///
/// TM5 has no --max-time equivalent (its own "duration" concept is a fixed Cycles count per
/// .cfg, not wall-clock time) and no confirmed CLI arguments at all - a raw config-path argument
/// produced "Something is wrong on the command line. See help for correct usage." in testing.
/// Duration is enforced externally and the process killed when it elapses, the same shape as
/// Prime95Runner (not FurMarkRunner, which gets a real self-stopping flag from the tool itself).
///
/// IMPORTANT - config selection works by overwriting a hardcoded file path, not a CLI flag or a
/// "remembered last config" setting. Confirmed via real testing: TM5 always looks for
/// "bin\Universal 2 @ LMhz.cfg" on launch (removing that exact file produces a "file was not
/// found" error naming it specifically); TM5's own TM5.ini only stores window position, nothing
/// about which config was last used. RamProfileSelector picks which of the real shipped .cfg
/// files' *content* to copy onto that path before each run. The on-screen "Configuration" label
/// stays cosmetically tied to whatever text was last associated with that filename and does NOT
/// reflect the copied-in content - confirmed by overwriting it with a different profile and
/// seeing that profile's actual test parameters (a 31-step Test Sequence) run, while the label
/// stayed unchanged. Not a bug in this wrapper - just don't trust that label for anything.
///
/// CONFIRMED (via real testing, not just inferred): Log.txt does NOT update incrementally during a
/// run - TM5 holds it open exclusively for the whole run. Directly observed: the app's status log
/// repeated "RAM log poll failed this cycle: ... being used by another process" on every poll
/// after the first, for an entire run. TestSessionController's live telemetry gets exactly one
/// successful read (possibly stale, from a previous session, if TM5 hadn't written its own new
/// session's line yet) and then freezes for the rest of the run - a real UX gap, but the
/// summary_stats/final telemetry this class returns are unaffected, since RunAsync's own read
/// happens after the process has exited and released the lock.
///
/// Still NOT confirmed (flagged honestly rather than assumed): (1) what a real detected memory
/// error looks like in the log - no error occurred in any real test run done while building this,
/// so <see cref="CountErrors"/> is a best-guess pattern; (2) what a natural full-cycle completion
/// (all configured Cycles finished before our external duration elapsed) looks like - every real
/// test run done while building this was stopped early, on purpose; (3) **the external-duration-
/// kill branch below has never actually fired and been observed** - every real run was stopped
/// manually well before its configured duration elapsed. That's the single most important gap for
/// whoever picks this up next to close.
/// </summary>
public sealed class TM5Runner
{
    // Deliberately NOT a bare "error" substring match - TM5's own UI shows a live "Errors: 0"
    // style field (visible in real testing), and a bare match would false-positive on exactly
    // that kind of healthy zero-count summary line if Log.txt contains something similar. The
    // negative lookahead excludes "error"/"errors"/"error(s)" immediately followed by ": 0" or
    // " 0" (the "\(?s?\)?" covers the parenthesized-plural form specifically, confirmed necessary
    // - "Error(s): 0" was a real false positive against an earlier, simpler version of this
    // pattern that only handled a bare "s"), while still matching "error detected",
    // "3 errors found", "Errors: 2", etc. Still a best guess - see class remarks, no real
    // detected-error line was observed to confirm against.
    private static readonly Regex ErrorLinePattern = new(
        @"error(?!\(?s?\)?\s*:?\s*0\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string ActiveConfigFileName = "Universal 2 @ LMhz.cfg";

    private readonly string _toolsDirectory;

    public TM5Runner(string toolsDirectory)
    {
        _toolsDirectory = toolsDirectory;
    }

    public string ExePath => Path.Combine(_toolsDirectory, "TM5.exe");

    public bool IsExePresent => File.Exists(ExePath);

    public async Task<TM5RunResult> RunAsync(
        RamConfig cfg,
        CancellationToken externalStop,
        Action<string> onLog,
        CancellationToken ct = default)
    {
        if (!IsExePresent)
        {
            throw new FileNotFoundException(
                $"TM5.exe not found at '{ExePath}'. Drop the real TestMem5 install there first - " +
                "see tools/TestMem5/README.md.", ExePath);
        }

        ActivateConfigProfile(cfg.ConfigProfile, onLog);

        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            WorkingDirectory = _toolsDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var stdout = new StringBuilder();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        // TM5 is a windowed GUI app, not console-subsystem - these events are expected to fire
        // rarely or never (unconfirmed either way, see class remarks). Kept for parity with
        // Prime95Runner/FurMarkRunner and in case a future TM5 version does write to stdout.
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (stdout) stdout.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (stdout) stdout.AppendLine(e.Data); } };

        onLog($"Starting TM5 from {ExePath}, duration={cfg.DurationMinutes}min");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var processExitTcs = new TaskCompletionSource();
        process.Exited += (_, _) => processExitTcs.TrySetResult();

        var durationTask = Task.Delay(TimeSpan.FromMinutes(cfg.DurationMinutes), ct);
        var stopTask = WaitForCancellationAsync(externalStop);

        var completed = await Task.WhenAny(processExitTcs.Task, durationTask, stopTask).ConfigureAwait(false);

        string? stopReason;
        var wasExternallyStopped = false;
        if (completed == processExitTcs.Task)
        {
            // TM5 exited on its own before the configured duration elapsed - see Prime95Runner's
            // identical reasoning: only treated as a genuine tool crash if it didn't log anything
            // explaining why (e.g. a detected error causing it to stop).
            var outputSoFar = ReadAll(stdout);
            var errCount = CountErrors(outputSoFar);
            stopReason = errCount > 0 ? null : StopReason.ToolCrash;
            onLog(errCount > 0
                ? $"TM5 exited on its own after logging {errCount} error line(s)."
                : "TM5 exited on its own before the configured duration elapsed with no error lines detected - treating as a tool crash.");
        }
        else if (completed == stopTask)
        {
            stopReason = null; // caller (TestSessionController) decides: user_abort vs client_error
            wasExternallyStopped = true;
            onLog("Stop requested - terminating TM5.");
            TryKill(process);
            await WaitBrieflyForExitAsync(process).ConfigureAwait(false);
        }
        else
        {
            stopReason = null; // normal finish: ran the full configured duration
            onLog($"Configured duration ({cfg.DurationMinutes} min) elapsed - stopping TM5.");
            TryKill(process);
            await WaitBrieflyForExitAsync(process).ConfigureAwait(false);
        }

        var finalOutput = ReadAll(stdout);
        var logPath = Path.Combine(_toolsDirectory, "Log.txt");
        if (File.Exists(logPath))
        {
            try
            {
                finalOutput += Environment.NewLine + "--- Log.txt ---" + Environment.NewLine +
                               await File.ReadAllTextAsync(logPath, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // File may still be locked by the just-killed process - not fatal, stdout capture stands alone.
            }
        }

        return new TM5RunResult
        {
            ExitCode = process.HasExited ? process.ExitCode : null,
            RawOutput = finalOutput,
            ErrorCount = CountErrors(finalOutput),
            StopReason = stopReason,
            WasExternallyStopped = wasExternallyStopped,
        };
    }

    /// <summary>
    /// Copies the resolved profile's real .cfg content onto TM5's hardcoded active-config path -
    /// see class remarks for why this, not a CLI flag, is how config selection actually works.
    /// No-ops (with a log line) if the resolved profile IS the active-config file itself, since
    /// File.Copy throws when source and destination are the same path.
    /// </summary>
    private void ActivateConfigProfile(string configProfile, Action<string> onLog)
    {
        var binDir = Path.Combine(_toolsDirectory, "bin");
        var sourceFileName = RamProfileSelector.SelectConfigFileName(configProfile);
        var sourcePath = Path.Combine(binDir, sourceFileName);
        var activePath = Path.Combine(binDir, ActiveConfigFileName);

        onLog($"Selected RAM test profile: {sourceFileName} (requested config_profile={configProfile})");

        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(activePath), StringComparison.OrdinalIgnoreCase))
        {
            onLog($"{sourceFileName} is already TM5's active-config file - nothing to copy.");
            return;
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException(
                $"RAM test profile '{sourceFileName}' not found at '{sourcePath}' - the TestMem5 install may be incomplete.",
                sourcePath);
        }

        File.Copy(sourcePath, activePath, overwrite: true);
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
}
