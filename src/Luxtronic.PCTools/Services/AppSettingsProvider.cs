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
    public int TelemetrySampleIntervalMs { get; }

    private AppSettingsProvider(RawSettings raw, string baseDir)
    {
        ServerBaseUrl = raw.ServerBaseUrl.TrimEnd('/');
        ApiKeyFilePath = ResolvePath(baseDir, raw.ApiKeyFilePath);
        ToolsDirectory = ResolveToolsDirectory(baseDir, raw.ToolsDirectory);
        TelemetrySampleIntervalMs = raw.TelemetrySampleIntervalMs > 0 ? raw.TelemetrySampleIntervalMs : 1000;
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
    /// In a real publish, tools\prime95 ships next to the exe. In dev, the exe lives several
    /// directories deep in bin\Debug\net8.0-windows\, so we also walk up looking for a
    /// tools\prime95 folder (repo root) as a convenience - first match wins, base dir preferred.
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

        [JsonPropertyName("TelemetrySampleIntervalMs")]
        public int TelemetrySampleIntervalMs { get; set; } = 1000;
    }
}
