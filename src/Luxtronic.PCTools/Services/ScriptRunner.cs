using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace Luxtronic.PCTools.Services;

/// <summary>A PowerShell script that ships inside the exe (Scripts/*.ps1, embedded resources).</summary>
public sealed record ScriptInfo(string FileName, string Name, string Description, string Text);

/// <summary>
/// The built-in script library and the runner for it. Scripts are embedded in the exe - nothing is
/// downloaded or read from the server. Each script starts with "# Name:" and "# Description:" lines.
/// A script is written to a temp file and run with a fresh powershell.exe (the app is already
/// elevated, so the child inherits admin); output is streamed line by line.
/// </summary>
public static class ScriptRunner
{
    private const string Prefix = "scripts/";

    public static IReadOnlyList<ScriptInfo> LoadAll()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var list = new List<ScriptInfo>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd().Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            var fileName = resource[Prefix.Length..];
            list.Add(new ScriptInfo(fileName, Header(text, "Name") ?? Path.GetFileNameWithoutExtension(fileName), Header(text, "Description") ?? "", text));
        }

        return list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? Header(string text, string key)
    {
        var marker = $"# {key}:";
        foreach (var line in text.Split('\n').Take(6))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[marker.Length..].Trim();
            }
        }

        return null;
    }

    /// <summary>Runs the script and returns its exit code. Cancelling kills the PowerShell process tree.</summary>
    public static async Task<int> RunAsync(ScriptInfo script, Action<string> onLine, CancellationToken ct)
    {
        var file = Path.Combine(Path.GetTempPath(), $"luxtronic-{Guid.NewGuid():N}.ps1");
        // UTF-8 with BOM so Windows PowerShell 5.1 reads non-ASCII text correctly.
        await File.WriteAllTextAsync(file, script.Text, new UTF8Encoding(true), CancellationToken.None);
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file })
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onLine("ERROR: " + e.Data); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            using var registration = ct.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception) { /* already exited */ }
            });
            await process.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
        finally
        {
            try { File.Delete(file); }
            catch (Exception) { /* temp file, best effort */ }
        }
    }
}
