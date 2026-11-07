using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Luxtronic.PCTools.Services;
using Luxtronic.PCTools.Services.Shell;

namespace Luxtronic.PCTools.UI.ViewModels;

/// <summary>The Scripts page: pick one of the PowerShell scripts built into the app, run it, watch the output.</summary>
public sealed partial class ScriptsViewModel : ObservableObject
{
    private readonly LogService _log;
    private CancellationTokenSource? _cts;

    public ScriptsViewModel(LogService log)
    {
        _log = log;
        Scripts = ScriptRunner.LoadAll();
        Selected = Scripts.FirstOrDefault();
    }

    public IReadOnlyList<ScriptInfo> Scripts { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScriptText), nameof(Description))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private ScriptInfo? _selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPick))]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _output = "";
    [ObservableProperty] private string _status = "";

    public string ScriptText => Selected?.Text ?? "";
    public string Description => Selected?.Description ?? "";
    public bool CanPick => !IsRunning;

    private bool CanRun() => Selected is not null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        var script = Selected!;
        _cts = new CancellationTokenSource();
        IsRunning = true;
        Output = "";
        Status = $"Running \"{script.Name}\"...";
        _log.Append($"Script started: {script.Name}");

        var buffer = new StringBuilder();
        var started = DateTime.Now;
        try
        {
            var code = await ScriptRunner.RunAsync(script, line =>
            {
                string snapshot;
                lock (buffer)
                {
                    buffer.AppendLine(line);
                    snapshot = buffer.ToString();
                }

                Application.Current.Dispatcher.BeginInvoke(() => Output = snapshot);
            }, _cts.Token);
            Status = code == 0 ? $"Finished in {(DateTime.Now - started).TotalSeconds:F0}s." : $"Finished with exit code {code}.";
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
        }
        catch (Exception ex)
        {
            Status = $"Could not run the script: {ex.Message}";
        }
        finally
        {
            lock (buffer) Output = buffer.ToString();
            _log.Append($"Script ended: {script.Name} - {Status}");
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void CopyOutput()
    {
        if (Output.Length > 0)
        {
            Clipboard.SetText(Output);
        }
    }
}
