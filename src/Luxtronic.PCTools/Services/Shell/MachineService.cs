using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Luxtronic.PCTools.Services.Shell;

/// <summary>
/// Owns the one <see cref="SensorMonitor"/> for the process plus everything the old MainWindow
/// derived from it: motherboard serial, sensor health, SSD summary/drive picker, and the live
/// CPU/GPU/RAM readout strings shown on both the Dashboard and Run Tests pages.
///
/// The idle poll timer and <see cref="TestSessionController"/>'s own poll loop must never run at
/// once (SensorMonitor.ReadCpu isn't safe to call from two places), so the Run Tests view-model
/// calls <see cref="PauseIdlePolling"/> before starting a test and <see cref="ResumeIdlePolling"/>
/// afterward - same contract the old window's Start/Stop handlers had.
/// </summary>
public sealed partial class MachineService : ObservableObject, IDisposable
{
    /// <summary>How long init waits for LibreHardwareMonitorLib's native driver open (and the nested
    /// WMI motherboard-serial lookup) before giving up and reporting a hang. Both can stall
    /// indefinitely on some hardware/WMI states instead of failing fast (seen in the field).</summary>
    private static readonly TimeSpan SensorInitTimeout = TimeSpan.FromSeconds(20);

    private readonly LogService _log;
    private readonly AppSettingsProvider _settings;
    private readonly HwInfoLauncher _hwInfo = new();
    private DispatcherTimer? _idleTimer;

    public MachineService(LogService log, AppSettingsProvider settings)
    {
        _log = log;
        _settings = settings;
        Sensors = new SensorMonitor();
    }

    public SensorMonitor Sensors { get; }

    [ObservableProperty] private string _moboSerial = "(reading...)";
    [ObservableProperty] private string _sensorStatus = "Initializing sensors...";
    [ObservableProperty] private bool _sensorsHealthy;
    [ObservableProperty] private bool _isInitialized;
    [ObservableProperty] private string _cpuReadout = "(waiting for sensors)";
    [ObservableProperty] private string _gpuReadout = "(waiting for sensors)";
    [ObservableProperty] private string _ramReadout = "(idle)";
    [ObservableProperty] private string _motherboardName = "";
    [ObservableProperty] private string _biosVersion = "--";
    [ObservableProperty] private string _biosDate = "--";
    [ObservableProperty] private string _cpuName = "";
    [ObservableProperty] private string _gpuName = "";

    [ObservableProperty] private string _ssdStatus = "(reading...)";

    /// <summary>One card per detected storage drive (empty until read, or if none/unreadable).</summary>
    public System.Collections.ObjectModel.ObservableCollection<DriveCard> Drives { get; } = new();

    /// <summary>The real mobo serial, or null when WMI gave none (sessions can't be created then).</summary>
    public string? RealMoboSerial { get; private set; }

    // Live tiles for the Dashboard's CPU and GPU cards (the string readouts above stay for the Run Tests page).
    public LiveStat CpuTemp { get; } = new("Temperature", " °C");
    public LiveStat CpuLoad { get; } = new("Load", " %");
    public LiveStat CpuClock { get; } = new("Clock", " MHz");
    public LiveStat CpuFan { get; } = new("Fan", " RPM");
    public IReadOnlyList<LiveStat> CpuStats => new[] { CpuTemp, CpuLoad, CpuClock, CpuFan };

    public LiveStat GpuTemp { get; } = new("Core temp", " °C");
    public LiveStat GpuHotSpot { get; } = new("Hot spot", " °C");
    public LiveStat GpuLoad { get; } = new("Load", " %");
    public LiveStat GpuCoreClock { get; } = new("Core clock", " MHz");
    public LiveStat GpuMemClock { get; } = new("Memory clock", " MHz");
    public LiveStat GpuFan { get; } = new("Fan", " RPM");
    public LiveStat GpuPower { get; } = new("Power", " W");
    public LiveStat GpuVram { get; } = new("VRAM used", "");
    public IReadOnlyList<LiveStat> GpuStats =>
        new[] { GpuTemp, GpuHotSpot, GpuLoad, GpuCoreClock, GpuMemClock, GpuFan, GpuPower, GpuVram };

    public LiveStat SsdReadSpeed { get; } = new("Read speed", " MB/s");
    public LiveStat SsdWriteSpeed { get; } = new("Write speed", " MB/s");
    public IReadOnlyList<LiveStat> SsdStats => new[] { SsdReadSpeed, SsdWriteSpeed };

    /// <summary>Live drive throughput during the SSD test (null, null clears the tiles at the end).</summary>
    public void UpdateSsdSpeed(double? readMbS, double? writeMbS) => OnUi(() =>
    {
        SsdReadSpeed.Set(readMbS);
        SsdWriteSpeed.Set(writeMbS);
    });

    public IReadOnlyList<SsdDriveOption> DriveOptions { get; private set; } = Array.Empty<SsdDriveOption>();

