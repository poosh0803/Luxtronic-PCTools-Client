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
    private IReadOnlyList<SsdDriveOption> _ssdDriveOptions = Array.Empty<SsdDriveOption>();

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
            InitializeSsdDrivePicker();
        }
        await InitializeDurationTextsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Shows the server-configured CPU/GPU/RAM test durations before the technician clicks Start,
    /// not just after (RunCpuTestSessionAsync/RunGpuTestSessionAsync/RunRamTestSessionAsync each
    /// fetch their own fresh copy at run time regardless - see
    /// TestSessionController.GetConfigAsync remarks). One config fetch, not three, since all three
    /// durations come from the same GET /api/config response. Best-effort: if the server's
    /// unreachable at app open, this just leaves a clear placeholder rather than blocking startup -
    /// the same fetch happens again for real when Start is clicked.
    /// </summary>
    private async Task InitializeDurationTextsAsync()
    {
        if (_controller is null)
        {
            CpuDurationText.Text = "(duration unavailable - controller not initialized)";
            GpuDurationText.Text = "(duration unavailable - controller not initialized)";
            RamDurationText.Text = "(duration unavailable - controller not initialized)";
            return;
        }

        try
        {
            var config = await _controller.GetConfigAsync().ConfigureAwait(true);
            CpuDurationText.Text = config.Cpu is { } cpuCfg
                ? $"(duration: {cpuCfg.DurationMinutes} min)"
                : "(duration: not set in server config)";
            GpuDurationText.Text = config.Gpu is { } gpuCfg
                ? $"(duration: {gpuCfg.DurationMinutes} min)"
                : "(duration: not set in server config)";
            RamDurationText.Text = config.Ram is { } ramCfg
                ? $"(duration: {ramCfg.DurationMinutes} min)"
                : "(duration: not set in server config)";
        }
        catch (Exception ex)
        {
            CpuDurationText.Text = "(duration unavailable - could not reach server)";
            GpuDurationText.Text = "(duration unavailable - could not reach server)";
            RamDurationText.Text = "(duration unavailable - could not reach server)";
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
            GpuTestCheck.IsEnabled = false;
            GpuTestCheck.IsChecked = false;
            RamTestCheck.IsEnabled = false;
            RamTestCheck.IsChecked = false;
            SsdTestCheck.IsEnabled = false;
            SsdTestCheck.IsChecked = false;
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
                    GpuTestCheck.IsEnabled = false;
                    GpuTestCheck.IsChecked = false;
                    RamTestCheck.IsEnabled = false;
                    RamTestCheck.IsChecked = false;
                    SsdTestCheck.IsEnabled = false;
                    SsdTestCheck.IsChecked = false;
                    StartButton.IsEnabled = false;
                    AppendLog("CPU/GPU/RAM/SSD tests disabled: sensors are not reporting data. Fix the driver/elevation issue above and restart the app.");
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
                GpuTestCheck.IsEnabled = false;
                GpuTestCheck.IsChecked = false;
                RamTestCheck.IsEnabled = false;
                RamTestCheck.IsChecked = false;
                SsdTestCheck.IsEnabled = false;
                SsdTestCheck.IsChecked = false;
                StartButton.IsEnabled = false;
            }
        }

        try
        {
            var apiKey = new ApiKeyProvider(_settings.ApiKeyFilePath);
            AppendLog($"API key loaded from {apiKey.SourcePath}.");
            _controller = new TestSessionController(
                _settings, apiKey, _sensors!,
                new Prime95Runner(_settings.ToolsDirectory), new FurMarkRunner(_settings.GpuToolsDirectory),
                new TM5Runner(_settings.RamToolsDirectory), new DiskSpdRunner(_settings.SsdToolsDirectory));
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

            var furmarkPath = Path.Combine(_settings.GpuToolsDirectory, "furmark.exe");
            var furmarkFound = File.Exists(furmarkPath);
            AppendLog(furmarkFound
                ? $"FurMark found at {furmarkPath}."
                : $"FurMark NOT found at {furmarkPath} - drop the real FurMark 2 install there before " +
                  "starting a GPU test (see tools/FurMark_win64/README.md). Start will fail until then.");

            var tm5Path = Path.Combine(_settings.RamToolsDirectory, "TM5.exe");
            var tm5Found = File.Exists(tm5Path);
            AppendLog(tm5Found
                ? $"TM5 found at {tm5Path}."
                : $"TM5 NOT found at {tm5Path} - drop the real TestMem5 install there before " +
                  "starting a RAM test (see tools/TestMem5/README.md). Start will fail until then.");

            var diskSpdPath = Path.Combine(_settings.SsdToolsDirectory, "DiskSpd64.exe");
            var diskSpdFound = File.Exists(diskSpdPath);
            AppendLog(diskSpdFound
                ? $"DiskSpd found at {diskSpdPath}."
                : $"DiskSpd NOT found at {diskSpdPath} - should already be bundled under " +
                  "tools/DiskSpd/. Start will fail until then.");
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

    /// <summary>Populates SsdDriveCombo with every detected physical drive, each resolved to its
    /// mounted letter(s) via DriveLetterResolver - so the technician picks a drive by model/serial
    /// (same identity shown in SsdSummaryText) rather than a bare letter. Best-effort like
    /// InitializeSsdSummary: a resolution failure leaves the combo empty with a log warning rather
    /// than blocking the rest of the app.</summary>
    private void InitializeSsdDrivePicker()
    {
        if (_sensors is null)
        {
            return;
        }

        try
        {
            var drives = _sensors.ReadSsds();
            _ssdDriveOptions = DriveLetterResolver.Resolve(drives);
            SsdDriveCombo.ItemsSource = _ssdDriveOptions;
            if (_ssdDriveOptions.Count > 0)
            {
                SsdDriveCombo.SelectedIndex = 0;
                TryDefaultTargetFolder(_ssdDriveOptions[0]); // idempotent even if SelectionChanged above also fired this
            }

            foreach (var option in _ssdDriveOptions)
            {
                AppendLog($"SSD benchmark candidate: {option.DisplayText}");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"WARNING: could not build SSD drive picker: {ex.Message}");
        }
    }

    // Null-guarded: CpuTestCheck's XAML-set IsChecked="True" fires this Checked handler during
    // InitializeComponent() itself, before GpuTestCheck/RamTestCheck/SsdTestCheck (declared later
    // in the XAML) have been assigned yet - confirmed by reproducing it, a real
    // NullReferenceException crash on every launch, not a hypothetical. All four are always
    // non-null for any real user interaction, which only happens after InitializeComponent() has
    // fully returned.
    //
    // Four-way mutual exclusion. For CPU/GPU this is purely a client-side simplification (the
    // server already supports running them together - CONTRACT.md §6/concurrency.js - just not
    // attempted client-side yet). For RAM and SSD specifically it's not just a simplification:
    // CONTRACT.md §6 requires both to never run concurrently with anything else, a real
    // server-enforced rule - don't relax RamTestCheck's/SsdTestCheck's exclusion even if a future
    // pass adds CPU+GPU "together" mode.
    private void CpuTestCheck_Checked(object sender, RoutedEventArgs e)
    {
        if (GpuTestCheck is not null) GpuTestCheck.IsChecked = false;
        if (RamTestCheck is not null) RamTestCheck.IsChecked = false;
        if (SsdTestCheck is not null) SsdTestCheck.IsChecked = false;
    }

    private void GpuTestCheck_Checked(object sender, RoutedEventArgs e)
    {
        if (CpuTestCheck is not null) CpuTestCheck.IsChecked = false;
        if (RamTestCheck is not null) RamTestCheck.IsChecked = false;
        if (SsdTestCheck is not null) SsdTestCheck.IsChecked = false;
    }

    private void RamTestCheck_Checked(object sender, RoutedEventArgs e)
    {
        if (CpuTestCheck is not null) CpuTestCheck.IsChecked = false;
        if (GpuTestCheck is not null) GpuTestCheck.IsChecked = false;
        if (SsdTestCheck is not null) SsdTestCheck.IsChecked = false;
    }

    private void SsdTestCheck_Checked(object sender, RoutedEventArgs e)
    {
        if (CpuTestCheck is not null) CpuTestCheck.IsChecked = false;
        if (GpuTestCheck is not null) GpuTestCheck.IsChecked = false;
        if (RamTestCheck is not null) RamTestCheck.IsChecked = false;
        SsdDrivePickerPanel.Visibility = Visibility.Visible;
    }

    private void SsdTestCheck_Unchecked(object sender, RoutedEventArgs e)
    {
        SsdDrivePickerPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>Sets SsdTargetFolderBox to the newly-selected drive's first resolved letter root
    /// (e.g. "D:\") - a starting point the technician can edit, not a locked value. Left untouched
    /// if the selected drive has no resolved letters (DriveLetterResolver's best-effort match
    /// found nothing) so the technician's own manual entry isn't clobbered.</summary>
    private void SsdDriveCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (SsdDriveCombo.SelectedItem is SsdDriveOption option)
        {
            TryDefaultTargetFolder(option);
        }
    }

    /// <summary>Fills SsdTargetFolderBox from <paramref name="option"/>'s first resolved drive
    /// letter, so the technician never has to type a path by hand for the common case - only when
    /// DriveLetterResolver couldn't resolve any letter for the selected drive does the box stay
    /// empty, requiring a manual entry (StartButton_Click still guards against that). Always
    /// overwrites, even if the box already has text - called both at picker-init time (so the
    /// auto-selected first drive is pre-filled without any technician action) and on every
    /// selection change (so switching drives doesn't leave a stale path pointing at the wrong
    /// physical drive).</summary>
    private void TryDefaultTargetFolder(SsdDriveOption option)
    {
        if (option.DriveLetters.Count > 0)
        {
            SsdTargetFolderBox.Text = $"{option.DriveLetters[0]}\\";
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

        // CpuTestCheck/GpuTestCheck/RamTestCheck/SsdTestCheck are mutually exclusive (see the
        // Checked handlers above), so at most one of these is true - this is just "was anything
        // selected at all".
        if (CpuTestCheck.IsChecked != true && GpuTestCheck.IsChecked != true &&
            RamTestCheck.IsChecked != true && SsdTestCheck.IsChecked != true)
        {
            MessageBox.Show(this, "Select a test to run (CPU, GPU, RAM, or SSD).",
                "Nothing selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SsdDriveOption? selectedSsdDrive = null;
        if (SsdTestCheck.IsChecked == true)
        {
            selectedSsdDrive = SsdDriveCombo.SelectedItem as SsdDriveOption;
            if (selectedSsdDrive is null)
            {
                MessageBox.Show(this, "Select a drive to benchmark first.",
                    "No drive selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Auto-fill from the selected drive rather than requiring the technician to type
            // anything - covers the case where they picked a drive but SelectionChanged's default
            // somehow didn't stick (e.g. programmatic selection). Only actually blocks Start if
            // the drive truly has no resolved letters AND nothing was typed manually either -
            // DiskSpdRunner has no raw-device fallback (see its class remarks), so there's no safe
            // path forward without a real folder.
            TryDefaultTargetFolder(selectedSsdDrive);
            if (string.IsNullOrWhiteSpace(SsdTargetFolderBox.Text))
            {
                MessageBox.Show(this,
                    "Couldn't auto-detect a folder on the selected drive - type one manually " +
                    "(e.g. \"D:\\\") before starting.",
                    "No target folder", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
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
            if (CpuTestCheck.IsChecked == true)
            {
                await _controller.RunCpuTestSessionAsync(request, AppendLog, UpdateLiveReadout).ConfigureAwait(true);
            }
            else if (GpuTestCheck.IsChecked == true)
            {
                await _controller.RunGpuTestSessionAsync(request, AppendLog, UpdateGpuLiveReadout).ConfigureAwait(true);
            }
            else if (RamTestCheck.IsChecked == true)
            {
                await _controller.RunRamTestSessionAsync(request, AppendLog, UpdateRamLiveReadout).ConfigureAwait(true);
            }
            else
            {
                await _controller.RunSsdTestSessionAsync(
                    request, selectedSsdDrive!.Drive, SsdTargetFolderBox.Text.Trim(), AppendLog).ConfigureAwait(true);
            }
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
        GpuTestCheck.IsEnabled = !running && _sensorsHealthy;
        RamTestCheck.IsEnabled = !running && _sensorsHealthy;
        SsdTestCheck.IsEnabled = !running && _sensorsHealthy;
        SsdDriveCombo.IsEnabled = !running;
        SsdTargetFolderBox.IsEnabled = !running;
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

            // This is only the idle-time GPU reader - RunGpuTestSessionAsync does its own polling
            // (via onReading -> UpdateGpuLiveReadout) while a GPU test is actually running, same
            // as CPU. StopIdleSensorTimer() is called before either test starts (see
            // StartButton_Click), so there's no risk of this and a running test's own poll loop
            // reading concurrently.
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

    /// <summary>GPU equivalent of <see cref="UpdateLiveReadout"/> - same threading contract
    /// (RunGpuTestSessionAsync's poll loop calls this from a background thread).</summary>
    private void UpdateGpuLiveReadout(GpuReadings reading)
    {
        void Update() => GpuStatusText.Text = SensorMonitor.FormatGpuLiveReadout(reading);

        if (Dispatcher.CheckAccess())
        {
            Update();
        }
        else
        {
            Dispatcher.Invoke(Update);
        }
    }

    /// <summary>RAM equivalent of <see cref="UpdateLiveReadout"/> - same threading contract
    /// (RunRamTestSessionAsync's Log.txt poll loop calls this from a background thread). No
    /// SensorMonitor.Format*LiveReadout equivalent exists for RAM (it's log-tailed text, not a
    /// SensorReadings/GpuReadings record), so this formats inline.</summary>
    private void UpdateRamLiveReadout(RamTestProgress progress)
    {
        void Update()
        {
            var mb = progress.TestedMb is double m ? $"{m:F0} MB" : "--";
            RamStatusText.Text = $"Tested: {mb}   Errors: {progress.ErrorCount}";
        }

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
