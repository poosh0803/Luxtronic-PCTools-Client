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
    long? PowerOnCount,
    long? MediaErrors);

/// <summary>
/// Reads drive identity (model, serial) and S.M.A.R.T. health data from LibreHardwareMonitorLib's
/// Storage hardware group.
///
/// Stateless by design - does NOT own a <see cref="Computer"/> instance. LibreHardwareMonitorLib
/// does not support multiple concurrent Computer instances in one process: an earlier version of
/// this class opened its own Computer(IsStorageEnabled=true), which corrupted SensorMonitor's
/// separate CPU-scoped Computer instance and made its next ReadCpu() throw a
/// NullReferenceException deep inside LHM's own GenericCpu.Update() - confirmed by reproducing it
/// directly. <see cref="SensorMonitor"/> now owns the single shared Computer (with
/// IsStorageEnabled=true) and calls <see cref="ReadAll"/> with its already-updated
/// <c>Computer.Hardware</c> collection via <see cref="SensorMonitor.ReadSsds"/>.
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
/// Not yet validated against a real ATA/SATA drive - ground-truthed only against two NVMe drives.
/// The ATA attribute IDs below (5 = Reallocated Sectors Count, 9 = Power-On Hours,
/// 12 = Power Cycle Count) are the standard, widely-documented SMART attribute table values, but
/// should be re-confirmed against a real SATA SSD/HDD before this is relied on for a technician's
/// pass/fail decision.
/// </summary>
public static class SsdSmartReader
{
    // NVMe SMART/health log attribute IDs, as synthesized by DiskInfoToolkit (confirmed via a
    // real elevated dump against two NVMe drives - Crucial CT1000P2SSD8, Micron 2210).
    private const byte NvmeAvailableSpareId = 3;
    private const byte NvmePercentageUsedId = 5;
    private const byte NvmePowerCyclesId = 11; // NVMe calls this "Power Cycles", not "Power On Count"
    private const byte NvmePowerOnHoursId = 12;
    private const byte NvmeMediaErrorsId = 14;

    // Standard ATA/SATA SMART attribute table IDs (industry-standard numbering, not yet
    // confirmed against a real drive via this codebase - see class remarks).
    private const byte AtaReallocatedSectorsId = 5;
    private const byte AtaPowerOnHoursId = 9;
    private const byte AtaPowerCycleCountId = 12;

    /// <summary>Reads current identity + SMART health for every drive in an already-updated
    /// hardware collection (i.e. after a Computer.Accept(visitor) call). A drive that fails to
    /// yield a serial/model still gets a best-effort entry rather than being dropped, matching
    /// the "log what's available, don't block on missing serials" approach already used for the
    /// motherboard serial (PROJECT_PLAN.md §8).</summary>
    public static IReadOnlyList<SsdSmartInfo> ReadAll(IEnumerable<IHardware> hardware)
    {
        var results = new List<SsdSmartInfo>();
        foreach (var hw in hardware)
        {
            if (hw is not StorageDevice storageDevice)
            {
                continue;
            }

            var storage = storageDevice.Storage;
            var isNvme = storage?.IsNVMe ?? false;
            var model = storage?.Model ?? hw.Name;
            var serial = storage?.SerialNumber;

            double? tempC = FindTemperature(hw);

            var attributesById = storageDevice.Attributes
                .GroupBy(a => a.Id)
                .ToDictionary(g => g.Key, g => g.First().Value);

            results.Add(BuildSsdInfo(model, serial, isNvme, tempC, attributesById));
        }

        return results;
    }

    /// <summary>
    /// NVMe drives report a "Composite Temperature" sensor, checked first since it's the primary
    /// reading. ATA/SATA drives (confirmed via a real WD SATA SSD, connected over USB) instead
    /// report a sensor literally named "Temperature" with no "Composite" qualifier - checked only
    /// as a fallback so it can't accidentally match NVMe's "Warning Temperature"/"Critical
    /// Temperature" sensors, which are threshold values, not actual readings, and would otherwise
    /// be the closest exact-name match if this were reordered.
    /// </summary>
    private static double? FindTemperature(IHardware hw)
    {
        var composite = hw.Sensors.FirstOrDefault(s =>
            s.SensorType == SensorType.Temperature &&
            s.Name.Contains("Composite", StringComparison.OrdinalIgnoreCase));
        if (composite is not null)
        {
            return composite.Value;
        }

        var plain = hw.Sensors.FirstOrDefault(s =>
            s.SensorType == SensorType.Temperature &&
            s.Name.Equals("Temperature", StringComparison.OrdinalIgnoreCase));
        return plain?.Value;
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
        long? powerOnCount = GetLong(attributesById, isNvme ? NvmePowerCyclesId : AtaPowerCycleCountId);
        long? mediaErrors = isNvme ? GetLong(attributesById, NvmeMediaErrorsId) : null;

        return new SsdSmartInfo(
            model, serialNumber, isNvme, temperatureC,
            percentageUsed, availableSpare, reallocatedSectors, powerOnHours, powerOnCount, mediaErrors);
    }

