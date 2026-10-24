namespace Luxtronic.PCTools.Services.Shell;

/// <summary>
/// Builds the <see cref="TestSessionController"/> lazily. ApiKeyProvider throws when the key file
/// is missing, and that has to surface in the UI (Dashboard/Run Tests) rather than crash DI
/// resolution at startup - so the controller is created on demand and the failure reason is kept.
/// </summary>
public sealed class ControllerProvider : IAsyncDisposable
{
    private readonly AppSettingsProvider _settings;
    private readonly MachineService _machine;
    private readonly LogService _log;
    private bool _attempted;

    public ControllerProvider(AppSettingsProvider settings, MachineService machine, LogService log)
    {
        _settings = settings;
        _machine = machine;
        _log = log;
    }

    /// <summary>Raised after <see cref="Reset"/> so pages can refresh server/API-key status.</summary>
    public event Action? Changed;

    public TestSessionController? Controller { get; private set; }

    private TestSessionController? _local;

    /// <summary>
    /// Controller for the single-test tabs: it never talks to the server (forced offline), so
    /// those runs submit nothing. Needs no API key and survives settings changes - it only depends
    /// on the tool folders, not the server settings.
    /// </summary>
    public TestSessionController GetLocal() => _local ??= new TestSessionController(
        _settings, new ApiKeyProvider("local", "local"), _machine.Sensors,
        new Prime95Runner(_settings.ToolsDirectory), new FurMarkRunner(_settings.GpuToolsDirectory),
        new TM5Runner(_settings.RamToolsDirectory), new DiskSpdRunner(_settings.SsdToolsDirectory),
        new MemtestVulkanRunner(_settings.VramToolsDirectory), forceOffline: true);

    /// <summary>True while any test - from a tab or from the Run Tests series - is running.</summary>
    public bool AnyRunning => TestSessionController.AnyRunning;

    public void RequestStopAll()
    {
        Controller?.RequestStop();
        _local?.RequestStop();
    }

    /// <summary>Why <see cref="Controller"/> is null (e.g. missing apikey.txt), or null if it built fine.</summary>
    public string? Error { get; private set; }

    public TestSessionController? GetOrCreate()
    {
        if (_attempted)
        {
            return Controller;
        }

        _attempted = true;
        try
        {
            var apiKey = new ApiKeyProvider(_settings.OfflineMode ? _settings.ApiKey ?? "offline" : _settings.ApiKey, AppSettingsProvider.SettingsPath);
            _log.Append("API key loaded.");
            Controller = new TestSessionController(
                _settings, apiKey, _machine.Sensors,
                new Prime95Runner(_settings.ToolsDirectory), new FurMarkRunner(_settings.GpuToolsDirectory),
                new TM5Runner(_settings.RamToolsDirectory), new DiskSpdRunner(_settings.SsdToolsDirectory),
                new MemtestVulkanRunner(_settings.VramToolsDirectory));
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _log.Append($"ERROR: {ex.Message}");
        }

        return Controller;
    }

    /// <summary>Drops the current controller so the next <see cref="GetOrCreate"/> picks up new
    /// settings (server URL / API key). Refuses while a test is running.</summary>
    public async Task<bool> ResetAsync()
    {
        if (TestSessionController.AnyRunning)
        {
            return false;
        }

        if (Controller is not null)
        {
            await Controller.DisposeAsync();
        }

        Controller = null;
        Error = null;
        _attempted = false;
        Changed?.Invoke();
        return true;
    }

    /// <summary>One line per bundled tool: found or missing (the old window logged these at startup).</summary>
    public IReadOnlyList<ToolStatus> CheckTools() => new[]
    {
        Check("Prime95", System.IO.Path.Combine(_settings.ToolsDirectory, "prime95.exe")),
        Check("FurMark", System.IO.Path.Combine(_settings.GpuToolsDirectory, "furmark.exe")),
        Check("TestMem5", System.IO.Path.Combine(_settings.RamToolsDirectory, "TM5.exe")),
        Check("DiskSpd", System.IO.Path.Combine(_settings.SsdToolsDirectory, "DiskSpd64.exe")),
        Check("memtest_vulkan", System.IO.Path.Combine(_settings.VramToolsDirectory, "memtest_vulkan.exe")),
    };

    private static ToolStatus Check(string name, string path) => new(name, path, System.IO.File.Exists(path));

    public async ValueTask DisposeAsync()
    {
        if (Controller is not null)
        {
            await Controller.DisposeAsync();
        }

        if (_local is not null)
        {
            await _local.DisposeAsync();
        }
    }
}

public sealed record ToolStatus(string Name, string Path, bool Found)
{
    public string Display => Found ? $"{Name}: found" : $"{Name}: NOT FOUND";
}
