using System.ComponentModel;
using System.Windows.Threading;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Luxtronic.PCTools.Models;
using Luxtronic.PCTools.Services;
using Luxtronic.PCTools.Services.Shell;

namespace Luxtronic.PCTools.UI.ViewModels;

/// <summary>
/// The Run Tests page: pick any combination of tests and run them one after another inside a single
/// server session, submitting every result (the single-test tabs, by contrast, never submit). Tests
/// always run in a fixed order - CPU, GPU, VRAM, RAM, SSD - and never overlap, which also satisfies
/// the server's rule that RAM and SSD run alone.
/// </summary>
public sealed partial class RunTestsViewModel : ObservableObject
{
    private readonly ControllerProvider _controllers;
    private readonly LogService _log;
    private readonly AppSettingsProvider _settings;
    private readonly RunState _state;

    public RunTestsViewModel(
        MachineService machine, ControllerProvider controllers, LogService log, AppSettingsProvider settings, RunState state)
    {
        Machine = machine;
        _controllers = controllers;
        _log = log;
        _settings = settings;
        _state = state;

        machine.Initialized += OnMachineInitialized;
        controllers.Changed += RefreshDurations; // raised after Settings are saved
        state.PropertyChanged += OnDependencyChanged;
        machine.PropertyChanged += OnDependencyChanged;
        _stepTimer.Tick += (_, _) => UpdateStepProgress();
        RefreshDurations();
        if (machine.IsInitialized) OnMachineInitialized();
    }

    public MachineService Machine { get; }

    // Session form
    [ObservableProperty] private string _customerName = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private bool _isNewBuild = true;
    public bool IsRepair { get => !IsNewBuild; set => IsNewBuild = !value; }
    partial void OnIsNewBuildChanged(bool value) => OnPropertyChanged(nameof(IsRepair));

