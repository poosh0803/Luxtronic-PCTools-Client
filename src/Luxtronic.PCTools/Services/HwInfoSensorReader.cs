using Hwinfo.SharedMemory;

namespace Luxtronic.PCTools.Services;

/// <summary>
/// Primary CPU and GPU sensor source, via HWiNFO64's Shared Memory interface. Storage/SMART stays
/// on LibreHardwareMonitorLib (see <see cref="SsdSmartReader"/>) - HWiNFO's shared memory doesn't
/// expose Power-On Hours/Count or Reallocated Sectors for the drives this was ground-truthed
/// against, and those are already sent to the server per the SSD_SMART_ADDENDUM.md contract.
///
/// This used to be a narrow fallback, only consulted when LibreHardwareMonitorLib's own CPU temp/
/// clock reads came back null (WinRing0-based MSR reads are unreliable on some real CPU/board
/// combinations - HWiNFO reads the same values correctly on the very same affected machines, via a
/// different, non-WinRing0 path). HWiNFO is now the sole CPU and GPU source instead: it's already
/// proven more reliable on affected hardware, and is now started unattended alongside the app (see
/// publish-assets/Launch.ps1 and tools/hwi/HWiNFO64.INI's ShowWelcomeAndProgress=0).
///
/// Requires HWiNFO64 running in the background with Settings > "Shared Memory Support" enabled -
/// a one-time, GUI-only toggle in the free version (no confirmed silent/INI-based way to enable
/// it without a human clicking it at least once - tools/hwi/HWiNFO64.INI in this repo already has
/// it set), and HWiNFO must be restarted after toggling that setting for the shared-memory block
/// to actually get created - toggling it while already running does not retroactively create the
/// mapping. If HWiNFO isn't running, or that setting isn't on, every read here fails closed
/// (returns nulls, never throws) - <see cref="SensorMonitor.Initialize"/> surfaces that as a clear
/// "HWiNFO not reachable" warning and disables the CPU test, the same way a dead LHM driver used
/// to be reported.
///
/// Label-matching is ground-truthed against a real, live HWiNFO64 shared-memory dump on this dev
/// machine (Intel Core i5-11400F + NVIDIA GTX 1080 Ti, 342 total readings) - see
/// HwInfoSensorReaderTests for the exact fixtures, captured from real data, not invented. Several
/// things confirmed there worth knowing:
/// - HWiNFO splits CPU sensors across multiple sub-groups with distinct GroupLabelOrig values
///   ("CPU [#0]: &lt;model&gt;" for clocks/usage, "...: DTS" and "...: Enhanced" for
///   temperatures) - the group filter matches "CPU" as a substring specifically so it catches all
///   of them, not just the main group.
/// - CPU fan is NOT under any "CPU [...]" group - it's reported under the motherboard/Super IO
///   group (e.g. "... (Nuvoton NCT6687D)") as a Fan-type reading labelled exactly "CPU". Same
///   "look outside the CPU-specific group" shape LHM itself needed for its own motherboard-fan
///   fallback.
/// - Real dumps include twelve "Core N T0/T1 Effective Clock" per-thread readings (SMT-related,
///   much lower than the real target clock) that the "Effective" exclusion filters out - not a
///   hypothetical case, an actual fixture in the real data.
/// - GPU readings need exact label matching, not Contains - the real dump has decoys that would
///   otherwise false-match: "GPU Memory Clock (measured)" alongside "GPU Memory Clock", "GPU
///   Thermal Limit" alongside "GPU Temperature"/"GPU Hot Spot Temperature", half a dozen
///   "... Input/Output Power" readings alongside "GPU Power", and a duplicate "GPU Fan" label used
///   for two different SensorTypes (Fan/RPM vs Other/%) - only the Fan-typed one is the RPM value.
/// - No "GPU Memory Total" reading exists in the shared memory - only "GPU Memory Allocated"
///   (used) and "GPU Memory Available" (free). Total is computed as their sum, a reasonable proxy
///   for physical VRAM capacity rather than a literal reading. Low-stakes: GPU data is UI-display
///   only, never sent to the server.
///
/// Only validated against this one Intel CPU / NVIDIA GPU / HWiNFO version combination - AMD/Intel
/// GPUs and other CPU vendors may use different label text for the same metrics.
/// </summary>
public sealed class HwInfoSensorReader : IDisposable
{
    private SharedMemoryReader? _reader;

    /// <summary>Current CPU readings via HWiNFO. All fields null (never throws) if HWiNFO isn't
    /// reachable right now - see <see cref="ReadCpuWithReachability"/> if the caller needs to tell
    /// "not reachable at all" apart from "reachable but nothing matched".</summary>
    public SensorReadings ReadCpu() => ReadCpuWithReachability().Cpu;

    /// <summary>Current GPU readings via HWiNFO, or null if no GPU group was found (no GPU
    /// installed, or HWiNFO not reachable). Never throws.</summary>
    public GpuReadings? ReadGpu()
    {
        try
        {
            _reader ??= new SharedMemoryReader();
            var readings = _reader.ReadLocal().ToList();
            return ExtractGpuReadings(readings);
        }
        catch
        {
            // Reset so a later call retries fresh construction, in case HWiNFO gets started after
            // an earlier failed attempt.
            _reader?.Dispose();
            _reader = null;
            return null;
        }
    }

