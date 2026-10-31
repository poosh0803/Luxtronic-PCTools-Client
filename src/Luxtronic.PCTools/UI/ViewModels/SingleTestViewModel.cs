using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Luxtronic.PCTools.Models;
using Luxtronic.PCTools.Services;
using Luxtronic.PCTools.Services.Shell;

namespace Luxtronic.PCTools.UI.ViewModels;

/// <summary>
/// One test's own tab. Runs that single test on this PC only - the controller used here is forced
/// offline, so nothing is sent to the server. (The Run Tests page is the one that submits.)
/// Derived classes say which test it is and how to start it.
/// </summary>
public abstract partial class SingleTestViewModel : ObservableObject
{
    private readonly ControllerProvider _controllers;
    private readonly RunState _state;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _startedAt;

    protected SingleTestViewModel(
        MachineService machine, ControllerProvider controllers, LogService log, AppSettingsProvider settings, RunState state)
    {
        Machine = machine;
        _controllers = controllers;
        Log = log;
        Settings = settings;
        _state = state;

        _timer.Tick += (_, _) => UpdateProgress();
        state.PropertyChanged += OnDependencyChanged;
        machine.PropertyChanged += OnDependencyChanged;
        controllers.Changed += RefreshText; // raised after Settings are saved
    }

    protected MachineService Machine { get; }
    protected LogService Log { get; }
    protected AppSettingsProvider Settings { get; }

    public MachineService MachineState => Machine;

    public abstract string Title { get; }
    public abstract string ToolName { get; }
    protected abstract TestKind Kind { get; }
    protected abstract string Component { get; }

    /// <summary>Run length from Settings, or null when the test has no fixed duration (SSD).</summary>
    protected abstract int? DurationMinutes { get; }

    public virtual string Description => "";

    public virtual IReadOnlyList<LiveStat>? LiveStats => null;
    public bool HasLiveStats => LiveStats is not null;
    public virtual bool ShowRamReadout => false;
    public virtual bool HasDrivePicker => false;

    public string DurationText => DurationMinutes is int m ? $"{m} min (change in Settings)" : "Runs until both passes finish";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _elapsedText = "";
    [ObservableProperty] private string _percentText = "";
    [ObservableProperty] private string _remainingText = "";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private bool _progressIndeterminate;
    [ObservableProperty] private string _resultText = "";

    [ObservableProperty] private bool _stopRequested;

    public bool CanEdit => !IsRunning;
    public bool HasResult => ResultText.Length > 0;

    partial void OnResultTextChanged(string value) => OnPropertyChanged(nameof(HasResult));

    /// <summary>Runs the test on <paramref name="local"/>. <paramref name="log"/> prefixes lines with the test name.</summary>
    protected abstract Task RunCoreAsync(TestSessionController local, CreateSessionRequest request, Action<string> log);

    protected virtual string? ValidateBeforeStart() => null;

    protected void RefreshText()
    {
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(Description));
    }

    private void OnDependencyChanged(object? sender, PropertyChangedEventArgs e) => StartCommand.NotifyCanExecuteChanged();

    private bool CanStart() => !_state.IsBusy && Machine.SensorsHealthy && Machine.IsInitialized;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (ValidateBeforeStart() is { } problem)
        {
            System.Windows.MessageBox.Show(problem, Title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        var local = _controllers.GetLocal();
        var request = new CreateSessionRequest
        {
            MoboSerial = Machine.RealMoboSerial ?? "local",
            SessionType = SessionType.Repair,
        };

        ResultText = "";
        StopRequested = false;
        IsRunning = true;
        _state.Begin();
        _startedAt = DateTime.Now;
        ProgressIndeterminate = DurationMinutes is null;
        ProgressPercent = 0;
        UpdateProgress();
        _timer.Start();

        Log.Append($"[{Title}] Starting - local run, results are NOT submitted to the server.");
        try
        {
            await RunCoreAsync(local, request, msg => Log.Append($"[{Title}] {msg}"));
        }
        catch (Exception ex)
        {
            Log.Append($"[{Title}] Test ended with an error: {ex.Message}");
            ResultText = $"The test could not run: {ex.Message}";
        }
        finally
        {
            _timer.Stop();
            ProgressIndeterminate = false;
            if (ResultText.Length == 0)
            {
                ResultText = DescribeResult(local.LastRun);
            }

            IsRunning = false;
            _state.End();
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop()
    {
        _controllers.RequestStopAll();
        StopRequested = true;
        Log.Append($"[{Title}] Stop requested - finishing up...");
    }

    private void UpdateProgress()
    {
        var elapsed = DateTime.Now - _startedAt;
        if (DurationMinutes is int minutes && minutes > 0)
        {
            var total = TimeSpan.FromMinutes(minutes);
            ProgressPercent = Math.Min(100, elapsed.TotalSeconds / total.TotalSeconds * 100);
            PercentText = $"{(int)ProgressPercent}%";
            ElapsedText = $"Elapsed {Format(elapsed)} of {Format(total)}";
            var remaining = total - elapsed;
            RemainingText = remaining > TimeSpan.Zero ? $"{Format(remaining)} remaining" : "finishing up...";
        }
        else
        {
            PercentText = "";
            ElapsedText = $"Elapsed {Format(elapsed)}";
            RemainingText = "";
        }
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";

    /// <summary>Plain-English outcome of the last run, shown on the tab only - never sent anywhere.</summary>
    private string DescribeResult(LocalRunSummary? run)
    {
        if (run is null)
        {
            return "No result was produced.";
        }

        var status = run.StopReason switch
        {
            null => "Completed",
            StopReason.UserAbort => "Stopped by you before it finished",
            StopReason.ToolCrash => "The test tool crashed or could not start",
            StopReason.ClientError => "Stopped because of a sensor or app error",
            var other => $"Stopped ({other})",
        };

        var parts = new List<string> { status };
        if (Number(run, "error_count") is double errors)
        {
            parts.Add(errors == 0 ? "no errors" : $"{errors:F0} error(s) found");
        }

        if (Number(run, "max_temp_c") is double temp)
        {
            parts.Add($"peak temperature {temp:F0} °C");
        }

        if (Number(run, "min_seq_read_mb_s") is double read)
        {
            parts.Add($"sequential read {read:F0} MB/s");
        }

        if (Number(run, "min_seq_write_mb_s") is double write)
        {
            parts.Add($"sequential write {write:F0} MB/s");
        }

        return string.Join(" - ", parts) + ".";
    }

    private static double? Number(LocalRunSummary run, string key) =>
        run.Stats.TryGetValue(key, out var value) && value is not null ? Convert.ToDouble(value) : null;
}
