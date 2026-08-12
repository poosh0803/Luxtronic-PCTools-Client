using System.Management;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Storage;

namespace Luxtronic.PCTools.Services;

public sealed record SensorReadings(
    double? CpuPackageTempC,
    double? CpuLoadPct,
    double? CpuFanRpm,
    double? CpuFrequencyMhz);

/// <summary>
/// One GPU's live readings. Ground-truthed against a real NVIDIA GTX 1080 Ti via HWiNFO's shared
/// memory (see <see cref="HwInfoSensorReader.ExtractGpuReadings"/>) - field-matching has not been
/// validated against AMD/Intel GPUs, which may use different sensor names for the same metrics.
/// </summary>
public sealed record GpuReadings(
    double? CoreTempC,
    double? HotSpotTempC,
    double? CoreClockMhz,
    double? MemoryClockMhz,
    double? LoadPct,
    double? FanRpm,
    double? PowerW,
    double? MemoryUsedMb,
    double? MemoryTotalMb);

public sealed class SensorInitResult
{
    public bool DriverLikelyLoaded { get; init; }
    public string? MotherboardSerial { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>
/// CPU and GPU sensors come from HWiNFO's shared memory (<see cref="HwInfoSensorReader"/>) -
/// HWiNFO has proven more reliable than LibreHardwareMonitorLib's WinRing0-based MSR reads on real
/// affected hardware (see HwInfoSensorReader's class remarks for the field evidence). Storage/SMART
/// stays on LibreHardwareMonitorLib (<see cref="ReadSsds"/> / <see cref="SsdSmartReader"/>) - HWiNFO's
/// shared memory doesn't expose Power-On Hours/Count or Reallocated Sectors, which are already sent
/// to the server per SSD_SMART_ADDENDUM.md. This class also does a WMI lookup for the motherboard
/// serial - LHM doesn't expose board serials, and neither does HWiNFO's shared memory (it only
/// reports sensor readings, not static system identity).
///
/// IMPORTANT - owns the only <see cref="Computer"/> instance for the whole app. LibreHardwareMonitorLib
/// does not support multiple concurrent Computer instances in one process: opening a second one
/// (previously done in a since-removed SsdSmartReader that owned its own) corrupted this one's
/// internal hardware state and made the very next call throw a NullReferenceException deep inside
/// LHM's own code - confirmed by reproducing it directly. Still relevant even though CPU/GPU no
/// longer go through this Computer instance - Storage still does.
/// </summary>
public sealed class SensorMonitor : IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private readonly HwInfoSensorReader _hwInfo = new();
    private bool _initialized;

    public SensorMonitor()
    {
        _computer = new Computer
        {
            // CPU/GPU sensing moved to HwInfoSensorReader - see class remarks. Motherboard/
            // Controller are left enabled even though nothing currently reads them (they only
            // mattered for CPU fan, which HWiNFO now provides) - deliberately not touched, to
            // avoid risking an unrelated regression in the still-LHM-backed Storage path for no
            // benefit.
            IsCpuEnabled = false,
            IsMotherboardEnabled = true,
            IsMemoryEnabled = false,
            IsGpuEnabled = false,
            IsStorageEnabled = true,
            IsNetworkEnabled = false,
            IsControllerEnabled = true,
        };
    }

    /// <summary>
    /// Opens the LHM Computer (for Storage), reads HWiNFO's shared memory once (for CPU), and
    /// returns an explicit health verdict. Callers MUST check DriverLikelyLoaded / TotalSensorsFound
    /// rather than assuming success just because no exception was thrown.
    /// </summary>
    public SensorInitResult Initialize()
    {
        _computer.Open();
        _computer.Accept(_visitor);
        _initialized = true;

        var (hwInfoReachable, cpuReading) = _hwInfo.ReadCpuWithReachability();
        var (driverLikelyLoaded, message) = EvaluateSensorHealth(
            hwInfoReachable, cpuReading.CpuPackageTempC.HasValue);

        var moboSerial = TryReadMotherboardSerialViaWmi();

        return new SensorInitResult
        {
            DriverLikelyLoaded = driverLikelyLoaded,
            MotherboardSerial = moboSerial,
            Message = message,
        };
    }

    /// <summary>
    /// Pure verdict+message logic for the HWiNFO-reachability health check - split out so it can
    /// be unit tested without a real HWiNFO instance running.
    ///
    /// Two distinct failure states, not one: (1) HWiNFO isn't running, or is running without
    /// Shared Memory Support enabled - nothing to read at all; (2) HWiNFO is reachable, but no CPU
    /// temperature reading matched this hardware's label set (e.g. an untested CPU vendor/HWiNFO
    /// version) - these need different guidance, so they get different messages.
    /// </summary>
    internal static (bool DriverLikelyLoaded, string Message) EvaluateSensorHealth(
        bool hwInfoReachable, bool cpuTempReadable)
    {
        if (!hwInfoReachable)
        {
            return (false,
                "WARNING: HWiNFO isn't reachable - either it isn't running, or Settings > " +
                "\"Shared Memory Support\" isn't enabled (a one-time, GUI-only toggle - HWiNFO " +
                "must be restarted after enabling it for the shared-memory block to actually get " +
                "created). No CPU or GPU sensor data will be available until this is resolved. " +
                "publish-assets/Launch.ps1 starts HWiNFO automatically before launching the app - " +
                "if you're running the exe directly instead, launch tools/hwi/HWiNFO64.exe yourself first.");
        }

        if (!cpuTempReadable)
        {
            return (false,
                "WARNING: HWiNFO is reachable, but no CPU temperature reading matched this " +
                "hardware's label set. This CPU/HWiNFO version combination may not be validated - " +
                "see HwInfoSensorReader's class remarks (only ground-truthed against one Intel " +
                "CPU so far).");
        }

        return (true, "Sensors OK - CPU temperature readable via HWiNFO.");
    }

    /// <summary>Re-polls HWiNFO's shared memory and returns the current CPU readings we stream as
    /// telemetry.</summary>
    public SensorReadings ReadCpu()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("SensorMonitor.Initialize() must be called first.");
        }

