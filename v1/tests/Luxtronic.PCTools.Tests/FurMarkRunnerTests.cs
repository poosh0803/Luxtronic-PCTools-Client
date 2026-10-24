using Luxtronic.PCTools.Services;
using Xunit;

namespace Luxtronic.PCTools.Tests;

public class FurMarkRunnerTests
{
    // ---- CountArtifacts -----------------------------------------------------------------------
    // NOTE: the "detection" case is a best-guess pattern, not ground-truthed against a real
    // detected artifact - see FurMarkRunner's class remarks. What IS ground-truthed here is the
    // real startup-line text captured from a live run, confirming it must NOT count as a hit.

    [Fact]
    public void CountArtifacts_EmptyStringReturnsZero()
    {
        Assert.Equal(0, FurMarkRunner.CountArtifacts(""));
    }

    [Fact]
    public void CountArtifacts_CleanOutputReturnsZero()
    {
        // Real captured excerpt from a clean 8-second furmark-gl run with --artifact-scanner
        // enabled, from _furmark_log.txt - only the startup announcement, no detections (expected
        // for a short clean run against a healthy GPU).
        var output = string.Join(Environment.NewLine, new[]
        {
            "(14:31:43:836)\tOpenGL extensions: 423",
            "(14:31:43:924)\t[ArtifactScanner] Artifact scanner for GPU stability checking.",
            "(14:31:52:236)\t[ Demo Quick Stats ]",
        });

        Assert.Equal(0, FurMarkRunner.CountArtifacts(output));
    }

    [Fact]
    public void CountArtifacts_LineBeyondStartupAnnouncement_CountsAsOne()
    {
        var output = string.Join(Environment.NewLine, new[]
        {
            "(14:31:43:924)\t[ArtifactScanner] Artifact scanner for GPU stability checking.",
            "(14:35:10:112)\t[ArtifactScanner] artifact detected at frame 4021",
        });

        Assert.Equal(1, FurMarkRunner.CountArtifacts(output));
    }

    [Fact]
    public void CountArtifacts_MultipleDetectionLines_CountsEach()
    {
        var output = string.Join(Environment.NewLine, new[]
        {
            "(14:31:43:924)\t[ArtifactScanner] Artifact scanner for GPU stability checking.",
            "(14:35:10:112)\t[ArtifactScanner] artifact detected at frame 4021",
            "(14:36:02:558)\t[ArtifactScanner] artifact detected at frame 5390",
        });

        Assert.Equal(2, FurMarkRunner.CountArtifacts(output));
    }

    [Fact]
    public void CountArtifacts_NoArtifactScannerMentionAtAll_ReturnsZero()
    {
        var output = string.Join(Environment.NewLine, new[]
        {
            "FurMark 2.10.2.0 (build: Oct 22 2025@12:39:49)",
            "[ Demo Quick Stats ]",
            "- frames               : 2466",
        });

        Assert.Equal(0, FurMarkRunner.CountArtifacts(output));
    }
}
