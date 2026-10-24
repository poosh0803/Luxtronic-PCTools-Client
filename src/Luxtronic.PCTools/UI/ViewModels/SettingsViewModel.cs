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
        new LoadLevelOption(CpuLoadLevel.Blend, "Blend (default) - mixed CPU and memory stress"),
        new LoadLevelOption(CpuLoadLevel.MaxHeat, "Max heat (small FFT) - hottest, for cooler and power checks"),
        new LoadLevelOption(CpuLoadLevel.BlendNoAvx, "Blend, AVX off - lower heat, closer to real-world use"),
    };

    [ObservableProperty] private CpuLoadLevel _cpuLoad;

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

        var error = _settings.Update(ServerUrl, ApiKey, interval, cpu, gpu, ram, vram, vramDevice, OfflineMode, CpuLoad);
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
