using Luxtronic.PCTools.Services;
using Xunit;

namespace Luxtronic.PCTools.Tests;

// Only the pure verdict+message logic is tested here. SensorMonitor.Initialize()/ReadCpu()
// wrap a real LibreHardwareMonitorLib Computer and need real hardware - that's out of scope
// for xUnit and is instead covered by the manual end-to-end checklist in the README.
public class SensorMonitorTests
{
    [Fact]
    public void EvaluateSensorHealth_BothCountsZero_NotLoadedWithGuidanceMessage()
    {
        var (driverLikelyLoaded, message) = SensorMonitor.EvaluateSensorHealth(totalSensors: 0, cpuSensors: 0);

        Assert.False(driverLikelyLoaded);
        Assert.Contains("Secure Boot", message);
        Assert.Contains("HVCI", message);
        Assert.Contains("Administrator", message);
    }

    [Fact]
    public void EvaluateSensorHealth_TotalNonzeroButCpuZero_StillNotLoaded()
    {
        // Per the current "&&" logic, a nonzero total (e.g. motherboard-only sensors) with no
        // CPU sensors still counts as the driver not being usably loaded.
        var (driverLikelyLoaded, message) = SensorMonitor.EvaluateSensorHealth(totalSensors: 5, cpuSensors: 0);

        Assert.False(driverLikelyLoaded);
        Assert.Contains("WARNING", message);
    }

    [Fact]
    public void EvaluateSensorHealth_BothCountsNonzero_LoadedWithCounts()
    {
        var (driverLikelyLoaded, message) = SensorMonitor.EvaluateSensorHealth(totalSensors: 39, cpuSensors: 12);

        Assert.True(driverLikelyLoaded);
        Assert.Contains("39", message);
        Assert.Contains("12", message);
        Assert.Contains("Sensors OK", message);
    }
}
