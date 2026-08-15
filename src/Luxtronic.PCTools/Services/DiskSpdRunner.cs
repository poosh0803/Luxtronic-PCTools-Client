using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Luxtronic.PCTools.Models;

namespace Luxtronic.PCTools.Services;

public sealed class DiskSpdRunResult
{
    public int? ExitCode { get; init; }
    public string RawOutput { get; init; } = "";
    public double? SeqReadMbS { get; init; }
    public double? SeqWriteMbS { get; init; }

    /// <summary>Always 0 - DiskSpd has no "detected N errors but kept going" concept like
    /// Prime95/FurMark/TM5's error-scanning; any I/O failure aborts the pass with a non-zero exit
    /// code instead, which surfaces as <see cref="StopReason"/> = tool_crash rather than as a
    /// count. Kept as a field (rather than hardcoding 0 at the call site) purely to match the
    /// summary_stats builder shape every other component uses - see
    /// TestSessionController.BuildSsdBenchSummaryStats.</summary>
    public int ErrorCount { get; init; }

    /// <summary>Null = normal finish - includes the case where a pass's exit code was non-zero
    /// but a valid throughput reading was still parsed (see class remarks: ground-truthed live,
    /// this happens for real). Models.StopReason.ToolCrash = a pass produced NO usable "total:"
    /// reading at all (ground-truthed: a real run against an unreachable path returned exit code 1
    /// with "Error opening file: ..." / "Error generating I/O requests" and no results section).
    /// Never set to UserAbort/ClientError here - same reasoning as Prime95Runner/FurMarkRunner.</summary>
    public string? StopReason { get; init; }

    /// <summary>True if RunAsync returned because externalStop was cancelled, rather than either
    /// pass completing on its own.</summary>
    public bool WasExternallyStopped { get; init; }
}

/// <summary>
/// Benchmarks sequential read/write throughput via DiskSpd (Microsoft, MIT-licensed), the real
/// benchmarking engine CrystalDiskMark bundles and uses internally for its own numbers
/// (tools/CrystalDiskMark/CdmResource/DiskSpd/). CrystalDiskMark's own DiskMark64.exe has NO
/// command-line/automation surface at all - confirmed by scanning the binary directly for
/// autostart/silent/headless/cmdline/csv/export keywords (none exist anywhere in it). It's
/// GUI-only, results reachable only via a "Save (text)" file-save dialog or a clipboard copy
/// behind a menu click. Driving that GUI would mean coordinate-based UI automation against a
/// window that, like TM5, always runs elevated - the same fragile, hard-to-verify path the
/// RAM/TM5 work hit (see HANDOFF.md's "elevation boundary blocks a lot of automated verification"
/// section). DiskSpd instead has a real documented CLI and plain stdout text output, so this
/// follows the same subprocess+parse pattern as Prime95Runner/FurMarkRunner rather than TM5's
/// file-poll workaround.
///
/// Reproduces CrystalDiskMark's flagship "SEQ1M Q8T1" test: 1 MiB block size, queue depth 8, 1
/// thread, unbuffered/no-cache I/O (-Sh - disables both OS buffering and hardware write caching,
/// so results reflect real disk throughput, not RAM buffering). Two separate DiskSpd invocations
/// against one scratch file - 100% read (-w0), then 100% write (-w100) - never against a raw
/// physical device, per PROJECT_PLAN.md's "no destructive SSD write-stress testing" constraint;
/// the file is created with -c inside the caller-supplied target folder and deleted afterward in
/// a finally block regardless of outcome. DiskSpd does NOT clean up its own test file - confirmed
/// directly during ground-truthing (the file was still present, full size, after a completed run).
///
/// Test file size (1 GiB) and per-direction duration (10s) are hardcoded here rather than
/// server-configured - CONTRACT.md §3's ssd subtree has no duration/size field, unlike
/// cpu/gpu/ram's duration_minutes (see SsdConfig's remarks) - a deliberate scope decision to
/// avoid touching the frozen contract for a first pass.
///
/// GROUND-TRUTHED text output shape (real runs, this dev machine, 2026-08-15): each pass prints a
/// "Total IO" section (plus separate "Read IO"/"Write IO" sections, which duplicate "Total IO"
/// exactly for a single-mode -w0/-w100 run and are therefore not separately parsed) ending in a
/// "total:" line shaped like:
///   total:        4418699264 |         4214 |    2104.65 |    2104.65
/// pipe-delimited as bytes | I/Os | MiB/s | I/O per s - ParseTotalMbS below takes field index 2.
/// Also noteworthy: this bundled DiskSpd build prints "Score: N" and "averageLatency: 0.000000"
/// lines not present in upstream DiskSpd's documented output (apparently a CrystalDiskMark-specific
/// fork/build) - neither is used here. A forced real failure (nonexistent target path) confirmed
/// exit code 1 with "Error opening file: ..." on stdout/stderr, "Error generating I/O requests",
/// and critically NO "Total IO"/"total:" section at all.
///
/// IMPORTANT - exit code alone is NOT a reliable crash signal, despite that forced-failure test
/// making it look that way. Ground-truthed live on a real technician run (2026-08-15, real Micron
/// NVMe boot drive): both passes printed complete, valid "total:" lines - Read 3131.72 MB/s,
/// Write 61.84 MB/s, matching what the server/dashboard later showed - yet DiskSpd still exited
/// with a non-zero, non-standard code (648390) on at least the write pass. Root cause not
/// confirmed (best guess: a post-measurement warning, e.g. the write-cache-disable IOCTL this
/// build's own "hardware write cache disabled, writethrough on" status line claims not actually
/// taking effect on every drive/controller). Whatever the cause, RunAsync below treats a pass as
/// having crashed only when ParseTotalMbS finds NO usable reading at all - the exit code is only
/// consulted (and only logged, not acted on) when both throughput readings parsed fine, so a
/// stray non-standard code doesn't wrongly flag a genuinely successful run as tool_crash/aborted.
/// </summary>
public sealed class DiskSpdRunner
{
    private const long TestFileSizeBytes = 1L * 1024 * 1024 * 1024; // 1 GiB - matches CDM's classic default test size
    private const int PassDurationSeconds = 10; // per direction - short enough for a technician workflow, long enough past initial ramp-up
    private const string TestFileName = "luxtronic_ssd_bench.tmp";

