using System.Management;

namespace Luxtronic.PCTools.Services;

/// <summary>
/// Picks which TestMem5 (TM5) .cfg profile to run. Deliberately deviates from CONTRACT.md §3's
/// "config flows one direction, client just executes" model for one specific reason: the shipped
/// profile pack (tools/TestMem5/bin/) only has one DDR5-tuned file per CPU platform ("DDR5 Intel
/// @ anta777.cfg", "DDR5 Ryzen3D @ anta777.cfg"), not a DDR5 variant of every intensity level a
/// server-driven config_profile string could name - so on a detected DDR5 system, the
/// platform-specific file is used regardless of what config_profile says. On DDR4 (or when
/// detection fails/is ambiguous), config_profile is honored normally, same as CPU's mode / GPU's
/// tool. Flagged in README's "Judgment calls against CONTRACT.md" for reconciliation.
/// </summary>
public static class RamProfileSelector
{
    private static readonly Dictionary<string, string> ConfigProfileToFileName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["anta777-absolut"] = "Absolut @ anta777.cfg",
        ["anta777-extreme"] = "Extreme @ anta777.cfg",
        ["anta777-heavy"] = "Heavy @ anta777.cfg",
        ["anta777-superlight2"] = "Super Light 2 @ anta777.cfg",
        ["1usmus-v3"] = "1usmus v3 @ 1usmus.cfg",
        ["universal2-lmhz"] = "Universal 2 @ LMhz.cfg",
        ["serj-default"] = "Default @ serj.cfg",
    };

    private const string DefaultConfigProfileFileName = "Extreme @ anta777.cfg";
    private const string Ddr5IntelFileName = "DDR5 Intel @ anta777.cfg";
    private const string Ddr5RyzenFileName = "DDR5 Ryzen3D @ anta777.cfg";

    /// <summary>
    /// Detects CPU vendor and RAM generation via WMI, then resolves the actual .cfg filename to
    /// use (see class remarks for the DDR5-overrides-config_profile reasoning).
    /// </summary>
    public static string SelectConfigFileName(string configProfile)
    {
        var isIntel = TryDetectIntelCpu();
        var isDdr5 = TryDetectDdr5();
        return ResolveConfigFileName(isIntel, isDdr5, configProfile);
    }

    /// <summary>
    /// Pure decision logic, split out from the WMI queries so it's unit testable without real
    /// hardware - mirrors Prime95Runner.GetTortureTestParams. isDdr5/isIntel are null when
    /// detection failed or DIMMs reported inconsistent generations - treated as "don't know,"
    /// falling through to configProfile rather than guessing.
    /// </summary>
    internal static string ResolveConfigFileName(bool? isIntel, bool? isDdr5, string configProfile)
    {
        if (isDdr5 == true)
        {
            // Unknown vendor defaults to the Intel file - the more generic/conservative of the
            // two DDR5 profiles, not tuned around AMD's Infinity Fabric/FCLK coupling the way the
            // Ryzen3D file is.
            return isIntel == false ? Ddr5RyzenFileName : Ddr5IntelFileName;
        }

        return ConfigProfileToFileName.TryGetValue(configProfile, out var fileName)
            ? fileName
            : DefaultConfigProfileFileName;
    }

    /// <summary>True/false if confidently detected, null if WMI failed or timed out. Explicit
    /// bounded timeout - see SensorMonitor.TryReadMotherboardSerialViaWmi's remarks for why a bare
    /// WMI query is known to hang indefinitely on some machines rather than fail fast.</summary>
    private static bool? TryDetectIntelCpu()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer FROM Win32_Processor");
            searcher.Options.Timeout = TimeSpan.FromSeconds(10);
            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["Manufacturer"] is string manufacturer && !string.IsNullOrWhiteSpace(manufacturer))
                {
                    if (manufacturer.Equals("GenuineIntel", StringComparison.OrdinalIgnoreCase)) return true;
                    if (manufacturer.Equals("AuthenticAMD", StringComparison.OrdinalIgnoreCase)) return false;
                    return null;
                }
            }
        }
        catch
        {
            // Treated as "unknown" - same reasoning as the motherboard-serial WMI read.
        }

        return null;
    }

    /// <summary>True/false if confidently detected from the first populated DIMM, null if WMI
    /// failed/timed out or no DIMMs reported a recognized SMBIOSMemoryType. Doesn't attempt to
    /// reconcile mixed-generation DIMMs (shouldn't happen on real hardware, and guessing wrong
    /// here would silently run the wrong stress profile) - any DIMM disagreeing with the first
    /// falls back to "unknown" rather than picking one.</summary>
    private static bool? TryDetectDdr5()
    {
        const ushort Ddr4 = 26;
        const ushort Ddr5 = 34;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT SMBIOSMemoryType FROM Win32_PhysicalMemory");
            searcher.Options.Timeout = TimeSpan.FromSeconds(10);

            bool? result = null;
            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["SMBIOSMemoryType"] is not ushort memType || (memType != Ddr4 && memType != Ddr5))
                {
                    continue;
                }

                var thisDimmIsDdr5 = memType == Ddr5;
                if (result is null)
                {
                    result = thisDimmIsDdr5;
                }
                else if (result != thisDimmIsDdr5)
                {
                    return null; // mixed generations reported - don't guess
                }
            }

            return result;
        }
        catch
        {
            return null;
        }
    }
}
