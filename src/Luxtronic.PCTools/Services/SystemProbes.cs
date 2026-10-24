using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Luxtronic.PCTools.Services;

/// <summary>Whole-system RAM usage, for the RAM test's telemetry: while TestMem5 runs, "memory in
/// use" climbing and holding is the visible proof that it allocated what it is testing.</summary>
public static class SystemMemory
{
    public readonly record struct Snapshot(double UsedPercent, double UsedGb, double TotalGb);

    public static Snapshot? Read()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0)
        {
            return null;
        }

        const double gb = 1024d * 1024 * 1024;
        var used = status.TotalPhys - status.AvailPhys;
        return new Snapshot(used * 100d / status.TotalPhys, used / gb, status.TotalPhys / gb);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

/// <summary>
/// Live read/write throughput of one drive letter, from Windows' own LogicalDisk performance
/// counters. DiskSpd only prints its numbers when a pass ends, so this is what makes a live speed
/// readout possible while the benchmark is running - and it is independent of the tool, so it shows
/// what the drive is really doing.
/// </summary>
public sealed class DiskSpeedMonitor : IDisposable
{
    private readonly PerformanceCounter _read;
    private readonly PerformanceCounter _write;

    private DiskSpeedMonitor(PerformanceCounter read, PerformanceCounter write)
    {
        _read = read;
        _write = write;
    }

    /// <summary>Monitor for the drive holding <paramref name="targetFolder"/>, or null if its
    /// counters can't be opened (e.g. a network path, or performance counters disabled).</summary>
    public static DiskSpeedMonitor? TryCreate(string targetFolder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(targetFolder));
            if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':')
            {
                return null;
            }

            var instance = root[..2].ToUpperInvariant(); // "D:" - how LogicalDisk names its instances
            var read = new PerformanceCounter("LogicalDisk", "Disk Read Bytes/sec", instance, readOnly: true);
            var write = new PerformanceCounter("LogicalDisk", "Disk Write Bytes/sec", instance, readOnly: true);
            read.NextValue(); // rate counters need a first sample to measure against
            write.NextValue();
            return new DiskSpeedMonitor(read, write);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Current read and write speed in MB/s (decimal megabytes, matching DiskSpd's figures).</summary>
    public (double ReadMbS, double WriteMbS) Read() => (_read.NextValue() / 1_000_000d, _write.NextValue() / 1_000_000d);

    public void Dispose()
    {
        _read.Dispose();
        _write.Dispose();
    }
}