    private readonly string _toolsDirectory;

    public DiskSpdRunner(string toolsDirectory)
    {
        _toolsDirectory = toolsDirectory;
    }

    public string ExePath => Path.Combine(_toolsDirectory, "DiskSpd64.exe");

    public bool IsExePresent => File.Exists(ExePath);

    /// <summary>
    /// Runs the read pass then the write pass against a scratch file in <paramref
    /// name="targetFolder"/> (created if missing), deleting the scratch file afterward regardless
    /// of outcome. <paramref name="externalStop"/> can cancel either pass early (technician
    /// clicked Stop) - each pass is short (~10s) so this is a safety valve, not the primary stop
    /// path CPU/GPU/RAM rely on.
    /// </summary>
    public async Task<DiskSpdRunResult> RunAsync(
        string targetFolder,
        CancellationToken externalStop,
        Action<string> onLog,
        CancellationToken ct = default)
    {
        if (!IsExePresent)
        {
            throw new FileNotFoundException(
                $"DiskSpd64.exe not found at '{ExePath}'. It should already be bundled under " +
                "tools/CrystalDiskMark/CdmResource/DiskSpd/ - check the tools folder wasn't trimmed.", ExePath);
        }

        Directory.CreateDirectory(targetFolder);
        var testFilePath = Path.Combine(targetFolder, TestFileName);

        var combinedOutput = new StringBuilder();
        double? seqReadMbS = null;
        double? seqWriteMbS = null;
        int? lastExitCode = null;
        var wasExternallyStopped = false;

        try
        {
            onLog($"Starting SSD sequential read benchmark against {testFilePath} (SEQ1M Q8T1, {PassDurationSeconds}s)...");
            var readPass = await RunOnePassAsync(testFilePath, write: false, externalStop, ct).ConfigureAwait(false);
            combinedOutput.AppendLine("--- READ PASS ---").AppendLine(readPass.Output);
            lastExitCode = readPass.ExitCode;
            wasExternallyStopped = readPass.WasExternallyStopped;

            if (wasExternallyStopped)
            {
                onLog("Stop requested - terminating DiskSpd read pass.");
            }
            else
            {
                seqReadMbS = ParseTotalMbS(readPass.Output);
                onLog(seqReadMbS is double r ? $"Read: {r:F1} MB/s" : "Read: could not parse throughput from DiskSpd output.");

                onLog($"Starting SSD sequential write benchmark against {testFilePath} (SEQ1M Q8T1, {PassDurationSeconds}s)...");
                var writePass = await RunOnePassAsync(testFilePath, write: true, externalStop, ct).ConfigureAwait(false);
                combinedOutput.AppendLine("--- WRITE PASS ---").AppendLine(writePass.Output);
                lastExitCode = writePass.ExitCode;
                wasExternallyStopped = writePass.WasExternallyStopped;

                if (wasExternallyStopped)
                {
                    onLog("Stop requested - terminating DiskSpd write pass.");
                }
                else
                {
                    seqWriteMbS = ParseTotalMbS(writePass.Output);
                    onLog(seqWriteMbS is double w ? $"Write: {w:F1} MB/s" : "Write: could not parse throughput from DiskSpd output.");
                }
            }
        }
        finally
        {
            TryDeleteTestFile(testFilePath, onLog);
        }

        // Trust successfully-parsed results over the raw exit code, not the other way around -
        // ground-truthed live via a real technician run: DiskSpd printed complete, valid "total:"
        // lines for BOTH passes (matched what the dashboard later showed - 3131.72/61.84 MB/s),
        // yet still exited with a non-zero, non-standard code (648390) on at least the write pass.
        // Root cause not confirmed (best guess: a post-measurement warning condition, e.g. the
        // write-cache-disable IOCTL this build's "hardware write cache disabled, writethrough on"
        // line claims to apply not fully taking effect on every drive/controller - not something
        // ever exercised in this session's earlier ground-truthing, which only forced a genuine
        // *pre-measurement* failure via a nonexistent path, exit code 1, no "total:" line at all).
        // Whatever the cause, a nonstandard nonzero code with genuinely parsed throughput numbers
        // is not the same failure class as "no results at all" - only the latter is a real crash.
        string? stopReason = null;
        if (!wasExternallyStopped)
        {
            var readOk = seqReadMbS is not null;
            var writeOk = seqWriteMbS is not null;
            if (!readOk || !writeOk)
            {
                stopReason = StopReason.ToolCrash;
                onLog($"DiskSpd produced no usable throughput reading for at least one pass " +
                      $"(read={(readOk ? "ok" : "missing")}, write={(writeOk ? "ok" : "missing")}, " +
                      $"last exit code={lastExitCode?.ToString() ?? "unknown"}) - treating as a tool crash.");
            }
            else if (lastExitCode is int code && code != 0)
            {
                onLog($"NOTE: DiskSpd's last exit code was non-zero ({code}), but both passes produced " +
                      "valid throughput readings - not treating this as a crash. Worth a second look if " +
                      "this keeps happening on the same hardware.");
            }
        }

        return new DiskSpdRunResult
        {
            ExitCode = lastExitCode,
            RawOutput = combinedOutput.ToString(),
            SeqReadMbS = seqReadMbS,
            SeqWriteMbS = seqWriteMbS,
            ErrorCount = 0,
            StopReason = stopReason,
            WasExternallyStopped = wasExternallyStopped,
        };
    }

