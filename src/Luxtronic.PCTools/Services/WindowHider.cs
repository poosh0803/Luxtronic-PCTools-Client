using System.Runtime.InteropServices;

namespace Luxtronic.PCTools.Services;

/// <summary>
/// Hides every visible top-level window that belongs to a process. Used to run Prime95 "in the
/// background": it is a normal Windows program with no hidden/silent switch, so its window is hidden
/// from here instead. A hidden window also has no taskbar button.
/// </summary>
public static class WindowHider
{
    private const int SwHide = 0;

    /// <summary>Hides the process's visible windows; returns how many were hidden this call.</summary>
    public static int HideWindowsOfProcess(int processId)
    {
        var hidden = 0;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var owner);
            if (owner == (uint)processId && IsWindowVisible(handle))
            {
                ShowWindow(handle, SwHide);
                hidden++;
            }

            return true;
        }, IntPtr.Zero);
        return hidden;
    }

    /// <summary>
    /// Keeps a just-started tool's windows hidden until cancelled: every 20 ms while it starts up (so any
    /// flash lasts a frame or two), then every 300 ms, since programs create and re-show windows early on.
    /// </summary>
    public static Task KeepHiddenAsync(int processId, string toolName, Action<string> onLog, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var announced = false;
            var sinceStart = System.Diagnostics.Stopwatch.StartNew();
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (HideWindowsOfProcess(processId) > 0 && !announced)
                    {
                        announced = true;
                        onLog($"{toolName}'s window was shown briefly and hidden - it is running in the background.");
                    }

                    await Task.Delay(sinceStart.Elapsed < TimeSpan.FromSeconds(8) ? 20 : 300, ct).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    break; // cancelled, or the process is gone
                }
            }
        }, ct);

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr handle, int command);
}
