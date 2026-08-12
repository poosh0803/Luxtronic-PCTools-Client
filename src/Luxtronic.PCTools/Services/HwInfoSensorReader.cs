using Hwinfo.SharedMemory;

namespace Luxtronic.PCTools.Services;

/// <summary>
/// Fallback CPU temperature/clock source using HWiNFO64's Shared Memory interface, for hardware
/// where LibreHardwareMonitorLib's WinRing0-based MSR reads don't work. Confirmed happening on
/// real technician PCs/laptops: CPU temp/clock null via LHM while GPU sensors (a different, non-
/// WinRing0 code path) read fine, and HWiNFO itself reads CPU temp correctly on the very same
/// machine, with Memory Integrity (HVCI) on and no other monitoring app running - ruling out both
/// of the causes SensorMonitor's health-check message names. This matches an open, unresolved
/// upstream LibreHardwareMonitorLib GitHub issue for the same CPU family, not something fixable
/// on the client side by working around HVCI or driver contention.
///
/// This is deliberately narrow - NOT a replacement for SensorMonitor/LibreHardwareMonitorLib.
/// Load/Fan/GPU/Storage all keep working reliably via LHM on affected hardware; only CPU
/// temperature and clock specifically need a fallback. See SensorMonitor.ReadCpu() for how the
/// two sources are merged (LHM tried first every poll; this is only invoked when LHM's own values
/// come back null, to avoid the overhead of a shared-memory read on every cycle when LHM already
/// works, which is the common case).
///
/// Requires HWiNFO64 running in the background with Settings > "Shared Memory Support" enabled -
/// a one-time, GUI-only toggle in the free version (no confirmed silent/INI-based way to enable
/// it without a human clicking it at least once), and HWiNFO must be restarted after toggling
/// that setting for the shared-memory block to actually get created - toggling it while already
/// running does not retroactively create the mapping. If HWiNFO isn't running, or that setting
/// isn't on, every read here fails closed (returns nulls) rather than throwing - callers must
/// treat that as "no fallback available right now", not an error condition, since this is
/// optional infrastructure the technician may or may not have running.
///
/// Label-matching in <see cref="ExtractCpuTempAndClock"/> is ground-truthed against a real
/// HWiNFO64 shared-memory dump (Intel Core i5-11400F, 342 total readings) - see
/// HwInfoSensorReaderTests.RealCpuReadings for the exact fixture. Two things confirmed there
/// worth knowing: HWiNFO splits CPU sensors across multiple sub-groups with distinct
/// GroupLabelOrig values ("CPU [#0]: &lt;model&gt;" for clocks/voltages, "...: DTS" and
/// "...: Enhanced" for temperatures) - the group filter below matches on "CPU" as a substring
/// specifically so it catches all of them, not just the main group; and real dumps include twelve
/// "Core N T0/T1 Effective Clock" per-thread readings (SMT-related, much lower than the real
/// target clock) that the "Effective" exclusion exists specifically to filter out - not a
/// hypothetical case, an actual fixture in the real data. Only validated against this one
/// Intel CPU/HWiNFO version combination - AMD and older/newer HWiNFO versions may use different
/// label text for the same metrics.
/// </summary>
public sealed class HwInfoSensorReader : IDisposable
{
    private SharedMemoryReader? _reader;

    /// <summary>Best-effort CPU temperature (°C) and average core clock (MHz) via HWiNFO's shared
    /// memory. Returns (null, null) - never throws - if HWiNFO isn't running, Shared Memory
    /// Support isn't enabled, or any other failure occurs; this is optional fallback
    /// infrastructure, not a required dependency.</summary>
    public (double? TempC, double? ClockMhz) ReadCpuTemperatureAndClock()
    {
        try
        {
            _reader ??= new SharedMemoryReader();
            var readings = _reader.ReadLocal().ToList();
            return ExtractCpuTempAndClock(readings);
        }
        catch
        {
            // Reset so a later call retries fresh construction, in case HWiNFO gets started (or
            // Shared Memory Support gets enabled) after an earlier failed attempt - a technician
            // shouldn't have to restart the whole app just because HWiNFO wasn't ready yet at the
            // moment this was first tried.
            _reader?.Dispose();
            _reader = null;
            return (null, null);
        }
    }

    /// <summary>
    /// Pure extraction logic, split out from <see cref="ReadCpuTemperatureAndClock"/> so it's
    /// unit testable without a real HWiNFO instance. See class remarks - ground-truthed against a
    /// real HWiNFO shared-memory dump.
    /// </summary>
    internal static (double? TempC, double? ClockMhz) ExtractCpuTempAndClock(IReadOnlyList<SensorReading> readings)
    {
        var cpuReadings = readings
            .Where(r => r.GroupLabelOrig.Contains("CPU", StringComparison.OrdinalIgnoreCase))
            .ToList();

        double? tempC = cpuReadings
            .Where(r => r.Type == SensorType.SensorTypeTemp &&
                        r.LabelOrig.Contains("Package", StringComparison.OrdinalIgnoreCase))
            .Select(r => (double?)r.Value)
            .FirstOrDefault();

        // Averaged across cores, same reasoning as SensorMonitor.AverageClockMhz for LHM: cores
        // can sit at very different clocks under partial load. Excludes "Bus"/"Effective" clocks,
        // which HWiNFO (like LHM) reports as separate Clock-type readings that aren't per-core
        // frequency.
        var coreClocks = cpuReadings
            .Where(r => r.Type == SensorType.SensorTypeClock &&
                        r.LabelOrig.Contains("Core", StringComparison.OrdinalIgnoreCase) &&
                        !r.LabelOrig.Contains("Bus", StringComparison.OrdinalIgnoreCase) &&
                        !r.LabelOrig.Contains("Effective", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Value)
            .ToList();

        double? clockMhz = coreClocks.Count > 0 ? coreClocks.Average() : null;

        return (tempC, clockMhz);
    }

    public void Dispose() => _reader?.Dispose();
}
