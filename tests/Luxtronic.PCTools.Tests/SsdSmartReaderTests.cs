using Luxtronic.PCTools.Services;
using Xunit;

namespace Luxtronic.PCTools.Tests;

// Only BuildSsdInfo (pure attribute extraction) is tested here - SsdSmartReader.Initialize()/
// ReadAll() wrap a real LibreHardwareMonitorLib Computer and need real drives, same boundary as
// SensorMonitor.ReadCpu() (see README's manual end-to-end checklist).
public class SsdSmartReaderTests
{
    // Ground-truthed against a real Crucial CT1000P2SSD8 NVMe drive.
    private static readonly Dictionary<byte, float> RealNvmeAttributes = new()
    {
        [1] = 0,        // Critical Warning
        [3] = 100,      // Available Spare
        [4] = 5,        // Available Spare Threshold
        [5] = 8,        // Percentage Used
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
        Assert.Equal(0, info.MediaErrors);
        Assert.Null(info.ReallocatedSectorsCount);
    }

    [Fact]
    public void BuildSsdInfo_Ata_PopulatesAtaFieldsNotNvmeFields()
    {
        // Standard ATA/SATA SMART table: id 5 = Reallocated Sectors Count, id 9 = Power-On Hours.
        // Values chosen to also exist at the *same numeric IDs* NVMe uses for unrelated metrics
        // (id 5, id 12-ish range) specifically to prove isNvme gates interpretation, not just presence.
        var ataAttributes = new Dictionary<byte, float>
        {
            [5] = 3,      // Reallocated Sectors Count (ATA) - NOT "Percentage Used"
            [9] = 12000,  // Power-On Hours (ATA)
        };

        var info = SsdSmartReader.BuildSsdInfo("Samsung 860 EVO", "S3Z9NB0K123456", isNvme: false, temperatureC: 35.0, ataAttributes);

        Assert.False(info.IsNvme);
        Assert.Equal(3, info.ReallocatedSectorsCount);
        Assert.Equal(12000, info.PowerOnHours);
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
}