    private async Task<(int? ExitCode, string Output, bool WasExternallyStopped)> RunOnePassAsync(
        string testFilePath, bool write, CancellationToken externalStop, CancellationToken ct)
    {
        // -c only creates the file if it doesn't already exist (confirmed during ground-truthing:
        // the read pass creates it, the write pass reuses the same file rather than recreating
        // it) - harmless either way since both passes request the same size.
        var writeFlag = write ? "-w100" : "-w0";
        var arguments = $"-d{PassDurationSeconds} -o8 -t1 -b1M -Sh {writeFlag} -c{TestFileSizeBytes} \"{testFilePath}\"";

        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = arguments,
            WorkingDirectory = _toolsDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var stdout = new StringBuilder();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (stdout) stdout.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (stdout) stdout.AppendLine(e.Data); } };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var processExitTcs = new TaskCompletionSource();
        process.Exited += (_, _) => processExitTcs.TrySetResult();

        var stopTask = WaitForCancellationAsync(externalStop);
        var completed = await Task.WhenAny(processExitTcs.Task, stopTask).ConfigureAwait(false);

        if (completed == stopTask)
        {
            TryKill(process);
            await WaitBrieflyForExitAsync(process).ConfigureAwait(false);
            return (process.HasExited ? process.ExitCode : null, ReadAll(stdout), true);
        }

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return (process.ExitCode, ReadAll(stdout), false);
    }

    /// <summary>Pure parser for DiskSpd's "Total IO" section, split out so it's unit testable
    /// against the real captured text in class remarks without a real process. Looks for the
    /// first "total:" line after the first "Total IO" header and takes its third pipe-delimited
    /// field (MiB/s) - not a full-output regex, so it doesn't care how many thread rows precede
    /// it (this wrapper always uses -t1, but this stays correct if that ever changes).</summary>
    internal static double? ParseTotalMbS(string output)
    {
        var totalIoIndex = output.IndexOf("Total IO", StringComparison.Ordinal);
        if (totalIoIndex < 0)
        {
            return null;
        }

        var totalLineIndex = output.IndexOf("total:", totalIoIndex, StringComparison.Ordinal);
        if (totalLineIndex < 0)
        {
            return null;
        }

        var lineEnd = output.IndexOfAny(new[] { '\r', '\n' }, totalLineIndex);
        var line = lineEnd < 0 ? output[totalLineIndex..] : output[totalLineIndex..lineEnd];

        var fields = line.Split('|');
        if (fields.Length < 3)
        {
            return null;
        }

        return double.TryParse(fields[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static void TryDeleteTestFile(string testFilePath, Action<string> onLog)
    {
        try
        {
            if (File.Exists(testFilePath))
            {
                File.Delete(testFilePath);
            }
        }
        catch (Exception ex)
        {
            onLog($"WARNING: could not delete SSD benchmark scratch file '{testFilePath}': {ex.Message}. " +
                  "Safe to delete manually - it's just benchmark filler data, not customer data.");
        }
    }

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
