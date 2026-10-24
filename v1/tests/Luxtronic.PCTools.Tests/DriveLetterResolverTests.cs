using Luxtronic.PCTools.Services;
using Xunit;

namespace Luxtronic.PCTools.Tests;

public class DriveLetterResolverTests
{
    private static SsdSmartInfo MakeDrive(string model, string? serial) =>
        new(model, serial, IsNvme: true, TemperatureC: 40, PercentageUsed: 1, AvailableSparePercent: 100,
            ReallocatedSectorsCount: null, PowerOnHours: 100, PowerOnCount: 5, MediaErrors: 0);

    // ---- ValuesLikelyMatch ------------------------------------------------------------------------

    [Fact]
    public void ValuesLikelyMatch_ExactMatch_ReturnsTrue()
    {
        Assert.True(DriveLetterResolver.ValuesLikelyMatch("CT1000P2SSD8", "CT1000P2SSD8"));
    }

    [Fact]
    public void ValuesLikelyMatch_DifferentCasing_ReturnsTrue()
    {
        Assert.True(DriveLetterResolver.ValuesLikelyMatch("ct1000p2ssd8", "CT1000P2SSD8"));
    }

    [Fact]
    public void ValuesLikelyMatch_ExtraWhitespaceAndPunctuation_ReturnsTrue()
    {
        // e.g. LHM's plain "Micron_2210_MTFDHBA512QFD" vs WMI's PNPDeviceID-style padding.
        Assert.True(DriveLetterResolver.ValuesLikelyMatch(" Micron_2210_MTFDHBA512QFD ", "Micron2210MTFDHBA512QFD"));
    }

    [Fact]
    public void ValuesLikelyMatch_OneContainsTheOtherAsSubstring_ReturnsTrue()
    {
        Assert.True(DriveLetterResolver.ValuesLikelyMatch("CT1000P2SSD8", "PROD_CT1000P2SSD8"));
    }

    [Fact]
    public void ValuesLikelyMatch_UnrelatedValues_ReturnsFalse()
    {
        // Real ground-truthed case: WMI's Win32_DiskDrive.SerialNumber for an NVMe drive
        // ("6479_A7FF_F000_0009.") shares zero characters in common with LHM's properly
        // ASCII-decoded serial ("2050E4D9C945") for the same physical drive - confirmed live, not
        // a formatting quirk normalization can bridge. This is exactly why model, not serial, is
        // the primary correlation key - see DriveLetterResolver's class remarks.
        Assert.False(DriveLetterResolver.ValuesLikelyMatch("2050E4D9C945", "6479A7FFF0000009"));
    }

    [Fact]
    public void ValuesLikelyMatch_EmptyStrings_ReturnsFalse()
    {
        Assert.False(DriveLetterResolver.ValuesLikelyMatch("", ""));
        Assert.False(DriveLetterResolver.ValuesLikelyMatch("ABC123", ""));
    }

    // ---- BuildOptions -----------------------------------------------------------------------------

    [Fact]
    public void BuildOptions_MatchingModel_AttachesResolvedLetters()
    {
        var drives = new[] { MakeDrive("CT1000P2SSD8", "2050E4D9C945") };
        var modelByIndex = new Dictionary<uint, string> { [1] = "CT1000P2SSD8" };
        var serialByIndex = new Dictionary<uint, string> { [1] = "6479_A7FF_F000_0009." }; // real WMI garbage - irrelevant here, model already resolved it
        var lettersByIndex = new Dictionary<uint, List<string>> { [1] = new() { "D:" } };

        var options = DriveLetterResolver.BuildOptions(drives, modelByIndex, serialByIndex, lettersByIndex);

        Assert.Single(options);
        Assert.Equal(new[] { "D:" }, options[0].DriveLetters);
    }

    [Fact]
    public void BuildOptions_NoMatchingModel_ReturnsEmptyLetters()
    {
        var drives = new[] { MakeDrive("CT1000P2SSD8", "2050E4D9C945") };
        var modelByIndex = new Dictionary<uint, string> { [0] = "Micron_2210_MTFDHBA512QFD" };
        var serialByIndex = new Dictionary<uint, string>();
        var lettersByIndex = new Dictionary<uint, List<string>> { [0] = new() { "C:" } };

        var options = DriveLetterResolver.BuildOptions(drives, modelByIndex, serialByIndex, lettersByIndex);

        Assert.Empty(options[0].DriveLetters);
    }

    [Fact]
    public void BuildOptions_TwoDisksSameModel_DisambiguatesBySerial()
    {
        var drives = new[] { MakeDrive("SameModelSSD", "SERIAL_B") };
        var modelByIndex = new Dictionary<uint, string> { [0] = "SameModelSSD", [1] = "SameModelSSD" };
        var serialByIndex = new Dictionary<uint, string> { [0] = "SERIAL_A", [1] = "SERIAL_B" };
        var lettersByIndex = new Dictionary<uint, List<string>>
        {
            [0] = new() { "C:" },
            [1] = new() { "D:" },
        };

        var options = DriveLetterResolver.BuildOptions(drives, modelByIndex, serialByIndex, lettersByIndex);

        Assert.Equal(new[] { "D:" }, options[0].DriveLetters);
    }

    [Fact]
    public void BuildOptions_TwoDisksSameModel_SerialAlsoAmbiguous_ReturnsEmptyRatherThanGuessing()
    {
        var drives = new[] { MakeDrive("SameModelSSD", null) };
        var modelByIndex = new Dictionary<uint, string> { [0] = "SameModelSSD", [1] = "SameModelSSD" };
        var serialByIndex = new Dictionary<uint, string>(); // e.g. both NVMe, WMI serial unusable for either
        var lettersByIndex = new Dictionary<uint, List<string>>
        {
            [0] = new() { "C:" },
            [1] = new() { "D:" },
        };

        var options = DriveLetterResolver.BuildOptions(drives, modelByIndex, serialByIndex, lettersByIndex);

        Assert.Empty(options[0].DriveLetters);
    }

    [Fact]
    public void BuildOptions_EmptyWmiData_ReturnsOneOptionPerDriveWithNoLetters()
    {
        var drives = new[] { MakeDrive("Drive A", "SERIAL_A"), MakeDrive("Drive B", "SERIAL_B") };

        var options = DriveLetterResolver.BuildOptions(
            drives, new Dictionary<uint, string>(), new Dictionary<uint, string>(), new Dictionary<uint, List<string>>());

        Assert.Equal(2, options.Count);
        Assert.All(options, o => Assert.Empty(o.DriveLetters));
    }

    // ---- SsdDriveOption.DisplayText -----------------------------------------------------------

    [Fact]
    public void DisplayText_WithLetters_IncludesThem()
    {
        var option = new SsdDriveOption(MakeDrive("Crucial CT1000P2SSD8", "2050E4D9C945"), new[] { "C:" });

        Assert.Equal("Crucial CT1000P2SSD8 (2050E4D9C945) - C:", option.DisplayText);
    }

    [Fact]
    public void DisplayText_NoLetters_SaysNoWritableVolume()
    {
        var option = new SsdDriveOption(MakeDrive("Crucial CT1000P2SSD8", "2050E4D9C945"), Array.Empty<string>());

        Assert.Equal("Crucial CT1000P2SSD8 (2050E4D9C945) - no writable volume found", option.DisplayText);
    }

    [Fact]
    public void DisplayText_NoSerial_ShowsPlaceholder()
    {
        var option = new SsdDriveOption(MakeDrive("Unknown Drive", null), Array.Empty<string>());

        Assert.Equal("Unknown Drive (no serial) - no writable volume found", option.DisplayText);
    }
}
