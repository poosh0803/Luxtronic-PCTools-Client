using System.Management;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Storage;

namespace Luxtronic.PCTools.Services;

public sealed record SensorReadings(
    double? CpuPackageTempC,
    double? CpuLoadPct,
    double? CpuFanRpm,
    double? CpuFrequencyMhz);

public sealed class SensorInitResult
{
    public bool DriverLikelyLoaded { get; init; }
    public int TotalSensorsFound { get; init; }
    public int CpuSensorsFound { get; init; }
    public string? MotherboardSerial { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>
/// Wraps LibreHardwareMonitorLib for CPU + storage sensor access, plus a WMI lookup for the
/// motherboard serial (LHM doesn't expose board serials - that's WMI's job).
///
/// This is the component that has to surface PROJECT_PLAN.md §8's named risk: the WinRing0
/// kernel driver LHM depends on can silently fail to load on Windows 11 with Secure Boot +
/// Memory Integrity (HVCI) enabled, and sensors then just come back empty - no exception, no
/// obvious error. Initialize() checks sensor counts explicitly and reports degraded state
/// rather than letting a caller assume "no exception thrown" means "sensors are working".
///
/// IMPORTANT - owns the only <see cref="Computer"/> instance for the whole app. LibreHardwareMonitorLib
/// does not support multiple concurrent Computer instances in one process: opening a second one
/// (previously done in a since-removed SsdSmartReader that owned its own Computer) corrupted this
/// one's internal CPU hardware state and made the very next ReadCpu() throw a
/// NullReferenceException deep inside LHM's own GenericCpu.Update() - confirmed by reproducing it
/// directly. SSD SMART reads (<see cref="ReadSsds"/>) go through this same Computer/visitor for
/// that reason, not a separate reader.
/// </summary>
public sealed class SensorMonitor : IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private bool _initialized;

    public SensorMonitor()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsMotherboardEnabled = true, // needed for Super IO fan/temp sensors on many boards
            IsMemoryEnabled = false,
            IsGpuEnabled = false,
            IsStorageEnabled = true,
            IsNetworkEnabled = false,
            IsControllerEnabled = true,
        };
    }

    /// <summary>
    /// Opens the LHM Computer, does a first poll, and returns an explicit health verdict.
    /// Callers MUST check DriverLikelyLoaded / TotalSensorsFound rather than assuming success
    /// just because no exception was thrown - see class remarks.
    /// </summary>
    public SensorInitResult Initialize()
    {
        _computer.Open();
        _computer.Accept(_visitor);
        _initialized = true;

        var cpuHardware = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);

        var totalSensors = _computer.Hardware.Sum(CountSensorsRecursive);
        var cpuSensors = cpuHardware is null ? 0 : CountSensorsRecursive(cpuHardware);

        // Sensor *objects* existing isn't enough - LHM can enumerate "CPU Package"/"CPU Core #N"
        // Temperature sensors while their Value stays permanently null (most commonly because
        // another hardware-monitoring app - HWiNFO, MSI Center/Afterburner, Corsair iCUE, etc. -
        // already has the WinRing0/Ring0 driver open exclusively). Load-type sensors don't need
        // driver access at all, so they read fine even in that degraded state and would make the
        // old count-only check falsely report "Sensors OK". Checking for at least one live
        // Temperature value catches that specific failure mode.
        var anyTemperatureValueReadable = _computer.Hardware
            .SelectMany(EnumerateSensorsRecursive)
            .Any(s => s.SensorType == SensorType.Temperature && s.Value.HasValue);

        var moboSerial = TryReadMotherboardSerialViaWmi();

        var (driverLikelyLoaded, message) = EvaluateSensorHealth(totalSensors, cpuSensors, anyTemperatureValueReadable);

        return new SensorInitResult
        {
            DriverLikelyLoaded = driverLikelyLoaded,
            TotalSensorsFound = totalSensors,
            CpuSensorsFound = cpuSensors,
            MotherboardSerial = moboSerial,
            Message = message,
        };
    }

    /// <summary>
    /// Pure verdict+message logic for the driver-health check described in the class remarks -
    /// split out from <see cref="Initialize"/> so it can be unit tested without a real
    /// LibreHardwareMonitorLib Computer/IHardware instance (which need real hardware).
    ///
    /// Three distinct states, not two: (1) zero sensors enumerated at all - the WinRing0 driver
    /// itself never loaded (Secure Boot/HVCI, no elevation); (2) sensors enumerated but no
    /// Temperature value is actually readable - the driver loaded but something else already has
    /// exclusive access to it (another monitoring app), so Load-type sensors work fine while
    /// everything else silently reads null; (3) both counts nonzero AND at least one temperature
    /// reads - genuinely healthy. Only (3) counts as DriverLikelyLoaded; (1) and (2) get distinct
    /// messages since the fix for each is different.
    /// </summary>
    internal static (bool DriverLikelyLoaded, string Message) EvaluateSensorHealth(
        int totalSensors, int cpuSensors, bool anyTemperatureValueReadable)
    {
        var sensorsEnumerated = totalSensors > 0 && cpuSensors > 0;
        var driverLikelyLoaded = sensorsEnumerated && anyTemperatureValueReadable;

        string message;
        if (driverLikelyLoaded)
        {
            message = $"Sensors OK - {totalSensors} sensor(s) found ({cpuSensors} on CPU).";
        }
        else if (sensorsEnumerated)
        {
            message = $"WARNING: {totalSensors} sensor(s) were found ({cpuSensors} on CPU) but " +
                      "temperature readings are all null. This usually means another hardware-" +
                      "monitoring app (HWiNFO, MSI Center/Afterburner, Corsair iCUE, etc.) already " +
                      "has the monitoring driver open exclusively - close other hardware-monitoring " +
                      "tools and relaunch. CPU load data may still work even though temp/clock/fan " +
                      "data will not, since load doesn't need driver access.";
        }
        else
        {
            message = "WARNING: LibreHardwareMonitorLib returned 0 sensors. This is the known failure " +
                      "mode where the WinRing0 kernel driver silently fails to load - typically caused " +
                      "by Secure Boot + Memory Integrity (HVCI) being enabled, or the app not running " +
                      "elevated. Check: (1) app is running as Administrator, (2) Windows Security > " +
                      "Device security > Core isolation > Memory integrity is OFF, or add a WinRing0 " +
                      "driver exception if your org requires HVCI on. No temps/load/fan data will be " +
                      "available until this is resolved.";
        }

        return (driverLikelyLoaded, message);
    }

    /// <summary>Re-polls hardware and returns the current CPU readings we stream as telemetry.</summary>
    public SensorReadings ReadCpu()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("SensorMonitor.Initialize() must be called first.");
        }

        _computer.Accept(_visitor);

        double? packageTemp = null;
        double? load = null;
        double? fanRpm = null;
        var coreClocks = new List<double>();

        foreach (var hw in _computer.Hardware)
        {
            foreach (var sensor in EnumerateSensorsRecursive(hw))
            {
                if (sensor.Value is null) continue;
                double value = sensor.Value.Value;

                if (hw.HardwareType == HardwareType.Cpu)
                {
                    if (sensor.SensorType == SensorType.Temperature && packageTemp is null &&
                        (sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                         sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase)))
                    {
                        packageTemp = value;
                    }
                    else if (sensor.SensorType == SensorType.Load && load is null &&
                             sensor.Name.Contains("Total", StringComparison.OrdinalIgnoreCase))
                    {
                        load = value;
                    }
                    // Per-core clock sensors (LHM names these "CPU Core #1", "CPU Core #2", ...).
                    // Excludes "Bus Speed", which LHM also reports as a Clock sensor but isn't a
                    // core frequency. Averaged below rather than taking one core, since cores can
                    // be at very different clocks under partial load (e.g. one boosted, others
                    // idle) and an average is more representative of "how hard is this CPU
                    // running" for the dashboard than an arbitrary single core would be.
                    else if (sensor.SensorType == SensorType.Clock &&
                             sensor.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) &&
                             !sensor.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase))
                    {
                        coreClocks.Add(value);
                    }
                }

                if (sensor.SensorType == SensorType.Fan && fanRpm is null &&
                    sensor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase))
                {
                    fanRpm = value;
                }
            }
        }

        // Fallback: no CPU-labelled fan found - take the first fan sensor on the motherboard.
        if (fanRpm is null)
        {
            var fallbackFan = _computer.Hardware
                .Where(h => h.HardwareType == HardwareType.Motherboard)
                .SelectMany(EnumerateSensorsRecursive)
                .FirstOrDefault(s => s.SensorType == SensorType.Fan && s.Value is not null);
            fanRpm = fallbackFan?.Value;
        }

        return new SensorReadings(packageTemp, load, fanRpm, AverageClockMhz(coreClocks));
    }

    /// <summary>Point-in-time SMART read for every drive LHM can see, through the same shared
    /// Computer/visitor as <see cref="ReadCpu"/> - see class remarks on why a second Computer
    /// instance isn't used. Not part of the continuous polling loop; called once at app open.</summary>
    public IReadOnlyList<SsdSmartInfo> ReadSsds()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("SensorMonitor.Initialize() must be called first.");
        }

        _computer.Accept(_visitor);
        return SsdSmartReader.ReadAll(_computer.Hardware);
    }

    /// <summary>
    /// Pure averaging helper split out from <see cref="ReadCpu"/> so it's unit testable without
    /// real hardware. Returns null (not 0 or NaN) when no core clock sensors were found, so the
    /// caller can tell "no data" apart from "averaged to zero".
    /// </summary>
    internal static double? AverageClockMhz(IReadOnlyCollection<double> coreClocksMhz) =>
        coreClocksMhz.Count == 0 ? null : coreClocksMhz.Average();

    /// <summary>
    /// Compact live readout for the UI's sensor status line (e.g. "CPU: 40C   Load: 12%   4187
    /// MHz   Fan: 2303 RPM"), shown in place of the static "Sensors OK - N sensor(s) found"
    /// message once sensors are confirmed healthy - a technician watching the app benefits more
    /// from seeing real live numbers than a one-time count. "--" stands in for any reading that's
    /// null this cycle (e.g. no fan sensor on this board) rather than omitting the field, so the
    /// layout doesn't jump around from sample to sample.
    /// </summary>
    internal static string FormatLiveReadout(SensorReadings reading)
    {
        var temp = reading.CpuPackageTempC is double t ? $"{t:F0}C" : "--";
        var load = reading.CpuLoadPct is double l ? $"{l:F0}%" : "--";
        var clock = reading.CpuFrequencyMhz is double f ? $"{f:F0} MHz" : "--";
        var fan = reading.CpuFanRpm is double r ? $"{r:F0} RPM" : "--";
        return $"CPU: {temp}   Load: {load}   {clock}   Fan: {fan}";
    }

    private static int CountSensorsRecursive(IHardware hw) => EnumerateSensorsRecursive(hw).Count();

    private static IEnumerable<ISensor> EnumerateSensorsRecursive(IHardware hw)
    {
        foreach (var s in hw.Sensors) yield return s;
        foreach (var sub in hw.SubHardware)
        {
            foreach (var s in EnumerateSensorsRecursive(sub)) yield return s;
        }
    }

    /// <summary>
    /// Motherboard serial number, used as the PC's primary identity (CONTRACT.md §2,
    /// machines.mobo_serial). Read via WMI (Win32_BaseBoard) rather than LibreHardwareMonitor,
    /// which doesn't expose board serials. Some boards report placeholder junk like
    /// "To Be Filled By O.E.M." or "Default string" instead of a real serial - that's returned
    /// as-is (still logged by the caller) rather than nulled out, since we need *something* to
    /// key sessions on; see README for the open question this raises about collisions across
    /// such boards.
    /// </summary>
    private static string? TryReadMotherboardSerialViaWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BaseBoard");
            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["SerialNumber"] is string serial && !string.IsNullOrWhiteSpace(serial))
                {
                    return serial.Trim();
                }
            }
        }
        catch
        {
            // WMI can be unavailable/blocked in locked-down environments - treated as "no
            // serial", surfaced to the caller as null so the UI can show an explicit warning
            // rather than silently sending an empty string as the PC's primary key.
        }

        return null;
    }

    public void Dispose()
    {
        if (_initialized)
        {
            _computer.Close();
        }
    }

    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware)
            {
                sub.Accept(this);
            }
        }

        public void VisitSensor(ISensor sensor) { }

        public void VisitParameter(IParameter parameter) { }
    }
}
