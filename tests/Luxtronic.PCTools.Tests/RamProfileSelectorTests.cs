using Luxtronic.PCTools.Services;
using Xunit;

namespace Luxtronic.PCTools.Tests;

public class RamProfileSelectorTests
{
    [Fact]
    public void ResolveConfigFileName_Ddr5Intel_PicksDdr5IntelProfile()
    {
        var result = RamProfileSelector.ResolveConfigFileName(isIntel: true, isDdr5: true, configProfile: "anta777-extreme");

        Assert.Equal("DDR5 Intel @ anta777.cfg", result);
    }

    [Fact]
    public void ResolveConfigFileName_Ddr5Amd_PicksDdr5Ryzen3DProfile()
    {
        var result = RamProfileSelector.ResolveConfigFileName(isIntel: false, isDdr5: true, configProfile: "anta777-extreme");

        Assert.Equal("DDR5 Ryzen3D @ anta777.cfg", result);
    }

    [Fact]
    public void ResolveConfigFileName_Ddr5UnknownVendor_DefaultsToIntelProfile()
    {
        // Intel's DDR5 file is the more generic/conservative of the two - not tuned around AMD's
        // Infinity Fabric/FCLK coupling the way the Ryzen3D file is.
        var result = RamProfileSelector.ResolveConfigFileName(isIntel: null, isDdr5: true, configProfile: "anta777-extreme");

        Assert.Equal("DDR5 Intel @ anta777.cfg", result);
    }

    [Theory]
    [InlineData("anta777-absolut", "Absolut @ anta777.cfg")]
    [InlineData("anta777-extreme", "Extreme @ anta777.cfg")]
    [InlineData("anta777-heavy", "Heavy @ anta777.cfg")]
    [InlineData("anta777-superlight2", "Super Light 2 @ anta777.cfg")]
    [InlineData("1usmus-v3", "1usmus v3 @ 1usmus.cfg")]
    [InlineData("universal2-lmhz", "Universal 2 @ LMhz.cfg")]
    [InlineData("serj-default", "Default @ serj.cfg")]
    public void ResolveConfigFileName_Ddr4_HonorsConfigProfile(string configProfile, string expectedFileName)
    {
        var result = RamProfileSelector.ResolveConfigFileName(isIntel: true, isDdr5: false, configProfile: configProfile);

        Assert.Equal(expectedFileName, result);
    }

    [Fact]
    public void ResolveConfigFileName_Ddr4UnknownConfigProfile_FallsBackToExtreme()
    {
        var result = RamProfileSelector.ResolveConfigFileName(isIntel: true, isDdr5: false, configProfile: "not-a-real-profile");

        Assert.Equal("Extreme @ anta777.cfg", result);
    }

    [Fact]
    public void ResolveConfigFileName_DdrGenerationUnknown_FallsBackToConfigProfile()
    {
        // WMI detection failed or DIMMs reported mixed/ambiguous generations - don't guess DDR5,
        // just honor the server's config_profile like the DDR4 case.
        var result = RamProfileSelector.ResolveConfigFileName(isIntel: true, isDdr5: null, configProfile: "anta777-heavy");

        Assert.Equal("Heavy @ anta777.cfg", result);
    }

    [Fact]
    public void ResolveConfigFileName_ConfigProfileMatchingIsCaseInsensitive()
    {
        var result = RamProfileSelector.ResolveConfigFileName(isIntel: true, isDdr5: false, configProfile: "ANTA777-EXTREME");

        Assert.Equal("Extreme @ anta777.cfg", result);
    }
}
