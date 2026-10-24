using System.Management;

namespace Luxtronic.PCTools.Services;

/// <summary>Board identity shown on the Dashboard's Motherboard card (the serial number used as
/// the PC's identity is read separately, by <see cref="SensorMonitor"/>).</summary>
public sealed record MotherboardInfo(string? Name, string? BiosVersion, string? BiosDate);

/// <summary>Reads board name and BIOS version via WMI (Win32_BaseBoard / Win32_BIOS). Best effort:
/// any query that fails or returns junk leaves that field null. WMI can stall on some machines, so
/// callers run this on a background thread with their own timeout.</summary>
public static class MotherboardInfoReader
{
    public static MotherboardInfo Read()
    {
        string? name = null;
        string? biosVersion = null;
        string? biosDate = null;

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (var board in searcher.Get().Cast<ManagementObject>())
            {
                name = BuildName(board["Manufacturer"] as string, board["Product"] as string);
                break;
            }
        }
        catch (Exception)
        {
            // leave null
        }

        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
            foreach (var bios in searcher.Get().Cast<ManagementObject>())
            {
                biosVersion = Clean(bios["SMBIOSBIOSVersion"] as string);
                if (bios["ReleaseDate"] is string raw && raw.Length > 0)
                {
                    biosDate = ManagementDateTimeConverter.ToDateTime(raw).ToString("yyyy-MM-dd");
                }

                break;
            }
        }
        catch (Exception)
        {
            // leave null
        }

        return new MotherboardInfo(name, biosVersion, biosDate);
    }

    internal static string? BuildName(string? manufacturer, string? product)
    {
        manufacturer = Clean(manufacturer);
        product = Clean(product);
        if (manufacturer is null) return product;
        if (product is null) return manufacturer;

        // Some boards already include the vendor in the product string.
        return product.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase) ? product : $"{manufacturer} {product}";
    }

    /// <summary>Null for empty strings and the placeholder junk some OEM boards report.</summary>
    internal static string? Clean(string? value)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        return value.Equals("To Be Filled By O.E.M.", StringComparison.OrdinalIgnoreCase)
               || value.Equals("Default string", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;
    }
}
