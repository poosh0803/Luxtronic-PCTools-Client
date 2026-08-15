using Luxtronic.PCTools.Services;
using Xunit;

namespace Luxtronic.PCTools.Tests;

public class DiskSpdRunnerTests
{
    // ---- ParseTotalMbS -------------------------------------------------------------------------
    // Real captured DiskSpd text output shape (see DiskSpdRunner's class remarks) - a "Total IO"
    // section ending in a "total:" line shaped like
    // "total:        4418699264 |         4214 |    2104.65 |    2104.65", pipe-delimited as
    // bytes | I/Os | MiB/s | I/O per s. Ground-truthed via real short probe runs on a dev machine
    // (2026-08-15), one for read (-w0) and one for write (-w100).

    [Fact]
    public void ParseTotalMbS_RealCapturedReadPassOutput_ExtractsMbS()
    {
        var output = string.Join(Environment.NewLine, new[]
        {
            "Total IO",
            "thread |       bytes     |     I/Os     |    MiB/s   |  I/O per s |  file",
            "------------------------------------------------------------------------------",
            "     0 |      4418699264 |         4214 |    2104.65 |    2104.65 | C:\\probe.dat (64MiB)",
            "------------------------------------------------------------------------------",
            "total:        4418699264 |         4214 |    2104.65 |    2104.65",
            "",
            "Read IO",
            "thread |       bytes     |     I/Os     |    MiB/s   |  I/O per s |  file",
        });

        Assert.Equal(2104.65, DiskSpdRunner.ParseTotalMbS(output));
    }

    [Fact]
    public void ParseTotalMbS_RealCapturedWritePassOutput_ExtractsMbS()
    {
        var output = string.Join(Environment.NewLine, new[]
        {
            "Total IO",
            "thread |       bytes     |     I/Os     |    MiB/s   |  I/O per s |  file",
            "------------------------------------------------------------------------------",
            "     0 |       143654912 |          137 |      68.20 |      68.20 | C:\\probe.dat (64MiB)",
            "------------------------------------------------------------------------------",
            "total:         143654912 |          137 |      68.20 |      68.20",
        });

        Assert.Equal(68.20, DiskSpdRunner.ParseTotalMbS(output));
    }

    [Fact]
    public void ParseTotalMbS_NoTotalIoSection_ReturnsNull()
    {
        Assert.Null(DiskSpdRunner.ParseTotalMbS("Error opening file: Z:\\nonexistent\\test.dat [3]"));
    }

    [Fact]
    public void ParseTotalMbS_EmptyString_ReturnsNull()
    {
        Assert.Null(DiskSpdRunner.ParseTotalMbS(""));
    }

    [Fact]
    public void ParseTotalMbS_TotalIoHeaderButNoTotalLine_ReturnsNull()
    {
        // Simulates a pass killed mid-run (Stop clicked) before DiskSpd printed its summary.
        var output = "Total IO" + Environment.NewLine + "thread |  bytes  |  I/Os  |  MiB/s";

        Assert.Null(DiskSpdRunner.ParseTotalMbS(output));
    }
}