    /// <summary>The PC's specification, sent to the server with each session (null until collected).</summary>
    public Dictionary<string, object>? SystemInfo { get; private set; }

    private IReadOnlyList<SsdSmartInfo> _ssdInfos = Array.Empty<SsdSmartInfo>();
    private MotherboardInfo _boardInfo = new(null, null, null);

    /// <summary>Raised on the UI thread once <see cref="InitializeAsync"/> finishes (success or not).</summary>
    public event Action? Initialized;

    public async Task InitializeAsync()
    {
        // Everything the old Launch.ps1 did before starting the app: unpack the bundled tools,
        // then start HWiNFO and wait for its shared memory - sensors can't initialize without it.
        try
        {
            SensorStatus = "Preparing bundled tools...";
            await ToolPack.EnsureExtractedAsync(_log.Append);
            SensorStatus = "Starting HWiNFO...";
            await _hwInfo.EnsureRunningAsync(_settings.HwInfoDirectory, _log.Append);
        }
        catch (Exception ex)
        {
            _log.Append($"ERROR preparing tools/HWiNFO: {ex.Message}");
        }

        SensorStatus = "Initializing sensors...";
        var initTask = Task.Run(() => Sensors.Initialize());
        var completed = await Task.WhenAny(initTask, Task.Delay(SensorInitTimeout));

        if (completed != initTask)
        {
            SensorsHealthy = false;
            SensorStatus =
                $"WARNING: Sensor initialization has not returned after {SensorInitTimeout.TotalSeconds:F0}s and may be hung " +
                "(LibreHardwareMonitorLib's native driver open or its WMI motherboard-serial lookup can stall on some " +
                "hardware/WMI states). Tests are disabled until this resolves - closing and relaunching (possibly after a " +
                "machine restart) is the most reliable fix.";
            MoboSerial = "(unavailable - sensor initialization has not completed)";
            _log.Append(SensorStatus);

            // Log (only) if the stuck call eventually returns - the technician was already told to restart.
            _ = initTask.ContinueWith(t => _log.Append(t.IsFaulted
                ? $"NOTE: delayed sensor initialization eventually failed: {t.Exception!.GetBaseException().Message}"
                : $"NOTE: delayed sensor initialization eventually completed: {t.Result.Message} (restart to pick it up cleanly)."),
                TaskScheduler.Default);
        }
        else
        {
            try
            {
                var result = await initTask;
                SensorsHealthy = result.DriverLikelyLoaded;
                RealMoboSerial = result.MotherboardSerial;
                MoboSerial = RealMoboSerial ?? "(not available - WMI returned no serial)";
                SensorStatus = result.Message;
                _log.Append(result.Message);

                if (RealMoboSerial is null)
                {
                    _log.Append("WARNING: no motherboard serial available via WMI (Win32_BaseBoard). " +
                                "This is the PC's primary identity per CONTRACT.md - sessions cannot be created without it.");
                }

                if (SensorsHealthy)
                {
                    LoadDeviceNames();
                    LoadSsds();
                    StartIdlePolling();
                }
                else
                {
                    _log.Append("Tests disabled: sensors are not reporting data. Fix the driver/elevation issue above and restart the app.");
                }
            }
            catch (Exception ex)
            {
                SensorsHealthy = false;
                SensorStatus = $"Sensor initialization threw an exception: {ex.Message}";
                MoboSerial = "(unavailable)";
                _log.Append($"ERROR initializing sensors: {ex}");
            }
        }

        await LoadMotherboardInfoAsync();
        await LoadSystemInfoAsync();

        IsInitialized = true;
        Initialized?.Invoke();
    }

    /// <summary>One-time SMART read + drive picker build. Goes through the shared SensorMonitor
    /// (LHM doesn't support two Computer instances in one process).</summary>
    private void LoadSsds()
    {
        try
        {
            var drives = Sensors.ReadSsds();
            _ssdInfos = drives;
            Drives.Clear();
            foreach (var drive in drives)
            {
                Drives.Add(DriveCard.From(drive));
                _log.Append($"SSD detected: {SsdSmartReader.FormatSsdSummary(drive)}");
                // Raw values so unit/field questions (e.g. host reads in GB) can be checked against the drive's own tools.
                _log.Append($"  fw={drive.Firmware} health={drive.HealthPercent} status={drive.HealthStatus} " +
                            $"hostReadsGb={drive.HostReadsGb} hostWritesGb={drive.HostWritesGb} capacityBytes={drive.CapacityBytes}");
            }

            SsdStatus = drives.Count == 0 ? "(no drives detected via LibreHardwareMonitorLib)" : "";

            DriveOptions = DriveLetterResolver.Resolve(drives);
            foreach (var option in DriveOptions)
            {
                _log.Append($"SSD benchmark candidate: {option.DisplayText}");
            }
        }
        catch (Exception ex)
        {
            SsdStatus = $"SMART read failed: {ex.Message}";
            _log.Append($"WARNING: SSD SMART read failed: {ex.Message}");
        }
    }

