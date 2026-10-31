using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Luxtronic.PCTools.Services;

/// <summary>One downloadable app as listed by the server's GET /api/apps.</summary>
public sealed class AppInfo
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "app";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("file_name")] public string? FileName { get; set; }

    // Postgres BIGINT arrives as a JSON string.
    [JsonPropertyName("file_size")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? FileSize { get; set; }

    [JsonPropertyName("file_sha256")] public string? FileSha256 { get; set; }
    [JsonPropertyName("icon_mime")] public string? IconMime { get; set; }
    [JsonPropertyName("updated_at")] public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// Lists the apps the server offers and downloads them into
/// %LocalAppData%\LuxtronicPCTools\apps\&lt;id&gt;\. The server's apps endpoints need no API key
/// (LAN-trust, same as its dashboard), so this uses its own plain HttpClient and reads the server
/// URL fresh on each call.
/// </summary>
public sealed class AppCatalogService : IDisposable
{
    private readonly AppSettingsProvider _settings;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Apps and drivers each get their own folder under %LocalAppData%\LuxtronicPCTools.</summary>
    public static string DownloadRoot(string category) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LuxtronicPCTools",
        category switch { "driver" => "drivers", "tool" => "tools", _ => "apps" });

    /// <summary>List/grid choice, shared by the Apps, Drivers and Tools tabs (kept for the session).</summary>
    public bool PreferGridView { get; set; }

    public AppCatalogService(AppSettingsProvider settings) => _settings = settings;

    private string Url(string path) => _settings.ServerBaseUrl.TrimEnd('/') + path;

    public async Task<IReadOnlyList<AppInfo>> ListAsync(string category, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        var list = await _http.GetFromJsonAsync<List<AppInfo>>(Url($"/api/apps?category={category}"), cts.Token).ConfigureAwait(false);
        return list ?? new List<AppInfo>();
    }

    /// <summary>The icon bytes, or null when the app has none.</summary>
    public async Task<byte[]?> GetIconAsync(AppInfo app, CancellationToken ct = default)
    {
        if (app.IconMime is null) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        using var resp = await _http.GetAsync(Url($"/api/apps/{app.Id}/icon?v={app.UpdatedAt.Ticks}"), cts.Token).ConfigureAwait(false);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false) : null;
    }

    /// <summary>Where this version of the app is (or will be) stored; changes when the server's copy is updated.</summary>
    public static string LocalPath(AppInfo app) =>
        Path.Combine(DownloadRoot(app.Category), app.Id, app.FileSha256 is { Length: >= 12 } sha ? sha[..12] : "file", SafeName(app.FileName));

    /// <summary>
    /// Deletes every downloaded file of this category (all apps, or all drivers). Returns how many
    /// app folders were removed and how many could not be (e.g. an installer is still running).
    /// </summary>
    public static (int Removed, int Failed) ClearDownloads(string category)
    {
        var root = DownloadRoot(category);
        if (!Directory.Exists(root)) return (0, 0);

        int removed = 0, failed = 0;
        foreach (var dir in Directory.GetDirectories(root))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }
        return (removed, failed);
    }

    /// <summary>Removes older downloaded versions of this app (sibling version folders), keeping the current one.</summary>
    private static void DeleteOldVersions(string currentFile)
    {
        var versionDir = Path.GetDirectoryName(currentFile)!;
        var appDir = Path.GetDirectoryName(versionDir)!;
        foreach (var dir in Directory.GetDirectories(appDir))
        {
            if (string.Equals(dir, versionDir, StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* in use - leave it */ }
        }
    }

    public static bool IsDownloaded(AppInfo app) => app.FileName is not null && File.Exists(LocalPath(app));

    private static string SafeName(string? name)
    {
        var clean = string.Concat((name ?? "download").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return clean.Length == 0 ? "download" : clean;
    }

    /// <summary>
    /// Downloads the app, verifying the server's SHA-256, and returns the local path. Written to a
    /// temp file first so an interrupted download never looks like a finished one.
    /// </summary>
    public async Task<string> DownloadAsync(AppInfo app, IProgress<double>? progress, CancellationToken ct)
    {
        if (app.FileName is null) throw new InvalidOperationException("This app has no file on the server yet.");

        var dest = LocalPath(app);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var tmp = dest + ".part";

        try
        {
            using var resp = await _http.GetAsync(Url($"/api/apps/{app.Id}/download"), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? app.FileSize ?? 0;

            using var sha = SHA256.Create();
            await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = File.Create(tmp))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total * 100);
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            }

            var actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (app.FileSha256 is not null && !actual.Equals(app.FileSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Downloaded file does not match the server's checksum - try again.");

            File.Move(tmp, dest, overwrite: true);
            DeleteOldVersions(dest);
            return dest;
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public void Dispose() => _http.Dispose();
}
