using System.ComponentModel;
using Luxtronic.PCTools.Services.Shell;
using Luxtronic.PCTools.UI.Pages;
using Luxtronic.PCTools.UI.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Abstractions;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Luxtronic.PCTools.UI.Windows;

public partial class MainWindow : FluentWindow
{
    private readonly MachineService _machine;
    private readonly ControllerProvider _controllers;
    private readonly Luxtronic.PCTools.Services.AppSettingsProvider _settings;
    private readonly LogService _log;
    private bool _closing;

    public MainWindow(
        INavigationViewPageProvider pageProvider,
        ISnackbarService snackbar,
        MachineService machine,
        ControllerProvider controllers,
        Luxtronic.PCTools.Services.AppSettingsProvider settings,
        LogService log)
    {
        _settings = settings;
        _log = log;
        _machine = machine;
        _controllers = controllers;

        SystemThemeWatcher.Watch(this);
        InitializeComponent();

        RootNavigation.SetPageProviderService(pageProvider);
        snackbar.SetSnackbarPresenter(SnackbarPresenter);

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        // Fresh install: the three-field connection wizard first. Skipping it saves the defaults
        // (so it is not shown again) and falls through to the Settings page below.
        if (_settings.IsFirstRun)
        {
            var wizard = new FirstRunWindow(_settings) { Owner = this };
            wizard.ShowDialog();
            if (!wizard.Completed)
            {
                _settings.MarkSetupSeen();
            }
        }

        // No API key yet (e.g. wizard skipped), so start on Settings where it is entered.
        // Deferred to idle: NavigationView selects its first item itself after Loaded, which
        // would otherwise override this and land on the Dashboard anyway.
        var firstPage = _settings.ApiKey is null && !_settings.OfflineMode ? typeof(SettingsPage) : typeof(DashboardPage);
        _ = Dispatcher.BeginInvoke(new Action(() => RootNavigation.Navigate(firstPage)),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await _machine.InitializeAsync();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        if (_controllers.AnyRunning)
        {
            var choice = System.Windows.MessageBox.Show("A test is still running. Stop it and close?", "Test in progress",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (choice != System.Windows.MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            _controllers.RequestStopAll();
        }

        // Closing must wait for the running test (if any) to finish its stop sequence, so cancel
        // this close, tear down, then close again.
        e.Cancel = true;
        _closing = true;
        _machine.Dispose();
        await _controllers.DisposeAsync();
        _ = Dispatcher.BeginInvoke(new Action(Close)); // not inline: Close() throws if called while this handler is still running
    }
}
