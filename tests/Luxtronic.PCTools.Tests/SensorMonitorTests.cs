using Luxtronic.PCTools.Services;
using Xunit;

namespace Luxtronic.PCTools.Tests;

// Only the pure verdict+message logic is tested here. SensorMonitor.Initialize()/ReadCpu()/
// ReadGpu() wrap real HWiNFO shared-memory/LibreHardwareMonitorLib Computer calls and need real
// hardware - that's out of scope for xUnit and is instead covered by the manual end-to-end
// checklist in the README.
public class SensorMonitorTests
{
    [Fact]
    public void EvaluateSensorHealth_HwInfoNotReachable_NotLoadedWithGuidanceMessage()
    {
        var (driverLikelyLoaded, message) = SensorMonitor.EvaluateSensorHealth(
            hwInfoReachable: false, cpuTempReadable: false);

        Assert.False(driverLikelyLoaded);
        Assert.Contains("isn't reachable", message);
        Assert.Contains("Shared Memory Support", message);
    }

    [Fact]
    public void EvaluateSensorHealth_ReachableButNoCpuTemp_NotLoadedWithDistinctMessage()
    {
        // Real-world case this is for: HWiNFO is running and readable, but this specific CPU/
        // HWiNFO version combination doesn't match the label set HwInfoSensorReader expects (e.g.
        // an untested CPU vendor) - a different, more specific problem than "HWiNFO isn't running
        // at all", so it needs its own message rather than reusing the "not reachable" one.
        var (driverLikelyLoaded, message) = SensorMonitor.EvaluateSensorHealth(
            hwInfoReachable: true, cpuTempReadable: false);

        Assert.False(driverLikelyLoaded);
        Assert.DoesNotContain("isn't reachable", message);
        Assert.Contains("no CPU temperature reading matched", message);
    }

    [Fact]
    public void EvaluateSensorHealth_ReachableAndCpuTempReadable_Loaded()
    {
        var (driverLikelyLoaded, message) = SensorMonitor.EvaluateSensorHealth(
            hwInfoReachable: true, cpuTempReadable: true);

        Assert.True(driverLikelyLoaded);
        Assert.Contains("Sensors OK", message);
    }

    [Fact]
    public void AverageClockMhz_NoCores_ReturnsNullNotZero()
    {
        // Null must mean "no data" distinctly from "averaged to zero" -- a CPU never legitimately
        // reports 0 MHz, so the caller (ReadCpu) relies on this to skip sending a telemetry
        // sample at all rather than sending a misleading 0.
        Assert.Null(SensorMonitor.AverageClockMhz(Array.Empty<double>()));
    }

    [Fact]
    public void AverageClockMhz_SingleCore_ReturnsThatValue()
    {
        Assert.Equal(4200.0, SensorMonitor.AverageClockMhz(new[] { 4200.0 }));
    }

    [Fact]
    public void AverageClockMhz_MultipleCoresAtDifferentSpeeds_ReturnsAverage()
    {
        // Realistic case: some cores boosted, some idle -- e.g. one core turbo-boosting while
        // others sit near base clock during a partial-load moment.
        var result = SensorMonitor.AverageClockMhz(new[] { 4800.0, 4800.0, 3600.0, 3600.0 });

        Assert.Equal(4200.0, result);
    }

    [Fact]
    public void FormatLiveReadout_AllValuesPresent_FormatsEachField()
    {
        var reading = new SensorReadings(CpuPackageTempC: 40.4, CpuLoadPct: 12.3, CpuFanRpm: 2303, CpuFrequencyMhz: 4187.32);

        var text = SensorMonitor.FormatLiveReadout(reading);

        Assert.Contains("CPU: 40C", text);
        Assert.Contains("Load: 12%", text);
        Assert.Contains("4187 MHz", text);
        Assert.Contains("Fan: 2303 RPM", text);
    }

    [Fact]
    public void FormatLiveReadout_AllValuesNull_UsesPlaceholderForEachField()
    {
        var reading = new SensorReadings(CpuPackageTempC: null, CpuLoadPct: null, CpuFanRpm: null, CpuFrequencyMhz: null);

        var text = SensorMonitor.FormatLiveReadout(reading);

        Assert.Equal("CPU: --   Load: --   --   Fan: --", text);
    }

    [Fact]
    public void FormatLiveReadout_FanMissingButOthersPresent_OnlyFanIsPlaceholder()
    {
        // Realistic partial case: board has no CPU fan header wired to Super IO, everything else reads fine.
        var reading = new SensorReadings(CpuPackageTempC: 55.0, CpuLoadPct: 99.0, CpuFanRpm: null, CpuFrequencyMhz: 4200.0);

        var text = SensorMonitor.FormatLiveReadout(reading);

        Assert.Contains("CPU: 55C", text);
        Assert.Contains("Load: 99%", text);
        Assert.Contains("4200 MHz", text);
        Assert.Contains("Fan: --", text);
    }

    [Fact]
    public void FormatGpuLiveReadout_NullReading_ReportsNoGpuDetected()
    {
        Assert.Equal("(no GPU detected)", SensorMonitor.FormatGpuLiveReadout(null));
    }

    [Fact]
    public void FormatGpuLiveReadout_AllValuesPresent_FormatsEachField()
    {
        // Ground-truthed against a real NVIDIA GTX 1080 Ti.
        var reading = new GpuReadings(
            CoreTempC: 38.0, HotSpotTempC: 52.1, CoreClockMhz: 1480.7, MemoryClockMhz: 5508.0,
            LoadPct: 25.4, FanRpm: 1092.0, PowerW: 63.6, MemoryUsedMb: 1626.0, MemoryTotalMb: 11264.0);

        var text = SensorMonitor.FormatGpuLiveReadout(reading);

        Assert.Contains("GPU: 38C (hotspot 52C)", text);
        Assert.Contains("Load: 25%", text);
        Assert.Contains("Core: 1481 MHz", text);
        Assert.Contains("Mem: 5508 MHz", text);
        Assert.Contains("Fan: 1092 RPM", text);
        Assert.Contains("Power: 64W", text);
        Assert.Contains("VRAM: 1626/11264 MB", text);
    }

    [Fact]
    public void FormatGpuLiveReadout_AllValuesNull_UsesPlaceholderForEachField()
    {
        var reading = new GpuReadings(null, null, null, null, null, null, null, null, null);

        var text = SensorMonitor.FormatGpuLiveReadout(reading);

        Assert.Equal("GPU: -- (hotspot --)   Load: --   Core: --   Mem: --   Fan: --   Power: --   VRAM: --", text);
    }

    [Fact]
    public void FormatGpuLiveReadout_OnlyOneOfMemUsedOrTotalPresent_VramIsPlaceholder()
    {
        // VRAM is shown as "used/total" - a partial reading (e.g. total known but used missing
        // this cycle) can't form that pair, so it must fall back to the placeholder rather than
        // showing a misleading half-formed value like "1626/-- MB".
        var reading = new GpuReadings(null, null, null, null, null, null, null, MemoryUsedMb: 1626.0, MemoryTotalMb: null);

        var text = SensorMonitor.FormatGpuLiveReadout(reading);

        Assert.Contains("VRAM: --", text);
    }
}
