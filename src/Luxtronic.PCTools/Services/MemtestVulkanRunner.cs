using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Luxtronic.PCTools.Models;

namespace Luxtronic.PCTools.Services;

/// <summary>Local (not server-supplied) settings for one VRAM test run.</summary>
public sealed record VramRunSettings(int DurationMinutes, int DeviceIndex);

public sealed class MemtestVulkanRunResult
{
    public int? ExitCode { get; init; }
    public string RawOutput { get; init; } = "";

    /// <summary>Number of "Error found" reports memtest_vulkan printed - any value above zero is a
    /// failed VRAM test (the server treats error_count &gt; 0 as a hard fail).</summary>
    public int ErrorCount { get; init; }

    /// <summary>Null = normal finish (the configured duration elapsed, or the tool stopped itself
    /// after reporting errors). StopReason.ToolCrash = it died or failed to start (no usable
    /// Vulkan device, driver fault such as ERROR_DEVICE_LOST...). Never UserAbort/ClientError here,
    /// same reasoning as <see cref="Prime95RunResult"/>.</summary>
    public string? StopReason { get; init; }

    public bool WasExternallyStopped { get; init; }
}

/// <summary>
/// Runs memtest_vulkan (https://github.com/GpuZelenograd/memtest_vulkan - a Vulkan compute VRAM
/// tester) for a configured duration. Looks for {ToolsDirectory}/memtest_vulkan.exe.
///
/// How it is driven, from the tool's own source (v0.5.0), not guessed:
/// - Started with a numeric first argument it runs non-interactively: no device menu, no "press
///   any key", output goes to stdout (and to memtest_vulkan.log in the working directory). The
///   argument is the device index as numbered in its own device list (1 = first listed); 0 means
///   autoselect.
/// - It has no duration option - it runs until interrupted - so, like Prime95Runner, the duration is
///   enforced here and the process is killed when it elapses (a normal finish).
/// - Each detected corruption is printed as a line starting "Error found." The exit code is a
///   status byte (0x40 signature | bit0 init ok | bit1 errors seen | bit2 abnormal exit), used only
///   when the tool exits by itself before the duration is up.
///
/// Not yet validated on a real run in this client - the line formats above come from reading the
/// source and the README, not from a captured run.
/// </summary>
public sealed class MemtestVulkanRunner
{
    private static readonly Regex ErrorLinePattern = new(@"^\s*Error found\b", RegexOptions.Compiled | RegexOptions.Multiline);

    private const int StatusErrorsBit = 0b00010;

    private readonly string _toolsDirectory;

    public MemtestVulkanRunner(string toolsDirectory)
    {
        _toolsDirectory = toolsDirectory;
    }

    public string ExePath => Path.Combine(_toolsDirectory, "memtest_vulkan.exe");

    public bool IsExePresent => File.Exists(ExePath);

    public async Task<MemtestVulkanRunResult> RunAsync(
        VramRunSettings settings,
        CancellationToken externalStop,
        Action<string> onLog,
        CancellationToken ct = default)
    {
        if (!IsExePresent)
        {
            throw new FileNotFoundException(
                $"memtest_vulkan.exe not found at '{ExePath}'. It should be bundled in tools/memtest_vulkan/ " +
                "(see that folder's README.md).", ExePath);
        }

        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = settings.DeviceIndex.ToString(),
            WorkingDirectory = _toolsDirectory, // it writes memtest_vulkan.log into its working directory
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Capture(output, e.Data, onLog);
        process.ErrorDataReceived += (_, e) => Capture(output, e.Data, onLog);

        onLog($"Starting memtest_vulkan (device {settings.DeviceIndex}) from {ExePath}, duration={settings.DurationMinutes}min");
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var exitTcs = new TaskCompletionSource();
        process.Exited += (_, _) => exitTcs.TrySetResult();

        var durationTask = Task.Delay(TimeSpan.FromMinutes(settings.DurationMinutes), ct);
        var stopTask = WaitForCancellationAsync(externalStop);
        var completed = await Task.WhenAny(exitTcs.Task, durationTask, stopTask).ConfigureAwait(false);

        string? stopReason = null;
        var wasExternallyStopped = false;
        int? exitCode = null;

        if (completed == exitTcs.Task)
        {
            // The tool ended on its own before the duration: either it stopped after finding
            // errors (a real result - let the server fail it on error_count), or it failed/crashed.
            exitCode = process.ExitCode;
            var statusBits = exitCode.Value & 0x1F;
            if ((statusBits & StatusErrorsBit) != 0 || CountErrors(ReadAll(output)) > 0)
            {
                onLog($"memtest_vulkan exited by itself with errors reported (exit code {exitCode}).");
            }
            else
            {
                stopReason = StopReason.ToolCrash;
                onLog($"memtest_vulkan exited on its own without finishing (exit code {exitCode}) - treating as a tool crash.");
            }
        }
        else
        {
            if (completed == stopTask)
            {
                wasExternallyStopped = true;
                onLog("Stop requested - terminating memtest_vulkan.");
            }
            else
            {
                onLog($"Configured duration ({settings.DurationMinutes} min) elapsed - stopping memtest_vulkan.");
            }

            TryKill(process);
            await WaitBrieflyForExitAsync(process).ConfigureAwait(false);
            if (process.HasExited)
            {
                exitCode = process.ExitCode;
            }
        }

        var raw = ReadAll(output);
        return new MemtestVulkanRunResult
        {
            ExitCode = exitCode,
            RawOutput = raw,
            ErrorCount = CountErrors(raw),
            StopReason = stopReason,
            WasExternallyStopped = wasExternallyStopped,
        };
    }

    internal static int CountErrors(string output) => ErrorLinePattern.Matches(output).Count;

    private static void Capture(StringBuilder output, string? line, Action<string> onLog)
    {
        if (line is null)
        {
            return;
        }

        lock (output)
        {
            output.AppendLine(line);
        }

        // Surface the tool's progress and any error headline in the UI log (not the multi-line
        // error tables - those stay in the raw output sent to the server).
        if (line.Contains("iteration", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Error found", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Runtime error", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Standard 5-minute", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Testing", StringComparison.OrdinalIgnoreCase))
        {
            onLog($"memtest_vulkan: {line.Trim()}");
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
            // Already gone - that is the goal anyway.
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
            // Moving on after 10s so the UI never hangs.
        }
    }

    private static Task WaitForCancellationAsync(CancellationToken token)
    {
        var tcs = new TaskCompletionSource();
        token.Register(() => tcs.TrySetResult());
        return tcs.Task;
    }
}
