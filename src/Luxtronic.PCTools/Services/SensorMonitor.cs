using System.Management;
using LibreHardwareMonitor.Hardware;

namespace Luxtronic.PCTools.Services;

public sealed record SensorReadings(
    double? CpuPackageTempC,
    double? CpuLoadPct,
    double? CpuFanRpm);

public sealed class SensorInitResult
{
    public bool DriverLikelyLoaded { get; init; }
    public int TotalSensorsFound { get; init; }
    public int CpuSensorsFound { get; init; }
    public string? MotherboardSerial { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>
/// Wraps LibreHardwareMonitorLib for CPU sensor access, plus a WMI lookup for the
/// motherboard serial (LHM doesn't expose board serials - that's WMI's job).
///
/// This is the component that has to surface PROJECT_PLAN.md §8's named risk: the WinRing0
/// kernel driver LHM depends on can silently fail to load on Windows 11 with Secure Boot +
/// Memory Integrity (HVCI) enabled, and sensors then just come back empty - no exception, no
/// obvious error. Initialize() checks sensor counts explicitly and reports degraded state
/// rather than letting a caller assume "no exception thrown" means "sensors are working".
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
            IsStorageEnabled = false,
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

        var moboSerial = TryReadMotherboardSerialViaWmi();

        var driverLikelyLoaded = totalSensors > 0 && cpuSensors > 0;

        var message = driverLikelyLoaded
            ? $"Sensors OK - {totalSensors} sensor(s) found ({cpuSensors} on CPU)."
            : "WARNING: LibreHardwareMonitorLib returned 0 sensors. This is the known failure " +
              "mode where the WinRing0 kernel driver silently fails to load - typically caused " +
              "by Secure Boot + Memory Integrity (HVCI) being enabled, or the app not running " +
              "elevated. Check: (1) app is running as Administrator, (2) Windows Security > " +
              "Device security > Core isolation > Memory integrity is OFF, or add a WinRing0 " +
              "driver exception if your org requires HVCI on. No temps/load/fan data will be " +
              "available until this is resolved.";

        return new SensorInitResult
        {
            DriverLikelyLoaded = driverLikelyLoaded,
            TotalSensorsFound = totalSensors,
            CpuSensorsFound = cpuSensors,
            MotherboardSerial = moboSerial,
            Message = message,
        };
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

        return new SensorReadings(packageTemp, load, fanRpm);
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
