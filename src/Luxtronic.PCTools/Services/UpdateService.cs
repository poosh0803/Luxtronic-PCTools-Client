using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Luxtronic.PCTools.Services.Shell;
using Serilog;

namespace Luxtronic.PCTools.Services;

/// <summary>What <see cref="UpdateService.CheckAsync"/> found on the server.</summary>
/// <param name="Releases">Published client releases, newest first.</param>
/// <param name="Latest">The newest release that has a file, or null.</param>
/// <param name="Running">The release whose file is identical to the running exe, or null for a build that was never published.</param>
/// <param name="UpdateAvailable">True when this exe is an older published release than <paramref name="Latest"/>.</param>
public sealed record UpdateCheck(IReadOnlyList<AppInfo> Releases, AppInfo? Latest, AppInfo? Running, bool UpdateAvailable);

/// <summary>
/// Finds, downloads and installs newer builds of the client itself, published on the server's
/// Publish page (CONTRACT.md section 4, category "client").
///
/// Which release is running is decided by comparing the SHA-256 of the running exe with each
/// release's checksum, not by version numbers: the exe's own assembly version only changes when
/// someone edits the csproj, while upload-release.ps1 numbers every upload. A build that was never
/// published matches nothing and is reported as such (no "update available" nag for development builds).
/// </summary>
public sealed class UpdateService
{
    private readonly AppCatalogService _catalog;
    private readonly AppSettingsProvider _settings;
    private readonly LogService _log;
    private Task<string?>? _runningSha;

    public UpdateService(AppCatalogService catalog, AppSettingsProvider settings, LogService log)
    {
        _catalog = catalog;
        _settings = settings;
        _log = log;
    }

    /// <summary>Raised (on a background thread) after each check: whether an update is available and the latest release.</summary>
    public event Action<bool, AppInfo?>? UpdateChecked;

    /// <summary>Path of the running exe, or null when run through "dotnet" (nothing to replace).</summary>
    public static string? ExePath
    {
        get
        {
            var path = Environment.ProcessPath;
            return path is null || Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                ? null
                : path;
        }
    }

    public static bool CanSelfUpdate => ExePath is not null;

    /// <summary>Removes leftovers of a finished update: the previous exe and the downloaded installer folder.</summary>
    public static void CleanupLeftovers()
    {
        try
        {
            if (ExePath is { } exe && File.Exists(exe + ".old")) File.Delete(exe + ".old");
            var downloads = AppCatalogService.DownloadRoot("client");
            if (Directory.Exists(downloads)) Directory.Delete(downloads, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not clean up update leftovers");
        }
    }

    /// <summary>SHA-256 (lower-case hex) of the running exe; computed once. Null if it can't be read.</summary>
    public Task<string?> GetRunningShaAsync() => _runningSha ??= Task.Run<string?>(() =>
    {
        try
        {
            if (ExePath is not { } exe) return null;
            using var stream = new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not hash the running exe");
            return null;
        }
    });

    public async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        var releases = await _catalog.ListAsync("client", ct).ConfigureAwait(false);
        var sha = await GetRunningShaAsync().ConfigureAwait(false);

        var latest = releases.FirstOrDefault(r => r.FileName is not null);
        var running = sha is null ? null : releases.FirstOrDefault(r => string.Equals(r.FileSha256, sha, StringComparison.OrdinalIgnoreCase));
        var available = latest is not null && running is not null &&
                        !string.Equals(running.FileSha256, latest.FileSha256, StringComparison.OrdinalIgnoreCase);

        UpdateChecked?.Invoke(available, latest);
        return new UpdateCheck(releases, latest, running, available);
    }

    /// <summary>Startup check: quiet, never throws, skipped in offline mode.</summary>
    public async Task CheckQuietlyAsync()
    {
        if (_settings.OfflineMode) return;
        try
        {
            var result = await CheckAsync().ConfigureAwait(false);
            if (result.UpdateAvailable)
            {
                _log.Append($"Update available: v{result.Latest!.Version} (see the Updates page).");
            }
        }
        catch (Exception ex)
        {
            Log.Information("Update check skipped: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Downloads <paramref name="release"/> (checksum-verified) and starts the helper that swaps it in
    /// after this process exits and relaunches it. Returns an error message, or null when the caller
    /// should now close the app.
    /// </summary>
    public async Task<string?> InstallAsync(AppInfo release, IProgress<double>? progress, CancellationToken ct)
    {
        if (ExePath is not { } exe) return "This copy was started through the .NET host, so it cannot replace itself. Run the published exe.";

        var downloaded = await _catalog.DownloadAsync(release, progress, ct).ConfigureAwait(false);
        _log.Append($"Updates: downloaded v{release.Version}; closing to install it.");
        return AppUpdater.ScheduleReplaceAndRestart(downloaded, exe);
    }
}

/// <summary>
/// Replaces the running exe. A running exe can't be overwritten, so (like <see cref="AppReset"/>) the
/// work is handed to a short-lived batch file started before the app exits: it waits for this process
/// to end, moves the current exe aside, copies the new one in (restoring the old one if that fails),
/// relaunches, and deletes itself.
/// </summary>
public static class AppUpdater
{
    /// <summary>Starts the helper. The caller must then close the app. Returns an error message, or null on success.</summary>
    public static string? ScheduleReplaceAndRestart(string newExe, string currentExe)
    {
        if (!File.Exists(newExe)) return $"The downloaded file is missing: {newExe}";

        try
        {
            var batPath = Path.Combine(Path.GetTempPath(), $"luxtronic-update-{Environment.ProcessId}.bat");
            File.WriteAllText(batPath, BuildScript(Environment.ProcessId, newExe, currentExe), new UTF8Encoding(false));

            // Started from this elevated process, so the helper and the relaunched app inherit
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
            return $"Could not start the update helper: {ex.Message}";
        }
    }

    /// <summary>The helper batch. Public so it can be inspected/tested without running it.</summary>
    public static string BuildScript(int pid, string newExe, string currentExe)
    {
        // No parenthesised blocks on purpose (see AppReset.BuildScript): plain goto loops only.
        var lines = new List<string>
        {
            "@echo off",
            "chcp 65001 >nul",
            "set tries=0",
            ":wait",
            $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul",
            "if errorlevel 1 goto swap",
            "set /a tries+=1",
            "if %tries% GTR 60 goto done",   // the app never exited: touch nothing, start nothing
            "ping -n 2 127.0.0.1 >nul",
            "goto wait",
            ":swap",
            "set n=0",
            ":retry",
            $"move /y \"{currentExe}\" \"{currentExe}.old\" >nul 2>nul",
            $"if not exist \"{currentExe}\" goto copynew",
            "set /a n+=1",                    // still locked (HWiNFO or the exe shutting down): try again
            "if %n% GEQ 20 goto start",
            "ping -n 2 127.0.0.1 >nul",
            "goto retry",
            ":copynew",
            $"copy /y \"{newExe}\" \"{currentExe}\" >nul",
            "if errorlevel 1 goto restore",
            $"del \"{newExe}\" >nul 2>nul",
            "goto start",
            ":restore",
            $"if exist \"{currentExe}.old\" move /y \"{currentExe}.old\" \"{currentExe}\" >nul 2>nul",
            ":start",
            $"start \"\" \"{currentExe}\"",
            ":done",
            "(goto) 2>nul & del \"%~f0\"",
        };

        return string.Join("\r\n", lines) + "\r\n";
    }
}
