using Hwinfo.SharedMemory;
using Luxtronic.PCTools.Services;
using Xunit;

namespace Luxtronic.PCTools.Tests;

// IMPORTANT: these fixtures use *assumed* HWiNFO label strings (Package temp, "Core N Clock"
// per-core clocks), not yet confirmed against a real HWiNFO shared-memory dump - see
// HwInfoSensorReader's class remarks. These tests lock in the extraction *logic* (group scoping,
// Bus/Effective clock exclusion, averaging, graceful nulls) so it's exercised now rather than
// left completely untested, but the exact label strings must be re-verified against real data
// before this reader is trusted for a technician's pass/fail-adjacent decision.
public class HwInfoSensorReaderTests
{
    private static SensorReading Reading(
        SensorType type, string labelOrig, double value, string groupLabelOrig = "CPU [#0]: Test CPU") =>
        new(Id: 0, Index: 0, Type: type, LabelOrig: labelOrig, LabelUser: labelOrig, Unit: "",
            Value: value, ValueMin: value, ValueMax: value, ValueAvg: value,
            GroupId: 0, GroupInstanceId: 0, GroupLabelUser: groupLabelOrig, GroupLabelOrig: groupLabelOrig);

    [Fact]
    public void ExtractCpuTempAndClock_TypicalReadings_ExtractsPackageTempAndAveragedCoreClocks()
    {
        var readings = new[]
        {
            Reading(SensorType.SensorTypeTemp, "CPU Package", 55.0),
            Reading(SensorType.SensorTypeTemp, "CPU (Tctl/Tdie)", 999.0), // decoy - not "Package"
            Reading(SensorType.SensorTypeClock, "Core 0 Clock", 4800.0),
            Reading(SensorType.SensorTypeClock, "Core 1 Clock", 3600.0),
            Reading(SensorType.SensorTypeClock, "Bus Clock", 100.0), // must be excluded
        };

        var (tempC, clockMhz) = HwInfoSensorReader.ExtractCpuTempAndClock(readings);

        Assert.Equal(55.0, tempC);
        Assert.Equal(4200.0, clockMhz);
    }

    [Fact]
    public void ExtractCpuTempAndClock_NoMatchingReadings_ReturnsNullNotException()
    {
        var readings = new[]
        {
            Reading(SensorType.SensorTypeUsage, "Total CPU Usage", 42.0),
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
        // e.g. a GPU or motherboard group elsewhere in the same shared-memory snapshot must not
        // contribute to CPU temp/clock, even if it happens to have a "Package"/"Core"-named
        // reading of the same SensorType.
        var readings = new[]
        {
            Reading(SensorType.SensorTypeTemp, "GPU Package", 70.0, groupLabelOrig: "GPU [#0]: Test GPU"),
            Reading(SensorType.SensorTypeClock, "Core Clock", 1500.0, groupLabelOrig: "GPU [#0]: Test GPU"),
        };

        var (tempC, clockMhz) = HwInfoSensorReader.ExtractCpuTempAndClock(readings);

        Assert.Null(tempC);
        Assert.Null(clockMhz);
    }

    [Fact]
    public void ExtractCpuTempAndClock_EffectiveClockExcluded_OnlyRealCoreClocksAveraged()
    {
        var readings = new[]
        {
            Reading(SensorType.SensorTypeClock, "Core 0 Clock", 4000.0),
            Reading(SensorType.SensorTypeClock, "Core 0 Effective Clock", 1200.0), // must be excluded
        };

        var (_, clockMhz) = HwInfoSensorReader.ExtractCpuTempAndClock(readings);

        Assert.Equal(4000.0, clockMhz);
    }
}