        return _hwInfo.ReadCpu();
    }

    /// <summary>Point-in-time SMART read for every drive LHM can see, through the shared
    /// Computer/visitor - see class remarks on why a second Computer instance isn't used. Not part
    /// of the continuous polling loop; called once at app open and after each CPU test run.</summary>
    public IReadOnlyList<SsdSmartInfo> ReadSsds()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("SensorMonitor.Initialize() must be called first.");
        }

        _computer.Accept(_visitor);
        return SsdSmartReader.ReadAll(_computer.Hardware);
    }

    /// <summary>Re-polls HWiNFO's shared memory and returns the current GPU readings, or null if
    /// no GPU group was found (no GPU installed, or HWiNFO not reachable). Field-matching only
    /// ground-truthed against NVIDIA so far.</summary>
    public GpuReadings? ReadGpu()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("SensorMonitor.Initialize() must be called first.");
        }

        return _hwInfo.ReadGpu();
    }

    /// <summary>
    /// Pure averaging helper split out from <see cref="HwInfoSensorReader.ExtractCpuReadings"/> so
    /// it's unit testable without real hardware. Returns null (not 0 or NaN) when no core clock
    /// sensors were found, so the caller can tell "no data" apart from "averaged to zero".
    /// </summary>
    internal static double? AverageClockMhz(IReadOnlyCollection<double> coreClocksMhz) =>
        coreClocksMhz.Count == 0 ? null : coreClocksMhz.Average();

    /// <summary>
    /// Compact live readout for the UI's sensor status line (e.g. "CPU: 40C   Load: 12%   4187
    /// MHz   Fan: 2303 RPM"), shown in place of the static "Sensors OK" message once sensors are
    /// confirmed healthy - a technician watching the app benefits more from seeing real live
    /// numbers than a one-time count. "--" stands in for any reading that's null this cycle (e.g.
    /// no fan sensor on this board) rather than omitting the field, so the layout doesn't jump
    /// around from sample to sample.
    /// </summary>
    internal static string FormatLiveReadout(SensorReadings reading)
    {
        var temp = reading.CpuPackageTempC is double t ? $"{t:F0}C" : "--";
        var load = reading.CpuLoadPct is double l ? $"{l:F0}%" : "--";
        var clock = reading.CpuFrequencyMhz is double f ? $"{f:F0} MHz" : "--";
        var fan = reading.CpuFanRpm is double r ? $"{r:F0} RPM" : "--";
        return $"CPU: {temp}   Load: {load}   {clock}   Fan: {fan}";
    }

    /// <summary>Live readout for the UI's GPU status line, same "--" placeholder convention as
    /// <see cref="FormatLiveReadout"/>. Null input (no GPU detected) gets its own message rather
    /// than a line full of placeholders.</summary>
    internal static string FormatGpuLiveReadout(GpuReadings? reading)
    {
        if (reading is null)
        {
            return "(no GPU detected)";
        }

        var temp = reading.CoreTempC is double t ? $"{t:F0}C" : "--";
        var hotSpot = reading.HotSpotTempC is double h ? $"{h:F0}C" : "--";
        var load = reading.LoadPct is double l ? $"{l:F0}%" : "--";
        var coreClock = reading.CoreClockMhz is double cc ? $"{cc:F0} MHz" : "--";
        var memClock = reading.MemoryClockMhz is double mc ? $"{mc:F0} MHz" : "--";
        var fan = reading.FanRpm is double f ? $"{f:F0} RPM" : "--";
        var power = reading.PowerW is double p ? $"{p:F0}W" : "--";
        var vram = reading.MemoryUsedMb is double mu && reading.MemoryTotalMb is double mt
            ? $"{mu:F0}/{mt:F0} MB"
            : "--";

        return $"GPU: {temp} (hotspot {hotSpot})   Load: {load}   Core: {coreClock}   Mem: {memClock}   " +
               $"Fan: {fan}   Power: {power}   VRAM: {vram}";
    }

    /// <summary>
    /// Motherboard serial number, used as the PC's primary identity (CONTRACT.md §2,
    /// machines.mobo_serial). Read via WMI (Win32_BaseBoard) rather than LibreHardwareMonitor,
    /// which doesn't expose board serials. Some boards report placeholder junk like
    /// "To Be Filled By O.E.M." or "Default string" instead of a real serial - that's returned
    /// as-is (still logged by the caller) rather than nulled out, since we need *something* to
    /// key sessions on; see README for the open question this raises about collisions across
    /// such boards.
    ///
    /// Explicit query timeout (<see cref="EnumerationOptions.Timeout"/>) - a bare
    /// ManagementObjectSearcher.Get() has no default timeout and is known to hang indefinitely
    /// rather than throw when the WMI repository/service is in a bad state on a given machine
    /// (seen on a real technician test machine: this call, invoked synchronously during app
    /// startup, was the leading suspect for a published build showing a permanently unresponsive
    /// window). This bounds it so the caller gets "no serial" back instead of hanging forever.
    /// </summary>
    private static string? TryReadMotherboardSerialViaWmi()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BaseBoard");
            searcher.Options.Timeout = TimeSpan.FromSeconds(10);
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
            // WMI can be unavailable/blocked in locked-down environments, or the bounded query
            // above can time out - both treated as "no serial", surfaced to the caller as null so
            // the UI can show an explicit warning rather than silently sending an empty string as
            // the PC's primary key.
        }

        return null;
    }

    public void Dispose()
    {
        if (_initialized)
        {
            _computer.Close();
        }
        _hwInfo.Dispose();
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
