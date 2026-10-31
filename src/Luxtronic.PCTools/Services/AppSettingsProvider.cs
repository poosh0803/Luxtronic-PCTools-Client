using System.IO;
using System.Text.Json;
using Luxtronic.PCTools.Services.Shell;

namespace Luxtronic.PCTools.Services;

/// <summary>How hard the CPU (Prime95) test loads the processor. Chosen in Settings, not by the server.</summary>
public enum CpuLoadLevel
{
    /// <summary>Prime95 "blend" - mixed CPU and memory stress.</summary>
    Blend,

    /// <summary>Prime95 "small FFT" - fits in cache, maximum heat and power. Cooler/PSU worst case.</summary>
    MaxHeat,

    /// <summary>Blend with AVX/AVX2/FMA3/AVX-512 switched off - far less heat and power, closer to heavy real-world use. The default.</summary>
    BlendNoAvx,
}

/// <summary>
/// The app's settings, with no config files to ship: the server URL and the technician's API key
/// are entered in the Settings page and stored per Windows user in
/// %LocalAppData%\LuxtronicPCTools\settings.json. Tool folders come from <see cref="ToolPack"/>.
///
/// The one-time migration below picks up an old-style apikey.txt next to the exe, so a technician
/// moving from the v1 client doesn't have to re-enter their key.
/// </summary>
public sealed class AppSettingsProvider
{
    public const string DefaultServerUrl = "http://192.168.68.255:7777";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };

    public static string SettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LuxtronicPCTools", "settings.json");

    public string ServerBaseUrl { get; private set; } = DefaultServerUrl;

    /// <summary>The technician's API key, or null until entered.</summary>
    public string? ApiKey { get; private set; }

    public const int DefaultTelemetryIntervalMs = 500;
    public const int MinTelemetryIntervalMs = 100;
    public const int MaxTelemetryIntervalMs = 10000;

    /// <summary>How often sensors are sampled and sent during a test. Read fresh by the poll loops
    /// each cycle, so a change in Settings applies to a test that is already running.</summary>
    public int TelemetrySampleIntervalMs { get; private set; } = DefaultTelemetryIntervalMs;

    // Per-test run lengths, set here rather than taken from the server config's duration_minutes
    // (thresholds still come from the server). Applied by TestSessionController after it fetches config.
    public const int DefaultCpuMinutes = 10;
    public const int DefaultGpuMinutes = 10;
    public const int DefaultRamMinutes = 30;
    public const int MinTestMinutes = 1;
    public const int MaxTestMinutes = 1440;

    /// <summary>Dev/testing switch: run everything locally and send nothing to the server (no config
    /// fetch, session, test-run or telemetry calls - see LuxApiClient/TelemetryPublisher).</summary>
    public bool OfflineMode { get; private set; }

    public const CpuLoadLevel DefaultCpuLoad = CpuLoadLevel.BlendNoAvx;

    public CpuLoadLevel CpuLoad { get; private set; } = DefaultCpuLoad;

    /// <summary>Hide Prime95's window during the CPU test (it runs in the background). On by default.</summary>
    public bool HideToolWindows { get; private set; } = true;

    public const int DefaultVramMinutes = 10;
    public const int DefaultVramDeviceIndex = 1;

    /// <summary>memtest_vulkan's own device number (1 = first device in its list, 0 = autoselect).</summary>
    public int VramDeviceIndex { get; private set; } = DefaultVramDeviceIndex;
    public int VramDurationMinutes { get; private set; } = DefaultVramMinutes;
    public int CpuDurationMinutes { get; private set; } = DefaultCpuMinutes;
    public int GpuDurationMinutes { get; private set; } = DefaultGpuMinutes;
    public int RamDurationMinutes { get; private set; } = DefaultRamMinutes;

    public string ToolsDirectory => ToolPack.Resolve("prime95");
    public string GpuToolsDirectory => ToolPack.Resolve("FurMark_win64");
    public string RamToolsDirectory => ToolPack.Resolve("TestMem5");
    public string SsdToolsDirectory => ToolPack.Resolve("DiskSpd");
    public string VramToolsDirectory => ToolPack.Resolve("memtest_vulkan");
    public string HwInfoDirectory => ToolPack.Resolve("hwi");

    public static AppSettingsProvider Load()
    {
        var settings = new AppSettingsProvider();
        try
        {
            if (File.Exists(SettingsPath))
            {
                var raw = JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(SettingsPath), JsonOptions);
                if (raw is not null)
                {
                    settings.ServerBaseUrl = Normalize(raw.ServerBaseUrl) ?? DefaultServerUrl;
                    settings.ApiKey = string.IsNullOrWhiteSpace(raw.ApiKey) ? null : raw.ApiKey.Trim();
                    settings.OfflineMode = raw.OfflineMode == true;
                    settings.HideToolWindows = raw.HideToolWindows ?? raw.HidePrimeWindow ?? true;
                    settings.CpuLoad = Enum.TryParse<CpuLoadLevel>(raw.CpuLoadLevel, out var load) ? load : DefaultCpuLoad;
                    settings.VramDurationMinutes = ValidMinutes(raw.VramMinutes) ?? DefaultVramMinutes;
                    settings.VramDeviceIndex = raw.VramDeviceIndex is >= 0 and <= 16 ? raw.VramDeviceIndex.Value : DefaultVramDeviceIndex;
                    settings.CpuDurationMinutes = ValidMinutes(raw.CpuMinutes) ?? DefaultCpuMinutes;
                    settings.GpuDurationMinutes = ValidMinutes(raw.GpuMinutes) ?? DefaultGpuMinutes;
                    settings.RamDurationMinutes = ValidMinutes(raw.RamMinutes) ?? DefaultRamMinutes;
                    if (raw.TelemetryIntervalMs is >= MinTelemetryIntervalMs and <= MaxTelemetryIntervalMs)
                    {
                        settings.TelemetrySampleIntervalMs = raw.TelemetryIntervalMs.Value;
                    }
                }
            }
        }
        catch (Exception)
        {
            // A corrupt settings file must not stop the app from opening - the technician can
            // simply re-enter the values on the Settings page.
        }

        if (settings.ApiKey is null)
        {
            var legacy = Path.Combine(AppContext.BaseDirectory, "apikey.txt");
            if (File.Exists(legacy) && File.ReadAllText(legacy).Trim() is { Length: > 0 } key)
            {
                settings.ApiKey = key;
                settings.TrySave();
            }
        }

        // Decided after the legacy apikey.txt migration above (which writes the file): a settings
        // file means this PC has been set up (or its setup was skipped) already.
        settings.IsFirstRun = !File.Exists(SettingsPath);
        return settings;
    }

    /// <summary>True on a fresh install (no settings file yet): the first-start wizard is shown.</summary>
    public bool IsFirstRun { get; private set; }

    /// <summary>
    /// First-start wizard: validates and stores just the server URL, API key and offline switch,
    /// leaving every other setting at its current value. Returns an error message, or null on success.
    /// </summary>
    public string? SetConnection(string serverUrl, string? apiKey, bool offlineMode)
    {
        var url = Normalize(serverUrl);
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "Server URL must look like http://192.168.68.255:7777";
        }

        if (!offlineMode && string.IsNullOrWhiteSpace(apiKey))
        {
            return "Enter the API key issued by the server, or turn on offline mode.";
        }

        ServerBaseUrl = url;
        ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        OfflineMode = offlineMode;
        IsFirstRun = false;
        return TrySave() ? null : $"Could not write {SettingsPath}.";
    }

    /// <summary>Records that setup was skipped, so the wizard is not shown again (defaults are saved).</summary>
    public void MarkSetupSeen()
    {
        IsFirstRun = false;
        TrySave();
    }

    /// <summary>Validates and stores new values. Returns an error message, or null on success.</summary>
    public string? Update(string serverUrl, string? apiKey, int telemetryIntervalMs, int cpuMinutes, int gpuMinutes, int ramMinutes,
        int vramMinutes, int vramDeviceIndex, bool offlineMode, CpuLoadLevel cpuLoad, bool hidePrimeWindow)
    {
        if (ValidMinutes(vramMinutes) is null)
        {
            return $"Test durations must be between {MinTestMinutes} and {MaxTestMinutes} minutes.";
        }

        if (vramDeviceIndex is < 0 or > 16)
        {
            return "VRAM test device number must be between 0 and 16 (1 = first GPU in memtest_vulkan's list, 0 = auto).";
        }

        if (ValidMinutes(cpuMinutes) is null || ValidMinutes(gpuMinutes) is null || ValidMinutes(ramMinutes) is null)
        {
            return $"Test durations must be between {MinTestMinutes} and {MaxTestMinutes} minutes.";
        }

        if (telemetryIntervalMs is < MinTelemetryIntervalMs or > MaxTelemetryIntervalMs)
        {
            return $"Telemetry interval must be between {MinTelemetryIntervalMs} and {MaxTelemetryIntervalMs} ms.";
        }

        var url = Normalize(serverUrl);
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "Server URL must look like http://192.168.68.255:7777";
        }

        TelemetrySampleIntervalMs = telemetryIntervalMs;
        OfflineMode = offlineMode;
        CpuLoad = cpuLoad;
        HideToolWindows = hidePrimeWindow;
        VramDurationMinutes = vramMinutes;
        VramDeviceIndex = vramDeviceIndex;
        CpuDurationMinutes = cpuMinutes;
        GpuDurationMinutes = gpuMinutes;
        RamDurationMinutes = ramMinutes;
        ServerBaseUrl = url;
        ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        return TrySave() ? null : $"Could not write {SettingsPath}.";
    }

    /// <summary>Applies and saves just the offline switch (used by the Settings toggle, which takes
    /// effect immediately rather than waiting for the Save button).</summary>
    public bool SetOfflineMode(bool offline)
    {
        OfflineMode = offline;
        return TrySave();
    }

    private bool TrySave()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath,
                JsonSerializer.Serialize(new StoredSettings { ServerBaseUrl = ServerBaseUrl, ApiKey = ApiKey, TelemetryIntervalMs = TelemetrySampleIntervalMs, CpuMinutes = CpuDurationMinutes, GpuMinutes = GpuDurationMinutes, RamMinutes = RamDurationMinutes, VramMinutes = VramDurationMinutes, VramDeviceIndex = VramDeviceIndex, OfflineMode = OfflineMode, CpuLoadLevel = CpuLoad.ToString(), HideToolWindows = HideToolWindows }, JsonOptions));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int? ValidMinutes(int? minutes) =>
        minutes is >= MinTestMinutes and <= MaxTestMinutes ? minutes : null;

    private static string? Normalize(string? url) => string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/');

    private sealed class StoredSettings
    {
        public string? ServerBaseUrl { get; set; }
        public string? ApiKey { get; set; }
        public int? TelemetryIntervalMs { get; set; }
        public int? CpuMinutes { get; set; }
        public int? GpuMinutes { get; set; }
        public int? RamMinutes { get; set; }
        public int? VramMinutes { get; set; }
        public int? VramDeviceIndex { get; set; }
        public bool? OfflineMode { get; set; }
        public string? CpuLoadLevel { get; set; }
        public bool? HideToolWindows { get; set; }
        public bool? HidePrimeWindow { get; set; } // old name, still read so an earlier choice carries over
    }
}
