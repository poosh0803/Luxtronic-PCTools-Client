using Hwinfo.SharedMemory;
using Luxtronic.PCTools.Services;
using Xunit;

namespace Luxtronic.PCTools.Tests;

// Ground-truthed against a real HWiNFO64 shared-memory dump (Intel Core i5-11400F) - the
// RealCpuReadings fixture below is a representative subset of the actual 342-reading dump, not
// invented label strings. Confirmed two things worth noting for future readers: (1) HWiNFO splits
// CPU sensors across multiple sub-groups with distinct GroupLabelOrig values ("CPU [#0]: <model>"
// for clocks/voltages, "CPU [#0]: <model>: DTS" and "...: Enhanced" for temperatures) - the
// Contains("CPU") group filter in HwInfoSensorReader catches all of them since "CPU" appears as a
// substring in every one, which is what makes the temp lookup work at all; (2) real dumps include
// "Core N T0/T1 Effective Clock" per-thread readings (SMT-related, very different/lower values
// than the real target clock) that the "Effective" exclusion is specifically there to filter out -
// this was a real fixture in the ground-truth data, not a hypothetical.
public class HwInfoSensorReaderTests
{
    private static SensorReading Reading(
        SensorType type, string labelOrig, double value, string groupLabelOrig) =>
        new(Id: 0, Index: 0, Type: type, LabelOrig: labelOrig, LabelUser: labelOrig, Unit: "",
            Value: value, ValueMin: value, ValueMax: value, ValueAvg: value,
            GroupId: 0, GroupInstanceId: 0, GroupLabelUser: groupLabelOrig, GroupLabelOrig: groupLabelOrig);

    private const string ClockGroup = "CPU [#0]: Intel Core i5-11400F";
    private const string DtsGroup = "CPU [#0]: Intel Core i5-11400F: DTS";
    private const string EnhancedGroup = "CPU [#0]: Intel Core i5-11400F: Enhanced";

    // A representative slice of the real 342-reading dump - six real cores at a fixed 4200MHz,
    // the exact set of decoys that must NOT be counted (Bus Clock, Ring/LLC Clock, twelve T0/T1
    // Effective Clock per-thread readings, Average Effective Clock), and both temp sub-groups
    // HWiNFO actually reports ("DTS" and "Enhanced" give slightly different CPU Package values,
    // 60C vs 62C in the real dump - both legitimate, ExtractCpuTempAndClock just takes whichever
    // comes first).
    private static readonly SensorReading[] RealCpuReadings =
    {
        Reading(SensorType.SensorTypeClock, "Core 0 Clock", 4200, ClockGroup),
        Reading(SensorType.SensorTypeClock, "Core 1 Clock", 4200, ClockGroup),
        Reading(SensorType.SensorTypeClock, "Core 2 Clock", 4200, ClockGroup),
        Reading(SensorType.SensorTypeClock, "Core 3 Clock", 4200, ClockGroup),
        Reading(SensorType.SensorTypeClock, "Core 4 Clock", 4200, ClockGroup),
        Reading(SensorType.SensorTypeClock, "Core 5 Clock", 4200, ClockGroup),
        Reading(SensorType.SensorTypeClock, "Bus Clock", 100, ClockGroup),
        Reading(SensorType.SensorTypeClock, "Ring/LLC Clock", 3600, ClockGroup),
        Reading(SensorType.SensorTypeClock, "Core 0 T0 Effective Clock", 899.48, ClockGroup),
        Reading(SensorType.SensorTypeClock, "Core 0 T1 Effective Clock", 655.26, ClockGroup),
        Reading(SensorType.SensorTypeClock, "Average Effective Clock", 875.31, ClockGroup),
        Reading(SensorType.SensorTypeTemp, "Core 0", 57, DtsGroup),
        Reading(SensorType.SensorTypeTemp, "Core 0 Distance to TjMAX", 43, DtsGroup),
        Reading(SensorType.SensorTypeTemp, "CPU Package", 60, DtsGroup),
        Reading(SensorType.SensorTypeTemp, "Core Max", 60, DtsGroup),
        Reading(SensorType.SensorTypeTemp, "CPU Package", 62, EnhancedGroup),
        Reading(SensorType.SensorTypeTemp, "CPU IA Cores", 62, EnhancedGroup),
    };

