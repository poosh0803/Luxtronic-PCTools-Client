using System.Collections.ObjectModel;
using System.Windows;
using Serilog;

namespace Luxtronic.PCTools.Services.Shell;

public sealed record LogEntry(DateTime Time, string Message)
{
    public string Display => $"[{Time:HH:mm:ss}] {Message}";
}

/// <summary>
/// App-wide status log: what the old window's LogBox showed, but shared between pages and also
/// written to the Serilog file. <see cref="Append"/> is safe to call from any thread (the test
/// poll loops call it from background threads).
/// </summary>
public sealed class LogService
{
    private const int MaxEntries = 5000;

    public ObservableCollection<LogEntry> Entries { get; } = new();

    public void Append(string message)
    {
        Log.Information("{Message}", message);
        var entry = new LogEntry(DateTime.Now, message);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Add(entry);
        }
        else
        {
            dispatcher.BeginInvoke(() => Add(entry));
        }
    }

    public void Clear() => Entries.Clear();

    private void Add(LogEntry entry)
    {
        Entries.Add(entry);
        while (Entries.Count > MaxEntries)
        {
            Entries.RemoveAt(0);
        }
    }
}