    /// <summary>
    /// One-line summary for the UI (e.g. "CT1000P2SSD8 (2050E4D9C945): 44C   Power-on: 14988h
    /// (3209x)" for NVMe, or "Model (Serial): 35C   Reallocated: 3   Power-on: 12000h (450x)" for
    /// ATA/SATA). NVMe intentionally omits PercentageUsed/AvailableSparePercent here - still
    /// captured on SsdSmartInfo for future use (e.g. once the server evaluates thresholds against
    /// them), just not surfaced in this summary line. "--" stands in for any null field.
    /// </summary>
    internal static string FormatSsdSummary(SsdSmartInfo info)
    {
        var serial = info.SerialNumber ?? "no serial";
        var temp = info.TemperatureC is double t ? $"{t:F0}C" : "--";
        var hours = info.PowerOnHours is long h ? $"{h}h" : "--";
        var count = info.PowerOnCount is long c ? $"{c}x" : "--";

        if (info.IsNvme)
        {
            return $"{info.Model} ({serial}): {temp}   Power-on: {hours} ({count})";
        }

        var reallocated = info.ReallocatedSectorsCount is long r ? r.ToString() : "--";
        return $"{info.Model} ({serial}): {temp}   Reallocated: {reallocated}   Power-on: {hours} ({count})";
    }

    /// <summary>
    /// summary_stats for a per-drive SSD test_run (CONTRACT.md §2/§7), key names matching
    /// config/default.json's ssd subtree exactly (max_smart_temp_c, max_smart_reallocated_sectors,
    /// max_smart_percentage_used, min_smart_available_spare_percent, max_smart_media_errors) -
    /// same "same key name as the threshold it's compared against" convention already used for
    /// CPU's max_temp_c. NVMe-only and ATA-only keys are only included when
    /// <see cref="SsdSmartInfo.IsNvme"/> makes them meaningful - see class remarks. error_count is
    /// always 0: this reports a passive SMART snapshot, not a benchmark run, so there's no
    /// destructive-test error count to report. min_seq_read_mb_s/min_seq_write_mb_s are never set
    /// here either - this function backs the passive per-CPU-run SMART report
    /// (TestSessionController.SubmitSsdSmartDataAsync), which has no throughput data; those two
    /// keys are only populated by the dedicated SSD benchmark test_run
    /// (TestSessionController.RunSsdTestSessionAsync / DiskSpdRunner), per the server's own
    /// documented convention that missing keys aren't evaluated.
    /// </summary>
    internal static Dictionary<string, object> BuildSummaryStats(SsdSmartInfo info)
    {
        var stats = new Dictionary<string, object> { ["error_count"] = 0 };
        if (info.TemperatureC is double t) stats["max_smart_temp_c"] = t;

        if (info.IsNvme)
        {
            if (info.PercentageUsed is double pu) stats["max_smart_percentage_used"] = pu;
            if (info.AvailableSparePercent is double sp) stats["min_smart_available_spare_percent"] = sp;
            if (info.MediaErrors is long me) stats["max_smart_media_errors"] = me;
        }
        else if (info.ReallocatedSectorsCount is long r)
        {
            stats["max_smart_reallocated_sectors"] = r;
        }

        return stats;
    }

    /// <summary>Human-readable tool_output_raw for a per-drive SSD test_run - there's no external
    /// tool process to capture stdout from (SMART is read in-process via LibreHardwareMonitorLib,
    /// unlike Prime95), so this is a formatted dump of everything captured instead.</summary>
    internal static string FormatToolOutputRaw(SsdSmartInfo info)
    {
        var lines = new List<string>
        {
            $"Drive: {info.Model}",
            $"Serial: {info.SerialNumber ?? "(unknown)"}",
            $"Bus: {(info.IsNvme ? "NVMe" : "ATA/SATA")}",
            $"Temperature: {(info.TemperatureC is double t ? $"{t}C" : "n/a")}",
            $"Power-on hours: {(info.PowerOnHours is long h ? h.ToString() : "n/a")}",
            $"Power-on count: {(info.PowerOnCount is long c ? c.ToString() : "n/a")}",
        };

        if (info.IsNvme)
        {
            lines.Add($"Percentage used: {(info.PercentageUsed is double pu ? $"{pu}%" : "n/a")}");
            lines.Add($"Available spare: {(info.AvailableSparePercent is double sp ? $"{sp}%" : "n/a")}");
            lines.Add($"Media errors: {(info.MediaErrors is long me ? me.ToString() : "n/a")}");
        }
        else
        {
            lines.Add($"Reallocated sectors: {(info.ReallocatedSectorsCount is long r ? r.ToString() : "n/a")}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static double? GetDouble(IReadOnlyDictionary<byte, float> attributesById, byte id) =>
        attributesById.TryGetValue(id, out var value) ? value : null;

    private static long? GetLong(IReadOnlyDictionary<byte, float> attributesById, byte id) =>
        attributesById.TryGetValue(id, out var value) ? (long)value : null;
}