    public void StartIdlePolling()
    {
        if (_idleTimer is not null || !SensorsHealthy)
        {
            return;
        }

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _idleTimer.Tick += (_, _) =>
        {
            if (CpuName.Length == 0 || GpuName.Length == 0)
            {
                LoadDeviceNames(); // HWiNFO may not have published a group yet when init ran
            }

            try { UpdateCpu(Sensors.ReadCpu()); }
            catch (Exception ex) { _log.Append($"Idle sensor poll failed: {ex.Message}"); }

            try { UpdateGpu(Sensors.ReadGpu()); }
            catch (Exception ex) { _log.Append($"Idle GPU poll failed: {ex.Message}"); }
        };
        _idleTimer.Start();
    }

    /// <summary>Board name + BIOS via WMI on a background thread with a bounded wait - WMI can stall
    /// on some machines (same hazard as the serial lookup during sensor init).</summary>
    private async Task LoadMotherboardInfoAsync()
    {
        try
        {
            var task = Task.Run(MotherboardInfoReader.Read);
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(10))) != task)
            {
                _log.Append("WARNING: motherboard info (WMI) did not respond within 10s.");
                return;
            }

            var info = await task;
            _boardInfo = info;
            MotherboardName = info.Name ?? "";
            BiosVersion = info.BiosVersion ?? "--";
            BiosDate = info.BiosDate ?? "--";
        }
        catch (Exception ex)
        {
            _log.Append($"Could not read motherboard info: {ex.Message}");
        }
    }

    /// <summary>Collects the PC specification for the report (WMI + what sensors already read), on a
    /// background thread with a bounded wait like the other WMI lookups.</summary>
    private async Task LoadSystemInfoAsync()
    {
        try
        {
            string? gpuName = GpuName.Length > 0 ? GpuName : null;
            double? vramMb = null;
            if (SensorsHealthy)
            {
                try { vramMb = Sensors.ReadGpu()?.MemoryTotalMb; }
                catch (Exception) { /* VRAM is a nice-to-have */ }
            }

            var task = Task.Run(() => SystemInfoCollector.Collect(_boardInfo, RealMoboSerial, _ssdInfos, gpuName, vramMb));
            if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(15))) != task)
            {
                _log.Append("WARNING: collecting PC specification (WMI) did not finish within 15s - reports will lack it.");
                return;
            }

            SystemInfo = await task;
            _log.Append($"PC specification collected ({SystemInfo.Count} sections) - sent with each submitted session.");
        }
        catch (Exception ex)
        {
            _log.Append($"Could not collect the PC specification: {ex.Message}");
        }
    }

    private void LoadDeviceNames()
    {
        try
        {
            var (cpu, gpu) = Sensors.ReadDeviceNames();
            if (CpuName.Length == 0 && cpu is not null) CpuName = cpu;
            if (GpuName.Length == 0 && gpu is not null) GpuName = gpu;
        }
        catch (Exception ex)
        {
            _log.Append($"Could not read CPU/GPU model names: {ex.Message}");
        }
    }

    public void PauseIdlePolling()
    {
        _idleTimer?.Stop();
        _idleTimer = null;
    }

    public void ResumeIdlePolling() => StartIdlePolling();

    // The Update* methods are called from TestSessionController's background poll loops as well
    // as the UI-thread idle timer, so they marshal themselves.
    public void UpdateCpu(SensorReadings reading) => OnUi(() =>
    {
        CpuReadout = SensorMonitor.FormatLiveReadout(reading);
        CpuTemp.Set(reading.CpuPackageTempC);
        CpuLoad.Set(reading.CpuLoadPct);
        CpuClock.Set(reading.CpuFrequencyMhz);
        CpuFan.Set(reading.CpuFanRpm);
    });

    public void UpdateGpu(GpuReadings? reading) => OnUi(() =>
    {
        GpuReadout = SensorMonitor.FormatGpuLiveReadout(reading);
        GpuTemp.Set(reading?.CoreTempC);
        GpuHotSpot.Set(reading?.HotSpotTempC);
        GpuLoad.Set(reading?.LoadPct);
        GpuCoreClock.Set(reading?.CoreClockMhz);
        GpuMemClock.Set(reading?.MemoryClockMhz);
        GpuFan.Set(reading?.FanRpm);
        GpuPower.Set(reading?.PowerW);
        GpuVram.Value = reading is { MemoryUsedMb: double used, MemoryTotalMb: double total } ? $"{used / 1024:F1} / {total / 1024:F1} GB" : "--";
    });

    public void UpdateRam(RamTestProgress progress) => OnUi(() =>
    {
        var mb = progress.TestedMb is double m ? $"{m:F0} MB" : "--";
        RamReadout = $"Tested: {mb}   Errors: {progress.ErrorCount}";
    });

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public void Dispose()
    {
        PauseIdlePolling();
        Sensors.Dispose();
        _hwInfo.Dispose();
    }
}
