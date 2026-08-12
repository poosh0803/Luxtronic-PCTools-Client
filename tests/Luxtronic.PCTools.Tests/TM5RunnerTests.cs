using Luxtronic.PCTools.Services;
using Xunit;

namespace Luxtronic.PCTools.Tests;

// NOTE: CountErrors is a best-guess pattern, not ground-truthed against a real detected memory
// error - no error occurred in any real TM5 test run done while building this wrapper. See
// TM5Runner's class remarks. What IS grounded in real testing is the negative-match case: TM5's
// own UI shows a live "Errors: 0" style field, which a naive bare "error" substring match would
// have false-positived on.
public class TM5RunnerTests
{
    [Fact]
    public void CountErrors_EmptyStringReturnsZero()
    {
        Assert.Equal(0, TM5Runner.CountErrors(""));
    }

    [Fact]
    public void CountErrors_CleanLogWithNoMentionOfErrorsReturnsZero()
    {
        var output = string.Join(Environment.NewLine, new[]
        {
            "-------- TestMem5 0.13.1 2026-08-12 16:29 --------",
            "0:00:00  Configuration: Extreme1",
            "0:00:00  Testing 672 MB x 12 (49%) out of 16265 MB",
            "0:00:27  Testing stopped by user",
        });

        Assert.Equal(0, TM5Runner.CountErrors(output));
    }

    [Theory]
    [InlineData("Errors: 0")]
    [InlineData("Error(s): 0")]
    [InlineData("errors 0")]
    public void CountErrors_ZeroCountSummaryLine_DoesNotFalsePositive(string line)
    {
        Assert.Equal(0, TM5Runner.CountErrors(line));
    }

    [Theory]
    [InlineData("Errors: 3")]
    [InlineData("Error detected at address 0x1234")]
    [InlineData("3 errors found")]
    public void CountErrors_NonZeroOrDescriptiveErrorLine_Counts(string line)
    {
        Assert.True(TM5Runner.CountErrors(line) > 0);
    }

    [Fact]
    public void CountErrors_CountsMultipleMatchesInOneString()
    {
        var output = string.Join(Environment.NewLine, new[]
        {
            "0:05:00  Error detected at address 0x1000",
            "0:10:00  Error detected at address 0x2000",
        });

        Assert.Equal(2, TM5Runner.CountErrors(output));
    }
}
