using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace Luxtronic.PCTools.Services.Shell;

/// <summary>
/// Starts the bundled HWiNFO64 (the sole CPU/GPU sensor source) and waits for its shared memory to
/// come up - what the old Launch.ps1 did before starting the app. HWiNFO64.INI beside the exe has
/// Shared Memory Support on and the welcome dialog off, so it starts unattended. If HWiNFO is
/// already running (technician's own copy) it is left alone and not closed on exit.
/// </summary>
public sealed class HwInfoLauncher : IDisposable
{
    private const string SharedMemoryName = @"Global\HWiNFO_SENS_SM2";
    private static readonly TimeSpan SharedMemoryTimeout = TimeSpan.FromSeconds(30);

    private Process? _started;

    public async Task EnsureRunningAsync(string hwInfoDirectory, Action<string> log)
    {
        if (SharedMemoryUp())
        {
            log("HWiNFO shared memory already available.");
            return;
        }

        var exe = Path.Combine(hwInfoDirectory, "HWiNFO64.exe");
        if (Process.GetProcessesByName("HWiNFO64").Length == 0)
        {
            if (!File.Exists(exe))
            {
                log($"WARNING: HWiNFO64.exe not found at {exe} - CPU/GPU sensors will be unavailable.");
                return;
            }

            try
            {
                _started = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = hwInfoDirectory, UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized });
                log("Started HWiNFO64.");
            }
            catch (Exception ex)
            {
                log($"WARNING: could not start HWiNFO64: {ex.Message}");
                return;
            }
        }
        else
        {
            log("HWiNFO64 is already running - waiting for its shared memory...");
        }

        // A freshly started HWiNFO needs a few seconds before its first sensor pass creates the block.
        var deadline = DateTime.UtcNow + SharedMemoryTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (SharedMemoryUp())
            {
                log("HWiNFO shared memory is up.");
                return;
            }

            await Task.Delay(500);
        }

        log("WARNING: HWiNFO's shared memory did not appear within 30s - CPU/GPU sensors may be unavailable.");
    }

    private static bool SharedMemoryUp()
    {
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(SharedMemoryName, MemoryMappedFileRights.Read);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            if (_started is { HasExited: false })
            {
                _started.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            // Best effort on shutdown.
        }

        _started?.Dispose();
    }
}
