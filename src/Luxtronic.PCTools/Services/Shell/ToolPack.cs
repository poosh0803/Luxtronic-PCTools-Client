using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace Luxtronic.PCTools.Services.Shell;

/// <summary>
/// The bundled third-party test tools (Prime95, FurMark, TestMem5, DiskSpd, HWiNFO) ship inside
/// the exe as one embedded zip (built by the BuildToolPack target in the csproj) and are unpacked
/// on first run to %LocalAppData%\LuxtronicPCTools\tools\pack-&lt;size&gt;. The pack id is the
/// embedded zip's length, so a new build with different tools unpacks into a fresh folder and old
/// folders are removed. A copy is needed (rather than running from memory) because the tools are
/// separate processes and several write beside themselves (TM5's Log.txt, Prime95's prime.txt).
///
/// A dev build made without a tools folder has no embedded pack; <see cref="Resolve"/> then falls
/// back to walking up from the exe looking for a repo-root "tools" folder, as the old client did.
/// </summary>
public static class ToolPack
{
    private const string ResourceName = "toolpack.zip";
    private const string CompleteMarker = ".complete";

    private static readonly string ToolsBase = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LuxtronicPCTools", "tools");

    private static readonly Lazy<long?> PackLength = new(() =>
    {
        using var stream = OpenPack();
        return stream?.Length;
    });

    public static bool HasEmbeddedPack => PackLength.Value is not null;

    public static string Root => Path.Combine(ToolsBase, $"pack-{PackLength.Value}");

    /// <summary>Folder for one tool (e.g. "prime95", "FurMark_win64", "TestMem5", "DiskSpd", "hwi").</summary>
    public static string Resolve(string folder)
    {
        if (HasEmbeddedPack)
        {
            return Path.Combine(Root, folder);
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var probe = Path.Combine(dir.FullName, "tools", folder);
            if (Directory.Exists(probe))
            {
                return probe;
            }
        }

        // Nothing found: point at a sensible place so "X not found at ..." messages make sense.
        return Path.Combine(AppContext.BaseDirectory, "tools", folder);
    }

    /// <summary>Unpacks the embedded tools if this exact pack isn't already on disk. No-op without a pack.</summary>
    public static Task EnsureExtractedAsync(Action<string> log) => Task.Run(() =>
    {
        if (!HasEmbeddedPack)
        {
            log("No embedded tool pack in this build - using the repo's tools folder.");
            return;
        }

        var root = Root;
        if (File.Exists(Path.Combine(root, CompleteMarker)))
        {
            return;
        }

        log("Unpacking bundled test tools (first run of this build)...");
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true); // half-finished earlier attempt
        }

        using (var stream = OpenPack()!)
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
        {
            zip.ExtractToDirectory(root);
        }

        File.WriteAllText(Path.Combine(root, CompleteMarker), DateTime.UtcNow.ToString("O"));
        log($"Tools unpacked to {root}.");

        RemoveOldPacks(root, log);
    });

    private static void RemoveOldPacks(string keep, Action<string> log)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(ToolsBase, "pack-*"))
            {
                if (!string.Equals(dir, keep, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
        catch (Exception ex)
        {
            log($"NOTE: could not clean up an old tool pack: {ex.Message}"); // e.g. a tool from it is still running
        }
    }

    private static Stream? OpenPack() => Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
}