    [Fact]
    public void ExtractCpuTempAndClock_RealDumpSlice_ExtractsPackageTempAndAveragedCoreClocks()
    {
        var (tempC, clockMhz) = HwInfoSensorReader.ExtractCpuTempAndClock(RealCpuReadings);

        // 60 (DTS) not 62 (Enhanced) - DTS sub-group's readings come first in this fixture, same
        // as the real dump's enumeration order; both are legitimate CPU Package readings.
        Assert.Equal(60.0, tempC);
        Assert.Equal(4200.0, clockMhz);
    }

    [Fact]
    public void ExtractCpuTempAndClock_NoMatchingReadings_ReturnsNullNotException()
    {
        var readings = new[]
        {
            Reading(SensorType.SensorTypeUsage, "Total CPU Usage", 42.0, ClockGroup),
        };

        var (tempC, clockMhz) = HwInfoSensorReader.ExtractCpuTempAndClock(readings);

        Assert.Null(tempC);
        Assert.Null(clockMhz);
    }

    [Fact]
    public void ExtractCpuTempAndClock_EmptyReadings_ReturnsNullNotException()
    {
        var (tempC, clockMhz) = HwInfoSensorReader.ExtractCpuTempAndClock(Array.Empty<SensorReading>());

        Assert.Null(tempC);
        Assert.Null(clockMhz);
    }

    [Fact]
    public void ExtractCpuTempAndClock_ReadingsFromOtherGroups_AreIgnored()
    {
        // e.g. the real dump's "dGPU [#0]: NVIDIA GeForce GTX 1080 Ti: ..." group must not
        // contribute to CPU temp/clock, even with a "Package"/"Core"-named reading of the same
        // SensorType - GPU groups don't contain "CPU" as a substring, unlike every CPU sub-group.
        var readings = new[]
        {
            Reading(SensorType.SensorTypeTemp, "GPU Package", 70.0, "dGPU [#0]: Test GPU"),
            Reading(SensorType.SensorTypeClock, "Core Clock", 1500.0, "dGPU [#0]: Test GPU"),
        };

        var (tempC, clockMhz) = HwInfoSensorReader.ExtractCpuTempAndClock(readings);

        Assert.Null(tempC);
        Assert.Null(clockMhz);
    }

    [Fact]
    public void ExtractCpuTempAndClock_BusAndRingClocksExcluded_OnlyRealCoreClocksAveraged()
    {
        var readings = new[]
        {
            Reading(SensorType.SensorTypeClock, "Core 0 Clock", 4000.0, ClockGroup),
            Reading(SensorType.SensorTypeClock, "Bus Clock", 100.0, ClockGroup), // must be excluded (no "Core")
            Reading(SensorType.SensorTypeClock, "Ring/LLC Clock", 3600.0, ClockGroup), // must be excluded (no "Core")
        };

        var (_, clockMhz) = HwInfoSensorReader.ExtractCpuTempAndClock(readings);

        Assert.Equal(4000.0, clockMhz);
    }

    [Fact]
    public void ExtractCpuTempAndClock_EffectiveClockExcluded_OnlyRealCoreClocksAveraged()
    {
        // Real dump fixture: HWiNFO reports 12 "Core N T0/T1 Effective Clock" per-thread
        // readings (SMT-related) alongside the 6 real "Core N Clock" readings - the "Effective"
        // exclusion exists specifically to filter these out, confirmed against real data, not
        // a hypothetical case.
        var readings = new[]
        {
            Reading(SensorType.SensorTypeClock, "Core 0 Clock", 4200.0, ClockGroup),
            Reading(SensorType.SensorTypeClock, "Core 0 T0 Effective Clock", 899.48, ClockGroup),
            Reading(SensorType.SensorTypeClock, "Core 0 T1 Effective Clock", 655.26, ClockGroup),
            Reading(SensorType.SensorTypeClock, "Average Effective Clock", 875.31, ClockGroup),
        };

        var (_, clockMhz) = HwInfoSensorReader.ExtractCpuTempAndClock(readings);

        Assert.Equal(4200.0, clockMhz);
    }
}
