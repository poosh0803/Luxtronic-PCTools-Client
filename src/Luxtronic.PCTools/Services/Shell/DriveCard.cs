namespace Luxtronic.PCTools.Services.Shell;

public sealed record DriveField(string Label, string Value);

/// <summary>One storage drive as shown on the Dashboard: a card with its identity and SMART health.</summary>
public sealed record DriveCard(string Model, string Bus, string HealthText, HealthLevel Level, IReadOnlyList<DriveField> Fields)
{
    public static DriveCard From(SsdSmartInfo d)
    {
        const string none = "--";

        var health = d.HealthPercent is double h ? $"{h:F0}%" : none;
        var level = d.HealthStatus?.ToLowerInvariant() switch
        {
            "good" => HealthLevel.Good,
            "caution" => HealthLevel.Warning,
            "bad" => HealthLevel.Bad,
            _ => d.HealthPercent switch
            {
                >= 50 => HealthLevel.Good,
                >= 20 => HealthLevel.Warning,
                < 20 => HealthLevel.Bad,
                _ => HealthLevel.Unknown,
            },
        };

        var healthText = d.HealthStatus is { Length: > 0 } status && d.HealthPercent is not null
            ? $"{health} - {status}"
            : d.HealthStatus ?? health;

        var fields = new List<DriveField>
        {
            new("Model", d.Model),
            new("Serial number", d.SerialNumber ?? none),
            new("Firmware", d.Firmware ?? none),
            new("Capacity", FormatCapacity(d.CapacityBytes)),
            new("Health", healthText),
            new("Temperature", d.TemperatureC is double t ? $"{t:F0} °C" : none),
            new("Power-on hours", d.PowerOnHours is long hours ? $"{hours:N0} h" : none),
            new("Power-on count", d.PowerOnCount is long count ? $"{count:N0}" : none),
            new("Total host reads", d.HostReadsGb is ulong r ? $"{r:N0} GB" : none),
            new("Total host writes", d.HostWritesGb is ulong w ? $"{w:N0} GB" : none),
        };

        return new DriveCard(d.Model, d.IsNvme ? "NVMe" : "SATA / ATA", healthText, level, fields);
    }

    private static string FormatCapacity(ulong? bytes)
    {
        if (bytes is not ulong b)
        {
            return "--";
        }

        var gb = b / 1_000_000_000d;
        return gb >= 1000 ? $"{gb / 1000:0.##} TB" : $"{gb:F0} GB";
    }
}

public enum HealthLevel { Unknown, Good, Warning, Bad }
