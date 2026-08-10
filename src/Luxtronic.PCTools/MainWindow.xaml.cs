using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Luxtronic.PCTools.Models;
using Luxtronic.PCTools.Services;

namespace Luxtronic.PCTools;

public partial class MainWindow : Window
{
    private readonly AppSettingsProvider _settings;
    private SensorMonitor? _sensors;
    private TestSessionController? _controller;
    private string? _moboSerial;
    private bool _sensorsHealthy;

    /// <summary>
    /// Polls sensors and updates <see cref="SensorStatusText"/> with a live readout while idle
    /// (no test running). Must be stopped before a test starts and restarted once it ends -
    /// SensorMonitor.ReadCpu() isn't safe to call concurrently from two places, and
    /// TestSessionController does its own polling (and pushes readings via onReading) while a
    /// test is in progress.
    /// </summary>
    private DispatcherTimer? _idleSensorTimer;

    public MainWindow(AppSettingsProvider settings)
    {
        InitializeComponent();
        _settings = settings;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeSensors();
    }

    /// <summary>
    /// Runs LibreHardwareMonitorLib init up front (on app open, not deferred to Start) so the
    /// technician sees the WinRing0/driver risk called out in PROJECT_PLAN.md §8 immediately,
    /// rather than 60 minutes into a test run.
    /// </summary>
    private void InitializeSensors()
    {
        try
        {
            _sensors = new SensorMonitor();
            var result = _sensors.Initialize();

            _sensorsHealthy = result.DriverLikelyLoaded;
            _moboSerial = result.MotherboardSerial;

            MoboSerialText.Text = _moboSerial ?? "(not available - WMI returned no serial)";
            SensorStatusText.Text = result.Message;
            SensorStatusText.Foreground = _sensorsHealthy ? Brushes.ForestGreen : Brushes.Firebrick;

            AppendLog(result.Message);
            if (_moboSerial is null)
            {
                AppendLog("WARNING: no motherboard serial available via WMI (Win32_BaseBoard). " +
                          "This is the PC's primary identity per CONTRACT.md - sessions cannot be created without it.");
            }

            if (!_sensorsHealthy)
            {
                CpuTestCheck.IsEnabled = false;
                CpuTestCheck.IsChecked = false;
                StartButton.IsEnabled = false;
                AppendLog("CPU test disabled: sensors are not reporting data. Fix the driver/elevation issue above and restart the app.");
            }
            else
            {
                StartIdleSensorTimer();
            }
        }
        catch (Exception ex)
        {
            _sensorsHealthy = false;
            SensorStatusText.Text = $"Sensor initialization threw an exception: {ex.Message}";
            SensorStatusText.Foreground = Brushes.Firebrick;
            MoboSerialText.Text = "(unavailable)";
            AppendLog($"ERROR initializing sensors: {ex}");
            CpuTestCheck.IsEnabled = false;
            CpuTestCheck.IsChecked = false;
            StartButton.IsEnabled = false;
        }

        try
        {
            var apiKey = new ApiKeyProvider(_settings.ApiKeyFilePath);
            AppendLog($"API key loaded from {apiKey.SourcePath}.");
            _controller = new TestSessionController(_settings, apiKey, _sensors!, new Prime95Runner(_settings.ToolsDirectory));
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            StartButton.IsEnabled = false;
        }

        if (_controller is not null)
        {
            var prime95Path = Path.Combine(_settings.ToolsDirectory, "prime95.exe");
            var exeFound = File.Exists(prime95Path);
            AppendLog(exeFound
                ? $"Prime95 found at {prime95Path}."
                : $"Prime95 NOT found at {prime95Path} - drop the real binary there before starting a " +
                  "test (see tools/prime95/README.md). Start will fail until then.");
        }

        AppendLog($"Server: {_settings.ServerBaseUrl}");
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is null || _controller.IsRunning)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_moboSerial))
        {
            MessageBox.Show(this,
                "No motherboard serial available - cannot create a session without the PC's primary identity. " +
                "See the status log for details.",
                "Cannot start", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (CpuTestCheck.IsChecked != true)
        {
            MessageBox.Show(this, "Select the CPU test to run (it's the only test wired up this pass).",
                "Nothing selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var request = new CreateSessionRequest
        {
            MoboSerial = _moboSerial!,
            CustomerName = string.IsNullOrWhiteSpace(CustomerNameBox.Text) ? null : CustomerNameBox.Text.Trim(),
            SessionType = NewBuildRadio.IsChecked == true ? SessionType.NewBuild : SessionType.Repair,
            Notes = string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim(),
        };

        SetRunningUiState(true);
        StopIdleSensorTimer();

        try
        {
            await _controller.RunCpuTestSessionAsync(request, AppendLog, UpdateLiveReadout).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppendLog($"Test session ended with an error: {ex.Message}");
            MessageBox.Show(this, $"Test session ended with an error:\n\n{ex.Message}",
                "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetRunningUiState(false);
            if (_sensorsHealthy)
            {
                StartIdleSensorTimer();
            }
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _controller?.RequestStop();
        AppendLog("Stop requested by technician - finishing up (completing test run, ending session)...");
        StopButton.IsEnabled = false;
    }

    private void SetRunningUiState(bool running)
    {
        StartButton.IsEnabled = !running;
        StopButton.IsEnabled = running;
        CustomerNameBox.IsEnabled = !running;
        NotesBox.IsEnabled = !running;
        NewBuildRadio.IsEnabled = !running;
        RepairRadio.IsEnabled = !running;
        CpuTestCheck.IsEnabled = !running && _sensorsHealthy;
        RunningIndicator.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Starts polling sensors ~1/sec to show a live readout while no test is running.
    /// Must not run concurrently with TestSessionController's own polling - see the field
    /// remarks on <see cref="_idleSensorTimer"/>.</summary>
    private void StartIdleSensorTimer()
    {
        if (_idleSensorTimer is not null)
        {
            return;
        }

        _idleSensorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _idleSensorTimer.Tick += (_, _) =>
        {
            try
            {
                UpdateLiveReadout(_sensors!.ReadCpu());
            }
            catch (Exception ex)
            {
                AppendLog($"Idle sensor poll failed: {ex.Message}");
            }
        };
        _idleSensorTimer.Start();
    }

    private void StopIdleSensorTimer()
    {
        _idleSensorTimer?.Stop();
        _idleSensorTimer = null;
    }

    /// <summary>Updates the sensor status line with live values (CONTRACT.md-independent - this
    /// is a UI-only convenience, not part of what's sent to the server). Can be called from a
    /// background thread (TestSessionController's poll loop) or the UI thread (the idle timer),
    /// so it marshals itself.</summary>
    private void UpdateLiveReadout(SensorReadings reading)
    {
        void Update() => SensorStatusText.Text = SensorMonitor.FormatLiveReadout(reading);

        if (Dispatcher.CheckAccess())
        {
            Update();
        }
        else
        {
            Dispatcher.Invoke(Update);
        }
    }

    private void AppendLog(string message)
    {
        void Append()
        {
            LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
            LogBox.ScrollToEnd();
        }

        if (Dispatcher.CheckAccess())
        {
            Append();
        }
        else
        {
            Dispatcher.Invoke(Append);
        }
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_controller is { IsRunning: true })
        {
            var choice = MessageBox.Show(this,
                "A test is still running. Stop it and close?",
                "Test in progress", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _controller.RequestStop();
        }

        StopIdleSensorTimer();
        _sensors?.Dispose();
        if (_controller is not null)
        {
            await _controller.DisposeAsync().ConfigureAwait(true);
        }
    }
}
