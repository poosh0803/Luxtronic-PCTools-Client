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

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var sensorsReady = await InitializeSensorsAsync().ConfigureAwait(true);
        if (sensorsReady)
        {
            InitializeSsdSummary();
        }
        await InitializeCpuDurationAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Shows the server-configured CPU test duration before the technician clicks Start, not
    /// just after (RunCpuTestSessionAsync fetches its own fresh copy at run time regardless - see
    /// TestSessionController.GetConfigAsync remarks). Best-effort: if the server's unreachable at
    /// app open, this just leaves a clear placeholder rather than blocking startup - the same
    /// fetch happens again for real when Start is clicked.
    /// </summary>
    private async Task InitializeCpuDurationAsync()
    {
        if (_controller is null)
        {
            CpuDurationText.Text = "(duration unavailable - controller not initialized)";
            return;
        }

        try
        {
            var config = await _controller.GetConfigAsync().ConfigureAwait(true);
            CpuDurationText.Text = config.Cpu is { } cpuCfg
                ? $"(duration: {cpuCfg.DurationMinutes} min)"
                : "(duration: not set in server config)";
        }
        catch (Exception ex)
        {
            CpuDurationText.Text = "(duration unavailable - could not reach server)";
            AppendLog($"WARNING: could not fetch server config for duration display: {ex.Message}");
        }
    }

    /// <summary>How long <see cref="InitializeSensorsAsync"/> waits for LibreHardwareMonitorLib's
    /// native driver open (and the WMI motherboard-serial lookup nested inside it) before giving up
    /// on blocking the UI thread and reporting a hang instead. Both are known to be able to stall
    /// indefinitely on certain hardware/WMI-repository states rather than fail fast - see remarks
    /// below.</summary>
    private static readonly TimeSpan SensorInitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Runs LibreHardwareMonitorLib init up front (on app open, not deferred to Start) so the
    /// technician sees the WinRing0/driver risk called out in PROJECT_PLAN.md §8 immediately,
    /// rather than 60 minutes into a test run.
    ///
    /// <see cref="SensorMonitor.Initialize"/> (native driver open, plus a synchronous WMI query for
    /// the motherboard serial) is run on a background thread with a bounded wait, not awaited
    /// directly on the UI thread - both of those calls are known to be able to hang rather than
    /// fail fast on some real machines (a published build was seen showing a permanently blank/
    /// unresponsive window on a technician's test machine; Task Manager reported it as "Not
    /// Responding" even though the app was launched elevated, ruling out a UAC-prompt wait). If the
    /// call doesn't return within <see cref="SensorInitTimeout"/>, this reports a clear "may be
    /// hung" warning and lets the UI stay responsive instead of freezing forever; the background
    /// task is left running (fire-and-forget) in case it eventually resolves, since there's no safe
    /// way to cancel a blocking native/WMI call already in progress.
    ///
    /// Returns whether sensors are usable so <see cref="MainWindow_Loaded"/> can skip
    /// <see cref="InitializeSsdSummary"/> when initialization is still in flight - calling
    /// <see cref="SensorMonitor.ReadSsds"/> while <see cref="SensorMonitor.Initialize"/> is still
    /// running on another thread would be a concurrent-use hazard on the shared Computer/visitor
    /// (see SensorMonitor's class remarks on why there's only ever one Computer instance).
    /// </summary>
    private async Task<bool> InitializeSensorsAsync()
    {
        _sensors = new SensorMonitor();
        SensorStatusText.Text = "Initializing sensors...";

        var initTask = Task.Run(() => _sensors.Initialize());
        var completed = await Task.WhenAny(initTask, Task.Delay(SensorInitTimeout)).ConfigureAwait(true);

        if (completed != initTask)
        {
            _sensorsHealthy = false;
            SensorStatusText.Text =
                $"WARNING: Sensor initialization has not returned after {SensorInitTimeout.TotalSeconds:F0}s " +
                "and may be hung (seen in the field - LibreHardwareMonitorLib's native driver open or its WMI " +
                "motherboard-serial lookup can stall indefinitely on some hardware/WMI states instead of " +
                "failing fast). The app stays usable, but the CPU test is disabled until this resolves - " +
                "closing and relaunching (possibly after a machine restart, if WMI is the culprit) is the " +
                "most reliable fix.";
            SensorStatusText.Foreground = Brushes.Firebrick;
            MoboSerialText.Text = "(unavailable - sensor initialization has not completed)";
            AppendLog(SensorStatusText.Text);
            CpuTestCheck.IsEnabled = false;
            CpuTestCheck.IsChecked = false;
            StartButton.IsEnabled = false;

            // Best-effort: if the stuck call eventually does return, at least log it instead of
            // silently discarding the result - but don't touch UI state beyond that, since the
            // technician has already been told a restart is the reliable path.
            _ = initTask.ContinueWith(t =>
            {
                var message = t.IsFaulted
                    ? $"Delayed sensor initialization eventually failed: {t.Exception!.GetBaseException().Message}"
                    : $"Delayed sensor initialization eventually completed: {t.Result.Message}";
                Dispatcher.Invoke(() => AppendLog(
                    $"NOTE: {message} (after the {SensorInitTimeout.TotalSeconds:F0}s hang warning above - " +
                    "restart the app to pick this up cleanly rather than relying on this late result)."));
            }, TaskScheduler.Default);
        }
        else
        {
            try
            {
                var result = await initTask.ConfigureAwait(true);

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

        return completed == initTask;
    }

    /// <summary>
    /// One-time point-in-time SMART read at app open, shown for informational/visibility purposes
    /// only - not wired into TestSessionController/orchestration yet (see README's "Current
    /// scope"), so this never blocks Start or affects the CPU test either way. Goes through
    /// _sensors.ReadSsds() (the same shared Computer as CPU reads) rather than a separate reader -
    /// LibreHardwareMonitorLib doesn't support two Computer instances in one process, see
    /// SensorMonitor's class remarks. Not gated on _sensorsHealthy (that verdict is specifically
    /// about CPU temperature readability - storage can read fine independent of it); ReadSsds()
    /// itself throws a clear InvalidOperationException if _sensors never initialized, caught below.
    /// </summary>
    private void InitializeSsdSummary()
    {
        if (_sensors is null)
        {
            SsdSummaryText.Text = "(unavailable - sensors did not initialize)";
            return;
        }

        try
        {
            var drives = _sensors.ReadSsds();

            if (drives.Count == 0)
            {
                SsdSummaryText.Text = "(no drives detected via LibreHardwareMonitorLib)";
                return;
            }

            SsdSummaryText.Text = string.Join(Environment.NewLine, drives.Select(SsdSmartReader.FormatSsdSummary));
            foreach (var drive in drives)
            {
                AppendLog($"SSD detected: {SsdSmartReader.FormatSsdSummary(drive)}");
            }
        }
        catch (Exception ex)
        {
            SsdSummaryText.Text = $"SMART read failed: {ex.Message}";
            AppendLog($"WARNING: SSD SMART read failed: {ex.Message}");
        }
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

        var ssdSerials = new List<string>();
        try
        {
            ssdSerials = _sensors!.ReadSsds()
                .Where(d => d.SerialNumber is not null)
                .Select(d => d.SerialNumber!)
                .ToList();
        }
        catch (Exception ex)
        {
            AppendLog($"WARNING: could not read SSD serials for session creation: {ex.Message}");
        }

        var request = new CreateSessionRequest
        {
            MoboSerial = _moboSerial!,
            CustomerName = string.IsNullOrWhiteSpace(CustomerNameBox.Text) ? null : CustomerNameBox.Text.Trim(),
            SessionType = NewBuildRadio.IsChecked == true ? SessionType.NewBuild : SessionType.Repair,
            Notes = string.IsNullOrWhiteSpace(NotesBox.Text) ? null : NotesBox.Text.Trim(),
            SsdSerials = ssdSerials.Count > 0 ? ssdSerials : null,
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

            // GPU isn't part of TestSessionController's poll loop (CPU-only test this pass), so
            // this - the idle timer - is its only reader. It naturally goes stale during a CPU
            // test run (timer stopped, same as CPU's live readout freezing), which is fine since
            // no GPU test can be running concurrently anyway (no GPU checkbox wired up yet).
            try
            {
                GpuStatusText.Text = SensorMonitor.FormatGpuLiveReadout(_sensors!.ReadGpu());
            }
            catch (Exception ex)
            {
                AppendLog($"Idle GPU poll failed: {ex.Message}");
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
