using System.Management;

namespace Luxtronic.PCTools.Services;

/// <summary>One physical SSD's identity (from SsdSmartReader) plus whichever drive letters WMI
/// could resolve it to - built for the SSD test's drive picker, so the technician selects a
/// physical drive by model/serial (same identity already shown in the "Storage (S.M.A.R.T.)"
/// summary) rather than a bare, unlabeled letter.</summary>
public sealed record SsdDriveOption(SsdSmartInfo Drive, IReadOnlyList<string> DriveLetters)
{
    /// <summary>ComboBox display text - "no writable volume found" is shown rather than an empty
    /// list so the technician can tell "this drive has no mounted filesystem DiskSpd could target"
    /// apart from "the picker is just empty/broken".</summary>
    public string DisplayText => DriveLetters.Count > 0
        ? $"{Drive.Model} ({Drive.SerialNumber ?? "no serial"}) - {string.Join(", ", DriveLetters)}"
        : $"{Drive.Model} ({Drive.SerialNumber ?? "no serial"}) - no writable volume found";
}

/// <summary>
/// Maps each physical drive to its mounted drive letter(s) via WMI (Win32_DiskDrive ->
/// Win32_DiskDriveToDiskPartition -> Win32_LogicalDiskToPartition -> Win32_LogicalDisk), so
/// DiskSpdRunner - which needs a real filesystem path, never a raw device, per PROJECT_PLAN.md's
/// "no destructive SSD write-stress testing" constraint - can target the same physical drive the
/// technician picked by model/serial in SsdSmartReader's list.
///
/// Matches primarily by MODEL, not serial - a real live finding, not a guess.
/// <c>Win32_DiskDrive.SerialNumber</c> for NVMe drives is NOT the same value LHM/DiskInfoToolkit
/// reports: ground-truthed directly on a real machine (Crucial CT1000P2SSD8) via
/// <c>Get-WmiObject Win32_DiskDrive</c> - WMI returned <c>"6479_A7FF_F000_0009."</c> (raw
/// underscore-grouped hex byte pairs) while LHM/the picker showed <c>"2050E4D9C945"</c> (the
/// properly ASCII-decoded NVMe identify-namespace serial, per the NVMe spec). These are two
/// different encodings of the same underlying bytes, not a formatting quirk any amount of
/// trim/case-normalization can bridge - <see cref="SerialsLikelyMatch"/>'s substring fallback
/// correctly found zero overlap between them. <c>Win32_DiskDrive.Model</c>, by contrast, matched
/// LHM's reported model exactly for both real NVMe drives on that same machine
/// ("CT1000P2SSD8", "Micron_2210_MTFDHBA512QFD") - confirmed via the same live WMI query. So
/// model is the primary correlation key here; the (unreliable-for-NVMe, unconfirmed-for-ATA/SATA)
/// serial match is only used as a tiebreaker if two physical drives share an identical model
/// string, and if it can't disambiguate, the drive is left with no matched letters rather than
/// risking DiskSpd targeting the WRONG physical drive - safety over completeness.
///
/// Best-effort otherwise, matching PROJECT_PLAN.md §8's "log what's available, don't block on
/// missing serials" approach already used for the motherboard serial in SensorMonitor: a drive
/// that can't be resolved to any letter (WMI itself unavailable, ambiguous same-model drives, or
/// the drive genuinely has no mounted volume) still shows up in the picker with an explicit "no
/// writable volume found" note rather than being silently dropped - the technician isn't blocked
/// from typing a path manually in that case (see MainWindow's SSD target folder box).
/// </summary>
public static class DriveLetterResolver
{
    public static IReadOnlyList<SsdDriveOption> Resolve(IReadOnlyList<SsdSmartInfo> drives)
    {
        var modelByDiskIndex = new Dictionary<uint, string>();
        var serialByDiskIndex = new Dictionary<uint, string>();
        var lettersByDiskIndex = new Dictionary<uint, List<string>>();

        try
        {
            using var diskSearcher = new ManagementObjectSearcher("SELECT Index, Model, SerialNumber FROM Win32_DiskDrive");
            diskSearcher.Options.Timeout = TimeSpan.FromSeconds(10);
            foreach (ManagementObject disk in diskSearcher.Get())
            {
                var index = (uint)disk["Index"];
                var model = (disk["Model"] as string)?.Trim();
                if (!string.IsNullOrEmpty(model))
                {
                    modelByDiskIndex[index] = model;
                }

                var serial = (disk["SerialNumber"] as string)?.Trim();
                if (!string.IsNullOrEmpty(serial))
                {
                    serialByDiskIndex[index] = serial;
                }

                lettersByDiskIndex[index] = ResolveLettersForDisk(index);
            }
        }
        catch
        {
            // WMI unavailable/blocked - every drive falls back to "no writable volume found" in
            // BuildOptions below, same "best-effort, don't block" approach as the motherboard
            // serial lookup in SensorMonitor.
        }

        return BuildOptions(drives, modelByDiskIndex, serialByDiskIndex, lettersByDiskIndex);
    }

