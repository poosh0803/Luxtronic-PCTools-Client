using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Storage;

namespace Luxtronic.PCTools.Services;

/// <summary>
/// One drive's identity + health snapshot. SMART info is read once per call, not streamed
/// continuously like CPU telemetry - health/wear data changes slowly, so point-in-time reads
/// (e.g. at SSD test start/end) are what CONTRACT.md's SSD threshold model (min_seq_*_mb_s,
/// smart_reallocated_sectors_max) actually needs.
///
/// <see cref="ReallocatedSectorsCount"/> is ATA/SATA-only and <see cref="PercentageUsed"/> /
/// <see cref="AvailableSparePercent"/> are NVMe-only - see the class remarks on
/// <see cref="SsdSmartReader"/> for why these can't be unified into one field. Exactly one of
/// those two groups will be non-null depending on <see cref="IsNvme"/>; a real technician PC with
/// an NVMe drive will never populate ReallocatedSectorsCount, and CONTRACT.md's
/// smart_reallocated_sectors_max config threshold has no NVMe equivalent evaluated server-side
/// yet - flagged for reconciliation, not silently worked around here.
/// </summary>
public sealed record SsdSmartInfo(
    string Model,
    string? SerialNumber,
    bool IsNvme,
    double? TemperatureC,
    double? PercentageUsed,
    double? AvailableSparePercent,
    long? ReallocatedSectorsCount,
    long? PowerOnHours,
    long? MediaErrors);

/// <summary>
/// Wraps LibreHardwareMonitorLib's Storage hardware group for drive identity (model, serial) and
/// S.M.A.R.T. health data. Uses its own <see cref="Computer"/> instance scoped to storage only
/// (IsStorageEnabled), separate from <see cref="SensorMonitor"/>'s CPU-only instance, since the
/// two are unrelated concerns with different lifecycles (CPU sensors get polled continuously
/// during a run; SMART data is read as point-in-time snapshots) and PROJECT_PLAN.md's concurrency
/// rule keeps SSD testing exclusive from everything else anyway.
///
/// IMPORTANT - attribute ID schemes are NOT unified across bus types. LibreHardwareMonitorLib
/// (via DiskInfoToolkit) exposes SMART data through the same <see cref="ISmart.Attributes"/>
/// list regardless of bus type, but the *meaning* of a given numeric ID depends entirely on
/// whether the drive is NVMe or ATA/SATA - e.g. attribute ID 5 is "Percentage Used" on NVMe but
/// "Reallocated Sectors Count" on ATA/SATA. These are unrelated metrics that happen to share a
/// number. Every attribute lookup here is gated on <c>isNvme</c> for exactly this reason -
/// reading an ID without checking bus type first would silently produce a wrong, dangerous
/// answer (e.g. reading an NVMe drive's 8% wear level as if it were a SATA drive's reallocated
/// sector count).
///
/// Not yet validated against a real ATA/SATA drive - ground-truthed only against two NVMe drives
/// (see git history/commit message for the exact dump). The ATA attribute IDs below
/// (5 = Reallocated Sectors Count, 9 = Power-On Hours) are the standard, widely-documented SMART
/// attribute table values, but should be re-confirmed against a real SATA SSD/HDD before this is
/// relied on for a technician's pass/fail decision.
/// </summary>
public sealed class SsdSmartReader : IDisposable
{
    // NVMe SMART/health log attribute IDs, as synthesized by DiskInfoToolkit (confirmed via a
    // real elevated dump against two NVMe drives - Crucial CT1000P2SSD8, Micron 2210).
    private const byte NvmeAvailableSpareId = 3;
    private const byte NvmePercentageUsedId = 5;
    private const byte NvmePowerOnHoursId = 12;
    private const byte NvmeMediaErrorsId = 14;

    // Standard ATA/SATA SMART attribute table IDs (industry-standard numbering, not yet
    // confirmed against a real drive via this codebase - see class remarks).
    private const byte AtaReallocatedSectorsId = 5;
    private const byte AtaPowerOnHoursId = 9;

    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private bool _initialized;

    public SsdSmartReader()
    {
        _computer = new Computer
        {
            IsCpuEnabled = false,
            IsMotherboardEnabled = false,
            IsMemoryEnabled = false,
            IsGpuEnabled = false,
            IsStorageEnabled = true,
            IsNetworkEnabled = false,
            IsControllerEnabled = false,
        };
    }

