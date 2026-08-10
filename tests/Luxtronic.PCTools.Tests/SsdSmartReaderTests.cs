using Luxtronic.PCTools.Services;
using Xunit;

namespace Luxtronic.PCTools.Tests;

// Only BuildSsdInfo/FormatSsdSummary (pure) are tested here - SsdSmartReader.ReadAll() needs a
// real, already-updated LibreHardwareMonitorLib hardware collection (via SensorMonitor's shared
// Computer), same boundary as SensorMonitor.ReadCpu() (see README's manual end-to-end checklist).
public class SsdSmartReaderTests
{
    // Ground-truthed against a real Crucial CT1000P2SSD8 NVMe drive.
    private static readonly Dictionary<byte, float> RealNvmeAttributes = new()
    {
        [1] = 0,        // Critical Warning
        [3] = 100,      // Available Spare
        [4] = 5,        // Available Spare Threshold
        [5] = 8,        // Percentage Used
        [11] = 3209,    // Power Cycles (NVMe's name for power-on count)
        [12] = 14988,   // Power On Hours
        [14] = 0,       // Media and Data Integrity Errors
    };

    [Fact]
    public void BuildSsdInfo_Nvme_PopulatesNvmeFieldsNotAtaFields()
    {
        var info = SsdSmartReader.BuildSsdInfo("CT1000P2SSD8", "2050E4D9C945", isNvme: true, temperatureC: 44.0, RealNvmeAttributes);

        Assert.True(info.IsNvme);
        Assert.Equal(8, info.PercentageUsed);
        Assert.Equal(100, info.AvailableSparePercent);
        Assert.Equal(14988, info.PowerOnHours);
        Assert.Equal(3209, info.PowerOnCount);
        Assert.Equal(0, info.MediaErrors);
        Assert.Null(info.ReallocatedSectorsCount);
    }

    [Fact]
    public void BuildSsdInfo_Ata_PopulatesAtaFieldsNotNvmeFields()
    {
        // Standard ATA/SATA SMART table: id 5 = Reallocated Sectors Count, id 9 = Power-On Hours,
        // id 12 = Power Cycle Count. Values chosen to also exist at the *same numeric IDs* NVMe
        // uses for unrelated metrics (id 5, id 11/12) specifically to prove isNvme gates
        // interpretation, not just presence.
        var ataAttributes = new Dictionary<byte, float>
        {
            [5] = 3,      // Reallocated Sectors Count (ATA) - NOT "Percentage Used"
            [9] = 12000,  // Power-On Hours (ATA)
            [12] = 450,   // Power Cycle Count (ATA) - NOT NVMe's "Power On Hours" id
        };

        var info = SsdSmartReader.BuildSsdInfo("Samsung 860 EVO", "S3Z9NB0K123456", isNvme: false, temperatureC: 35.0, ataAttributes);

        Assert.False(info.IsNvme);
        Assert.Equal(3, info.ReallocatedSectorsCount);
        Assert.Equal(12000, info.PowerOnHours);
        Assert.Equal(450, info.PowerOnCount);
        Assert.Null(info.PercentageUsed);
        Assert.Null(info.AvailableSparePercent);
        Assert.Null(info.MediaErrors);
    }

    [Fact]
    public void BuildSsdInfo_SameAttributeIdMeansDifferentThingsAcrossBusTypes()
    {
        // The exact scenario the class remarks warn about: id 5 present in both attribute sets
        // but must be read as two unrelated metrics depending on isNvme.
        var sharedIdAttributes = new Dictionary<byte, float> { [5] = 42 };

        var nvme = SsdSmartReader.BuildSsdInfo("drive", "serial", isNvme: true, temperatureC: null, sharedIdAttributes);
        var ata = SsdSmartReader.BuildSsdInfo("drive", "serial", isNvme: false, temperatureC: null, sharedIdAttributes);

        Assert.Equal(42, nvme.PercentageUsed);
        Assert.Null(nvme.ReallocatedSectorsCount);

        Assert.Equal(42, ata.ReallocatedSectorsCount);
        Assert.Null(ata.PercentageUsed);
    }

    [Fact]
    public void BuildSsdInfo_MissingAttributes_FieldsAreNullNotZeroOrException()
    {
        var info = SsdSmartReader.BuildSsdInfo("Unknown Drive", null, isNvme: true, temperatureC: null, new Dictionary<byte, float>());

        Assert.Null(info.PercentageUsed);
        Assert.Null(info.AvailableSparePercent);
        Assert.Null(info.PowerOnHours);
        Assert.Null(info.PowerOnCount);
        Assert.Null(info.MediaErrors);
        Assert.Null(info.ReallocatedSectorsCount);
        Assert.Null(info.SerialNumber);
        Assert.Null(info.TemperatureC);
    }

    [Fact]
    public void BuildSsdInfo_PassesThroughModelSerialAndTemperatureUnchanged()
    {
        var info = SsdSmartReader.BuildSsdInfo("Micron_2210_MTFDHBA512QFD", "21182EA3AFE6", isNvme: true, temperatureC: 47.0, RealNvmeAttributes);

        Assert.Equal("Micron_2210_MTFDHBA512QFD", info.Model);
        Assert.Equal("21182EA3AFE6", info.SerialNumber);
        Assert.Equal(47.0, info.TemperatureC);
    }

    [Fact]
    public void FormatSsdSummary_Nvme_ShowsModelSerialTempAndPowerOnNotUsedOrSpare()
    {
        // Used%/Spare% deliberately omitted from the summary per user feedback - still captured
        // on SsdSmartInfo (see BuildSsdInfo_Nvme_PopulatesNvmeFieldsNotAtaFields) for later use,
        // just not shown in this line. Power-on hours + count are shown per later feedback.
        var info = SsdSmartReader.BuildSsdInfo("CT1000P2SSD8", "2050E4D9C945", isNvme: true, temperatureC: 44.0, RealNvmeAttributes);

        var text = SsdSmartReader.FormatSsdSummary(info);

        Assert.Equal("CT1000P2SSD8 (2050E4D9C945): 44C   Power-on: 14988h (3209x)", text);
        Assert.DoesNotContain("Used", text);
        Assert.DoesNotContain("Spare", text);
        Assert.DoesNotContain("Reallocated", text);
    }

    [Fact]
    public void FormatSsdSummary_Ata_ShowsReallocatedAndPowerOnNotUsedOrSpare()
    {
        var ataAttributes = new Dictionary<byte, float> { [5] = 3, [9] = 12000, [12] = 450 };
        var info = SsdSmartReader.BuildSsdInfo("Samsung 860 EVO", "S3Z9NB0K123456", isNvme: false, temperatureC: 35.0, ataAttributes);

        var text = SsdSmartReader.FormatSsdSummary(info);

        Assert.Contains("Samsung 860 EVO (S3Z9NB0K123456)", text);
        Assert.Contains("35C", text);
        Assert.Contains("Reallocated: 3", text);
        Assert.Contains("Power-on: 12000h (450x)", text);
        Assert.DoesNotContain("Used:", text);
        Assert.DoesNotContain("Spare:", text);
    }

    [Fact]
    public void FormatSsdSummary_MissingSerialAndTemperature_UsesPlaceholders()
    {
        var info = SsdSmartReader.BuildSsdInfo("Unknown Drive", null, isNvme: true, temperatureC: null, new Dictionary<byte, float>());

        var text = SsdSmartReader.FormatSsdSummary(info);

        Assert.Equal("Unknown Drive (no serial): --   Power-on: -- (--)", text);
    }
}
