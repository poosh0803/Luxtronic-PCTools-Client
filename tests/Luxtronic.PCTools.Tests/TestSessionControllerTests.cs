using Luxtronic.PCTools.Services;
using Xunit;

namespace Luxtronic.PCTools.Tests;

// Only the pure summary_stats builder is tested here - the rest of TestSessionController mixes
// in real HTTP/WebSocket/sensor orchestration that isn't unit-testable without integration
// infrastructure (see README's manual end-to-end checklist).
public class TestSessionControllerTests
{
    [Fact]
    public void BuildSummaryStats_ErrorCountAlwaysPresent()
    {
        var summary = TestSessionController.BuildSummaryStats(errorCount: 3, maxTempObserved: null, avgLoadPct: null);

        Assert.True(summary.ContainsKey("error_count"));
        Assert.Equal(3, summary["error_count"]);
    }

    [Fact]
    public void BuildSummaryStats_MaxTempPresentWithCorrectValue_WhenObserved()
    {
        var summary = TestSessionController.BuildSummaryStats(errorCount: 0, maxTempObserved: 87.5, avgLoadPct: null);

        Assert.True(summary.ContainsKey("max_temp_c"));
        Assert.Equal(87.5, summary["max_temp_c"]);
    }

    [Fact]
    public void BuildSummaryStats_MaxTempAbsent_WhenNoneObserved()
    {
        var summary = TestSessionController.BuildSummaryStats(errorCount: 0, maxTempObserved: null, avgLoadPct: null);

        Assert.False(summary.ContainsKey("max_temp_c"));
    }

    [Fact]
    public void BuildSummaryStats_AvgLoadPresentWithCorrectValue_WhenObserved()
    {
        var summary = TestSessionController.BuildSummaryStats(errorCount: 0, maxTempObserved: null, avgLoadPct: 98.2);

        Assert.True(summary.ContainsKey("avg_load_pct"));
        Assert.Equal(98.2, summary["avg_load_pct"]);
    }

    [Fact]
    public void BuildSummaryStats_AvgLoadAbsent_WhenNoneObserved()
    {
        var summary = TestSessionController.BuildSummaryStats(errorCount: 0, maxTempObserved: null, avgLoadPct: null);

        Assert.False(summary.ContainsKey("avg_load_pct"));
    }

    [Fact]
    public void BuildSummaryStats_AllFieldsPresent_WhenAllObserved()
    {
        var summary = TestSessionController.BuildSummaryStats(errorCount: 1, maxTempObserved: 90.0, avgLoadPct: 99.9);

        Assert.Equal(3, summary.Count);
        Assert.Equal(1, summary["error_count"]);
        Assert.Equal(90.0, summary["max_temp_c"]);
        Assert.Equal(99.9, summary["avg_load_pct"]);
    }
}
