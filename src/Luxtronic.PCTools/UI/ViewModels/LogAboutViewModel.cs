using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Luxtronic.PCTools.Services.Shell;

namespace Luxtronic.PCTools.UI.ViewModels;

public sealed partial class LogAboutViewModel : ObservableObject
{
    private readonly LogService _log;

    public LogAboutViewModel(LogService log) => _log = log;

    public ObservableCollection<LogEntry> Entries => _log.Entries;

    public string Version { get; } =
        typeof(LogAboutViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "2.0.0";

    public string LogFolder => App.LogDirectory;

    [RelayCommand]
    private void Clear() => _log.Clear();

    [RelayCommand]
    private void Copy()
    {
        if (_log.Entries.Count > 0)
        {
            Clipboard.SetText(string.Join(Environment.NewLine, _log.Entries.Select(e => e.Display)));
        }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        Directory.CreateDirectory(App.LogDirectory);
        Process.Start(new ProcessStartInfo(App.LogDirectory) { UseShellExecute = true });
    }
}
