using Luxtronic.PCTools.Services;
using Xunit;

namespace Luxtronic.PCTools.Tests;

public class Prime95RunnerTests
{
    // ---- CountErrors ------------------------------------------------------------------------

    [Theory]
    [InlineData("FATAL ERROR: something went wrong")]
    [InlineData("fatal error: something went wrong")]
    [InlineData("Fatal Error: something went wrong")]
    [InlineData("ROUNDOFF ERROR exceeded threshold")]
    [InlineData("roundoff error exceeded threshold")]
    [InlineData("an error occurred during self-test")]
    [InlineData("AN ERROR OCCURRED DURING SELF-TEST")]
    [InlineData("Hardware failure detected")]
    [InlineData("HARDWARE FAILURE detected")]
    public void CountErrors_DetectsEachPatternCaseInsensitively(string line)
    {
        Assert.Equal(1, Prime95Runner.CountErrors(line));
    }

    [Fact]
    public void CountErrors_CountsMultipleMatchesInOneString()
    {
        var output = string.Join(Environment.NewLine, new[]
        {
            "Self-test 1344K passed!",
            "FATAL ERROR: illegal sumout",
            "Self-test 1344K passed!",
            "ROUNDOFF ERROR exceeded 0.4",
            "an error occurred, continuing",
        });

        Assert.Equal(3, Prime95Runner.CountErrors(output));
    }

    [Fact]
    public void CountErrors_CleanOutputReturnsZero()
    {
        var output = string.Join(Environment.NewLine, new[]
        {
            "Prime95 64-bit version 30.19, RdtscTiming=1",
            "Self-test 1344K passed!",
            "Self-test 1344K passed!",
            "Executing torture test...",
        });

        Assert.Equal(0, Prime95Runner.CountErrors(output));
    }

    [Fact]
    public void CountErrors_EmptyStringReturnsZero()
    {
        Assert.Equal(0, Prime95Runner.CountErrors(""));
    }

    // ---- GetTortureTestParams -----------------------------------------------------------------

    [Fact]
    public void GetTortureTestParams_SmallFft_ReturnsMaxHeatRange()
    {
        var result = Prime95Runner.GetTortureTestParams("small_fft");

        Assert.Equal(4, result.MinFft);
        Assert.Equal(32, result.MaxFft);
    }

    [Fact]
    public void GetTortureTestParams_Blend_ReturnsWideFftRange()
    {
        var result = Prime95Runner.GetTortureTestParams("blend");

        Assert.Equal(8, result.MinFft);
        Assert.Equal(4096, result.MaxFft);
    }

    [Theory]
    [InlineData("Small_FFT")]
    [InlineData("SMALL_FFT")]
    [InlineData("sMaLl_FfT")]
    public void GetTortureTestParams_ModeMatchingIsCaseInsensitive(string mode)
    {
        var result = Prime95Runner.GetTortureTestParams(mode);

        Assert.Equal(4, result.MinFft);
        Assert.Equal(32, result.MaxFft);
    }

    [Fact]
    public void GetTortureTestParams_ThreadsComesFromProcessorCount()
    {
        var result = Prime95Runner.GetTortureTestParams("blend");

        Assert.Equal(Math.Max(1, Environment.ProcessorCount), result.Threads);
    }
}
