using System.Diagnostics;
using System.IO;
using System.Text;

namespace Luxtronic.PCTools.Services.Shell;

/// <summary>
/// "Reset everything": deletes the app's whole data folder (%LocalAppData%\LuxtronicPCTools - settings
/// with the API key, theme, logs and the unpacked test tools) and starts the app again as if it had
/// just been installed.
///
/// The app can't delete that folder itself while it runs (its log file is open, and the tools may be
/// running), so it hands the job to a short-lived batch file started before the app exits: the batch
/// waits for this process to end, removes the folder (retrying a few times in case a tool such as
/// HWiNFO is still shutting down), relaunches the exe, and deletes itself.
/// </summary>
public static class AppReset
{
    /// <summary>The folder that gets deleted.</summary>
    public static string DataFolder { get; } = Path.GetDirectoryName(AppSettingsProvider.SettingsPath)!;

    /// <summary>
    /// Starts the helper batch. The caller must then close the app. Returns an error message, or null
    /// on success. <paramref name="willRestart"/> is false when the app can't be relaunched (for
    /// example when run through "dotnet run"), in which case the folder is only deleted.
    /// </summary>
    public static string? ScheduleWipeAndRestart(out bool willRestart)
    {
        willRestart = false;

        // Never delete anything but the app's own folder, however this gets called.
        if (!string.Equals(Path.GetFileName(DataFolder), "LuxtronicPCTools", StringComparison.OrdinalIgnoreCase))
        {
            return $"Refusing to delete '{DataFolder}': it is not the LuxtronicPCTools data folder.";
        }

        var exe = Environment.ProcessPath;
        var canRestart = exe is not null && !Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        willRestart = canRestart;

        try
        {
            var batPath = Path.Combine(Path.GetTempPath(), $"luxtronic-reset-{Environment.ProcessId}.bat");
            File.WriteAllText(batPath, BuildScript(Environment.ProcessId, DataFolder, canRestart ? exe : null), new UTF8Encoding(false));

            // Started from this elevated process, so the helper (and the relaunched app) inherit
            // elevation and no second UAC prompt appears.
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{batPath}\"\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            return null;
        }
        catch (Exception ex)
        {
            return $"Could not start the reset helper: {ex.Message}";
        }
    }

    /// <summary>The helper batch. Public so it can be exercised against a scratch folder.</summary>
    public static string BuildScript(int pid, string folder, string? exeToRestart)
    {
        // No parenthesised blocks on purpose: %variables% inside them are expanded when the whole
        // block is parsed, which breaks counters. Plain goto loops behave.
        var lines = new List<string>
        {
            "@echo off",
            "chcp 65001 >nul",
            "set tries=0",
            ":wait",
            $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul",
            "if errorlevel 1 goto wipe",
            "set /a tries+=1",
            "if %tries% GTR 60 goto wipe",
            "ping -n 2 127.0.0.1 >nul",
            "goto wait",
            ":wipe",
            "set n=0",
            ":retry",
            $"rd /s /q \"{folder}\" 2>nul",
            $"if not exist \"{folder}\" goto done",
            "set /a n+=1",
            "if %n% GEQ 8 goto done",
            "ping -n 2 127.0.0.1 >nul",
            "goto retry",
            ":done",
        };
        if (exeToRestart is not null)
        {
            lines.Add($"start \"\" \"{exeToRestart}\"");
        }

        lines.Add("(goto) 2>nul & del \"%~f0\""); // a batch file may delete itself as its last act

        return string.Join("\r\n", lines) + "\r\n";
    }
}
