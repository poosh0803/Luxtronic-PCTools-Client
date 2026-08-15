using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Luxtronic.PCTools.Services;

/// <summary>
/// Loads appsettings.json (copied next to the exe on build/publish) and resolves the
/// relative paths it contains into absolute ones. Deliberately hand-rolled instead of
/// Microsoft.Extensions.Configuration to keep the dependency surface small for an
/// eventual self-contained single-file publish.
/// </summary>
public sealed class AppSettingsProvider
{
    public string ServerBaseUrl { get; }
    public string ApiKeyFilePath { get; }
    public string ToolsDirectory { get; }
    public string GpuToolsDirectory { get; }
    public string RamToolsDirectory { get; }
    public string SsdToolsDirectory { get; }
    public int TelemetrySampleIntervalMs { get; }

    private AppSettingsProvider(RawSettings raw, string baseDir)
    {
        ServerBaseUrl = raw.ServerBaseUrl.TrimEnd('/');
        ApiKeyFilePath = ResolvePath(baseDir, raw.ApiKeyFilePath);
        ToolsDirectory = ResolveToolsDirectory(baseDir, raw.ToolsDirectory);
        GpuToolsDirectory = ResolveToolsDirectory(baseDir, raw.GpuToolsDirectory);
        RamToolsDirectory = ResolveToolsDirectory(baseDir, raw.RamToolsDirectory);
        SsdToolsDirectory = ResolveToolsDirectory(baseDir, raw.SsdToolsDirectory);
        TelemetrySampleIntervalMs = raw.TelemetrySampleIntervalMs > 0 ? raw.TelemetrySampleIntervalMs : 500;
    }

    public static AppSettingsProvider Load()
    {
        var baseDir = AppContext.BaseDirectory;
        var path = Path.Combine(baseDir, "appsettings.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"appsettings.json not found next to the executable ({path}). " +
                "It should be copied to the output directory automatically on build - check the csproj.");
        }

        var json = File.ReadAllText(path);
        var raw = JsonSerializer.Deserialize<RawSettings>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidDataException("appsettings.json parsed to null.");

        return new AppSettingsProvider(raw, baseDir);
    }

    private static string ResolvePath(string baseDir, string configuredPath)
    {
        return Path.IsPathRooted(configuredPath) ? configuredPath : Path.Combine(baseDir, configuredPath);
    }

    /// <summary>
    /// Generic over whichever tools subfolder is configured (tools\prime95, tools\FurMark_win64,
    /// ...). In a real publish, the folder ships next to the exe. In dev, the exe lives several
    /// directories deep in bin\Debug\net8.0-windows\, so we also walk up looking for the folder
    /// (repo root) as a convenience - first match wins, base dir preferred.
    /// </summary>
    private static string ResolveToolsDirectory(string baseDir, string configuredRelativePath)
    {
        if (Path.IsPathRooted(configuredRelativePath))
        {
            return configuredRelativePath;
        }

        var candidate = Path.Combine(baseDir, configuredRelativePath);
        if (Directory.Exists(candidate))
        {
            return candidate;
        }

        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var probe = Path.Combine(dir.FullName, configuredRelativePath);
            if (Directory.Exists(probe))
            {
                return probe;
            }
        }

        // Nothing found anywhere - fall back to the path next to the exe so error messages
        // (e.g. "prime95.exe not found at X") point somewhere sensible.
        return candidate;
    }

    private sealed class RawSettings
    {
        [JsonPropertyName("ServerBaseUrl")]
        public string ServerBaseUrl { get; set; } = "http://192.168.68.255:7777";

        [JsonPropertyName("ApiKeyFilePath")]
        public string ApiKeyFilePath { get; set; } = "apikey.txt";

        [JsonPropertyName("ToolsDirectory")]
        public string ToolsDirectory { get; set; } = "tools\\prime95";

        [JsonPropertyName("GpuToolsDirectory")]
        public string GpuToolsDirectory { get; set; } = "tools\\FurMark_win64";

        [JsonPropertyName("RamToolsDirectory")]
        public string RamToolsDirectory { get; set; } = "tools\\TestMem5";

        /// <summary>DiskSpd only (DiskSpd64.exe + its MIT license notice) - originally shipped
        /// nested inside a full CrystalDiskMark install (tools\CrystalDiskMark\CdmResource\DiskSpd\),
        /// but CrystalDiskMark's own GUI (DiskMark64.exe) is never invoked (no CLI/automation
        /// surface exists on it at all - see DiskSpdRunner's class remarks), so the ~250 unused
        /// GUI/language/theme files were trimmed and just DiskSpd64.exe kept, moved to its own
        /// tools\DiskSpd\ folder so the directory name reflects what's actually used.</summary>
        [JsonPropertyName("SsdToolsDirectory")]
        public string SsdToolsDirectory { get; set; } = "tools\\DiskSpd";

        [JsonPropertyName("TelemetrySampleIntervalMs")]
        public int TelemetrySampleIntervalMs { get; set; } = 500;
    }
}