    // Which tests to include (several allowed)
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartCommand))] private bool _includeCpu = true;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartCommand))] private bool _includeGpu;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartCommand))] private bool _includeVram;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartCommand))] private bool _includeRam;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(StartCommand))] private bool _includeSsd;

    // Durations shown next to each test (set in Settings)
    [ObservableProperty] private string _cpuDuration = "";
    [ObservableProperty] private string _gpuDuration = "";
    [ObservableProperty] private string _vramDuration = "";
    [ObservableProperty] private string _ramDuration = "";

    // SSD picker
    public IReadOnlyList<SsdDriveOption> DriveOptions => Machine.DriveOptions;
    [ObservableProperty] private SsdDriveOption? _selectedDrive;
    [ObservableProperty] private string _targetFolder = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _isRunning;

    // Progress of the test that is running right now (the main bar), plus an overall bar across all ticked tests.
    [ObservableProperty] private string _stepText = "";
    [ObservableProperty] private string _stepCounterText = "";
    [ObservableProperty] private double _stepProgress;
    [ObservableProperty] private bool _stepIndeterminate;
    [ObservableProperty] private string _stepPercentText = "";
    [ObservableProperty] private string _stepElapsedText = "";
    [ObservableProperty] private string _stepRemainingText = "";
    [ObservableProperty] private double _overallProgress;
    [ObservableProperty] private string _overallText = "";

    private readonly DispatcherTimer _stepTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _stepStartedAt;
    private TimeSpan? _stepDuration; // null for a test with no fixed length (SSD)
    private int _stepIndex;
    private int _stepTotal;

    public bool CanEdit => !IsRunning;

    public bool IsOffline => _settings.OfflineMode;

    // Overwritten on every drive change so a stale path never points at the wrong physical drive.
    partial void OnSelectedDriveChanged(SsdDriveOption? value) => ApplyDefaultFolder(value);

    private void ApplyDefaultFolder(SsdDriveOption? option)
    {
        if (option is { DriveLetters.Count: > 0 })
        {
            TargetFolder = $"{option.DriveLetters[0]}\\";
        }
    }

    private void OnMachineInitialized()
    {
        OnPropertyChanged(nameof(DriveOptions));
        SelectedDrive ??= DriveOptions.FirstOrDefault();
        StartCommand.NotifyCanExecuteChanged();
    }

    private void OnDependencyChanged(object? sender, PropertyChangedEventArgs e) => StartCommand.NotifyCanExecuteChanged();

    private void RefreshDurations()
    {
        var level = _settings.CpuLoad switch
        {
            CpuLoadLevel.MaxHeat => "max heat",
            CpuLoadLevel.BlendNoAvx => "blend, AVX off",
            _ => "blend",
        };
        CpuDuration = $"{_settings.CpuDurationMinutes} min, {level}";
        GpuDuration = $"{_settings.GpuDurationMinutes} min";
        VramDuration = $"{_settings.VramDurationMinutes} min";
        RamDuration = $"{_settings.RamDurationMinutes} min";
        OnPropertyChanged(nameof(IsOffline));
    }

    private bool AnySelected => IncludeCpu || IncludeGpu || IncludeVram || IncludeRam || IncludeSsd;

    private bool CanStart() => !_state.IsBusy && Machine.SensorsHealthy && Machine.IsInitialized && AnySelected;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var controller = _controllers.GetOrCreate();
        if (controller is null)
        {
            Warn("Cannot start", _controllers.Error ?? "The test controller is not available.");
            return;
        }

        if (string.IsNullOrWhiteSpace(Machine.RealMoboSerial))
        {
            Warn("Cannot start",
                "No motherboard serial available - cannot create a session without the PC's primary identity. See the Log page for details.");
            return;
        }

        if (IncludeSsd)
        {
            if (SelectedDrive is null)
            {
                Warn("No drive selected", "Select a drive to benchmark first.");
                return;
            }

            ApplyDefaultFolder(SelectedDrive);
            if (string.IsNullOrWhiteSpace(TargetFolder))
            {
                Warn("No target folder",
                    "Couldn't auto-detect a folder on the selected drive - type one manually (e.g. \"D:\\\") before starting.");
                return;
            }
        }

        var tests = new List<TestKind>();
        if (IncludeCpu) tests.Add(TestKind.Cpu);
        if (IncludeGpu) tests.Add(TestKind.Gpu);
        if (IncludeVram) tests.Add(TestKind.Vram);
        if (IncludeRam) tests.Add(TestKind.Ram);
        if (IncludeSsd) tests.Add(TestKind.Ssd);

        var ssdSerials = new List<string>();
        try
        {
            ssdSerials = Machine.Sensors.ReadSsds()
                .Where(d => d.SerialNumber is not null)
                .Select(d => d.SerialNumber!)
                .ToList();
        }
        catch (Exception ex)
        {
            _log.Append($"WARNING: could not read SSD serials for session creation: {ex.Message}");
        }

        var request = new CreateSessionRequest
        {
            MoboSerial = Machine.RealMoboSerial,
            CustomerName = string.IsNullOrWhiteSpace(CustomerName) ? null : CustomerName.Trim(),
            SessionType = IsNewBuild ? SessionType.NewBuild : SessionType.Repair,
            Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
            SsdSerials = ssdSerials.Count > 0 ? ssdSerials : null,
            SystemInfo = Machine.SystemInfo,
        };

        if (_settings.OfflineMode)
        {
            _log.Append("OFFLINE MODE: nothing from this series is sent to the server.");
        }

        IsRunning = true;
        _state.Begin();
        StepText = "Starting...";

        try
        {
            await controller.RunSeriesAsync(
                request, tests, SelectedDrive, TargetFolder.Trim(), _log.Append,
                onStep: (test, index, total) => OnUi(() => BeginStep(test, index, total)),
                onCpu: Machine.UpdateCpu, onGpu: Machine.UpdateGpu, onRam: Machine.UpdateRam,
                onSsdSpeed: Machine.UpdateSsdSpeed);
        }
        catch (Exception ex)
        {
            _log.Append($"Test series ended with an error: {ex.Message}");
            Warn("Error", $"Test series ended with an error:\n\n{ex.Message}", MessageBoxImage.Error);
        }
        finally
        {
            _stepTimer.Stop();
            StepText = "";
            StepCounterText = "";
            StepProgress = 0;
            StepPercentText = "";
            StepElapsedText = "";
            StepRemainingText = "";
            OverallProgress = 0;
            OverallText = "";
            IsRunning = false;
            _state.End();
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Stop()
    {
        _controllers.RequestStopAll();
        _log.Append("Stop requested by technician - finishing the current test and skipping the rest...");
        StepText = "Stopping...";
    }

    private void BeginStep(TestKind test, int index, int total)
    {
        _stepIndex = index;
        _stepTotal = total;
        _stepStartedAt = DateTime.Now;
        _stepDuration = test switch
        {
            TestKind.Cpu => TimeSpan.FromMinutes(_settings.CpuDurationMinutes),
            TestKind.Gpu => TimeSpan.FromMinutes(_settings.GpuDurationMinutes),
            TestKind.Vram => TimeSpan.FromMinutes(_settings.VramDurationMinutes),
            TestKind.Ram => TimeSpan.FromMinutes(_settings.RamDurationMinutes),
            _ => null, // SSD runs until both passes finish
        };
        StepText = Label(test);
        StepCounterText = $"Test {index} of {total}";
        StepIndeterminate = _stepDuration is null;
        UpdateStepProgress();
        _stepTimer.Start();
    }

    private void UpdateStepProgress()
    {
        var elapsed = DateTime.Now - _stepStartedAt;
        double fraction = 0;
        if (_stepDuration is { } total && total > TimeSpan.Zero)
        {
            fraction = Math.Min(1, elapsed.TotalSeconds / total.TotalSeconds);
            StepProgress = fraction * 100;
            StepPercentText = $"{(int)StepProgress}%";
            StepElapsedText = $"Elapsed {FormatSpan(elapsed)} of {FormatSpan(total)}";
            var remaining = total - elapsed;
            StepRemainingText = remaining > TimeSpan.Zero ? $"{FormatSpan(remaining)} remaining" : "finishing up...";
        }
        else
        {
            StepPercentText = "";
            StepElapsedText = $"Elapsed {FormatSpan(elapsed)}";
            StepRemainingText = "";
        }

        OverallProgress = ((_stepIndex - 1) + fraction) * 100.0 / Math.Max(1, _stepTotal);
        OverallText = $"Overall: {_stepIndex - 1} of {_stepTotal} tests done";
    }

    private static string FormatSpan(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}";

    private static string Label(TestKind test) => test switch
    {
        TestKind.Cpu => "CPU",
        TestKind.Gpu => "GPU",
        TestKind.Vram => "VRAM",
        TestKind.Ram => "RAM",
        _ => "SSD",
    };

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private static void Warn(string title, string message, MessageBoxImage image = MessageBoxImage.Warning)
        => MessageBox.Show(message, title, MessageBoxButton.OK, image);
}