    private static List<string> ResolveLettersForDisk(uint diskIndex)
    {
        var letters = new List<string>();
        try
        {
            // NOTE: no WQL-level backslash-doubling needed here, despite that being the commonly
            // repeated advice for ASSOCIATORS OF queries - ground-truthed live on this dev machine
            // (Get-CimInstance -Query with a doubled path returned zero results/"Not found"; the
            // plain single-escaped device path "\\.\PHYSICALDRIVEn" - i.e. what the device path
            // actually looks like - is what ManagementObjectSearcher needs). Confirmed against
            // real PHYSICALDRIVE0/1 on this machine.
            using var partitionSearcher = new ManagementObjectSearcher(
                $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='\\\\.\\PHYSICALDRIVE{diskIndex}'}} " +
                "WHERE AssocClass = Win32_DiskDriveToDiskPartition");
            partitionSearcher.Options.Timeout = TimeSpan.FromSeconds(10);
            foreach (ManagementObject partition in partitionSearcher.Get())
            {
                using var logicalSearcher = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} " +
                    "WHERE AssocClass = Win32_LogicalDiskToPartition");
                logicalSearcher.Options.Timeout = TimeSpan.FromSeconds(10);
                foreach (ManagementObject logicalDisk in logicalSearcher.Get())
                {
                    if (logicalDisk["DeviceID"] is string letter)
                    {
                        letters.Add(letter);
                    }
                }
            }
        }
        catch
        {
            // Best-effort per-disk - a failure here just leaves this one disk with no letters.
        }

        return letters;
    }

    /// <summary>Pure matching/assembly logic, split out from <see cref="Resolve"/> so it's unit
    /// testable without real WMI. Model is the primary key (see class remarks for why serial
    /// isn't); <paramref name="serialByDiskIndex"/> is only consulted to disambiguate two disks
    /// that share an identical model string.</summary>
    internal static IReadOnlyList<SsdDriveOption> BuildOptions(
        IReadOnlyList<SsdSmartInfo> drives,
        IReadOnlyDictionary<uint, string> modelByDiskIndex,
        IReadOnlyDictionary<uint, string> serialByDiskIndex,
        IReadOnlyDictionary<uint, List<string>> lettersByDiskIndex)
    {
        var options = new List<SsdDriveOption>();
        foreach (var drive in drives)
        {
            var modelMatches = modelByDiskIndex
                .Where(kv => ValuesLikelyMatch(drive.Model, kv.Value))
                .Select(kv => kv.Key)
                .ToList();

            uint? chosenIndex = modelMatches.Count switch
            {
                1 => modelMatches[0],
                > 1 => DisambiguateBySerial(modelMatches, drive.SerialNumber, serialByDiskIndex),
                _ => null,
            };

            var letters = chosenIndex is uint index && lettersByDiskIndex.TryGetValue(index, out var found)
                ? found
                : new List<string>();

            options.Add(new SsdDriveOption(drive, letters));
        }

        return options;
    }

    /// <summary>Only reached when 2+ physical drives report the identical model string (rare -
    /// e.g. two matching SSDs in one machine). Falls back to the (for NVMe, confirmed-unreliable -
    /// see class remarks) serial match anyway, since it's still strictly better than guessing; if
    /// it can't pick a single candidate, returns null (no letters shown) rather than risking
    /// DiskSpd targeting the wrong physical drive.</summary>
    private static uint? DisambiguateBySerial(
        IReadOnlyList<uint> candidates, string? wantedSerial, IReadOnlyDictionary<uint, string> serialByDiskIndex)
    {
        if (wantedSerial is null)
        {
            return null;
        }

        uint? match = null;
        foreach (var index in candidates)
        {
            if (serialByDiskIndex.TryGetValue(index, out var serial) && ValuesLikelyMatch(wantedSerial, serial))
            {
                if (match is not null)
                {
                    return null; // more than one serial-matched candidate - genuinely ambiguous
                }

                match = index;
            }
        }

        return match;
    }

    /// <summary>Trimmed, alphanumeric-only, case-insensitive equality-or-substring comparison -
    /// shared by model matching (the primary, confirmed-reliable key) and serial matching (the
    /// tiebreaker, confirmed-unreliable for NVMe - see class remarks). Substring containment
    /// handles one side carrying extra vendor-prefix/padding text rather than an exact match.</summary>
    internal static bool ValuesLikelyMatch(string a, string b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na.Length == 0 || nb.Length == 0)
        {
            return false;
        }

        return na == nb || na.Contains(nb) || nb.Contains(na);
    }

    private static string Normalize(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}
