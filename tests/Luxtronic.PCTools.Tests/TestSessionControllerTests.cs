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

    // ---- BuildGpuSummaryStats -----------------------------------------------------------------
    // Same shape as BuildSummaryStats minus avg_load_pct - CONTRACT.md §3's gpu config subtree
    // only thresholds error_count/max_temp_c.

    [Fact]
    public void BuildGpuSummaryStats_ErrorCountAlwaysPresent()
    {
        var summary = TestSessionController.BuildGpuSummaryStats(errorCount: 2, maxTempObserved: null);

        Assert.True(summary.ContainsKey("error_count"));
        Assert.Equal(2, summary["error_count"]);
    }

    [Fact]
    public void BuildGpuSummaryStats_MaxTempPresentWithCorrectValue_WhenObserved()
    {
        var summary = TestSessionController.BuildGpuSummaryStats(errorCount: 0, maxTempObserved: 82.3);

        Assert.True(summary.ContainsKey("max_temp_c"));
        Assert.Equal(82.3, summary["max_temp_c"]);
    }

    [Fact]
    public void BuildGpuSummaryStats_MaxTempAbsent_WhenNoneObserved()
    {
        var summary = TestSessionController.BuildGpuSummaryStats(errorCount: 0, maxTempObserved: null);

        Assert.False(summary.ContainsKey("max_temp_c"));
    }

    [Fact]
    public void BuildGpuSummaryStats_NeverContainsAvgLoadPct()
    {
        var summary = TestSessionController.BuildGpuSummaryStats(errorCount: 1, maxTempObserved: 88.0);

        Assert.False(summary.ContainsKey("avg_load_pct"));
        Assert.Equal(2, summary.Count);
    }

    // ---- BuildRamSummaryStats -----------------------------------------------------------------
    // CONTRACT.md §3's ram config subtree only thresholds error_count (max_errors) - no
    // temperature or load field exists for ram, so this is just the one key, always present.

    [Fact]
    public void BuildRamSummaryStats_ErrorCountAlwaysPresent()
    {
        var summary = TestSessionController.BuildRamSummaryStats(errorCount: 0);

        Assert.True(summary.ContainsKey("error_count"));
        Assert.Equal(0, summary["error_count"]);
        Assert.Single(summary);
    }

    [Fact]
    public void BuildRamSummaryStats_NonZeroErrorCount()
    {
        var summary = TestSessionController.BuildRamSummaryStats(errorCount: 5);

        Assert.Equal(5, summary["error_count"]);
    }

    // ---- ParseRamLog ---------------------------------------------------------------------------
    // Real captured Log.txt shape (see TM5Runner's class remarks) - a fresh session header, a
    // "Testing N MB x..." line, and eventually a stop/completion line.

    [Fact]
    public void ParseRamLog_RealCapturedShape_ExtractsTestedMbAndZeroErrors()
    {
        var log = string.Join(Environment.NewLine, new[]
        {
            "-------- TestMem5 0.13.1 2026-08-12 16:29 --------",
            "0:00:00  Configuration: Universal 2 @ LMhz",
            "0:00:00  Testing 672 MB x 12 (49%) out of 16265 MB",
        });

        var progress = TestSessionController.ParseRamLog(log);

        Assert.Equal(672.0, progress.TestedMb);
        Assert.Equal(0, progress.ErrorCount);
    }

    [Fact]
    public void ParseRamLog_MultipleTestingLines_UsesTheLastOne()
    {
        // A real run would show increasing "Testing N MB x..." lines as cycles progress (not
        // confirmed whether Log.txt actually appends multiple such lines per session - see
        // TM5Runner's class remarks on what's unconfirmed about live updates - but if it does,
        // the most recent one is what matters for a live "how far along is this" readout).
        var log = string.Join(Environment.NewLine, new[]
        {
            "0:00:00  Testing 560 MB x 12 (41%) out of 16265 MB",
            "0:05:00  Testing 700 MB x 12 (51%) out of 16265 MB",
        });

        var progress = TestSessionController.ParseRamLog(log);

        Assert.Equal(700.0, progress.TestedMb);
    }

    [Fact]
    public void ParseRamLog_NoTestingLineYet_TestedMbIsNull()
    {
        var progress = TestSessionController.ParseRamLog("-------- TestMem5 0.13.1 2026-08-12 16:29 --------");

        Assert.Null(progress.TestedMb);
    }

    [Fact]
    public void ParseRamLog_EmptyLog_ReturnsNullTestedMbAndZeroErrors()
    {
        var progress = TestSessionController.ParseRamLog("");

        Assert.Null(progress.TestedMb);
        Assert.Equal(0, progress.ErrorCount);
    }

    [Fact]
    public void ParseRamLog_ReusesTM5RunnerCountErrors_ZeroCountSummaryDoesNotFalsePositive()
    {
        var log = string.Join(Environment.NewLine, new[]
        {
            "0:00:00  Testing 560 MB x 12 (41%) out of 16265 MB",
            "Errors: 0",
        });

        var progress = TestSessionController.ParseRamLog(log);

        Assert.Equal(0, progress.ErrorCount);
    }
}