    /// <summary>
    /// One shared-memory read, returning both whether HWiNFO was reachable at all and the CPU
    /// readings extracted from it - used by <see cref="SensorMonitor.Initialize"/>'s health check,
    /// which needs to distinguish "HWiNFO isn't running / Shared Memory Support isn't enabled"
    /// (Reachable = false) from "HWiNFO is running but no CPU temperature reading matched this
    /// hardware's label set" (Reachable = true, Cpu.CpuPackageTempC = null) - those need different
    /// guidance messages.
    /// </summary>
    public (bool Reachable, SensorReadings Cpu) ReadCpuWithReachability()
    {
        try
        {
            _reader ??= new SharedMemoryReader();
            var readings = _reader.ReadLocal().ToList();
            if (readings.Count == 0)
            {
                return (false, new SensorReadings(null, null, null, null));
            }

            var (temp, load, fan, clock) = ExtractCpuReadings(readings);
            return (true, new SensorReadings(temp, load, fan, clock));
        }
        catch
        {
            _reader?.Dispose();
            _reader = null;
            return (false, new SensorReadings(null, null, null, null));
        }
    }

    /// <summary>
    /// Pure extraction logic, split out so it's unit testable without a real HWiNFO instance. See
    /// class remarks - ground-truthed against a real HWiNFO shared-memory dump.
    /// </summary>
    internal static (double? TempC, double? LoadPct, double? FanRpm, double? ClockMhz) ExtractCpuReadings(
        IReadOnlyList<SensorReading> readings)
    {
        var cpuReadings = readings
            .Where(r => r.GroupLabelOrig.Contains("CPU", StringComparison.OrdinalIgnoreCase))
            .ToList();

        double? tempC = cpuReadings
            .Where(r => r.Type == SensorType.SensorTypeTemp &&
                        r.LabelOrig.Contains("Package", StringComparison.OrdinalIgnoreCase))
            .Select(r => (double?)r.Value)
            .FirstOrDefault();

        double? loadPct = cpuReadings
            .Where(r => r.Type == SensorType.SensorTypeUsage &&
                        r.LabelOrig.Equals("Total CPU Usage", StringComparison.OrdinalIgnoreCase))
            .Select(r => (double?)r.Value)
            .FirstOrDefault();

        // Averaged across cores, same reasoning as SensorMonitor.AverageClockMhz used for LHM:
        // cores can sit at very different clocks under partial load. Excludes "Bus"/"Effective"
        // clocks, which HWiNFO reports as separate Clock-type readings that aren't per-core
        // frequency.
        var coreClocks = cpuReadings
            .Where(r => r.Type == SensorType.SensorTypeClock &&
                        r.LabelOrig.Contains("Core", StringComparison.OrdinalIgnoreCase) &&
                        !r.LabelOrig.Contains("Bus", StringComparison.OrdinalIgnoreCase) &&
                        !r.LabelOrig.Contains("Effective", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Value)
            .ToList();
        double? clockMhz = coreClocks.Count > 0 ? coreClocks.Average() : null;

        // Not under any "CPU [...]" group - see class remarks. Searches all readings, not the
        // CPU-filtered subset above.
        double? fanRpm = readings
            .Where(r => r.Type == SensorType.SensorTypeFan &&
                        r.LabelOrig.Equals("CPU", StringComparison.OrdinalIgnoreCase))
            .Select(r => (double?)r.Value)
            .FirstOrDefault();

        return (tempC, loadPct, fanRpm, clockMhz);
    }

    /// <summary>
    /// Pure extraction logic for the GPU group, split out so it's unit testable without a real
    /// HWiNFO instance. Exact label matching throughout - see class remarks for the real decoys
    /// this needs to avoid. Only handles one GPU (first "...GPU [#0]..." group found), matching
    /// the single-GPU behavior the LHM-based implementation this replaces already had.
    /// </summary>
    internal static GpuReadings? ExtractGpuReadings(IReadOnlyList<SensorReading> readings)
    {
        var gpuReadings = readings
            .Where(r => r.GroupLabelOrig.Contains("GPU [#0]", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (gpuReadings.Count == 0)
        {
            return null;
        }

        double? Find(SensorType type, string exactLabel) => gpuReadings
            .Where(r => r.Type == type && r.LabelOrig.Equals(exactLabel, StringComparison.OrdinalIgnoreCase))
            .Select(r => (double?)r.Value)
            .FirstOrDefault();

        double? coreTemp = Find(SensorType.SensorTypeTemp, "GPU Temperature");
        double? hotSpotTemp = Find(SensorType.SensorTypeTemp, "GPU Hot Spot Temperature");
        double? coreClock = Find(SensorType.SensorTypeClock, "GPU Clock");
        double? memClock = Find(SensorType.SensorTypeClock, "GPU Memory Clock");
        double? load = Find(SensorType.SensorTypeUsage, "GPU Core Load");
        double? fan = Find(SensorType.SensorTypeFan, "GPU Fan");
        double? power = Find(SensorType.SensorTypePower, "GPU Power");
        double? memUsed = Find(SensorType.SensorTypeOther, "GPU Memory Allocated");
        double? memAvailable = Find(SensorType.SensorTypeOther, "GPU Memory Available");

        // No direct "Total" reading exists - see class remarks.
        double? memTotal = memUsed.HasValue && memAvailable.HasValue ? memUsed + memAvailable : null;

        return new GpuReadings(coreTemp, hotSpotTemp, coreClock, memClock, load, fan, power, memUsed, memTotal);
    }

    public void Dispose() => _reader?.Dispose();
}
