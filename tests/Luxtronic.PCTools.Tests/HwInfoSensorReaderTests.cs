using Hwinfo.SharedMemory;
using Luxtronic.PCTools.Services;
using Xunit;

namespace Luxtronic.PCTools.Tests;

// Ground-truthed against a real, live HWiNFO64 shared-memory dump (Intel Core i5-11400F + NVIDIA
// GTX 1080 Ti, 342 total readings) - both fixtures below are representative subsets of the actual
// dump, not invented label strings. Confirmed several things worth noting for future readers:
// (1) HWiNFO splits CPU sensors across multiple sub-groups with distinct GroupLabelOrig values
// ("CPU [#0]: <model>" for clocks/usage, "CPU [#0]: <model>: DTS" and "...: Enhanced" for
// temperatures) - the Contains("CPU") group filter in HwInfoSensorReader catches all of them;
// (2) real dumps include "Core N T0/T1 Effective Clock" per-thread readings (SMT-related, very
// different/lower values than the real target clock) that the "Effective" exclusion is
// specifically there to filter out; (3) CPU fan is reported under the motherboard/Super IO group,
// not any "CPU [...]" group; (4) GPU readings need exact label matching (not Contains) because the
// real dump has decoy readings that share a substring with the label actually wanted - see
// RealGpuReadings below.
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
    private const string MotherboardGroup = "MSI MPG B560I GAMING EDGE WIFI (MS-7D19) (Nuvoton NCT6687D)";
    private const string GpuGroup = "dGPU [#0]: NVIDIA GeForce GTX 1080 Ti: ASUS GTX 1080 Ti Founders Edition";

    // A representative slice of the real 342-reading dump - six real cores at a fixed 4200MHz, the
    // exact set of decoys that must NOT be counted (Bus Clock, Ring/LLC Clock, twelve T0/T1
    // Effective Clock per-thread readings, Average Effective Clock, per-core Usage/Utility, Max
    // CPU/Thread Usage, Total CPU Utility), both temp sub-groups HWiNFO actually reports ("DTS" and
    // "Enhanced" give slightly different CPU Package values, 60C vs 62C in the real dump - both
    // legitimate, ExtractCpuReadings just takes whichever comes first), the real "Total CPU Usage"
    // reading, and a motherboard-group CPU fan reading alongside a decoy "System 1" fan that must
    // NOT be picked.
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
        Reading(SensorType.SensorTypeUsage, "Core 0 T0 Usage", 9.03, ClockGroup),
        Reading(SensorType.SensorTypeUsage, "Core 1 T0 Usage", 13.29, ClockGroup),
        Reading(SensorType.SensorTypeUsage, "Max CPU/Thread Usage", 13.29, ClockGroup),
        Reading(SensorType.SensorTypeUsage, "Total CPU Usage", 45.0, ClockGroup),
        Reading(SensorType.SensorTypeUsage, "Total CPU Utility", 46.5, ClockGroup),
        Reading(SensorType.SensorTypeFan, "CPU", 2222, MotherboardGroup),
        Reading(SensorType.SensorTypeFan, "System 1", 800, MotherboardGroup),
    };

    // A representative slice of the real 76-reading dGPU dump - the fields ExtractGpuReadings
    // needs, plus the specific real decoys that make exact (not Contains) matching necessary:
    // "GPU Thermal Limit" alongside the two temps, "GPU Memory Clock (measured)"/"GPU Video
    // Clock"/"GPU Effective Clock" alongside the two clocks, several non-Core Load readings, a
    // duplicate "GPU Fan" label used for two different SensorTypes (Fan/RPM vs Other/%), and
    // several "... Input/Output Power" readings alongside "GPU Power". No "GPU Memory Total"
    // reading exists in the real dump - only Allocated (used) and Available (free).
    private static readonly SensorReading[] RealGpuReadings =
    {
        Reading(SensorType.SensorTypeTemp, "GPU Temperature", 31.8, GpuGroup),
        Reading(SensorType.SensorTypeTemp, "GPU Hot Spot Temperature", 45.8, GpuGroup),
        Reading(SensorType.SensorTypeTemp, "GPU Thermal Limit", 84, GpuGroup),
        Reading(SensorType.SensorTypeClock, "GPU Clock", 1481, GpuGroup),
        Reading(SensorType.SensorTypeClock, "GPU Memory Clock", 5508, GpuGroup),
        Reading(SensorType.SensorTypeClock, "GPU Memory Clock (measured)", 85.76, GpuGroup),
        Reading(SensorType.SensorTypeClock, "GPU Video Clock", 544, GpuGroup),
        Reading(SensorType.SensorTypeClock, "GPU Effective Clock", 29.6, GpuGroup),
        Reading(SensorType.SensorTypeUsage, "GPU Core Load", 25.4, GpuGroup),
        Reading(SensorType.SensorTypeUsage, "GPU Memory Controller Load", 7, GpuGroup),
        Reading(SensorType.SensorTypeUsage, "GPU Bus Load", 0, GpuGroup),
        Reading(SensorType.SensorTypeFan, "GPU Fan", 1092, GpuGroup),
        Reading(SensorType.SensorTypeOther, "GPU Fan", 23, GpuGroup),
        Reading(SensorType.SensorTypePower, "GPU Power", 63.6, GpuGroup),
        Reading(SensorType.SensorTypePower, "GPU Core (NVVDD) Input Power (sum)", 1.57, GpuGroup),
        Reading(SensorType.SensorTypePower, "GPU PCIe +12V Input Power", 3.74, GpuGroup),
        Reading(SensorType.SensorTypeOther, "GPU Memory Allocated", 1626.0, GpuGroup),
        Reading(SensorType.SensorTypeOther, "GPU Memory Available", 9638.0, GpuGroup),
    };

    [Fact]
    public void ExtractCpuReadings_RealDumpSlice_ExtractsAllFourFields()
    {
        var (tempC, loadPct, fanRpm, clockMhz) = HwInfoSensorReader.ExtractCpuReadings(RealCpuReadings);

        // 60 (DTS) not 62 (Enhanced) - DTS sub-group's readings come first in this fixture, same
        // as the real dump's enumeration order; both are legitimate CPU Package readings.
        Assert.Equal(60.0, tempC);
        Assert.Equal(45.0, loadPct);
        Assert.Equal(2222.0, fanRpm);
        Assert.Equal(4200.0, clockMhz);
    }

    [Fact]
    public void ExtractCpuReadings_NoMatchingReadings_ReturnsAllNullNotException()
    {
        var readings = new[]
        {
            Reading(SensorType.SensorTypeVolt, "Vcore", 1.2, ClockGroup),
        };

        var (tempC, loadPct, fanRpm, clockMhz) = HwInfoSensorReader.ExtractCpuReadings(readings);

        Assert.Null(tempC);
        Assert.Null(loadPct);
        Assert.Null(fanRpm);
        Assert.Null(clockMhz);
    }

    [Fact]
    public void ExtractCpuReadings_EmptyReadings_ReturnsAllNullNotException()
    {
        var (tempC, loadPct, fanRpm, clockMhz) = HwInfoSensorReader.ExtractCpuReadings(Array.Empty<SensorReading>());

        Assert.Null(tempC);
        Assert.Null(loadPct);
        Assert.Null(fanRpm);
        Assert.Null(clockMhz);
    }

    [Fact]
    public void ExtractCpuReadings_ReadingsFromOtherGroups_AreIgnoredForTempAndClock()
    {
        // e.g. the real dump's "dGPU [#0]: NVIDIA GeForce GTX 1080 Ti: ..." group must not
        // contribute to CPU temp/clock, even with a "Package"/"Core"-named reading of the same
        // SensorType - GPU groups don't contain "CPU" as a substring, unlike every CPU sub-group.
        var readings = new[]
        {
            Reading(SensorType.SensorTypeTemp, "GPU Package", 70.0, "dGPU [#0]: Test GPU"),
            Reading(SensorType.SensorTypeClock, "Core Clock", 1500.0, "dGPU [#0]: Test GPU"),
        };

        var (tempC, _, _, clockMhz) = HwInfoSensorReader.ExtractCpuReadings(readings);

        Assert.Null(tempC);
        Assert.Null(clockMhz);
    }

    [Fact]
    public void ExtractCpuReadings_PerCoreUsageAndUtility_NeverPickedAsTotalLoad()
    {
        // Real fixture: per-core Usage readings, "Max CPU/Thread Usage", and "Total CPU Utility"
        // (a different metric) all coexist with "Total CPU Usage" in the same group - only the
        // exact label "Total CPU Usage" may be picked.
        var readings = new[]
        {
            Reading(SensorType.SensorTypeUsage, "Core 0 T0 Usage", 99.0, ClockGroup),
            Reading(SensorType.SensorTypeUsage, "Max CPU/Thread Usage", 99.0, ClockGroup),
            Reading(SensorType.SensorTypeUsage, "Total CPU Utility", 50.0, ClockGroup),
        };

        var (_, loadPct, _, _) = HwInfoSensorReader.ExtractCpuReadings(readings);

        Assert.Null(loadPct);
    }

    [Fact]
    public void ExtractCpuReadings_BusAndRingClocksExcluded_OnlyRealCoreClocksAveraged()
    {
        var readings = new[]
        {
            Reading(SensorType.SensorTypeClock, "Core 0 Clock", 4000.0, ClockGroup),
            Reading(SensorType.SensorTypeClock, "Bus Clock", 100.0, ClockGroup), // must be excluded (no "Core")
            Reading(SensorType.SensorTypeClock, "Ring/LLC Clock", 3600.0, ClockGroup), // must be excluded (no "Core")
        };

        var (_, _, _, clockMhz) = HwInfoSensorReader.ExtractCpuReadings(readings);

        Assert.Equal(4000.0, clockMhz);
    }

    [Fact]
    public void ExtractCpuReadings_EffectiveClockExcluded_OnlyRealCoreClocksAveraged()
    {
        // Real dump fixture: HWiNFO reports 12 "Core N T0/T1 Effective Clock" per-thread readings
        // (SMT-related) alongside the 6 real "Core N Clock" readings - the "Effective" exclusion
        // exists specifically to filter these out, confirmed against real data, not a hypothetical.
        var readings = new[]
        {
            Reading(SensorType.SensorTypeClock, "Core 0 Clock", 4200.0, ClockGroup),
            Reading(SensorType.SensorTypeClock, "Core 0 T0 Effective Clock", 899.48, ClockGroup),
            Reading(SensorType.SensorTypeClock, "Core 0 T1 Effective Clock", 655.26, ClockGroup),
            Reading(SensorType.SensorTypeClock, "Average Effective Clock", 875.31, ClockGroup),
        };

        var (_, _, _, clockMhz) = HwInfoSensorReader.ExtractCpuReadings(readings);

        Assert.Equal(4200.0, clockMhz);
    }

    [Fact]
    public void ExtractCpuReadings_FanNotUnderCpuGroup_StillFoundViaMotherboardGroup()
    {
        // Real fixture: CPU fan is reported under the motherboard/Super IO group, not any
        // "CPU [...]" group - the fan lookup must search all readings, not the CPU-filtered subset.
        var readings = new[]
        {
            Reading(SensorType.SensorTypeFan, "CPU", 2268.0, MotherboardGroup),
            Reading(SensorType.SensorTypeFan, "System 1", 769.0, MotherboardGroup),
        };

        var (_, _, fanRpm, _) = HwInfoSensorReader.ExtractCpuReadings(readings);

        Assert.Equal(2268.0, fanRpm);
    }

    [Fact]
    public void ExtractGpuReadings_RealDumpSlice_ExtractsAllFields()
    {
        var gpu = HwInfoSensorReader.ExtractGpuReadings(RealGpuReadings);

        Assert.NotNull(gpu);
        Assert.Equal(31.8, gpu!.CoreTempC);
        Assert.Equal(45.8, gpu.HotSpotTempC);
        Assert.Equal(1481.0, gpu.CoreClockMhz);
        Assert.Equal(5508.0, gpu.MemoryClockMhz);
        Assert.Equal(25.4, gpu.LoadPct);
        Assert.Equal(1092.0, gpu.FanRpm);
        Assert.Equal(63.6, gpu.PowerW);
        Assert.Equal(1626.0, gpu.MemoryUsedMb);
        // No direct "Total" reading exists - computed as Allocated + Available.
        Assert.Equal(1626.0 + 9638.0, gpu.MemoryTotalMb);
    }

    [Fact]
    public void ExtractGpuReadings_NoGpuGroup_ReturnsNull()
    {
        var gpu = HwInfoSensorReader.ExtractGpuReadings(RealCpuReadings);

        Assert.Null(gpu);
    }

    [Fact]
    public void ExtractGpuReadings_DuplicateFanLabelDifferentType_OnlyFanTypeIsUsedForRpm()
    {
        // Real fixture: "GPU Fan" appears twice with the same label but different SensorTypes -
        // Fan/RPM and Other/%. Only the Fan-typed one is the RPM value FanRpm needs.
        var gpu = HwInfoSensorReader.ExtractGpuReadings(RealGpuReadings);

        Assert.NotNull(gpu);
        Assert.Equal(1092.0, gpu!.FanRpm);
    }

    [Fact]
    public void ExtractGpuReadings_MemoryClockMeasuredVariant_DoesNotShadowExactMatch()
    {
        // "GPU Memory Clock (measured)" is a real, different reading from "GPU Memory Clock" -
        // Contains() would ambiguously match either depending on enumeration order; exact-equals
        // must always pick "GPU Memory Clock".
        var gpu = HwInfoSensorReader.ExtractGpuReadings(RealGpuReadings);

        Assert.NotNull(gpu);
        Assert.Equal(5508.0, gpu!.MemoryClockMhz);
    }

    [Fact]
    public void ExtractGpuReadings_MultipleInputPowerReadings_OnlyMainGpuPowerIsUsed()
    {
        var gpu = HwInfoSensorReader.ExtractGpuReadings(RealGpuReadings);

        Assert.NotNull(gpu);
        Assert.Equal(63.6, gpu!.PowerW);
    }
}