    public void Initialize()
    {
        _computer.Open();
        _initialized = true;
    }

    /// <summary>Reads current identity + SMART health for every drive LibreHardwareMonitorLib
    /// can see. A drive that fails to yield a serial/model still gets a best-effort entry rather
    /// than being dropped, matching the "log what's available, don't block on missing serials"
    /// approach already used for the motherboard serial (PROJECT_PLAN.md §8).</summary>
    public IReadOnlyList<SsdSmartInfo> ReadAll()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("SsdSmartReader.Initialize() must be called first.");
        }

        _computer.Accept(_visitor);

        var results = new List<SsdSmartInfo>();
        foreach (var hw in _computer.Hardware)
        {
            if (hw is not StorageDevice storageDevice)
            {
                continue;
            }

            var storage = storageDevice.Storage;
            var isNvme = storage?.IsNVMe ?? false;
            var model = storage?.Model ?? hw.Name;
            var serial = storage?.SerialNumber;

            double? tempC = hw.Sensors
                .FirstOrDefault(s => s.SensorType == SensorType.Temperature &&
                                      s.Name.Contains("Composite", StringComparison.OrdinalIgnoreCase))
                ?.Value;

            var attributesById = storageDevice.Attributes
                .GroupBy(a => a.Id)
                .ToDictionary(g => g.Key, g => g.First().Value);

            results.Add(BuildSsdInfo(model, serial, isNvme, tempC, attributesById));
        }

        return results;
    }

    /// <summary>
    /// Pure attribute-extraction logic, split out from <see cref="ReadAll"/> so it's unit
    /// testable without real hardware. See class remarks - attribute IDs are only meaningful
    /// relative to <paramref name="isNvme"/>, never read unconditionally.
    /// </summary>
    internal static SsdSmartInfo BuildSsdInfo(
        string model, string? serialNumber, bool isNvme, double? temperatureC,
        IReadOnlyDictionary<byte, float> attributesById)
    {
        double? percentageUsed = isNvme ? GetDouble(attributesById, NvmePercentageUsedId) : null;
        double? availableSpare = isNvme ? GetDouble(attributesById, NvmeAvailableSpareId) : null;
        long? reallocatedSectors = isNvme ? null : GetLong(attributesById, AtaReallocatedSectorsId);
        long? powerOnHours = GetLong(attributesById, isNvme ? NvmePowerOnHoursId : AtaPowerOnHoursId);
        long? mediaErrors = isNvme ? GetLong(attributesById, NvmeMediaErrorsId) : null;

        return new SsdSmartInfo(
            model, serialNumber, isNvme, temperatureC,
            percentageUsed, availableSpare, reallocatedSectors, powerOnHours, mediaErrors);
    }

    /// <summary>
    /// One-line summary for the UI (e.g. "CT1000P2SSD8 (2050E4D9C945): 44C   Used: 8%   Spare:
    /// 100%" for NVMe, or "Model (Serial): 35C   Reallocated: 3   Power-on: 12000h" for ATA/SATA)
    /// - shows the fields that are actually meaningful for the drive's bus type rather than a
    /// fixed set of columns, since (per class remarks) NVMe and ATA/SATA drives don't share a
    /// SMART vocabulary. "--" stands in for any null field.
    /// </summary>
    internal static string FormatSsdSummary(SsdSmartInfo info)
    {
        var serial = info.SerialNumber ?? "no serial";
        var temp = info.TemperatureC is double t ? $"{t:F0}C" : "--";

        if (info.IsNvme)
        {
            var used = info.PercentageUsed is double u ? $"{u:F0}%" : "--";
            var spare = info.AvailableSparePercent is double s ? $"{s:F0}%" : "--";
            return $"{info.Model} ({serial}): {temp}   Used: {used}   Spare: {spare}";
        }

        var reallocated = info.ReallocatedSectorsCount is long r ? r.ToString() : "--";
        var hours = info.PowerOnHours is long h ? $"{h}h" : "--";
        return $"{info.Model} ({serial}): {temp}   Reallocated: {reallocated}   Power-on: {hours}";
    }

    private static double? GetDouble(IReadOnlyDictionary<byte, float> attributesById, byte id) =>
        attributesById.TryGetValue(id, out var value) ? value : null;

    private static long? GetLong(IReadOnlyDictionary<byte, float> attributesById, byte id) =>
        attributesById.TryGetValue(id, out var value) ? (long)value : null;

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
