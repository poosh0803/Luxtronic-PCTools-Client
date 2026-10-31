using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Luxtronic.PCTools.Services;
using Luxtronic.PCTools.Services.Shell;

namespace Luxtronic.PCTools.UI.ViewModels;

/// <summary>
/// Server URL + the technician's API key (the only two things that need configuring - stored per
/// Windows user, no files to ship), the theme, and a read-only list of where things live.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettingsProvider _settings;
    private readonly AppThemeService _theme;
    private readonly ControllerProvider _controllers;

    public SettingsViewModel(AppSettingsProvider settings, AppThemeService theme, ControllerProvider controllers)
    {
        _settings = settings;
        _theme = theme;
        _controllers = controllers;
        _selectedTheme = theme.Current;
        _serverUrl = settings.ServerBaseUrl;
        _apiKey = settings.ApiKey ?? "";
        _telemetryInterval = settings.TelemetrySampleIntervalMs.ToString();
        _cpuMinutes = settings.CpuDurationMinutes.ToString();
        _gpuMinutes = settings.GpuDurationMinutes.ToString();
        _ramMinutes = settings.RamDurationMinutes.ToString();
        _vramMinutes = settings.VramDurationMinutes.ToString();
        _vramDevice = settings.VramDeviceIndex.ToString();
        _offlineMode = settings.OfflineMode;
        _cpuLoad = settings.CpuLoad;
        _hideTools = settings.HideToolWindows;
        Rows = new[]
        {
            new SettingRow("Settings file", AppSettingsProvider.SettingsPath),
            new SettingRow("Prime95 folder", settings.ToolsDirectory),
            new SettingRow("FurMark folder", settings.GpuToolsDirectory),
            new SettingRow("TestMem5 folder", settings.RamToolsDirectory),
            new SettingRow("DiskSpd folder", settings.SsdToolsDirectory),
            new SettingRow("HWiNFO folder", settings.HwInfoDirectory),
        };
    }

    public IReadOnlyList<SettingRow> Rows { get; }

    public IReadOnlyList<ThemeChoice> Themes { get; } = Enum.GetValues<ThemeChoice>();

    [ObservableProperty] private ThemeChoice _selectedTheme;

    [ObservableProperty] private string _serverUrl;

    [ObservableProperty] private string _apiKey;

    /// <summary>Sampling interval in milliseconds, as typed (parsed on save).</summary>
    [ObservableProperty] private string _telemetryInterval;

    [ObservableProperty] private string _cpuMinutes;
    [ObservableProperty] private string _gpuMinutes;
    [ObservableProperty] private string _ramMinutes;
    [ObservableProperty] private string _vramMinutes;
    [ObservableProperty] private string _vramDevice;

    public IReadOnlyList<LoadLevelOption> LoadLevels { get; } = new[]
    {
        new LoadLevelOption(CpuLoadLevel.Blend, "Blend - mixed CPU and memory stress"),
        new LoadLevelOption(CpuLoadLevel.MaxHeat, "Max heat (small FFT) - hottest, for cooler and power checks"),
        new LoadLevelOption(CpuLoadLevel.BlendNoAvx, "Blend, AVX off (default) - lower heat, closer to real-world use"),
    };

    [ObservableProperty] private CpuLoadLevel _cpuLoad;

    /// <summary>Run Prime95 in the background (its window hidden) during the CPU test.</summary>
    [ObservableProperty] private bool _hideTools;

    /// <summary>Dev switch: run tests without contacting the server.</summary>
    [ObservableProperty] private bool _offlineMode;

    [ObservableProperty] private string _statusMessage = "";

    [ObservableProperty] private bool _statusIsError;

    // The offline switch takes effect the moment it is flipped - no Save press needed, so it can't
    // be left "on" in the UI but not actually applied.
    partial void OnOfflineModeChanged(bool value) => _ = ApplyOfflineAsync(value);

    private async Task ApplyOfflineAsync(bool value)
    {
        _settings.SetOfflineMode(value);
        if (!await _controllers.ResetAsync())
        {
            Report("A test is running - the offline switch applies to the next test run after it finishes.", isError: true);
            return;
        }

        Report(value ? "Offline mode ON - tests will not contact the server." : "Offline mode OFF - tests will use the server.", isError: false);
    }

    partial void OnSelectedThemeChanged(ThemeChoice value) => _theme.Apply(value);

    [RelayCommand]
    private async Task SaveAsync()
    {
        var error = SaveSettings();
        if (error is not null)
        {
            Report(error, isError: true);
            return;
        }

        ServerUrl = _settings.ServerBaseUrl;
        if (!await _controllers.ResetAsync())
        {
            Report("Saved, but a test is running - the new values apply after it finishes and you save again.", isError: true);
            return;
        }

        Report("Saved.", isError: false);
    }

    /// <summary>
    /// Puts the test and tuning settings back to the app's defaults and saves them. Deliberately
    /// leaves the server URL and API key alone - wiping a technician's key would be destructive and
    /// is not what "defaults" is for.
    /// </summary>
    [RelayCommand]
    private async Task ResetDefaultsAsync()
    {
        var choice = MessageBox.Show(
            "Reset the test durations, CPU load level, VRAM GPU number, telemetry interval, offline mode and theme to their defaults?\n\n" +
            "Your server URL and API key are not changed.",
            "Reset to defaults", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (choice != MessageBoxResult.Yes)
        {
            return;
        }

        CpuMinutes = AppSettingsProvider.DefaultCpuMinutes.ToString();
        GpuMinutes = AppSettingsProvider.DefaultGpuMinutes.ToString();
        RamMinutes = AppSettingsProvider.DefaultRamMinutes.ToString();
        VramMinutes = AppSettingsProvider.DefaultVramMinutes.ToString();
        VramDevice = AppSettingsProvider.DefaultVramDeviceIndex.ToString();
        TelemetryInterval = AppSettingsProvider.DefaultTelemetryIntervalMs.ToString();
        CpuLoad = AppSettingsProvider.DefaultCpuLoad;
        HideTools = true;
        OfflineMode = false;
        SelectedTheme = ThemeChoice.Dark;

        await SaveAsync();
        if (!StatusIsError)
        {
            Report("Defaults restored and saved. Server URL and API key were left as they are.", isError: false);
        }
    }

    /// <summary>
    /// Deletes the whole app data folder (see <see cref="AppReset"/>) and restarts the app. Destructive -
    /// settings, the API key, logs and the unpacked tools all go - so it asks first and refuses while a
    /// test is running.
    /// </summary>
    [RelayCommand]
    private void ResetEverything()
    {
        if (_controllers.AnyRunning)
        {
            Report("A test is running - stop it before resetting everything.", isError: true);
            return;
        }

        var choice = MessageBox.Show(
            "This deletes ALL of the app's saved data and restarts it:\n\n" +
            "  - your settings, including the server URL and API key\n" +
            "  - the log files\n" +
            "  - the unpacked test tools (they are unpacked again on restart)\n\n" +
            "You will need to enter the API key again. Continue?",
            "Reset everything", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (choice != MessageBoxResult.Yes)
        {
            return;
        }

        var error = AppReset.ScheduleWipeAndRestart(out var willRestart);
        if (error is not null)
        {
            Report(error, isError: true);
            return;
        }

        if (!willRestart)
        {
            MessageBox.Show("The saved data will be deleted as the app closes. Start the app again yourself (it was run through the .NET host, so it can't relaunch itself).",
                "Reset everything", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // Closing through the main window runs its normal shutdown (stops HWiNFO, releases the log file),
        // after which the helper deletes the folder and relaunches the exe.
        Application.Current.MainWindow?.Close();
    }

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        var error = SaveSettings();
        if (error is not null)
        {
            Report(error, isError: true);
            return;
        }

        if (!await _controllers.ResetAsync())
        {
            Report("A test is running - try again when it finishes.", isError: true);
            return;
        }

        var controller = _controllers.GetOrCreate();
        if (controller is null)
        {
            Report(_controllers.Error ?? "Could not create the test controller.", isError: true);
            return;
        }

        Report("Contacting server...", isError: false);
        try
        {
            var config = await controller.GetConfigAsync();
            Report(config.Cpu is null
                ? "Connected, but the server config has no CPU section."
                : $"Connected. Server config OK (CPU test {config.Cpu.DurationMinutes} min).", isError: false);
        }
        catch (Exception ex)
        {
            Report($"Could not reach the server with these values: {ex.Message}", isError: true);
        }
    }

    private string? SaveSettings()
    {
        if (!int.TryParse(TelemetryInterval.Trim(), out var interval))
        {
            return "Telemetry interval must be a whole number of milliseconds.";
        }

        if (!int.TryParse(CpuMinutes.Trim(), out var cpu) || !int.TryParse(GpuMinutes.Trim(), out var gpu) ||
            !int.TryParse(RamMinutes.Trim(), out var ram) || !int.TryParse(VramMinutes.Trim(), out var vram))
        {
            return "Test durations must be whole numbers of minutes.";
        }

        if (!int.TryParse(VramDevice.Trim(), out var vramDevice))
        {
            return "VRAM test device must be a whole number.";
        }

        var error = _settings.Update(ServerUrl, ApiKey, interval, cpu, gpu, ram, vram, vramDevice, OfflineMode, CpuLoad, HideTools);
        if (error is null)
        {
            TelemetryInterval = _settings.TelemetrySampleIntervalMs.ToString();
        }

        return error;
    }

    private void Report(string message, bool isError)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }
}

public sealed record SettingRow(string Name, string Value);

public sealed record LoadLevelOption(CpuLoadLevel Level, string Label);
