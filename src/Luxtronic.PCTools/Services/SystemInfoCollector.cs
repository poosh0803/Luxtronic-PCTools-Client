using System.Management;

namespace Luxtronic.PCTools.Services;

/// <summary>
/// Gathers the PC's specification (CPU, graphics, memory modules, motherboard, drives, OS) into the
/// "system_info" object the server stores with each session, so a customer reading the report can
/// see what machine it is about. Everything is best effort: a WMI query that fails or returns
/// junk just leaves that part out - the server's formatter skips what isn't there. WMI can stall on
/// some machines, so callers run this on a background thread with their own timeout.
///
/// Key names are the contract with the server's src/lib/systemInfo.js - keep them in sync.
/// </summary>
public static class SystemInfoCollector
{
    public static Dictionary<string, object> Collect(
        MotherboardInfo board, string? boardSerial, IReadOnlyList<SsdSmartInfo> drives,
        string? gpuNameFromHwInfo, double? gpuVramMb)
    {
        var info = new Dictionary<string, object>();

        if (ReadCpu() is { } cpu) info["cpu"] = cpu;

        var gpus = ReadGpus(gpuNameFromHwInfo, gpuVramMb);
        if (gpus.Count > 0) info["gpu"] = gpus;

        if (ReadMemory() is { } memory) info["memory"] = memory;

        var boardInfo = new Dictionary<string, object>();
        Put(boardInfo, "name", board.Name);
        Put(boardInfo, "serial", boardSerial);
        Put(boardInfo, "bios_version", board.BiosVersion);
        Put(boardInfo, "bios_date", board.BiosDate);
        if (boardInfo.Count > 0) info["motherboard"] = boardInfo;

        var storage = drives.Select(DescribeDrive).ToList();
        if (storage.Count > 0) info["storage"] = storage;

        if (ReadOs() is { } os) info["os"] = os;

        return info;
    }

    private static Dictionary<string, object>? ReadCpu()
    {
        foreach (var row in Query("SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor"))
        {
            var cpu = new Dictionary<string, object>();
            Put(cpu, "name", Clean(row["Name"] as string));
            Put(cpu, "cores", ToDouble(row["NumberOfCores"]));
            Put(cpu, "threads", ToDouble(row["NumberOfLogicalProcessors"]));
            Put(cpu, "base_clock_mhz", ToDouble(row["MaxClockSpeed"]));
            return cpu.Count > 0 ? cpu : null; // first physical CPU only - these are single-socket machines
        }

        return null;
    }

    private static List<Dictionary<string, object>> ReadGpus(string? hwInfoName, double? hwInfoVramMb)
    {
        var gpus = new List<Dictionary<string, object>>();
        foreach (var row in Query("SELECT Name, DriverVersion FROM Win32_VideoController"))
        {
            var name = Clean(row["Name"] as string);
            if (name is null || IsVirtualAdapter(name))
            {
                continue;
            }

            var gpu = new Dictionary<string, object> { ["name"] = name };
            Put(gpu, "driver_version", Clean(row["DriverVersion"] as string));

            // WMI's AdapterRAM is a 32-bit field that caps at 4 GB, so it is wrong for modern cards.
            // HWiNFO's figure is only used for the card it named (matching either way round).
            if (hwInfoVramMb is double mb && hwInfoName is not null &&
                (name.Contains(hwInfoName, StringComparison.OrdinalIgnoreCase) ||
                 hwInfoName.Contains(name, StringComparison.OrdinalIgnoreCase)))
            {
                gpu["vram_gb"] = Math.Round(mb / 1024d, 1);
            }

            gpus.Add(gpu);
        }

        return gpus;
    }

    private static bool IsVirtualAdapter(string name) =>
        name.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Remote", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Parsec", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, object>? ReadMemory()
    {
        var modules = new List<Dictionary<string, object>>();
        double totalGb = 0;
        foreach (var row in Query(
                     "SELECT DeviceLocator, BankLabel, Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, SMBIOSMemoryType FROM Win32_PhysicalMemory"))
        {
            var sizeGb = ToDouble(row["Capacity"]) is double bytes ? Math.Round(bytes / (1024d * 1024 * 1024), 1) : (double?)null;
            var module = new Dictionary<string, object>();
            Put(module, "slot", Clean(row["DeviceLocator"] as string) ?? Clean(row["BankLabel"] as string));
            Put(module, "size_gb", sizeGb);
            Put(module, "type", MemoryTypeName(ToDouble(row["SMBIOSMemoryType"])));
            Put(module, "speed_mhz", ToDouble(row["ConfiguredClockSpeed"]) is > 0 and var cfg ? cfg : ToDouble(row["Speed"]));
            Put(module, "manufacturer", Clean(row["Manufacturer"] as string));
            Put(module, "part_number", Clean(row["PartNumber"] as string));
            modules.Add(module);
            totalGb += sizeGb ?? 0;
        }

        if (modules.Count == 0)
        {
            return null;
        }

        return new Dictionary<string, object> { ["total_gb"] = Math.Round(totalGb, 1), ["modules"] = modules };
    }

    // SMBIOS "memory type" codes (the ones that matter for desktops/laptops).
    private static string? MemoryTypeName(double? code) => code switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        _ => null,
    };

    private static Dictionary<string, object>? ReadOs()
    {
        foreach (var row in Query("SELECT Caption, Version, OSArchitecture FROM Win32_OperatingSystem"))
        {
            var os = new Dictionary<string, object>();
            Put(os, "name", Clean(row["Caption"] as string));
            Put(os, "version", Clean(row["Version"] as string));
            Put(os, "architecture", Clean(row["OSArchitecture"] as string));
            return os.Count > 0 ? os : null;
        }

        return null;
    }

    private static Dictionary<string, object> DescribeDrive(SsdSmartInfo d)
    {
        var drive = new Dictionary<string, object> { ["model"] = d.Model, ["bus"] = d.IsNvme ? "NVMe" : "SATA" };
        Put(drive, "serial", d.SerialNumber);
        Put(drive, "firmware", d.Firmware);
        if (d.CapacityBytes is ulong bytes) drive["capacity_gb"] = Math.Round(bytes / 1_000_000_000d, 0); // drive makers count in decimal GB
        Put(drive, "health_percent", d.HealthPercent);
        Put(drive, "power_on_hours", d.PowerOnHours);
        Put(drive, "power_on_count", d.PowerOnCount);
        if (d.HostReadsGb is ulong r) drive["host_reads_gb"] = r;
        if (d.HostWritesGb is ulong w) drive["host_writes_gb"] = w;
        return drive;
    }

    private static IEnumerable<ManagementObject> Query(string wql)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            return searcher.Get().Cast<ManagementObject>().ToList();
        }
        catch (Exception)
        {
            return Array.Empty<ManagementObject>();
        }
    }

    private static void Put(Dictionary<string, object> target, string key, object? value)
    {
        if (value is not null && value is not "")
        {
            target[key] = value;
        }
    }

    private static string? Clean(string? value) => MotherboardInfoReader.Clean(value);

    private static double? ToDouble(object? value)
    {
        try
        {
            return value is null ? null : Convert.ToDouble(value);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
